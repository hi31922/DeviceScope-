"""Read-only firmware and display specifications from Linux-exposed evidence."""
from __future__ import annotations

import re


UNAVAILABLE = "Unavailable"
EFI_GLOBAL_GUID = "8be4df61-93ca-11d2-aa0d-00e098032b8c"
_PLACEHOLDERS = {"", "none", "unknown", "not specified", "not applicable", "default string",
                 "to be filled by o.e.m.", "to be filled by oem", "system manufacturer",
                 "system product name"}


def _text(value):
    value = str(value or "").strip()
    return UNAVAILABLE if value.casefold() in _PLACEHOLDERS else value


def _row(label, value):
    return {"label": label, "value": str(value)}


def _entries(path, pattern):
    try:
        return sorted(path.glob(pattern))
    except OSError:
        return []


def _binary(path, limit):
    try:
        with path.open("rb") as handle:
            return handle.read(limit)
    except (OSError, ValueError):
        return None


def firmware_section(system):
    rows = [_row("Manufacturer", _text(system.read("/sys/class/dmi/id/bios_vendor"))),
            _row("Version", _text(system.read("/sys/class/dmi/id/bios_version"))),
            _row("Release date", _text(system.read("/sys/class/dmi/id/bios_date")))]
    notes = []
    kernel = (system.read("/proc/sys/kernel/osrelease") + system.read("/proc/version")).casefold()
    wsl = "microsoft" in kernel or "wsl" in kernel
    try:
        efi = system.path("/sys/firmware/efi").is_dir()
    except OSError:
        efi = False
    if efi:
        mode = "UEFI (kernel EFI interface)"
        secure_path = system.path(f"/sys/firmware/efi/efivars/SecureBoot-{EFI_GLOBAL_GUID}")
        value = _binary(secure_path, 6)
        # efivarfs prefixes the one-byte UEFI value with four attribute bytes.
        if value is not None and len(value) == 5 and value[4] in (0, 1):
            secure = "Enabled" if value[4] else "Disabled"
        else:
            secure = "Unavailable (SecureBoot UEFI variable unreadable or not exposed)"
            notes.append("Secure Boot is not inferred from permissions or a missing EFI variable.")
    else:
        mode = "Unavailable (firmware interface not exposed)"
        secure = "Unavailable (no readable UEFI SecureBoot interface)"
        notes.append("Absence of the Linux EFI interface does not establish legacy BIOS mode or disabled Secure Boot.")
    if wsl:
        notes.append("WSL exposes its virtual environment; these firmware fields do not establish the Windows host's firmware or Secure Boot state.")
    rows.extend([_row("Boot firmware mode", mode), _row("Secure Boot", secure)])
    return {"title": "Firmware", "rows": rows}, notes


def parse_edid(data):
    """Use only a checksum-valid EDID base block; do not invent mode timings."""
    if data is None or len(data) < 128 or data[:8] != b"\x00\xff\xff\xff\xff\xff\xff\x00" or sum(data[:128]) % 256:
        return {"manufacturer": UNAVAILABLE, "model": UNAVAILABLE}
    code = int.from_bytes(data[8:10], "big")
    letters = [(code >> shift) & 31 for shift in (10, 5, 0)]
    maker = "".join(chr(value + 64) for value in letters) if not code & 0x8000 and all(1 <= value <= 26 for value in letters) else UNAVAILABLE
    name = UNAVAILABLE
    for offset in (54, 72, 90, 108):
        descriptor = data[offset:offset + 18]
        if descriptor[:5] == b"\x00\x00\x00\xfc\x00":
            raw = descriptor[5:].split(b"\n", 1)[0].split(b"\x00", 1)[0]
            if all(32 <= value <= 126 for value in raw):
                name = _text(raw.decode("ascii"))
                break
    return {"manufacturer": maker, "model": name}


