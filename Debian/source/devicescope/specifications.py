"""Read-only Linux system specifications; unavailable firmware is never guessed."""
from __future__ import annotations

import re
import shlex
from pathlib import Path

from .devices import _ids_database
from .system import human_bytes

UNAVAILABLE = "Unavailable"
_PLACEHOLDERS = {"", "unknown", "none", "not specified", "not available", "n/a",
                 "not applicable", "not present", "unspecified",
                 "to be filled by o.e.m.", "to be filled by oem", "default string",
                 "system manufacturer", "system product name", "base board manufacturer",
                 "base board product name", "o.e.m.", "oem"}


def clean(value):
    value = str(value or "").strip()
    return value if value.casefold() not in _PLACEHOLDERS else UNAVAILABLE


def _read(system, path, limit=65536):
    return system.read(path, limit=limit).strip()


def _entries(system, path):
    try:
        return sorted(system.path(path).iterdir(), key=lambda item: item.name)[:8192]
    except OSError:
        return []


def _section(title, rows):
    return {"title": title, "rows": [{"label": label, "value": str(value)} for label, value in rows]}


def parse_os_release(text):
    """Read os-release assignments as text, without evaluating shell expressions."""
    result = {}
    for line in text.splitlines():
        key, sep, value = line.partition("=")
        if not sep or not re.fullmatch(r"[A-Z_][A-Z_0-9]*", key):
            continue
        try:
            words = shlex.split(value, comments=False, posix=True)
        except ValueError:
            continue
        if len(words) == 1:
            result[key] = words[0]
    return result


def _os_section(system):
    values = parse_os_release(_read(system, "/etc/os-release") or
                              _read(system, "/usr/lib/os-release"))
    return _section("Operating System", [
        ("Name", clean(values.get("PRETTY_NAME") or values.get("NAME"))),
        ("Edition / variant", clean(values.get("VARIANT") or values.get("VARIANT_ID"))),
        ("Version", clean(values.get("VERSION") or values.get("VERSION_ID"))),
        ("Kernel release", clean(_read(system, "/proc/sys/kernel/osrelease"))),
    ])


def _processor_section(system):
    text = _read(system, "/proc/cpuinfo", limit=2**20)
    models, vendors, processors = [], [], set()
    for line in text.splitlines():
        key, sep, value = line.partition(":")
        if not sep:
            continue
        key, value = key.strip(), value.strip()
        if key in {"model name", "cpu model", "Processor"} and clean(value) != UNAVAILABLE:
            if value not in models:
                models.append(value)
        elif key == "vendor_id" and value and value not in vendors:
            vendors.append(value)
        elif key == "processor" and value.isdigit():
            processors.add(value)
    rows = [(f"Full model {index}", model) for index, model in enumerate(models, 1)]
    if not rows:
        rows.append(("Full model", UNAVAILABLE))
    rows.extend([("Reported vendor", ", ".join(vendors) or UNAVAILABLE),
                 ("Logical processors", str(len(processors)) if processors else UNAVAILABLE)])
    return _section("Processor", rows)


def _pci_id(text):
    match = re.fullmatch(r"(?:0x)?([0-9a-f]{4})", text.strip(), re.I)
    return match[1].lower() if match else ""


