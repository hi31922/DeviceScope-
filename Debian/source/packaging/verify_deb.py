"""Independently verify a Debian archive against its portable DeviceScope build.

This checks package structure and installed contents without extracting files,
running Linux binaries, invoking dpkg or changing the host system.
"""
from __future__ import annotations

import argparse
import hashlib
import io
from pathlib import Path, PurePosixPath
import posixpath
import re
import tarfile
import zipfile


def require(condition, message):
    if not condition:
        raise ValueError(message)


def ar_members(data):
    require(data.startswith(b"!<arch>\n"), "Missing Debian ar magic.")
    result = []
    offset = 8
    while offset < len(data):
        header = data[offset:offset + 60]
        require(len(header) == 60 and header[-2:] == b"`\n", "Invalid ar header boundary.")
        name = header[:16].decode("ascii").rstrip().removesuffix("/")
        require(name not in [item[0] for item in result], "Duplicate ar member.")
        for field, base in ((header[16:28], 10), (header[28:34], 10),
                            (header[34:40], 10), (header[40:48], 8)):
            int(field.strip(), base)
        size = int(header[48:58].strip())
        require(size >= 0, "Invalid ar member size.")
        offset += 60
        body = data[offset:offset + size]
        require(len(body) == size, "Truncated ar member.")
        result.append((name, body))
        offset += size
        if size % 2:
            require(data[offset:offset + 1] == b"\n", "Missing ar padding byte.")
            offset += 1
    require(offset == len(data), "Unexpected trailing ar bytes.")
    require([name for name, _ in result] == ["debian-binary", "control.tar.gz", "data.tar.gz"],
            "Unexpected Debian member order or names.")
    require(result[0][1] == b"2.0\n", "Unsupported Debian archive version.")
    return dict(result)


def safe_name(name):
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and bool(path.parts),
            f"Unsafe archive path: {name}")
    return path.as_posix()


def read_tar(data):
    records = {}
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        for member in archive:
            name = safe_name(member.name)
            require(name not in records, f"Duplicate tar member: {name}")
            require(member.isfile() or member.isdir() or member.issym() or member.islnk(),
                    f"Unsupported tar member type: {name}")
            contents = archive.extractfile(member).read() if member.isfile() else None
            records[name] = (member, contents)
    return records


def portable_records(path):
    data = Path(path).read_bytes()
    marker = b"__DEVICESCOPE_PAYLOAD_BELOW__\n"
    offset = data.index(marker) + len(marker)
    header, payload = data[:offset], data[offset:]
    match = re.search(rb'if \[ "\$actual" != "([a-f0-9]{64})"', header)
    require(match is not None and hashlib.sha256(payload).hexdigest().encode() == match[1],
            "Portable payload hash does not match its header.")
    return read_tar(payload)


def control_fields(data):
    fields = {}
    current = None
    for line in data.decode("utf-8").splitlines():
        if line.startswith((" ", "\t")):
            require(current is not None, "Orphan control continuation line.")
            fields[current] += "\n" + line[1:]
        else:
            key, sep, value = line.partition(":")
            require(bool(sep) and bool(key) and key not in fields, "Malformed or duplicate control field.")
            current = key
            fields[key] = value.strip()
    return fields


