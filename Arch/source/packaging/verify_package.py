"""Check the final shell/payload boundary, hashes and packaged Python sources."""
import hashlib
import io
from pathlib import Path, PurePosixPath
import re
import posixpath
import sys
import tarfile
import zipfile


def verify(path):
    path = Path(path)
    data = path.read_bytes()
    marker = b"__DEVICESCOPE_PAYLOAD_BELOW__\n"
    offset = data.index(marker) + len(marker)
    header, payload = data[:offset], data[offset:]
    if not header.startswith(b"#!/bin/sh\n") or not payload.startswith(b"\x1f\x8b"):
        raise ValueError("Missing shell header or gzip boundary.")
    line = int(re.search(rb"tail -n \+(\d+)", header)[1])
    if line != header.count(b"\n") + 1:
        raise ValueError("Payload line offset is incorrect.")
    expected = re.search(rb'if \[ "\$actual" != "([a-f0-9]{64})"', header)[1].decode()
    if hashlib.sha256(payload).hexdigest() != expected:
        raise ValueError("Payload SHA-256 mismatch.")
    sidecar = path.with_suffix(path.suffix + ".sha256").read_text("ascii").split()[0]
    if hashlib.sha256(data).hexdigest() != sidecar:
        raise ValueError("Release sidecar SHA-256 mismatch.")
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        members = archive.getmembers()
        names = [m.name for m in members]
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive path.")
        for member in members:
            name = PurePosixPath(member.name)
            if name.is_absolute() or ".." in name.parts or name.parts[0] not in {"app", "python"}:
                raise ValueError(f"Unsafe payload path: {member.name}")
            if member.issym() or member.islnk():
                target = PurePosixPath(member.linkname)
                resolved = posixpath.normpath(str(name.parent / target) if member.issym() else str(target))
                if target.is_absolute() or not resolved.startswith("python/"):
                    raise ValueError(f"Unexpected link target: {member.linkname}")
        required = ["python/bin/python3", "python/lib/tcl9.0/init.tcl", "python/lib/tk9.0/tk.tcl",
                    "python/lib/python3.13/LICENSE.txt", "app/DeviceScope.pyz", "app/THIRD_PARTY.md",
                    "app/third-party-licenses/LICENSE.libX11.txt", "app/third-party-licenses/LICENSE.libxcb.txt"]
        for name in required:
            if name not in names:
                raise ValueError(f"Required payload file is missing: {name}")
        python_binary = archive.getmember("python/bin/python3.13")
        if not python_binary.mode & 0o111:
            raise ValueError("Python ELF executable permission missing.")
        binary = archive.extractfile(python_binary).read()
        if binary[:5] != b"\x7fELF\x02" or binary[18:20] != b"\x3e\x00":
            raise ValueError("Wrong runtime architecture.")
        app = archive.extractfile("app/DeviceScope.pyz").read()
    with zipfile.ZipFile(io.BytesIO(app)) as source:
        modules = [name for name in source.namelist() if name.endswith(".py")]
        for name in modules:
            compile(source.read(name), name, "exec")
        if source.read("devicescope/icon.png")[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError("Missing original PNG window icon.")
        tests = sum(name.startswith("devicescope_tests/test_") for name in modules)
    # A separate file is useful for running --version in the build host's Python.
    path.with_name(".verification-app.pyz").write_bytes(app)
    path.with_name(".verification-header.sh").write_bytes(header)
    print(f"Verified {path.name}: {len(members)} archive entries, {len(modules)} Python sources, {tests} test modules.")


if __name__ == "__main__":
    verify(sys.argv[1])
