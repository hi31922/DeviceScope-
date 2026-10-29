"""Linux measurements from kernel counters; missing telemetry stays missing."""
from __future__ import annotations

import csv
import io
import math
import re
import time

from .system import System


def _integer(text):
    try:
        return int(text.strip(), 10)
    except (ValueError, AttributeError):
        return None


def _read_path(path, limit=4096):
    try:
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            return handle.read(limit).strip()
    except OSError:
        return ""


def _entries(path, pattern):
    try:
        return sorted(path.glob(pattern))
    except OSError:
        return []


def _source_id(prefix, path):
    try:
        return f"{prefix}:{path.resolve(strict=True)}"
    except (OSError, RuntimeError):
        return f"{prefix}:{path}"


def parse_cpu(text):
    """Eight non-overlapping aggregate counters, through steal (not guest)."""
    for line in text.splitlines():
        fields = line.split()
        if fields and fields[0] == "cpu":
            try:
                values = tuple(int(field) for field in fields[1:9])
                if len(values) < 4 or any(value < 0 for value in values):
                    return None
                return values + (0,) * (8 - len(values))
            except ValueError:
                return None
    return None


def parse_memory(text):
    fields = {}
    for line in text.splitlines():
        key, sep, value = line.partition(":")
        if sep:
            words = value.split()
            if words:
                number = _integer(words[0])
                if number is not None and number >= 0:
                    fields[key] = number * (1024 if len(words) > 1 and words[1] == "kB" else 1)
    total = fields.get("MemTotal")
    available = fields.get("MemAvailable")
    if not total or available is None or available > total:
        return {"ram_percent": None, "ram_used": None, "ram_total": total or None}
    used = total - available
    return {"ram_percent": used / total * 100, "ram_used": used, "ram_total": total}


def _temperature(text):
    value = _integer(text)
    if value is None:
        return None
    celsius = value / 1000
    # Sensor error sentinels and physically implausible board temperatures are not graphs.
    return celsius if -100 <= celsius <= 250 else None


def read_sensors(system):
    sensors = []
    for chip in _entries(system.path("/sys/class/hwmon"), "hwmon*"):
        name = _read_path(chip / "name") or chip.name
        for source in _entries(chip, "temp[0-9]*_input"):
            stem = source.name.removesuffix("_input")
            # Some type-4 thermistor channels report millivolts. Without the
            # board's conversion, conservatively leave those channels unavailable.
            if _read_path(chip / (stem + "_type")) == "4":
                continue
            if _read_path(chip / (stem + "_enable")) == "0":
                continue
            if _read_path(chip / (stem + "_fault")) == "1":
                continue
            value = _temperature(_read_path(source))
            if value is None:
                continue
            label = _read_path(chip / (stem + "_label")) or stem
            sensors.append({"id": _source_id("hwmon", source), "name": f"{name} / {label}",
                            "celsius": value})
    # Thermal zones retain their kernel-provided identity; they are not assumed CPU cores.
    for zone in _entries(system.path("/sys/class/thermal"), "thermal_zone*"):
        value = _temperature(_read_path(zone / "temp"))
        if value is not None:
            label = _read_path(zone / "type") or zone.name
            sensors.append({"id": _source_id("thermal", zone / "temp"), "name": f"Thermal zone / {label}",
                            "celsius": value})
    return sensors


def parse_nvidia(text):
    rows, sensors = [], []
    try:
        source = csv.reader(io.StringIO(text))
        for fields in source:
            if len(fields) != 7:
                continue
            uuid, name, bus, busy, used, total, temperature = (field.strip() for field in fields)
            if not uuid.startswith("GPU-"):
                continue
            utilization = _integer(busy)
            used_mb, total_mb = _integer(used), _integer(total)
            rows.append({"id": uuid, "name": name, "bus": bus,
                         "utilization": utilization if utilization is not None and 0 <= utilization <= 100 else None,
                         "memory_used": used_mb * 1024**2 if used_mb is not None and used_mb >= 0 else None,
                         "memory_total": total_mb * 1024**2 if total_mb is not None and total_mb >= 0 else None})
            temp = _integer(temperature)
            if temp is not None and -100 <= temp <= 250:
                sensors.append({"id": f"nvidia:{uuid}", "name": f"{name} / GPU temperature", "celsius": float(temp)})
    except csv.Error:
        pass
    return rows, sensors


