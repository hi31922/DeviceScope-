"""Versioned, bounded JSON device snapshots; never include process lists."""
from __future__ import annotations

import copy
import datetime as dt
import json
import math
import os
import tempfile
from pathlib import Path

MAX_BYTES = 16 * 1024 * 1024
MAX_DEVICES = 20000
_FIELDS = ("id", "name", "category", "device_type", "driver", "status",
           "needs_attention", "hardware_ids", "details", "evidence")
_TEXT_FIELDS = ("id", "name", "category", "device_type", "status", "evidence")


def _json_value(value, depth=0):
    if depth > 12:
        raise ValueError("Snapshot data is nested too deeply.")
    if value is None or type(value) in (bool, int):
        return
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("Snapshot numbers must be finite.")
        return
    if isinstance(value, str):
        if len(value) > 1024 * 1024:
            raise ValueError("A snapshot text field exceeds its size limit.")
        try:
            value.encode("utf-8")
        except UnicodeEncodeError as exc:
            raise ValueError("Snapshot text contains invalid Unicode.") from exc
        return
    if isinstance(value, list):
        if len(value) > 20000:
            raise ValueError("A snapshot list exceeds its size limit.")
        for item in value:
            _json_value(item, depth + 1)
        return
    if isinstance(value, dict):
        if len(value) > 2000 or any(not isinstance(key, str) for key in value):
            raise ValueError("Invalid snapshot object keys.")
        for key, item in value.items():
            _json_value(key, depth + 1)
            _json_value(item, depth + 1)
        return
    raise ValueError("Snapshot fields must contain JSON values.")


def _validate_rows(devices):
    if not isinstance(devices, list) or len(devices) > MAX_DEVICES:
        raise ValueError("Invalid or oversized device list.")
    result, seen = [], set()
    for device in devices:
        if not isinstance(device, dict):
            raise ValueError("Each snapshot device must be an object.")
        # Comparison-only metadata is deliberately not persisted.
        row = {key: device[key] for key in _FIELDS if key in device}
        if any(not isinstance(row.get(key), str) for key in _TEXT_FIELDS):
            raise ValueError("A device has missing or invalid text fields.")
        identity = row["id"]
        if not identity.strip() or len(identity) > 8192 or identity in seen:
            raise ValueError("Device IDs must be nonempty and unique.")
        seen.add(identity)
        if row.get("driver") is not None and not isinstance(row["driver"], str):
            raise ValueError("Invalid device driver field.")
        if type(row.get("needs_attention")) is not bool:
            raise ValueError("Invalid device attention flag.")
        if not isinstance(row.get("details"), dict):
            raise ValueError("Invalid device details.")
        ids = row.get("hardware_ids")
        if not isinstance(ids, list) or len(ids) > 256 or any(not isinstance(i, str) for i in ids):
            raise ValueError("Invalid hardware ID list.")
        _json_value(row)
        result.append(copy.deepcopy(row))
    return result


def save_snapshot(path, devices: list[dict]) -> None:
    rows = _validate_rows(devices)
    document = {"format": "devicescope-linux", "schema_version": 1, "platform": "linux",
                "created_at": dt.datetime.now(dt.timezone.utc).isoformat(), "devices": rows}
    data = json.dumps(document, ensure_ascii=False, indent=2, allow_nan=False).encode("utf-8")
    if len(data) > MAX_BYTES:
        raise ValueError("Snapshot exceeds the 16 MiB limit.")
    target = Path(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", dir=target.parent, prefix=".devicescope-",
                                         suffix=".tmp", delete=False) as handle:
            temporary = Path(handle.name)
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, target)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Snapshot contains duplicate object keys.")
        result[key] = value
    return result


def _reject_constant(value):
    raise ValueError(f"Invalid snapshot numeric constant: {value}.")


def load_snapshot(path) -> list[dict]:
    with Path(path).open("rb") as handle:
        data = handle.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("Snapshot exceeds the 16 MiB limit.")
    try:
        document = json.loads(data.decode("utf-8-sig"), object_pairs_hook=_unique_pairs,
                              parse_constant=_reject_constant)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exc:
        raise ValueError(f"Invalid snapshot JSON: {exc}") from exc
    if not isinstance(document, dict) or document.get("format") != "devicescope-linux":
        raise ValueError("This is not a DeviceScope Linux snapshot.")
    if type(document.get("schema_version")) is not int or document["schema_version"] != 1:
        raise ValueError("Unsupported snapshot schema version.")
    if document.get("platform") != "linux":
        raise ValueError("This snapshot is not from Linux.")
    try:
        timestamp = dt.datetime.fromisoformat(document.get("created_at", ""))
        if timestamp.tzinfo is None:
            raise ValueError("Timestamp needs a timezone.")
    except (TypeError, ValueError):
        raise ValueError("Snapshot creation time is invalid.") from None
    return _validate_rows(document.get("devices"))


def _comparison_row(row):
    # Only stable inventory fields are compared. Dynamic link speed/state and sensor
    # detail values remain visible in snapshots without turning every scan into a change.
    stable = {key: row.get(key) for key in
              ("name", "category", "device_type", "driver", "needs_attention", "hardware_ids")}
    details = row.get("details", {})
    if "Interface" not in details and "network" not in row.get("category", "").lower():
        stable["status"] = row.get("status")
    stable["details"] = {key: value for key, value in details.items() if key not in
                         {"Operational state", "Speed (Mbit/s)", "Link speed", "Classification basis"}}
    return stable


def compare_devices(current: list[dict], previous: list[dict]) -> list[dict]:
    now, before = _validate_rows(current), _validate_rows(previous)
    past = {row["id"]: row for row in before}
    result = []
    for item in now:
        row = copy.deepcopy(item)
        old = past.pop(row["id"], None)
        if old is None:
            row["change"] = "Added"
        elif _comparison_row(row) != _comparison_row(old):
            row["change"] = "Changed"
            row["previous"] = old
        else:
            row["change"] = "Unchanged"
        result.append(row)
    for item in past.values():
        row = copy.deepcopy(item)
        row["change"] = "Removed"
        result.append(row)
    return result
