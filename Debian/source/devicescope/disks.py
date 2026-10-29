"""Read-only Linux disk inventory, SMART evidence and mounted filesystem space."""
from __future__ import annotations

import json
import math
import os
import re
import time

from .system import System


def _read(path, limit=4096):
    try:
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            return handle.read(limit).strip()
    except OSError:
        return ""


def _int(value):
    try:
        return int(value.strip()) if isinstance(value, str) else int(value)
    except (ValueError, TypeError, OverflowError):
        return None


def _finite_number(value):
    if isinstance(value, bool):
        return None
    try:
        number = float(value)
        return number if math.isfinite(number) else None
    except (ValueError, TypeError, OverflowError):
        return None


def _decode_mount(value):
    return re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), value)


def parse_mountinfo(text):
    """Decode mountinfo's escaped fields; kernel mount IDs are not disk numbers."""
    mounts = []
    for line in text.splitlines():
        left, separator, right = line.partition(" - ")
        if not separator:
            continue
        a, b = left.split(), right.split()
        if len(a) < 6 or len(b) < 3 or not re.fullmatch(r"\d+:\d+", a[2]):
            continue
        mount = _decode_mount(a[4])
        if not mount.startswith("/"):
            continue
        mounts.append({"mount": mount, "filesystem": b[0], "source": _decode_mount(b[1]),
                       "device": a[2], "root": _decode_mount(a[3]), "options": a[5]})
    return mounts


_PSEUDO = {"proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "securityfs", "cgroup", "cgroup2",
           "pstore", "debugfs", "tracefs", "configfs", "mqueue", "hugetlbfs", "fusectl", "rpc_pipefs",
           "binfmt_misc", "autofs", "efivarfs", "nsfs", "ramfs"}
_REMOTE = {"nfs", "nfs4", "cifs", "smb3", "smbfs", "afs", "9p", "ceph", "glusterfs", "lustre", "sshfs"}


def read_volumes(system):
    volumes, notes, seen = [], [], set()
    for mount in parse_mountinfo(system.read("/proc/self/mountinfo", limit=2**20)):
        filesystem = mount["filesystem"]
        if filesystem in _PSEUDO:
            continue
        if filesystem in _REMOTE or filesystem.startswith("fuse."):
            notes.append(f"Space skipped for {mount['mount']}: remote/userspace filesystem {filesystem}; this scan measures local data filesystems.")
            continue
        # Bind mounts and btrfs subvolumes share a filesystem space pool. Do not
        # count its same capacity repeatedly under different paths.
        key = (mount["device"], filesystem)
        if key in seen:
            continue
        try:
            usage = os.statvfs(system.path(mount["mount"]))
            unit = usage.f_frsize or usage.f_bsize
            total = unit * usage.f_blocks
            free = unit * usage.f_bfree
            available = unit * usage.f_bavail
            if total <= 0 or free < 0 or free > total:
                continue
            used = total - free
            # Match df's usage denominator: privileged reserved blocks are not
            # available to the user. Keep actual total/free values separately.
            denominator = used + max(0, available)
            volumes.append({**mount, "total_bytes": total, "used_bytes": used,
                            "free_bytes": free, "available_bytes": max(0, available),
                            "percent": used / denominator * 100 if denominator else 0})
            seen.add(key)
        except (OSError, AttributeError) as exc:
            notes.append(f"Space unavailable for {mount['mount']}: {exc}")
    if not volumes:
        notes.append("No readable mounted data filesystems were found.")
    return volumes, notes


def parse_smart_json(text, exit_code):
    """Nonzero health bits remain valid evidence; command failure bits do not."""
    details, temperature = {}, None
    try:
        data = json.loads(text)
        if not isinstance(data, dict):
            raise ValueError("expected JSON object")
    except (ValueError, TypeError):
        return {"health": "Unavailable", "temperature_c": None, "details": {},
                "smart_text": text[:262144] or "No SMART JSON returned.",
                "note": "SMART output was unavailable or invalid JSON."}
    smartctl = data.get("smartctl")
    smartctl = smartctl if isinstance(smartctl, dict) else {}
    messages = smartctl.get("messages")
    if isinstance(messages, list):
        details["SMART messages"] = "\n".join(str(row.get("string", "")) for row in messages if isinstance(row, dict))
    code = _int(exit_code)
    details["SMART exit status"] = code if code is not None else "Unavailable"
    temperature_data = data.get("temperature")
    if isinstance(temperature_data, dict):
        temperature = _finite_number(temperature_data.get("current"))
        if temperature is not None and not -100 <= temperature <= 250:
            temperature = None
    hours = data.get("power_on_time")
    if isinstance(hours, dict) and isinstance(hours.get("hours"), (int, float)):
        details["Power-on hours"] = hours["hours"]
    for source, label in (("model_name", "SMART model"), ("serial_number", "Serial number"),
                          ("firmware_version", "Firmware version"), ("rotation_rate", "Rotation rate")):
        if source in data:
            details[label] = data[source]

    nvme = data.get("nvme_smart_health_information_log")
    nvme = nvme if isinstance(nvme, dict) else {}
    for source, label in (("percentage_used", "NVMe endurance estimate used (%)"),
                          ("available_spare", "NVMe available spare (%)"),
                          ("available_spare_threshold", "NVMe spare threshold (%)"),
                          ("critical_warning", "NVMe critical-warning bitmask"),
                          ("media_errors", "NVMe media/data integrity errors"),
                          ("num_err_log_entries", "NVMe error-log entries"),
                          ("unsafe_shutdowns", "NVMe unsafe shutdowns")):
        if source in nvme:
            details[label] = nvme[source]
    if "percentage_used" in nvme:
        details["Endurance interpretation"] = "Manufacturer life estimate consumed, not a universal health percentage; it may exceed 100%."
    status = data.get("smart_status")
    status = status if isinstance(status, dict) else {}
    passed = status.get("passed")
    error = code is None or code < 0 or bool(code & 0x07)
    # Bit 2 can coexist with a successfully read, failing SMART status: failing
    # log/attribute subcommands must not hide a direct failure from the disk.
    if passed is False or (code is not None and code >= 0 and code & 0x08):
        health = "SMART reports failure"
        note = "The device reported a failing SMART status."
    elif (_int(nvme.get("critical_warning")) or 0) != 0 or (code is not None and code >= 0 and code & 0x10):
        health = "SMART warning"
        note = "A current SMART attribute threshold or NVMe critical warning was reported."
    elif error:
        health = "Unavailable (SMART command incomplete)"
        note = "SMART could not be read completely; permission, standby, unsupported transport or command errors are not a health result."
    elif passed is True:
        health = "SMART self-assessment passed"
        note = "A passed self-assessment is not a guarantee against failure."
    else:
        health = "SMART status unavailable"
        note = "No explicit SMART self-assessment result was reported."
    if error:
        details["SMART collection"] = "Incomplete; command/transport errors are recorded in the exit status."
        if health in ("SMART reports failure", "SMART warning"):
            note += " Other SMART commands also failed; the scan is incomplete."
    historical = []
    for mask, text in ((0x20, "past attribute threshold failure"), (0x40, "device error-log records"),
                       (0x80, "self-test-log error records")):
        if code is not None and code >= 0 and code & mask:
            historical.append(text)
    if historical:
        details["SMART recorded history"] = "; ".join(historical)
        note += " Recorded history: " + "; ".join(historical) + "."
    # Read-only raw evidence includes ATA normalized/raw attributes and NVMe
    # counters without pretending that vendor-defined bytes have one meaning.
    return {"health": health, "temperature_c": temperature, "details": details,
            "smart_text": json.dumps(data, indent=2, ensure_ascii=False)[:262144], "note": note}


