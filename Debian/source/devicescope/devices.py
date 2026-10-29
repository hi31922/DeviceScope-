"""Read Linux device identity and topology without changing any device settings."""
from __future__ import annotations

import re
from pathlib import Path


def _children(system, path, limit=8192):
    try:
        return sorted(system.path(path).iterdir(), key=lambda item: item.name)[:limit]
    except OSError:
        return []


def _read(system, path, limit=65536):
    return system.read(path, limit=limit).strip()


def _logical(system, path):
    """Keep fixture host paths out of identities and exported reports."""
    try:
        return "/" + Path(path).resolve().relative_to(system.root.resolve()).as_posix()
    except (OSError, RuntimeError, ValueError):
        return str(path).replace("\\", "/")


def _canonical(system, logical):
    return _logical(system, system.path(logical))


def _uevent(system, logical):
    return dict(line.split("=", 1) for line in _read(system, logical + "/uevent").splitlines()
                if "=" in line)


def _link_name(system, logical):
    target = system.path(logical)
    try:
        if target.is_symlink():
            return target.resolve(strict=True).name
    except (OSError, RuntimeError):
        pass
    return ""


def _driver(system, logical):
    uevent = _uevent(system, logical)
    name = _link_name(system, logical + "/driver") or uevent.get("DRIVER", "")
    module = _link_name(system, logical + "/driver/module")
    return name, module


def _topology(system, logical, bus, endpoint=False):
    canonical = _canonical(system, logical)
    device = _canonical(system, logical + "/device")
    combined = canonical + "/" + device
    if endpoint:
        return "Audio endpoint", "Kernel ALSA PCM endpoint; this row describes an audio endpoint, not a driver package."
    if ("/devices/virtual/" in combined or bus in {"virtio", "vmbus", "xen"}
            or re.search(r"/(?:virtio\d+|vmbus|xen)(?:/|$)", combined)):
        return "Virtual device", "The kernel sysfs topology identifies a virtual device or virtual bus."
    if bus in {"pci", "usb"} or re.search(r"/(?:pci[0-9a-f]{4}:|usb\d+/)", combined):
        return "Physical hardware", "Kernel-reported PCI/USB transport. This does not distinguish built-in from external hardware or exclude VM-emulated hardware."
    if bus in {"acpi", "platform"}:
        return "System component", f"Kernel {bus.upper()} system/platform enumeration; the role does not prove a separate external device."
    return "Unknown", "The exposed topology does not establish physical, virtual or built-in placement."


def _ids_database(system, kind):
    vendors, products = {}, {}
    paths = (f"/usr/share/hwdata/{kind}.ids", f"/usr/share/misc/{kind}.ids",
             f"/var/lib/{'pciutils' if kind == 'pci' else 'usbutils'}/{kind}.ids")
    text = next((value for path in paths if (value := system.read(path, limit=4 * 1024 * 1024))), "")
    current = None
    for line in text.splitlines():
        if re.match(r"^[0-9A-Fa-f]{4}\s+\S", line):
            current, name = line.split(None, 1)
            current = current.lower()
            vendors[current] = name.strip()
        elif current and re.match(r"^\t[0-9A-Fa-f]{4}\s+\S", line):
            product, name = line.strip().split(None, 1)
            products[(current, product.lower())] = name.strip()
        elif line and not line.startswith(("#", "\t")):
            current = None
    return vendors, products


def _hex(value, width=4):
    text = value.lower().removeprefix("0x")
    return text.zfill(width) if re.fullmatch(r"[0-9a-f]+", text or "?") else ""


