"""Kernel-shaped sysfs/proc fixtures; no real device or service is modified."""
from __future__ import annotations

import json
from pathlib import Path, PurePosixPath
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from devicescope import devices, diagnostics
from devicescope.system import CommandResult


class FixturePath:
    def __init__(self, system, logical):
        self.system, self.logical = system, logical

    def is_file(self):
        return self.logical in self.system.files

    def is_dir(self):
        prefix = self.logical.rstrip("/") + "/"
        return (self.logical in self.system.directories
                or any(path.startswith(prefix) for path in self.system.files)
                or any(path.startswith(prefix) for path in self.system.links))

    def exists(self):
        return self.is_file() or self.is_dir() or self.is_symlink()

    def is_symlink(self):
        return self.logical in self.system.links

    def resolve(self, strict=False):
        return Path(self.system.links.get(self.logical, self.logical))


class FixtureSystem:
    def __init__(self, files=None, links=None, canonical=None, directories=(), replies=None):
        self.files = files or {}
        self.links = links or {}
        self.canonical = canonical or {}
        self.directories = set(directories)
        self.replies = replies or {}
        self.calls = []
        self.root = Path("/")

    def path(self, logical):
        return FixturePath(self, str(logical))

    def read(self, logical, limit=1048576):
        return self.files.get(str(logical), "")[:limit]

    def run(self, args, timeout=5):
        self.calls.append(tuple(args))
        return self.replies.get(tuple(args), CommandResult(None, stderr=args[0] + " is unavailable in fixture"))

    def children(self, system, logical, limit=8192):
        prefix = logical.rstrip("/") + "/"
        names = {path[len(prefix):].split("/")[0]
                 for path in (*self.files, *self.links, *self.directories) if path.startswith(prefix)}
        return [SimpleNamespace(name=name) for name in sorted(names)[:limit] if name]

    def scan(self):
        with patch.object(devices, "_children", self.children), patch.object(
                devices, "_canonical", lambda system, logical: self.canonical.get(logical, logical)):
            return devices.scan_devices(self)


def pci_fixture(driver="example_net"):
    base = "/sys/bus/pci/devices/0000:03:00.0"
    files = {base + "/vendor": "0x10ec\n", base + "/device": "0x8168\n",
             base + "/subsystem_vendor": "0x1043\n", base + "/subsystem_device": "0x8677\n",
             base + "/class": "0x020000\n", base + "/modalias": "pci:v000010ECd00008168sv00001043sd00008677bc02sc00i00\n",
             "/usr/share/misc/pci.ids": "10ec  Realtek Semiconductor\n\t8168  PCI Express Gigabit Ethernet controller\n1043  ASUSTeK Computer\n"}
    if driver:
        files[base + "/uevent"] = "DRIVER=" + driver + "\nPCI_CLASS=20000\n"
    return FixtureSystem(files, links={base + "/driver/module": "/sys/module/r8169"} if driver else {})