def parse_xrandr(text):
    """Read output state and advertised modes; '*' is the current refresh rate."""
    outputs = []
    current = None
    for line in text.splitlines():
        head = re.match(r"^(\S+)\s+(connected|disconnected)(?:\s|$)", line)
        if head:
            current = None
            if head[2] == "connected" and len(outputs) < 128:
                geometry = re.search(r"(?:^|\s)(\d+)x(\d+)[+-]\d+[+-]\d+(?:\s|$)", line)
                current = {"name": head[1], "virtual": head[1].upper().startswith("XWAYLAND"),
                           "resolution": f"{geometry[1]}x{geometry[2]}" if geometry else None,
                           "refresh_hz": None, "modes": []}
                outputs.append(current)
            continue
        if current is None:
            continue
        mode = re.match(r"^\s+(\d+x\d+i?)(?:_[^\s]+)?\s+(.+)$", line)
        if not mode:
            continue
        rates = []
        for token in mode[2].split():
            rate = re.fullmatch(r"(\d+(?:\.\d+)?)([+*]*)", token)
            if not rate or not 0 < float(rate[1]) <= 10000:
                continue
            number = f"{float(rate[1]):g}"
            if number not in rates:
                rates.append(number)
            if "*" in rate[2]:
                # A rotated output's geometry can differ from its mode dimensions.
                # The server's current marker still identifies the refresh rate.
                if current["resolution"] is None:
                    current["resolution"] = mode[1]
                current["refresh_hz"] = number
        description = mode[1] + (" @ " + ", ".join(rates) + " Hz" if rates else " (refresh unavailable)")
        if description not in current["modes"] and len(current["modes"]) < 256:
            current["modes"].append(description)
    return outputs


def display_section(system):
    rows, notes, connectors = [], [], []
    for path in _entries(system.path("/sys/class/drm"), "card*-*")[:128]:
        if not re.fullmatch(r"card\d+-.+", path.name):
            continue
        status = system.read(f"/sys/class/drm/{path.name}/status", limit=128).strip()
        if status != "connected":
            continue
        modes = []
        for mode in system.read(f"/sys/class/drm/{path.name}/modes", limit=65536).splitlines():
            mode = mode.strip()
            if re.fullmatch(r"\d+x\d+(?:i)?", mode) and mode not in modes and len(modes) < 256:
                modes.append(mode)
        edid = parse_edid(_binary(path / "edid", 4096))
        connectors.append({"name": path.name, "output_name": path.name.split("-", 1)[1],
                           "modes": modes, **edid})
    result = system.run(["xrandr", "--query"], timeout=3)
    outputs = parse_xrandr(result.stdout) if result.ok else []
    if not result.ok:
        notes.append("Display current modes: " + (result.stderr.strip() or "xrandr did not return a successful query."))
    rows.append(_row("Connected kernel DRM connectors", str(len(connectors)) if connectors else UNAVAILABLE))
    for index, connector in enumerate(connectors, 1):
        prefix = f"Connector {index}"
        rows.extend([_row(prefix, connector["name"]),
                     _row(prefix + " model", connector["model"]),
                     _row(prefix + " manufacturer ID (EDID)", connector["manufacturer"]),
                     _row(prefix + " supported resolutions (kernel)", "; ".join(connector["modes"]) or UNAVAILABLE)])
        # Names differ across display stacks. Merge only an exact unique name;
        # geometry or resolution similarity is not monitor identity evidence.
        matches = [entry for entry in outputs if not entry["virtual"] and entry["name"] == connector["output_name"]]
        duplicates = sum(item["output_name"] == connector["output_name"] for item in connectors)
        match = matches[0] if len(matches) == 1 and duplicates == 1 else {}
        rows.extend([_row(prefix + " current resolution", match.get("resolution") or UNAVAILABLE),
                     _row(prefix + " current refresh rate", (match["refresh_hz"] + " Hz") if match.get("refresh_hz") else UNAVAILABLE)])
    for index, output in enumerate(outputs, 1):
        prefix = f"Desktop output {index}"
        kind = "XWayland virtual output" if output["virtual"] else "X11 reported output"
        rows.extend([_row(prefix, f"{output['name']} ({kind})"),
                     _row(prefix + " current resolution", output["resolution"] or UNAVAILABLE),
                     _row(prefix + " current refresh rate", (output["refresh_hz"] + " Hz") if output["refresh_hz"] else UNAVAILABLE),
                     _row(prefix + " reported supported modes", "; ".join(output["modes"]) or UNAVAILABLE)])
    if any(output["virtual"] for output in outputs):
        notes.append("XWayland virtual outputs describe the compatibility desktop, not the physical monitor's complete capabilities or native refresh rate.")
    notes.append("Kernel connectors and desktop outputs are separate evidence sources and may describe the same monitor. Only exact unique connector names are matched. Supported modes are those reported by the active driver/display stack, not a promise of every panel capability.")
    if not connectors and not outputs:
        rows.extend([_row("Current resolution", UNAVAILABLE), _row("Current refresh rate", UNAVAILABLE),
                     _row("Reported supported modes", UNAVAILABLE)])
    return {"title": "Display", "rows": rows}, notes


def platform_sections(system):
    firmware, firmware_notes = firmware_section(system)
    displays, display_notes = display_section(system)
    return {"sections": [firmware, displays], "notes": firmware_notes + display_notes}