def _gpus_section(system, notes):
    vendors, models = _ids_database(system, "pci")
    # These names describe the actual registered numeric PCI vendors, not a guess
    # from marketing/model strings. Full model names still require the ID database.
    known_vendors = {"10de": "NVIDIA", "1002": "AMD", "8086": "Intel"}
    gpus, identities = [], set()

    def record(identity, logical, driver=""):
        if identity in identities:
            return
        identities.add(identity)
        vendor = _pci_id(_read(system, logical + "/vendor"))
        product = _pci_id(_read(system, logical + "/device"))
        gpus.append({"identity": identity,
                     "manufacturer": clean(vendors.get(vendor) or known_vendors.get(vendor)),
                     "model": clean(models.get((vendor, product))),
                     "ids": f"{vendor}:{product}" if vendor and product else UNAVAILABLE,
                     "driver": clean(driver)})

    for entry in _entries(system, "/sys/bus/pci/devices"):
        logical = "/sys/bus/pci/devices/" + entry.name
        try:
            display = int(_read(system, logical + "/class"), 16) >> 16 == 3
        except ValueError:
            display = False
        if not display:
            continue
        uevent = dict(line.split("=", 1) for line in _read(system, logical + "/uevent").splitlines() if "=" in line)
        record(entry.name, logical, uevent.get("DRIVER", ""))

    for card in _entries(system, "/sys/class/drm"):
        if not re.fullmatch(r"card\d+", card.name):
            continue
        logical = "/sys/class/drm/" + card.name + "/device"
        uevent = dict(line.split("=", 1) for line in _read(system, logical + "/uevent").splitlines() if "=" in line)
        identity = uevent.get("PCI_SLOT_NAME", "")
        if not identity:
            try:
                resolved = system.path(logical).resolve(strict=True)
                identity = resolved.name if re.fullmatch(r"[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]", resolved.name) else "drm:" + str(resolved)
            except (OSError, RuntimeError):
                identity = "drm:" + card.name
        record(identity, logical, uevent.get("DRIVER", ""))

    rows = [("Detected graphics adapters", str(len(gpus)) if gpus else UNAVAILABLE)]
    for index, gpu in enumerate(gpus, 1):
        rows.extend([(f"GPU {index} — manufacturer", gpu["manufacturer"]),
                     (f"GPU {index} — full model", gpu["model"]),
                     (f"GPU {index} — kernel identity", gpu["identity"]),
                     (f"GPU {index} — PCI vendor:device", gpu["ids"]),
                     (f"GPU {index} — bound driver", gpu["driver"])])
    if not gpus:
        notes.append("No readable PCI display-class or DRM adapters were exposed by this Linux environment.")
    if any(gpu["model"] == UNAVAILABLE for gpu in gpus):
        notes.append("Full GPU model names require a matching local pci.ids database entry; numeric IDs remain visible when readable.")
    return _section("Graphics", rows)


def _memory_size(value):
    if value.strip().casefold() in {"no module installed", "not installed"}:
        return 0
    match = re.fullmatch(r"(\d+)\s+(KB|MB|GB|TB)", value.strip(), re.I)
    if not match:
        return None
    amount = int(match[1])
    return amount * 1024 ** {"KB": 1, "MB": 2, "GB": 3, "TB": 4}[match[2].upper()] if amount > 0 else None


def _memory_speed(value):
    match = re.fullmatch(r"(\d+)\s+(MT/s|MHz)", value.strip(), re.I)
    if not match or int(match[1]) <= 0:
        return UNAVAILABLE
    return f"{int(match[1])} " + ("MT/s" if match[2].casefold() == "mt/s" else "MHz")


def parse_memory_devices(text):
    """Parse decoded SMBIOS type 17 records, retaining partial inventory evidence."""
    records, current = [], None
    for line in text.splitlines():
        if line.strip() == "Memory Device":
            if current is not None:
                records.append(current)
            current = {}
        elif line.startswith("Handle "):
            if current is not None:
                records.append(current)
            current = None
        elif current is not None and ":" in line and line[:1].isspace():
            key, value = line.strip().split(":", 1)
            current[key] = value.strip()
    if current is not None:
        records.append(current)
    modules, complete = [], bool(records)
    for record in records:
        capacity = _memory_size(record.get("Size", ""))
        if capacity == 0:
            continue
        if capacity is None:
            complete = False
        modules.append({"capacity": capacity,
                        "locator": clean(record.get("Locator")),
                        "type": clean(record.get("Type")),
                        "configured_speed": _memory_speed(record.get("Configured Memory Speed") or record.get("Configured Clock Speed", "")),
                        "rated_speed": _memory_speed(record.get("Speed", "")),
                        "manufacturer": clean(record.get("Manufacturer")),
                        "part_number": clean(record.get("Part Number"))})
    total = sum(module["capacity"] for module in modules) if complete and modules else None
    return {"modules": modules, "installed_bytes": total}