def _row(system, logical, bus, name, category, ids=(), endpoint=False):
    driver, module = _driver(system, logical)
    device_type, evidence = _topology(system, logical, bus, endpoint)
    details = {"Sysfs path": logical, "Canonical sysfs path": _canonical(system, logical),
               "Bus / class": bus, "Classification basis": evidence,
               "Bound driver": driver or "No bound driver reported",
               "Driver module": module or "Not exposed (driver may be built into the kernel)"}
    modalias = _read(system, logical + "/modalias")
    if modalias:
        details["Module alias"] = modalias
    status = "Driver bound" if driver else "No bound driver reported"
    result = {"id": bus + ":" + Path(logical).name, "name": name, "category": category,
              "device_type": device_type, "driver": driver or "Not exposed", "status": status,
              "needs_attention": False, "hardware_ids": [item for item in ids if item],
              "details": details, "evidence": evidence, "sysfs_path": logical,
              "bus": bus, "driver_module": module,
              "event_tokens": [Path(logical).name]}
    return result


def _scan_pci(system):
    vendors, products = _ids_database(system, "pci")
    classes = {"01": "Storage controllers", "02": "Network adapters", "03": "Display adapters",
               "04": "Multimedia", "05": "Memory controllers", "06": "System bridges",
               "07": "Communications", "08": "System peripherals", "09": "Input controllers",
               "0b": "Processors", "0c": "Serial bus controllers", "0d": "Wireless controllers"}
    result = []
    for item in _children(system, "/sys/bus/pci/devices"):
        logical = "/sys/bus/pci/devices/" + item.name
        vendor, product = (_hex(_read(system, logical + "/" + key)) for key in ("vendor", "device"))
        cls = _hex(_read(system, logical + "/class"), 6)
        product_name = products.get((vendor, product))
        name = product_name or (f"{vendors.get(vendor, 'PCI')} device {vendor or 'unknown'}:{product or 'unknown'}")
        ids = [f"PCI\\VEN_{vendor.upper()}&DEV_{product.upper()}"] if vendor and product else []
        subvendor, subproduct = (_hex(_read(system, logical + "/" + key))
                                 for key in ("subsystem_vendor", "subsystem_device"))
        if ids and subvendor and subproduct:
            ids.append(ids[0] + f"&SUBSYS_{subproduct.upper()}{subvendor.upper()}")
        row = _row(system, logical, "pci", name, classes.get(cls[:2], "PCI devices"), ids)
        row["details"].update({"PCI address": item.name, "Vendor ID": vendor or "Unavailable",
                               "Device ID": product or "Unavailable", "PCI class": cls or "Unavailable",
                               "Manufacturer": vendors.get(vendor, "Not in local ID database"),
                               "Hardware ID format": "DeviceScope formats the numeric sysfs IDs using familiar PCI notation; Windows IDs are not queried."})
        result.append(row)
    return result


def _scan_usb(system):
    vendors, products = _ids_database(system, "usb")
    result = []
    for item in _children(system, "/sys/bus/usb/devices"):
        logical = "/sys/bus/usb/devices/" + item.name
        interface = ":" in item.name
        parent = logical.split(":", 1)[0] if interface else logical
        vendor, product = (_hex(_read(system, parent + "/" + key)) for key in ("idVendor", "idProduct"))
        label = _read(system, parent + "/product") or products.get((vendor, product)) or "USB device"
        if interface:
            label = (_read(system, logical + "/interface") or label) + " [interface " + item.name + "]"
        ids = [f"USB\\VID_{vendor.upper()}&PID_{product.upper()}"] if vendor and product else []
        row = _row(system, logical, "usb", label, "USB interfaces" if interface else "USB devices", ids)
        authorized = _read(system, logical + "/authorized")
        if authorized == "0":
            row.update(status="Not authorized by kernel policy", needs_attention=True)
        row["details"].update({"Manufacturer": _read(system, parent + "/manufacturer") or vendors.get(vendor, "Unavailable"),
                               "USB topology address": item.name,
                               "Vendor ID": vendor or "Unavailable", "Product ID": product or "Unavailable",
                               "Authorization": {"1": "Authorized", "0": "Not authorized"}.get(authorized, "Not exposed"),
                               "Serial": _read(system, parent + "/serial") or "Not exposed",
                               "Speed (Mbit/s)": _read(system, parent + "/speed") or "Not exposed",
                               "Hardware ID format": "DeviceScope formats numeric sysfs USB IDs using familiar USB notation."})
        # The parent USB device's lack of an interface driver is normal for composite devices.
        if not interface and not row["driver_module"]:
            row["details"]["Driver note"] = "USB functional drivers normally bind to interface rows; a composite parent without a functional driver is not itself an error."
        result.append(row)
    return result