class DeviceTests(unittest.TestCase):
    def test_pci_ids_are_real_values_and_subsystem_order_is_correct(self):
        result = pci_fixture().scan()
        row = result["devices"][0]
        self.assertEqual(row["hardware_ids"], ["PCI\\VEN_10EC&DEV_8168", "PCI\\VEN_10EC&DEV_8168&SUBSYS_86771043"])
        self.assertEqual(row["name"], "PCI Express Gigabit Ethernet controller")
        self.assertEqual(row["driver"], "example_net")
        self.assertEqual(row["driver_module"], "r8169")
        self.assertEqual(row["device_type"], "Physical hardware")

    def test_no_driver_is_not_automatically_an_error_or_code_28(self):
        row = pci_fixture(driver="").scan()["devices"][0]
        self.assertFalse(row["needs_attention"])
        self.assertEqual(row["status"], "No bound driver reported")
        self.assertNotIn("28", row["status"])

    def test_ids_database_handles_tabs_and_truncated_lines(self):
        system = pci_fixture()
        system.files["/usr/share/misc/pci.ids"] = "10ec\tRealtek\n\t8168\tExact product\n1234 \n\tabcd \n"
        self.assertEqual(system.scan()["devices"][0]["name"], "Exact product")

    def test_bad_ids_are_not_fabricated(self):
        system = pci_fixture()
        system.files["/sys/bus/pci/devices/0000:03:00.0/vendor"] = "unreadable"
        row = system.scan()["devices"][0]
        self.assertEqual(row["hardware_ids"], [])
        self.assertEqual(row["details"]["Vendor ID"], "Unavailable")

    def test_usb_parent_and_functional_interface_are_distinct(self):
        parent = "/sys/bus/usb/devices/1-3"
        interface = parent + ":1.0"
        system = FixtureSystem({parent + "/idVendor": "0b05", parent + "/idProduct": "18f3",
                                parent + "/product": "Gaming Mouse", parent + "/authorized": "1",
                                interface + "/interface": "HID interface", interface + "/uevent": "DRIVER=usbhid"})
        rows = system.scan()["devices"]
        self.assertEqual(len(rows), 2)
        self.assertTrue(all(row["hardware_ids"] == ["USB\\VID_0B05&PID_18F3"] for row in rows))
        function = next(row for row in rows if row["category"] == "USB interfaces")
        self.assertEqual(function["driver"], "usbhid")
        self.assertIn("interface", function["name"])
        self.assertTrue(all(not row["needs_attention"] for row in rows))

    def test_unauthorized_usb_is_reported_as_policy_not_missing_driver(self):
        base = "/sys/bus/usb/devices/2-2"
        row = FixtureSystem({base + "/idVendor": "1234", base + "/idProduct": "abcd",
                             base + "/authorized": "0"}).scan()["devices"][0]
        self.assertTrue(row["needs_attention"])
        self.assertIn("policy", row["status"])

    def test_virtual_network_uses_kernel_topology_not_adapter_name(self):
        logical = "/sys/class/net/realtek-looking-name"
        system = FixtureSystem({logical + "/address": "02:00:00:00:00:01", logical + "/operstate": "down"},
                               canonical={logical: "/sys/devices/virtual/net/realtek-looking-name"})
        row = system.scan()["devices"][0]
        self.assertEqual(row["device_type"], "Virtual device")
        self.assertFalse(row["needs_attention"])

    def test_virtio_ancestor_overrides_pci_transport(self):
        logical = "/sys/class/net/enp1s0"
        system = FixtureSystem({logical + "/address": "52:54:00:12:34:56"},
                               canonical={logical: "/sys/devices/pci0000:00/0000:00:03.0/virtio0/net/enp1s0"})
        self.assertEqual(system.scan()["devices"][0]["device_type"], "Virtual device")

    def test_physical_network_can_get_bound_driver_from_device_link(self):
        logical = "/sys/class/net/enp3s0"
        system = FixtureSystem({logical + "/operstate": "down", logical + "/device/uevent": "DRIVER=r8169"},
                               canonical={logical: "/sys/devices/pci0000:00/0000:03:00.0/net/enp3s0"})
        row = system.scan()["devices"][0]
        self.assertEqual(row["device_type"], "Physical hardware")
        self.assertEqual(row["driver"], "r8169")
        self.assertFalse(row["needs_attention"])

    def test_sound_pcm_is_an_endpoint(self):
        row = FixtureSystem({"/sys/class/sound/pcmC0D0p/id": "Desktop audio"}).scan()["devices"][0]
        self.assertEqual(row["device_type"], "Audio endpoint")
        self.assertEqual(row["details"]["Direction"], "Playback")

    def test_partition_rows_are_not_misrepresented_as_whole_disks(self):
        system = FixtureSystem({"/sys/class/block/sda/device/model": "Test disk",
                               "/sys/class/block/sda1/partition": "1"})
        rows = system.scan()["devices"]
        self.assertEqual([row["id"] for row in rows], ["block:sda"])

    def test_acpi_system_component_preserves_firmware_hid(self):
        row = FixtureSystem({"/sys/bus/acpi/devices/PNP0C09:00/hid": "PNP0C09",
                             "/sys/bus/acpi/devices/PNP0C09:00/status": "15"}).scan()["devices"][0]
        self.assertEqual(row["device_type"], "System component")
        self.assertEqual(row["hardware_ids"], ["ACPI:PNP0C09"])

    def test_blueman_is_reported_separately_from_bluez(self):
        system = FixtureSystem({"/proc/100/comm": "bluetoothd\n", "/proc/101/comm": "blueman-applet\n",
                                "/sys/class/bluetooth/hci0/name": "test-controller"})
        info = system.scan()["bluetooth"]
        self.assertEqual(info["stack_running"], "Observed")
        self.assertEqual(info["frontend"], "Blueman running")
        self.assertEqual(info["adapters"], 1)
        self.assertIn("does not replace BlueZ", info["note"])
        self.assertFalse(any("start" in args or "scan" in args or "pair" in args for args in system.calls))

    def test_empty_sysfs_does_not_invent_hardware(self):
        result = FixtureSystem().scan()
        self.assertEqual(result["devices"], [])
        self.assertTrue(any("unavailable" in note for note in result["notes"]))


