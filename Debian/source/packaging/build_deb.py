"""Cross-package the verified portable Linux payload as an amd64 Debian package.

Uses Python's standard library; does not run Linux binaries or install anything.
The app and runtime are identical to the supplied .run, installed under /opt.
"""
from __future__ import annotations

import argparse
import copy
import gzip
import hashlib
import io
import math
from pathlib import Path, PurePosixPath
import posixpath
import re
import tarfile
import zipfile

VERSION = "0.8.0-2"
APP_ROOT = "opt/devicescope"
LAUNCHER = b'''#!/bin/sh
set -eu
app=/opt/devicescope
unset PYTHONHOME PYTHONPATH TCLLIBPATH LD_PRELOAD LD_LIBRARY_PATH
export TCL_LIBRARY="$app/python/lib/tcl9.0"
export TK_LIBRARY="$app/python/lib/tk9.0"
exec "$app/python/bin/python3" -I -B "$app/app/DeviceScope.pyz" "$@"
'''
DESKTOP = b'''[Desktop Entry]
Type=Application
Name=DeviceScope
Comment=Inspect hardware, drivers, processes and live performance
Exec=/usr/bin/devicescope
TryExec=/usr/bin/devicescope
Icon=devicescope
Terminal=false
Categories=System;Monitor;
StartupNotify=false
'''


def portable_payload(path):
    data = Path(path).read_bytes()
    marker = b"__DEVICESCOPE_PAYLOAD_BELOW__\n"
    offset = data.index(marker) + len(marker)
    header, payload = data[:offset], data[offset:]
    expected = re.search(rb'if \[ "\$actual" != "([a-f0-9]{64})"', header)
    if not expected or hashlib.sha256(payload).hexdigest().encode() != expected[1]:
        raise ValueError("Portable payload SHA-256 mismatch.")
    return payload


def _tar_bytes(files):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w", format=tarfile.GNU_FORMAT) as archive:
        for name, data in files.items():
            info = tarfile.TarInfo("./" + name)
            info.mode, info.uid, info.gid = 0o644, 0, 0
            info.uname = info.gname = "root"
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
    return gzip.compress(buffer.getvalue(), mtime=0)


def _ar_member(handle, name, content):
    header = (f"{name + '/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(content):<10}`\n").encode("ascii")
    if len(header) != 60:
        raise ValueError("Invalid ar member header.")
    handle.write(header)
    handle.write(content)
    if len(content) % 2:
        handle.write(b"\n")


def build(portable, output):
    payload = portable_payload(portable)
    buffer = io.BytesIO()
    names, directories, md5sums = set(), set(), []
    installed_size = 0

    with tarfile.open(fileobj=buffer, mode="w:gz", format=tarfile.GNU_FORMAT) as target:
        def add(info, data=None):
            nonlocal installed_size
            path = info.name.removeprefix("./")
            if path in names:
                if info.isdir() and path in directories:
                    return
                raise ValueError(f"Duplicate install path: {path}")
            parent = str(PurePosixPath(path).parent)
            if parent in names and parent not in directories:
                raise ValueError(f"Install parent is not a directory: {parent}")
            if parent != "." and parent not in names:
                directory = tarfile.TarInfo("./" + parent)
                directory.type, directory.mode = tarfile.DIRTYPE, 0o755
                add(directory)
            info.uid = info.gid = 0
            info.uname = info.gname = "root"
            info.mtime = 0
            info.pax_headers = {}
            if info.isfile():
                if data is None:
                    raise ValueError(f"Missing file contents: {path}")
                info.size = len(data)
                md5sums.append(f"{hashlib.md5(data).hexdigest()}  {path}\n")
                installed_size += math.ceil(len(data) / 1024)
            elif info.issym():
                installed_size += math.ceil(len(info.linkname.encode()) / 1024)
            else:
                installed_size += 1
            target.addfile(info, io.BytesIO(data) if data is not None else None)
            names.add(path)
            if info.isdir():
                directories.add(path)

        with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as source:
            for original in source:
                name = PurePosixPath(original.name)
                if name.is_absolute() or ".." in name.parts or not name.parts or name.parts[0] not in {"python", "app"}:
                    raise ValueError(f"Unexpected portable path: {original.name}")
                if not (original.isfile() or original.isdir() or original.issym() or original.islnk()):
                    raise ValueError(f"Unexpected archive type: {original.name}")
                info = copy.copy(original)
                info.name = f"./{APP_ROOT}/{original.name}"
                if original.issym() or original.islnk():
                    link = PurePosixPath(original.linkname)
                    resolved = posixpath.normpath(str(name.parent / link) if original.issym() else str(link))
                    if link.is_absolute() or not resolved.startswith("python/"):
                        raise ValueError(f"Unexpected runtime link: {original.linkname}")
                    if original.islnk():
                        info.linkname = f"./{APP_ROOT}/{original.linkname}"
                data = source.extractfile(original).read() if original.isfile() else None
                add(info, data)
            with zipfile.ZipFile(io.BytesIO(source.extractfile("app/DeviceScope.pyz").read())) as app:
                icon = app.read("devicescope/icon.png")
            if icon[:8] != b"\x89PNG\r\n\x1a\n":
                raise ValueError("Missing app icon.")
        copyright_text = ("DeviceScope application: Twoball.\n"
                          "DeviceScope application code: MIT license. See /usr/share/doc/devicescope/LICENSE.\n"
                          "Bundled Python and native libraries retain their upstream licenses.\n"
                          "Notices: /opt/devicescope/app/third-party-licenses/ and\n"
                          "/opt/devicescope/python/lib/python3.13/LICENSE.txt\n").encode()
        for path, data, mode in [
            ("usr/bin/devicescope", LAUNCHER, 0o755),
            ("usr/share/applications/devicescope.desktop", DESKTOP, 0o644),
            ("usr/share/pixmaps/devicescope.png", icon, 0o644),
            ("usr/share/doc/devicescope/copyright", copyright_text, 0o644),
            ("usr/share/doc/devicescope/LICENSE", Path(__file__).parent.parent.joinpath("LICENSE").read_bytes(), 0o644),
            ("usr/share/doc/devicescope/README.md", Path(__file__).with_name("DEBIAN.md").read_bytes(), 0o644),
        ]:
            info = tarfile.TarInfo("./" + path)
            info.mode = mode
            add(info, data)

    control = f'''Package: devicescope
Version: {VERSION}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: DeviceScope local build <noreply@example.invalid>
Installed-Size: {installed_size}
Depends: libc6 (>= 2.17)
Suggests: smartmontools, kmod, pciutils, usbutils, xwayland, fonts-dejavu-core, dmidecode, x11-xserver-utils
Description: Native hardware and system diagnostics desktop utility
 View real Linux devices, driver bindings, process memory, supported
 performance counters and disk health in one window. Python and Tcl/Tk
 are bundled. The desktop needs an X11 or XWayland session and fonts.
'''.encode("utf-8")
    control_archive = _tar_bytes({"control": control, "md5sums": "".join(md5sums).encode()})
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as handle:
        handle.write(b"!<arch>\n")
        for name, data in [("debian-binary", b"2.0\n"), ("control.tar.gz", control_archive), ("data.tar.gz", buffer.getvalue())]:
            _ar_member(handle, name, data)
    checksum = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(".deb.sha256").write_text(f"{checksum}  {output.name}\n", encoding="ascii")
    print(f"Built {output} ({output.stat().st_size:,} bytes)\nSHA-256: {checksum}")
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("portable", type=Path, help="The reviewed .run package")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    build(args.portable, args.output or args.portable.with_name(f"devicescope_{VERSION}_amd64.deb"))