def _driver_from_parent(system, row):
    """Class objects often expose their actual driver on a parent device."""
    if row["driver"] != "Not exposed":
        return
    logical = row["sysfs_path"] + "/device"
    driver, module = _driver(system, logical)
    if not driver:
        return
    row["driver"], row["driver_module"], row["status"] = driver, module, "Driver bound"
    row["details"].update({"Bound driver": driver,
                           "Driver module": module or "Not exposed (may be built into kernel)",
                           "Driver sysfs path": logical})


def _scan_classes(system):
    result = []
    for item in _children(system, "/sys/class/net"):
        logical = "/sys/class/net/" + item.name
        row = _row(system, logical, "net", item.name, "Network interfaces")
        _driver_from_parent(system, row)
        state = _read(system, logical + "/operstate")
        if state:
            row["status"] = "Link " + state
        row["details"].update({"Interface": item.name, "MAC address": _read(system, logical + "/address") or "Unavailable",
                               "Operational state": state or "Unavailable",
                               "Speed (Mbit/s)": _read(system, logical + "/speed") or "Unavailable",
                               "MTU": _read(system, logical + "/mtu") or "Unavailable",
                               "Driver note": "A down or unplugged link is not proof of a missing driver."})
        ifindex = _read(system, logical + "/ifindex")
        if ifindex.isdigit():
            row["details"]["Kernel event device"] = "n" + ifindex
        result.append(row)
    for item in _children(system, "/sys/class/bluetooth"):
        if not re.fullmatch(r"hci\d+", item.name):
            continue
        logical = "/sys/class/bluetooth/" + item.name
        row = _row(system, logical, "bluetooth", "Bluetooth adapter " + item.name, "Bluetooth")
        _driver_from_parent(system, row)
        row["details"].update({"Bluetooth stack": "BlueZ userspace manages the Linux Bluetooth stack; Blueman is a desktop manager for BlueZ.",
                               "Address": _read(system, logical + "/address") or "Not exposed",
                               "Adapter name": _read(system, logical + "/name") or "Not exposed"})
        result.append(row)
    for item in _children(system, "/sys/class/block"):
        logical = "/sys/class/block/" + item.name
        if system.path(logical + "/partition").exists():
            continue
        model = _read(system, logical + "/device/model")
        row = _row(system, logical, "block", model or item.name, "Block devices")
        _driver_from_parent(system, row)
        row["details"].update({"Block device": "/dev/" + item.name,
                               "Model": model or "Not exposed",
                               "Vendor": _read(system, logical + "/device/vendor") or "Not exposed",
                               "Device numbers": _read(system, logical + "/dev") or "Unavailable"})
        result.append(row)
    for item in _children(system, "/sys/class/input"):
        if not re.fullmatch(r"input\d+", item.name):
            continue
        logical = "/sys/class/input/" + item.name
        ids = []
        vendor, product = (_hex(_read(system, logical + "/id/" + key)) for key in ("vendor", "product"))
        if vendor and product:
            ids.append("LINUX_INPUT:vendor=" + vendor + ",product=" + product)
        row = _row(system, logical, "input", _read(system, logical + "/name") or item.name, "Input devices", ids)
        _driver_from_parent(system, row)
        row["details"].update({"Physical location": _read(system, logical + "/phys") or "Not exposed",
                               "Input bus type": _read(system, logical + "/id/bustype") or "Not exposed"})
        result.append(row)
    for item in _children(system, "/sys/class/sound"):
        endpoint = bool(re.fullmatch(r"pcmC\d+D\d+[pc]", item.name))
        if not endpoint and not re.fullmatch(r"card\d+", item.name):
            continue
        logical = "/sys/class/sound/" + item.name
        name = _read(system, logical + "/id") or item.name
        row = _row(system, logical, "sound", name, "Audio endpoints" if endpoint else "Sound cards", endpoint=endpoint)
        _driver_from_parent(system, row)
        if endpoint:
            row["details"]["Direction"] = "Playback" if item.name.endswith("p") else "Capture"
        result.append(row)
    return result


