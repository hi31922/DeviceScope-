"""Selected-device kernel driver evidence and bounded local event inspection."""
from __future__ import annotations

import json
import re
from pathlib import PurePosixPath


def _module_metadata(text):
    values = {}
    for line in text.splitlines():
        key, separator, value = line.partition(":")
        if separator:
            values.setdefault(key.strip(), []).append(value.strip())
    return values


def _installed_modules(system):
    release = system.read("/proc/sys/kernel/osrelease", limit=256).strip()
    if not re.fullmatch(r"[A-Za-z0-9_.+~:-]+", release or "?"):
        return None
    text = system.read(f"/lib/modules/{release}/modules.dep", limit=8 * 1024 * 1024)
    if not text:
        text = system.read(f"/usr/lib/modules/{release}/modules.dep", limit=8 * 1024 * 1024)
    if not text:
        return None
    result = set()
    for line in text.splitlines():
        path = line.partition(":")[0]
        basename = PurePosixPath(path).name
        result.add(basename.split(".ko", 1)[0].replace("-", "_"))
    return result


def _firmware_evidence(system, names):
    evidence = []
    for name in names[:40]:
        if not name or name.startswith("/") or ".." in PurePosixPath(name).parts:
            evidence.append(name + ": unsupported declaration; not resolved")
            continue
        if any(mark in name for mark in "*?["):
            evidence.append(name + ": declared pattern; availability not inferred")
            continue
        found = next((path for root in ("/lib/firmware", "/usr/lib/firmware")
                      for suffix in ("", ".xz", ".zst")
                      if system.path(path := root + "/" + name + suffix).is_file()), None)
        evidence.append(name + (": present at " + found if found else ": not found in standard paths (may be optional or device-specific)"))
    if len(names) > 40:
        evidence.append(f"{len(names) - 40} more declarations omitted from this bounded inspection.")
    return "\n".join(evidence) or "No firmware declarations were returned."


def _event_match(device, entry):
    """Match exact kernel device keys first; never match friendly product names."""
    key = entry.get("_KERNEL_DEVICE", "")
    if not isinstance(key, str):
        key = ""
    name = PurePosixPath(device.get("sysfs_path", "")).name
    bus = device.get("bus", "")
    if key and (key == "+" + bus + ":" + name or key == device.get("details", {}).get("Kernel event device")):
        return True
    message = entry.get("MESSAGE", "")
    if not isinstance(message, str):
        return False
    for token in device.get("event_tokens", []):
        # Broad generic tokens can attach unrelated kernel messages to a device.
        if not isinstance(token, str) or not token or len(token) < 3:
            continue
        # Kernel prefixes conventionally append ': ' after an exact identity.
        # A colon followed by another identity character is still part of a name.
        if re.search(r"(?<![A-Za-z0-9_.:\-])" + re.escape(token)
                     + r"(?=$|[^A-Za-z0-9_.:\-]|:(?:\s|$))", message):
            return True
    return False


def _events(system, device):
    notes = ["Events cover at most the newest 1,500 kernel entries from this boot; up to 100 exact-identity matches are shown. No matches do not prove an error never occurred."]
    result = system.run(["journalctl", "--dmesg", "--boot", "--no-pager", "--output=json", "-n", "1500"], timeout=6)
    matched = []
    if result.ok:
        for line in result.stdout.splitlines():
            try:
                entry = json.loads(line)
            except (ValueError, TypeError):
                continue
            if isinstance(entry, dict) and _event_match(device, entry):
                message = entry.get("MESSAGE", "")
                if not isinstance(message, str):
                    message = "[Non-text kernel message; omitted]"
                priority = str(entry.get("PRIORITY", "?"))
                matched.append("[priority " + priority + "] " + message)
        if result.stderr.strip():
            notes.append(result.stderr.strip())
    else:
        notes.append("Journal access unavailable: " + (result.stderr.strip() or "command failed"))
        fallback = system.run(["dmesg", "--color=never"], timeout=4)
        if fallback.ok:
            for line in fallback.stdout.splitlines()[-1500:]:
                if _event_match(device, {"MESSAGE": line}):
                    matched.append(line)
            notes.append("Fallback source: the current kernel ring buffer; entries may have been overwritten.")
        else:
            notes.append("Kernel ring buffer unavailable: " + (fallback.stderr.strip() or "command failed"))
    return "\n".join(matched[-100:]) or "No matching entries were readable in this bounded scan.", notes


def diagnose(system, device):
    """Inspect a selected inventory row; does not install/load/reset anything."""
    details = dict(device.get("details", {}))
    notes = ["Linux does not use Windows Device Manager problem codes. 'No bound driver' is evidence about binding, not proof of a missing or broken package."]
    module = device.get("driver_module", "")
    driver = device.get("driver", "Not exposed")
    if module and re.fullmatch(r"[A-Za-z0-9_.-]+", module) and not module.startswith("-"):
        info = system.run(["modinfo", "--", module], timeout=4)
        if info.ok:
            metadata = _module_metadata(info.stdout)
            for key in ("filename", "version", "description", "license", "signer", "vermagic"):
                if key in metadata:
                    details["Module " + key] = "\n".join(metadata[key])
            dependency_names = [item.strip() for text in metadata.get("depends", []) for item in text.split(",") if item.strip()]
            installed = _installed_modules(system)
            dependencies = []
            for name in dependency_names[:64]:
                normalized = name.replace("-", "_")
                if not re.fullmatch(r"[A-Za-z0-9_.]+", normalized):
                    dependencies.append(name + ": unsupported module name")
                elif system.path("/sys/module/" + normalized).is_dir():
                    dependencies.append(name + ": loaded (or built into running kernel)")
                elif installed is not None and normalized in installed:
                    dependencies.append(name + ": present in modules.dep; not observed loaded")
                elif installed is not None:
                    dependencies.append(name + ": not found in readable modules.dep (may be built in)")
                else:
                    dependencies.append(name + ": load/install state unavailable")
            details["Declared module dependencies"] = "\n".join(dependencies) or "No depends entries returned (this is not a full runtime dependency analysis)."
            details["Declared firmware files"] = _firmware_evidence(system, metadata.get("firmware", []))
            notes.append("Firmware declarations apply to a module's supported devices. An absent declared file alone does not prove this device needs it.")
        else:
            details["Module inspection"] = info.stderr.strip() or "modinfo could not read metadata."
    else:
        details["Module inspection"] = "No safe module identity was exposed. Driver code may be built into the kernel; no module name is guessed from a product name."
    events, event_notes = _events(system, device)
    notes.extend(event_notes)
    summary = ("Kernel policy reports this device as not authorized." if device.get("needs_attention")
               else "Bound driver: " + driver + "." if driver != "Not exposed"
               else "No bound driver was reported; inspect its role, module alias and kernel events before concluding a driver is missing.")
    return {"summary": summary, "details": details, "events": events, "notes": notes}
