"""Real-shaped Linux specification fixtures; no host firmware or settings touched."""
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from devicescope import specifications as specs
from devicescope.system import CommandResult, System


def module(size="8 GB", kind="DDR4", configured="2933 MT/s", rated="3200 MT/s", locator="DIMM_A1"):
    return f"""Handle 0x0010, DMI type 17, 92 bytes
Memory Device
        Size: {size}
        Locator: {locator}
        Type: {kind}
        Speed: {rated}
        Configured Memory Speed: {configured}
        Manufacturer: Actual Memory Vendor
        Part Number: Exact Module Part 1

"""


class SpecificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.system = System(self.root, commands=False)
        self.virtual_files = {}
        native_read = self.system.read
        self.read_patch = patch.object(self.system, "read", side_effect=lambda path, limit=1048576:
                                      self.virtual_files[str(path)][:limit] if str(path) in self.virtual_files
                                      else native_read(path, limit))
        native_entries = specs._entries
        def entries(system, path):
            if path == "/sys/bus/pci/devices":
                prefix = path + "/"
                names = {name[len(prefix):].split("/")[0] for name in self.virtual_files if name.startswith(prefix)}
                return [SimpleNamespace(name=name) for name in sorted(names)]
            return native_entries(system, path)
        self.entry_patch = patch.object(specs, "_entries", side_effect=entries)
        self.read_patch.start()
        self.entry_patch.start()

    def tearDown(self):
        self.entry_patch.stop()
        self.read_patch.stop()
        self.temp.cleanup()

    def put(self, name, data):
        # PCI addresses contain colons, which Windows cannot create as filenames.
        if name.startswith("/sys/bus/pci/devices/"):
            self.virtual_files[name] = data
            return
        p = self.root / name.lstrip("/")
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(data, encoding="utf-8")

    def section(self, title, report):
        return {r["label"]: r["value"] for s in report["sections"] if s["title"] == title for r in s["rows"]}

    def capture(self, reply=None):
        with patch.object(self.system, "run", return_value=reply or CommandResult(None, stderr="dmidecode not installed")), \
             patch.object(specs, "_platform_sections", return_value={"sections": [], "notes": ["Platform fixture"]}):
            return specs.capture_specifications(self.system)

    def test_os_release_is_text_and_missing_edition_is_not_guessed(self):
        self.put("/etc/os-release", 'NAME="Arch Linux"\nPRETTY_NAME="$(touch /tmp/not-executed)"\nVERSION_ID="rolling"\n')
        self.put("/proc/sys/kernel/osrelease", "6.12-fixture")
        os = self.section("Operating System", self.capture())
        self.assertEqual("$(touch /tmp/not-executed)", os["Name"])
        self.assertEqual("Unavailable", os["Edition / variant"])
        self.assertEqual("rolling", os["Version"])
        self.assertEqual("6.12-fixture", os["Kernel release"])

    def test_full_processor_models_are_preserved_and_thread_duplicates_removed(self):
        self.put("/proc/cpuinfo", "processor : 0\nmodel name : Example Exact CPU 7950X @ 4.50GHz\nvendor_id : AuthenticAMD\n\nprocessor : 1\nmodel name : Example Exact CPU 7950X @ 4.50GHz\nvendor_id : AuthenticAMD\n")
        cpu = self.section("Processor", self.capture())
        self.assertEqual("Example Exact CPU 7950X @ 4.50GHz", cpu["Full model 1"])
        self.assertNotIn("Full model 2", cpu)
        self.assertEqual("2", cpu["Logical processors"])

    def test_memory_units_and_configured_vs_rated_are_preserved(self):
        text = module() + module("16 GB", "DDR5", "2400 MHz", "4800 MT/s", "DIMM_B1") + module("No Module Installed")
        result = specs.parse_memory_devices(text)
        self.assertEqual(24 * 1024**3, result["installed_bytes"])
        self.assertEqual(2, len(result["modules"]))
        self.assertEqual("2933 MT/s", result["modules"][0]["configured_speed"])
        self.assertEqual("3200 MT/s", result["modules"][0]["rated_speed"])
        self.assertEqual("2400 MHz", result["modules"][1]["configured_speed"])
        self.assertEqual("4800 MT/s", result["modules"][1]["rated_speed"])
        self.assertEqual("DDR5", result["modules"][1]["type"])

    def test_partial_installed_memory_is_not_reported_as_complete_total(self):
        result = specs.parse_memory_devices(module() + module("Unknown", "Unknown", "Unknown", "0 MT/s"))
        self.assertIsNone(result["installed_bytes"])
        self.assertEqual(8 * 1024**3, result["modules"][0]["capacity"])
        self.assertIsNone(result["modules"][1]["capacity"])
        self.assertEqual("Unavailable", result["modules"][1]["type"])
        self.assertEqual("Unavailable", result["modules"][1]["configured_speed"])
        self.assertEqual("Unavailable", result["modules"][1]["rated_speed"])
        self.assertIsNone(specs.parse_memory_devices(module("No Module Installed"))["installed_bytes"])

    def test_denied_firmware_keeps_os_usable_memory_distinct(self):
        self.put("/proc/meminfo", "MemTotal: 2048 kB\n")
        reply = CommandResult(1, stdout=module("16 GB"), stderr="Permission denied")
        report = self.capture(reply)
        ram = self.section("Memory", report)
        self.assertEqual("Unavailable", ram["Total installed RAM (firmware)"])
        self.assertEqual("2.0 MiB", ram["OS-usable physical RAM"])
        self.assertEqual("Unavailable", ram["DDR / memory type"])
        self.assertTrue(any("Permission denied" in note for note in report["notes"]))

    def pci(self, address, vendor="0x10de", product="0x1234"):
        base = "/sys/bus/pci/devices/" + address
        for key, data in {"vendor": vendor, "device": product, "class": "0x030000", "uevent": "DRIVER=nvidia\n"}.items():
            self.put(base + "/" + key, data)

    def test_all_pci_gpus_retained_while_duplicate_drm_adapter_is_removed(self):
        self.pci("0000:01:00.0")
        self.pci("0000:02:00.0")
        self.put("/usr/share/misc/pci.ids", "10de  NVIDIA Corporation\n\t1234  Full Exact Example GPU Model\n")
        self.put("/sys/class/drm/card0/device/uevent", "PCI_SLOT_NAME=0000:01:00.0\nDRIVER=nvidia\n")
        self.put("/sys/class/drm/card0/device/vendor", "0x10de")
        self.put("/sys/class/drm/card0/device/device", "0x1234")
        gpu = self.section("Graphics", self.capture())
        self.assertEqual("2", gpu["Detected graphics adapters"])
        self.assertEqual("Full Exact Example GPU Model", gpu["GPU 1 — full model"])
        self.assertEqual("Full Exact Example GPU Model", gpu["GPU 2 — full model"])
        self.assertNotEqual(gpu["GPU 1 — kernel identity"], gpu["GPU 2 — kernel identity"])

    def test_unknown_gpu_model_is_not_fabricated_from_numeric_id(self):
        self.pci("0000:01:00.0", "0xabcd", "0x9876")
        report = self.capture()
        gpu = self.section("Graphics", report)
        self.assertEqual("Unavailable", gpu["GPU 1 — manufacturer"])
        self.assertEqual("Unavailable", gpu["GPU 1 — full model"])
        self.assertEqual("abcd:9876", gpu["GPU 1 — PCI vendor:device"])
        self.assertTrue(any("pci.ids" in note for note in report["notes"]))

    def test_drm_only_gpu_is_reported_without_inventing_a_pci_model(self):
        self.put("/sys/class/drm/card0/device/uevent", "DRIVER=virtio_gpu\n")
        gpu = self.section("Graphics", self.capture())
        self.assertEqual("1", gpu["Detected graphics adapters"])
        self.assertEqual("Unavailable", gpu["GPU 1 — full model"])
        self.assertEqual("virtio_gpu", gpu["GPU 1 — bound driver"])

    def test_storage_uses_whole_disk_models_and_ignores_memory_loop_and_partitions(self):
        self.put("/sys/block/nvme0n1/device/model", "Exact NVMe 2TB Model")
        self.put("/sys/block/nvme0n1/size", "4096")
        self.put("/sys/block/loop0/size", "4000")
        self.put("/sys/block/nvme0n1p1/partition", "1")
        disk = self.section("Storage", self.capture())
        self.assertEqual("/dev/nvme0n1", disk["Drive 1 — device"])
        self.assertEqual("Exact NVMe 2TB Model", disk["Drive 1 — model"])
        self.assertEqual("2.0 MiB", disk["Drive 1 — capacity"])
        self.assertFalse(any(key.startswith("Drive 2") for key in disk))

    def test_empty_sysfs_and_placeholder_board_values_are_explicitly_unavailable(self):
        self.put("/sys/class/dmi/id/board_vendor", "To Be Filled By O.E.M.")
        self.put("/sys/class/dmi/id/board_name", "Default string")
        report = self.capture()
        self.assertEqual("Unavailable", self.section("Motherboard", report)["Manufacturer"])
        self.assertEqual("Unavailable", self.section("Motherboard", report)["Model"])
        self.assertEqual("Unavailable", self.section("Storage", report)["Detected drives"])
        self.assertEqual("Unavailable", self.section("Graphics", report)["Detected graphics adapters"])
        self.assertIn("Platform fixture", report["notes"])


if __name__ == "__main__":
    unittest.main()