def _scan_system_buses(system):
    result = []
    for bus in ("acpi", "platform", "virtio", "vmbus", "xen"):
        for item in _children(system, f"/sys/bus/{bus}/devices"):
            logical = f"/sys/bus/{bus}/devices/" + item.name
            name = _read(system, logical + "/description") or _read(system, logical + "/name") or item.name
            ids = []
            if hid := _read(system, logical + "/hid"):
                ids.append("ACPI:" + hid)
            row = _row(system, logical, bus, name, "System components" if bus in {"acpi", "platform"} else "Virtual devices", ids)
            if acpi_status := _read(system, logical + "/status"):
                row["details"]["ACPI status bitmask"] = acpi_status
            result.append(row)
    return result


def _bluetooth_info(system, devices):
    process_names = set()
    for item in _children(system, "/proc", 32768):
        if item.name.isdigit():
            name = _read(system, "/proc/" + item.name + "/comm", 256)
            if name in {"bluetoothd", "blueman-applet", "blueman-manager", "blueman-mechani"}:
                process_names.add(name)
    service = system.run(["systemctl", "show", "bluetooth.service", "--no-pager",
                          "--property=LoadState,ActiveState,SubState"], timeout=3)
    service_values = dict(line.split("=", 1) for line in service.stdout.splitlines() if "=" in line) if service.ok else {}
    bus = system.run(["busctl", "--system", "--timeout=2", "call", "org.freedesktop.DBus",
                      "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", "org.bluez"], timeout=3)
    dbus_owner = {"b true": True, "b false": False}.get(bus.stdout.strip()) if bus.ok else None
    front_installed = any(system.path(path).is_file() for path in (
        "/usr/share/applications/blueman-manager.desktop", "/usr/bin/blueman-manager"))
    running = "bluetoothd" in process_names or dbus_owner is True
    front_running = any(name.startswith("blueman-") for name in process_names)
    return {"stack": "BlueZ (Linux Bluetooth userspace)",
            "stack_running": "Observed" if running else "Not observed (visibility may be restricted)",
            "dbus_owner": "Yes" if dbus_owner is True else "No" if dbus_owner is False else "Unavailable",
            "service": ", ".join(f"{key}={value}" for key, value in service_values.items()) or service.stderr.strip() or "Unavailable",
            "frontend": ("Blueman running" if front_running else "Blueman installed" if front_installed else "Blueman not detected; another desktop manager may be in use"),
            "adapters": sum(row["bus"] == "bluetooth" for row in devices),
            "note": "Blueman controls the BlueZ API; it does not replace BlueZ. No Bluetooth discovery, pairing or service changes are performed."}


def scan_devices(system):
    """Inventory kernel-reported objects. No driver installation or root access."""
    notes = ["Linux sysfs does not expose Windows Device Manager codes such as Code 28. A missing driver link alone is not treated as a driver failure.",
             "Hardware IDs come from kernel numeric IDs; product names use local pci.ids/usb.ids databases when available. No web lookup is performed."]
    if not system.path("/sys/bus").is_dir():
        notes.append("Linux sysfs is unavailable or inaccessible. Run this edition on Linux with /sys and /proc mounted.")
    devices = _scan_pci(system) + _scan_usb(system) + _scan_classes(system) + _scan_system_buses(system)
    devices.sort(key=lambda row: (row["category"], row["name"].casefold(), row["id"]))
    return {"devices": devices, "notes": notes, "bluetooth": _bluetooth_info(system, devices)}
