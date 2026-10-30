# Third-party runtime notices

DeviceScope Linux includes the official Astral **Python Build Standalone** runtime:

- Release: `20261026`
- Python: `3.13.18`, baseline `x86_64-unknown-linux-gnu`, install-only stripped distribution
- Archive: `cpython-3.13.18+20261026-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz`
- SHA-256: `df15fe60d3ff55bf9dcf25824b5572756bb8816ff93865a273d3301d7066a8b7`
- Upstream: https://github.com/astral-sh/python-build-standalone
- Release: https://github.com/astral-sh/python-build-standalone/releases/tag/20261026

The bundled upstream installation is kept intact except for Python bytecode caches. Its Python license is in `python/lib/python3.13/LICENSE.txt` after extraction. Tcl/Tk and the other compiled runtime components have their own notices; those are included under `app/third-party-licenses/` in the release payload. Python's distribution license incorporates additional component notices. Bundled pip's own vendored license files are retained too. DeviceScope does not invoke pip at runtime.

The Python Build Standalone project distributes components under their respective licenses; its own build-system license does not replace those component licenses. See https://docs.astral.sh/python-build-standalone/running/#licensing .

The computer/magnifier image was provided by the project owner and is copied unchanged.

No Blueman, BlueZ, NVIDIA driver, smartmontools, kernel module or sensor driver is bundled. When present, optional system utilities are called read-only using argument lists, never a shell command string.


DeviceScope application code is MIT-licensed. Its notice is included as app/LICENSE in the portable payload. This does not replace any dependency license.