def verify(package, portable):
    package = Path(package)
    data = package.read_bytes()
    sidecar = package.with_suffix(".deb.sha256").read_text("ascii").split()
    require(sidecar == [hashlib.sha256(data).hexdigest(), package.name],
            "Debian package checksum sidecar mismatch.")
    ar = ar_members(data)
    controls = read_tar(ar["control.tar.gz"])
    require(set(controls) == {"control", "md5sums"}, "Unexpected control members or maintainer scripts.")
    for name, (member, _) in controls.items():
        require(member.isfile() and member.uid == member.gid == 0 and not member.mode & 0o022,
                f"Invalid ownership/type/mode for control member {name}.")
    fields = control_fields(controls["control"][1])
    for key in ("Package", "Version", "Architecture", "Maintainer", "Installed-Size", "Depends", "Description"):
        require(bool(fields.get(key)), f"Missing required control field {key}.")
    require(fields["Package"] == "devicescope", "Wrong package name.")
    require(fields["Version"] == "0.8.0-2", "Wrong package version.")
    require(fields["Architecture"] == "amd64", "Wrong package architecture.")
    require(int(fields["Installed-Size"]) > 0, "Invalid installed size.")
    require("libc6 (>= 2.17)" in fields["Depends"], "Missing runtime libc dependency.")
    require("XWayland" in fields["Description"] and "fonts" in fields["Description"],
            "Desktop requirements are missing from the description.")

    installed = read_tar(ar["data.tar.gz"])
    for name, (member, _) in installed.items():
        allowed = (name in {"opt", "opt/devicescope", "usr", "usr/bin", "usr/bin/devicescope", "usr/share"}
                   or name.startswith("opt/devicescope/") or name.startswith("usr/share/"))
        require(allowed, f"Unexpected install destination: {name}")
        require(member.uid == member.gid == 0, f"Non-root ownership: {name}")
        # Linux ignores symlink mode bits; upstream symlinks conventionally use 0777.
        if not member.issym():
            require(not member.mode & 0o6002, f"World-writable or privileged mode: {name}")
        parent = PurePosixPath(name).parent
        if str(parent) != ".":
            require(str(parent) in installed and installed[str(parent)][0].isdir(),
                    f"Missing or non-directory install parent: {name}")
        if member.issym() or member.islnk():
            link = PurePosixPath(member.linkname)
            require(not link.is_absolute(), f"Absolute runtime link: {name}")
            target = posixpath.normpath(str(PurePosixPath(name).parent / link)
                                       if member.issym() else str(link))
            require(target.startswith("opt/devicescope/python/"), f"Runtime link escapes its prefix: {name}")
            require(target in installed, f"Runtime link target absent: {name}")

    portable = portable_records(portable)
    prefix = "opt/devicescope/"
    mapped = {name[len(prefix):] for name in installed if name.startswith(prefix)}
    implicit_directories = {str(parent) for name in portable for parent in PurePosixPath(name).parents
                            if str(parent) != "."}
    require(set(portable) <= mapped <= set(portable) | implicit_directories,
            "Installed runtime/app paths differ from the portable package.")
    require(all(installed[prefix + name][0].isdir() for name in mapped - set(portable)),
            "Implicit install parents must be directories.")
    for name, (original, original_bytes) in portable.items():
        current, current_bytes = installed[prefix + name]
        require(current.type == original.type and current.mode == original.mode,
                f"Portable type/mode differs after relocation: {name}")
        require(current_bytes == original_bytes, f"Portable file contents differ: {name}")
        if original.issym():
            require(current.linkname == original.linkname, f"Symbolic link changed: {name}")
        elif original.islnk():
            require(safe_name(current.linkname) == prefix + safe_name(original.linkname),
                    f"Hard link relocation differs: {name}")
    binary, binary_data = installed[prefix + "python/bin/python3.13"]
    require(binary_data[:5] == b"\x7fELF\x02" and binary_data[18:20] == b"\x3e\x00"
            and binary.mode & 0o111, "Python is not an executable x86-64 ELF runtime.")
    python_link = installed[prefix + "python/bin/python3"][0]
    require(python_link.issym(), "Python launcher symlink is missing.")

    launcher, launcher_data = installed["usr/bin/devicescope"]
    require(launcher.isfile() and launcher.mode == 0o755, "App launcher is not executable.")
    require(launcher_data.startswith(b"#!/bin/sh\n") and b'app=/opt/devicescope\n' in launcher_data,
            "App launcher uses an unexpected prefix/interpreter.")
    require(b'exec "$app/python/bin/python3" -I -B "$app/app/DeviceScope.pyz" "$@"\n' in launcher_data,
            "App launcher does not execute the bundled application or preserve arguments.")
    for setting in (b'unset PYTHONHOME PYTHONPATH TCLLIBPATH LD_PRELOAD LD_LIBRARY_PATH',
                    b'export TCL_LIBRARY="$app/python/lib/tcl9.0"', b'export TK_LIBRARY="$app/python/lib/tk9.0"'):
        require(setting in launcher_data, "App launcher is missing runtime isolation settings.")
    desktop = installed["usr/share/applications/devicescope.desktop"][1].decode("utf-8")
    for setting in ("[Desktop Entry]", "Type=Application", "Name=DeviceScope", "Exec=/usr/bin/devicescope",
                    "TryExec=/usr/bin/devicescope", "Icon=devicescope", "Terminal=false"):
        require(setting in desktop.splitlines(), f"Missing desktop entry setting: {setting}")
    with zipfile.ZipFile(io.BytesIO(installed[prefix + "app/DeviceScope.pyz"][1])) as app:
        require(installed["usr/share/pixmaps/devicescope.png"][1] == app.read("devicescope/icon.png"),
                "Desktop icon differs from the supplied app icon.")
    require("usr/share/doc/devicescope/copyright" in installed, "Missing copyright notice.")

    expected_md5 = {name: hashlib.md5(contents).hexdigest() for name, (member, contents)
                    in installed.items() if member.isfile()}
    md5sums = {}
    for line in controls["md5sums"][1].decode("utf-8").splitlines():
        digest, sep, name = line.partition("  ")
        require(bool(sep) and re.fullmatch(r"[a-f0-9]{32}", digest) and name not in md5sums,
                "Malformed or duplicate md5sums record.")
        md5sums[name] = digest
    require(md5sums == expected_md5, "md5sums are incomplete or differ from installed regular files.")
    print(f"Verified {package.name}: {len(installed)} install entries, {len(expected_md5)} file hashes; "
          "portable code/runtime preserved, amd64 metadata, launcher, desktop icon and package checksum valid.")
    return fields


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("portable", type=Path)
    arguments = parser.parse_args()
    verify(arguments.package, arguments.portable)