class DiagnosticTests(unittest.TestCase):
    def row(self):
        return {"id": "pci:0000:03:00.0", "sysfs_path": "/sys/bus/pci/devices/0000:03:00.0",
                "bus": "pci", "name": "Device", "driver": "r8169", "driver_module": "r8169",
                "event_tokens": ["0000:03:00.0"], "details": {}}

    def test_event_matching_does_not_match_similar_pci_address_or_product_name(self):
        row = self.row()
        self.assertTrue(diagnostics._event_match(row, {"MESSAGE": "r8169 0000:03:00.0: link down"}))
        self.assertFalse(diagnostics._event_match(row, {"MESSAGE": "r8169 0000:03:00.01: other device"}))
        self.assertFalse(diagnostics._event_match(row, {"MESSAGE": "Device r8169 failed"}))
        self.assertTrue(diagnostics._event_match(row, {"_KERNEL_DEVICE": "+pci:0000:03:00.0"}))

    def test_structured_network_identity_is_exact(self):
        row = self.row()
        row["details"]["Kernel event device"] = "n12"
        self.assertTrue(diagnostics._event_match(row, {"_KERNEL_DEVICE": "n12"}))
        self.assertFalse(diagnostics._event_match(row, {"_KERNEL_DEVICE": "n123"}))

    def test_module_dependencies_and_optional_firmware_evidence(self):
        command = ("modinfo", "--", "r8169")
        system = FixtureSystem({"/proc/sys/kernel/osrelease": "6.8.0-test",
                                "/lib/modules/6.8.0-test/modules.dep": "kernel/drivers/realtek.ko: kernel/lib/libphy.ko\nkernel/lib/libphy.ko:\n",
                                "/lib/firmware/present.bin.xz": "compressed"},
                               directories={"/sys/module/mdio"},
                               replies={command: CommandResult(0, "filename: /lib/modules/6.8.0-test/r8169.ko\nversion: 1.2\ndepends: mdio,libphy\nfirmware: absent.bin\nfirmware: present.bin\n")})
        result = diagnostics.diagnose(system, self.row())
        self.assertIn("mdio: loaded", result["details"]["Declared module dependencies"])
        self.assertIn("libphy: present in modules.dep", result["details"]["Declared module dependencies"])
        firmware = result["details"]["Declared firmware files"]
        self.assertIn("may be optional", firmware)
        self.assertIn("present.bin: present at /lib/firmware/present.bin.xz", firmware)

    def test_declared_firmware_traversal_is_not_resolved(self):
        result = diagnostics._firmware_evidence(FixtureSystem(), ["../../secret", "/etc/shadow", "rtl/*.bin"])
        self.assertIn("unsupported declaration", result)
        self.assertIn("declared pattern", result)

    def test_unavailable_events_and_tools_are_explicit(self):
        result = diagnostics.diagnose(FixtureSystem(), self.row())
        self.assertIn("unavailable", result["details"]["Module inspection"])
        self.assertTrue(any("Journal access unavailable" in note for note in result["notes"]))
        self.assertTrue(any("Kernel ring buffer unavailable" in note for note in result["notes"]))

    def test_json_journal_only_returns_selected_device(self):
        entries = [{"MESSAGE": "r8169 0000:03:00.0: hardware event", "PRIORITY": "3"},
                   {"MESSAGE": "r8169 0000:04:00.0: another event", "PRIORITY": "3"}]
        command = ("journalctl", "--dmesg", "--boot", "--no-pager", "--output=json", "-n", "1500")
        result = diagnostics.diagnose(FixtureSystem(replies={command: CommandResult(0, "\n".join(json.dumps(entry) for entry in entries))}), self.row())
        self.assertIn("hardware event", result["events"])
        self.assertNotIn("another event", result["events"])

    def test_nontext_structured_journal_message_does_not_crash(self):
        entry = {"_KERNEL_DEVICE": "+pci:0000:03:00.0", "MESSAGE": [65, 66]}
        command = ("journalctl", "--dmesg", "--boot", "--no-pager", "--output=json", "-n", "1500")
        result = diagnostics.diagnose(FixtureSystem(replies={command: CommandResult(0, json.dumps(entry))}), self.row())
        self.assertIn("Non-text kernel message", result["events"])

    def test_module_name_cannot_inject_an_option_or_path(self):
        system = FixtureSystem()
        row = self.row()
        row["driver_module"] = "--help"
        diagnostics.diagnose(system, row)
        self.assertFalse(any(command[0] == "modinfo" for command in system.calls))


if __name__ == "__main__":
    unittest.main()