class PerformanceSampler:
    def __init__(self, system: System):
        self.system = system
        self.previous_cpu = None
        self.nvidia_sample = ([], [], "")
        self.nvidia_at = -math.inf

    def sample(self):
        notes = []
        current = parse_cpu(self.system.read("/proc/stat"))
        cpu = None
        if current is not None and self.previous_cpu is not None:
            deltas = [now - old for now, old in zip(current, self.previous_cpu)]
            # iowait is explicitly allowed to decrease; discard this interval instead of
            # drawing a negative/over-100 sample or hiding it through clamping.
            if all(delta >= 0 for delta in deltas) and sum(deltas) > 0:
                cpu = (sum(deltas) - deltas[3] - deltas[4]) / sum(deltas) * 100
        self.previous_cpu = current
        if current is None:
            notes.append("Aggregate CPU counters are unavailable in /proc/stat.")
        elif cpu is None:
            notes.append("CPU needs two valid counter samples; counter resets leave a graph gap.")
        memory = parse_memory(self.system.read("/proc/meminfo"))
        if memory["ram_percent"] is None:
            notes.append("RAM requires valid MemTotal and MemAvailable counters; no estimate is substituted.")

        gpus, nvidia_cards = [], []
        for card in _entries(self.system.path("/sys/class/drm"), "card[0-9]*"):
            if not re.fullmatch(r"card\d+", card.name):
                continue
            device = card / "device"
            if not device.exists():
                continue
            vendor = _read_path(device / "vendor").lower()
            try:
                driver = (device / "driver").resolve(strict=True).name
            except (OSError, RuntimeError):
                driver = ""
            if vendor == "0x10de" or driver == "nvidia":
                nvidia_cards.append(card.name)
                continue
            usage = _integer(_read_path(device / "gpu_busy_percent"))
            used = _integer(_read_path(device / "mem_info_vram_used"))
            total = _integer(_read_path(device / "mem_info_vram_total"))
            vendor_name = {"0x1002": "AMD", "0x8086": "Intel"}.get(vendor, vendor or "GPU")
            name = f"{card.name} / {vendor_name}" + (f" / {driver}" if driver else "")
            gpus.append({"id": card.name, "name": name,
                         "utilization": usage if usage is not None and 0 <= usage <= 100 else None,
                         "memory_used": used if used is not None and used >= 0 else None,
                         "memory_total": total if total is not None and total >= 0 else None})
            if vendor == "0x8086" and usage is None:
                notes.append(f"{card.name}: Intel GPU load is unavailable from this kernel's sysfs interface. DeviceScope does not estimate i915/xe load from CPU activity or VRAM use.")

        sensors = read_sensors(self.system)
        if nvidia_cards:
            now = time.monotonic()
            if now - self.nvidia_at >= 5:
                result = self.system.run(["nvidia-smi", "--query-gpu=uuid,name,pci.bus_id,utilization.gpu,memory.used,memory.total,temperature.gpu",
                                          "--format=csv,noheader,nounits"], timeout=2)
                rows, gpu_sensors = parse_nvidia(result.stdout) if result.ok else ([], [])
                error = "" if rows else (result.stderr.strip() or "NVIDIA GPU telemetry is unavailable.")
                self.nvidia_sample = (rows, gpu_sensors, error)
                self.nvidia_at = now
            rows, gpu_sensors, error = self.nvidia_sample
            if rows:
                gpus.extend(rows)
                sensors.extend(gpu_sensors)
            else:
                gpus.extend({"id": card, "name": f"{card} / NVIDIA", "utilization": None,
                             "memory_used": None, "memory_total": None} for card in nvidia_cards)
            if error:
                notes.append(error)
            notes.append("NVIDIA telemetry is queried at most once every five seconds; intermediate samples retain the last reading.")
        if not gpus:
            notes.append("No readable DRM GPU was found.")
        if any(row["utilization"] is None for row in gpus):
            notes.append("GPU load is available only from supported kernel gpu_busy_percent or NVIDIA's installed nvidia-smi; unavailable is not 0%.")
        if not sensors:
            notes.append("No readable temperature sensors were found; DeviceScope does not install sensor drivers.")
        else:
            notes.append("Sensor labels come from the kernel, without board-specific calibration. hwmon and thermal-zone entries can describe the same sensor; they are not added or assumed to be CPU cores.")
        return {"cpu_percent": cpu, **memory, "gpus": gpus, "sensors": sensors,
                "notes": notes, "time": time.time()}
