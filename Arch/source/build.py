#!/usr/bin/env python3
"""Build the single Linux file on Windows or Linux using only Python stdlib.

The pinned upstream Linux runtime is SHA-256 verified; it is never executed by
the cross-packager. No pip installation, shell eval, root or FUSE is needed.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent
VERSION = "0.8.0"
MARKER = "__DEVICESCOPE_PAYLOAD_BELOW__"

HEADER = '''#!/bin/sh
# DeviceScope Linux @VERSION@. The gzip archive after the marker contains
# the app, Python and Tcl/Tk. No system packages or settings are changed.
set -eu
umask 077
if [ "$(uname -s)" != Linux ]; then
    echo "This DeviceScope package runs on Linux." >&2; exit 1
fi
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) echo "This package needs an x86-64 Intel/AMD CPU. ARM needs a separate runtime build." >&2; exit 1 ;;
esac
for tool in tail tar sha256sum mktemp; do
    command -v "$tool" >/dev/null 2>&1 || { echo "Missing system utility: $tool" >&2; exit 1; }
done
if [ "${1:-}" = --version ]; then echo "DeviceScope Linux @VERSION@"; exit 0; fi
workdir=$(mktemp -d "${TMPDIR:-/tmp}/devicescope.XXXXXXXX")
cleanup() { rm -rf -- "$workdir"; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' HUP TERM
tail -n +@LINE@ "$0" > "$workdir/payload.tar.gz"
actual=$(sha256sum "$workdir/payload.tar.gz")
actual=${actual%% *}
if [ "$actual" != "@SHA256@" ]; then
    echo "DeviceScope package integrity check failed. Copy or download it again." >&2; exit 1
fi
tar -xzf "$workdir/payload.tar.gz" -C "$workdir"
rm -f -- "$workdir/payload.tar.gz"
# Keep Python and Tcl isolated from the caller's Python/Tcl installation.
unset PYTHONHOME PYTHONPATH TCLLIBPATH LD_PRELOAD LD_LIBRARY_PATH
export TCL_LIBRARY="$workdir/python/lib/tcl9.0"
export TK_LIBRARY="$workdir/python/lib/tk9.0"
export PYTHONDONTWRITEBYTECODE=1
set +e
"$workdir/python/bin/python3" -I -B "$workdir/app/DeviceScope.pyz" "$@"
result=$?
set -e
if [ "$result" -eq 126 ]; then
    echo "Cannot execute the bundled runtime. If /tmp is mounted noexec, set TMPDIR to a private executable filesystem." >&2
fi
exit "$result"
__DEVICESCOPE_PAYLOAD_BELOW__
'''


def digest(path):
    with path.open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def app_zip():
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("__main__.py", "from devicescope.__main__ import main\nmain()\n")
        for path in sorted((ROOT / "devicescope").iterdir()):
            if path.suffix in (".py", ".png"):
                archive.writestr("devicescope/" + path.name, path.read_bytes())
        archive.writestr("devicescope_tests/__init__.py", "")
        for path in sorted((ROOT / "tests").glob("test_*.py")):
            archive.writestr("devicescope_tests/" + path.name, path.read_bytes())
    return stream.getvalue()


def add_bytes(archive, name, data, mode=0o644):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    archive.addfile(info, io.BytesIO(data))


def build(output, allow_download=True):
    manifest = json.loads((ROOT / "packaging/runtime.json").read_text("utf-8"))
    runtime = ROOT / "packaging/runtime.tar.gz"
    if not runtime.exists():
        if not allow_download:
            raise RuntimeError("Pinned runtime archive missing; run build.py once with network access.")
        print(f"Downloading pinned upstream runtime: {manifest['name']}")
        urllib.request.urlretrieve(manifest["url"], runtime)
    if digest(runtime) != manifest["sha256"]:
        raise RuntimeError("Upstream Python archive SHA-256 mismatch. Refusing to package it.")
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="devicescope-build-") as temporary:
        payload = Path(temporary) / "payload.tar.gz"
        with tarfile.open(payload, "w:gz", compresslevel=6) as target, tarfile.open(runtime, "r:gz") as source:
            names = set()
            for member in source:
                if not member.name.startswith("python/") or ".." in Path(member.name).parts:
                    raise RuntimeError("Unexpected path in upstream runtime.")
                # Keep upstream shared libraries, Tcl resources and licenses intact.
                # Omitting only bytecode makes the distribution smaller and ABI-neutral.
                if member.name.endswith((".pyc", ".pyo")) or "/__pycache__/" in member.name:
                    continue
                member.uid = member.gid = 0
                member.uname = member.gname = ""
                target.addfile(member, source.extractfile(member) if member.isfile() else None)
                names.add(member.name)
            for required in ("python/bin/python3", "python/lib/tcl9.0/init.tcl", "python/lib/tk9.0/tk.tcl",
                             "python/lib/libtcl9.0.so", "python/lib/libtcl9tk9.0.so"):
                if required not in names:
                    raise RuntimeError(f"Pinned runtime is missing {required}")
            add_bytes(target, "app/DeviceScope.pyz", app_zip())
            for filename in ("README.md", "THIRD_PARTY.md"):
                path = ROOT / filename
                if path.exists():
                    add_bytes(target, "app/" + filename, path.read_bytes())
            for path in sorted((ROOT / "packaging/third-party-licenses").glob("LICENSE*")):
                add_bytes(target, "app/third-party-licenses/" + path.name, path.read_bytes())
        header = HEADER.replace("@VERSION@", VERSION).replace("@SHA256@", digest(payload))
        # The marker line itself is not included in the gzip stream.
        header = header.replace("@LINE@", str(header.count("\n") + 1))
        with output.open("wb") as handle, payload.open("rb") as archive:
            handle.write(header.encode("utf-8"))
            import shutil
            shutil.copyfileobj(archive, handle)
    output.chmod(0o755)
    checksum = digest(output)
    output.with_suffix(output.suffix + ".sha256").write_text(f"{checksum}  {output.name}\n", encoding="ascii")
    print(f"Built {output} ({output.stat().st_size:,} bytes)\nSHA-256: {checksum}")
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / f"dist/DeviceScope-{VERSION}-linux-x86_64.run")
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    build(args.output, not args.offline)