def scan_disks(system: System):
    disks, notes = [], []
    try:
        paths = sorted(system.path("/sys/block").iterdir())[:128]
    except OSError:
        paths = []
        notes.append("Block-device inventory is unavailable in /sys/block.")
    started = time.monotonic()
    smart_available = True
    for path in paths:
        name = path.name
        if not re.fullmatch(r"[A-Za-z0-9_.!+-]+", name):
            continue
        if re.fullmatch(r"(?:loop|ram|zram|fd)\d+", name):
            continue
        sectors = _int(_read(path / "size"))
        size = sectors * 512 if sectors is not None and sectors >= 0 else None
        model = _read(path / "device/model") or _read(path / "device/name") or "Unavailable"
        device_type = "Rotational" if _read(path / "queue/rotational") == "1" else "Non-rotational / unspecified"
        try:
            canonical = path.resolve(strict=True).as_posix()
        except (OSError, RuntimeError):
            canonical = ""
        virtual = bool(re.fullmatch(r"(?:dm-\d+|md\d+)", name)) or "/devices/virtual/block/" in canonical
        details = {"Device node": f"/dev/{name}", "Storage type": device_type,
                   "Removable": {"0": "No", "1": "Yes"}.get(_read(path / "removable"), "Unavailable"),
                   "Vendor": _read(path / "device/vendor") or "Unavailable",
                   "Logical block size": _int(_read(path / "queue/logical_block_size")),
                   "Physical block size": _int(_read(path / "queue/physical_block_size"))}
        row = {"id": f"block:{name}", "name": name, "model": model, "size_bytes": size,
               "health": "Unavailable", "temperature_c": None, "details": details,
               "smart_text": "No SMART data collected."}
        if virtual:
            row["health"] = "Virtual / aggregate block device"
            row["smart_text"] = "SMART is not assigned to a guessed underlying physical disk."
        elif not smart_available:
            row["smart_text"] = "smartctl is not installed; inventory and volume space remain available."
        elif time.monotonic() - started > 6:
            row["smart_text"] = "SMART collection time budget reached. Other disks' health is not inferred."
            notes.append(f"SMART skipped for {name}: scan time budget reached.")
        else:
            result = system.run(["smartctl", "--all", "--json", "--nocheck=standby,3", f"/dev/{name}"], timeout=1.5)
            if result.code is None:
                row["smart_text"] = result.stderr.strip() or "SMART command unavailable."
                notes.append(f"{name}: {row['smart_text']}")
                if "not installed" in result.stderr or not system.commands:
                    smart_available = False
            else:
                parsed = parse_smart_json(result.stdout, result.code)
                row.update({key: parsed[key] for key in ("health", "temperature_c", "smart_text")})
                row["details"].update(parsed["details"])
                row["details"]["SMART interpretation"] = parsed["note"]
                if result.stderr.strip():
                    row["details"]["SMART diagnostic stderr"] = result.stderr.strip()
        disks.append(row)
    volumes, volume_notes = read_volumes(system)
    notes.extend(volume_notes)
    notes.append("SMART is read only, without elevation; standby checks are requested. Transport autodetection can still wake some bridges. USB/RAID/NVMe permissions and support vary; unavailable never means healthy.")
    notes.append("Filesystem space is shown per mounted filesystem, deduplicating bind mounts/subvolumes; free includes reserved blocks, available excludes them. No guessed physical-disk mapping is made.")
    if not disks:
        notes.append("No supported block devices were found.")
    return {"disks": disks, "volumes": volumes, "notes": notes}