def _memory_section(system, notes):
    meminfo = _read(system, "/proc/meminfo")
    usable = re.search(r"^MemTotal:\s+(\d+)\s+kB\s*$", meminfo, re.M)
    result = system.run(["dmidecode", "--type", "17"], timeout=3)
    memory = parse_memory_devices(result.stdout) if result.ok else {"modules": [], "installed_bytes": None}
    modules = memory["modules"]
    types = sorted({module["type"] for module in modules if module["type"] != UNAVAILABLE})
    rows = [("Total installed RAM (firmware)", human_bytes(memory["installed_bytes"])),
            ("OS-usable physical RAM", human_bytes(int(usable[1]) * 1024) if usable else UNAVAILABLE),
            ("DDR / memory type", ", ".join(types) or UNAVAILABLE)]
    for index, module in enumerate(modules, 1):
        prefix = f"Module {index}"
        rows.extend([(prefix + " — slot", module["locator"]),
                     (prefix + " — capacity", human_bytes(module["capacity"])),
                     (prefix + " — type", module["type"]),
                     (prefix + " — configured speed", module["configured_speed"]),
                     (prefix + " — rated speed", module["rated_speed"]),
                     (prefix + " — manufacturer", module["manufacturer"]),
                     (prefix + " — part number", module["part_number"])])
    if memory["installed_bytes"] is None:
        reason = clean(result.stderr[:1000]) if not result.ok else "Firmware did not report complete installed-module capacities."
        notes.append("Installed RAM/type/speed require readable SMBIOS memory devices via dmidecode; no sudo or capacity estimate is used. " + reason)
    notes.append("OS-usable RAM excludes firmware/kernel reservations and is shown separately from installed RAM. Memory speeds retain the firmware tool's MHz or MT/s unit; configured and rated speeds are separate.")
    return _section("Memory", rows)


def _motherboard_section(system):
    return _section("Motherboard", [("Manufacturer", clean(_read(system, "/sys/class/dmi/id/board_vendor"))),
                                     ("Model", clean(_read(system, "/sys/class/dmi/id/board_name"))),
                                     ("Version", clean(_read(system, "/sys/class/dmi/id/board_version")))])


def _storage_section(system):
    rows = []
    count = 0
    for device in _entries(system, "/sys/block"):
        name = device.name
        if re.fullmatch(r"(?:loop|ram|zram|fd)\d+", name) or not re.fullmatch(r"[A-Za-z0-9_.!+-]+", name):
            continue
        logical = "/sys/block/" + name
        if _read(system, logical + "/partition"):
            continue
        sectors = _read(system, logical + "/size")
        size = int(sectors) * 512 if sectors.isdecimal() else None
        count += 1
        prefix = f"Drive {count}"
        rows.extend([(prefix + " — device", "/dev/" + name),
                     (prefix + " — model", clean(_read(system, logical + "/device/model") or _read(system, logical + "/device/name"))),
                     (prefix + " — manufacturer", clean(_read(system, logical + "/device/vendor"))),
                     (prefix + " — capacity", human_bytes(size))])
    return _section("Storage", rows or [("Detected drives", UNAVAILABLE)])


def _platform_sections(system):
    from .specifications_platform import platform_sections
    return platform_sections(system)


def capture_specifications(system):
    notes = []
    sections = [_os_section(system), _processor_section(system), _gpus_section(system, notes),
                _memory_section(system, notes), _motherboard_section(system), _storage_section(system)]
    platform = _platform_sections(system)
    sections.extend(platform["sections"])
    notes.extend(platform.get("notes", []))
    return {"sections": sections, "notes": notes}
