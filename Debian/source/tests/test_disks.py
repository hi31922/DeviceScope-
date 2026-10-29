import json
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from devicescope.disks import parse_mountinfo, parse_smart_json, read_volumes, scan_disks
from devicescope.system import System, CommandResult


class DiskTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.system = System(self.tmp.name, commands=False)

    def put(self, name, value):
        target = self.system.path(name)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value, encoding="utf-8")

    def test_nonzero_smart_health_bits_not_discarded(self):
        result = parse_smart_json(json.dumps({"smart_status": {"passed": False}, "temperature": {"current": 42}}), 8)
        self.assertEqual(result["health"], "SMART reports failure")
        self.assertEqual(result["temperature_c"], 42)
        history = parse_smart_json(json.dumps({"smart_status": {"passed": True}}), 64)
        self.assertEqual(history["health"], "SMART self-assessment passed")
        self.assertIn("error-log", history["note"])

    def test_permission_or_malformed_not_healthy(self):
        result = parse_smart_json('{"smart_status":{"passed":true}}', 2)
        self.assertIn("Unavailable", result["health"])
        self.assertEqual(parse_smart_json("not JSON", 0)["health"], "Unavailable")
        self.assertEqual(parse_smart_json("[]", 0)["health"], "Unavailable")
        self.assertEqual(parse_smart_json("{}", 0)["health"], "SMART status unavailable")

    def test_partial_command_failure_keeps_definitive_disk_failure(self):
        result = parse_smart_json('{"smart_status":{"passed":false}}', 12)
        self.assertEqual(result["health"], "SMART reports failure")
        self.assertIn("incomplete", result["note"])
        partial_ok = parse_smart_json('{"smart_status":{"passed":true}}', 4)
        self.assertIn("Unavailable", partial_ok["health"])

    def test_nvme_wear_not_health_percentage(self):
        result = parse_smart_json(json.dumps({"smart_status": {"passed": True},
                                  "nvme_smart_health_information_log": {"percentage_used": 108,
                                                                       "critical_warning": 1}}), 0)
        self.assertEqual(result["health"], "SMART warning")
        self.assertEqual(result["details"]["NVMe endurance estimate used (%)"], 108)
        self.assertIn("not a universal health", result["details"]["Endurance interpretation"])

    def test_mount_escapes_and_reserved_blocks(self):
        text = ("42 1 8:1 / / rw,relatime - ext4 /dev/sda1 rw\n"
                "43 1 8:1 /data /bind\\040mount rw - ext4 /dev/sda1 rw\n"
                "44 1 0:6 / /proc rw - proc proc rw\n")
        self.assertEqual(parse_mountinfo(text)[1]["mount"], "/bind mount")
        self.put("/proc/self/mountinfo", text)
        space = SimpleNamespace(f_frsize=1024, f_bsize=1024, f_blocks=1000, f_bfree=300, f_bavail=250)
        with patch("devicescope.disks.os.statvfs", return_value=space, create=True):
            volumes, notes = read_volumes(self.system)
        self.assertEqual(len(volumes), 1)
        self.assertEqual(volumes[0]["used_bytes"], 700 * 1024)
        self.assertEqual(volumes[0]["free_bytes"], 300 * 1024)
        self.assertEqual(volumes[0]["available_bytes"], 250 * 1024)
        self.assertAlmostEqual(volumes[0]["percent"], 700 / 950 * 100)

    def test_inventory_capacity_and_no_health_guess(self):
        self.put("/sys/block/sda/size", "1000")
        self.put("/sys/block/sda/device/model", "Test model")
        self.put("/sys/block/sda/queue/rotational", "1")
        self.put("/sys/block/loop0/size", "100")
        report = scan_disks(self.system)
        self.assertEqual(len(report["disks"]), 1)
        row = report["disks"][0]
        self.assertEqual(row["size_bytes"], 512000)
        self.assertEqual(row["health"], "Unavailable")
        self.assertIsNone(row["temperature_c"])

    def test_unreadable_mount_does_not_hide_accessible_bind_mount(self):
        self.put("/proc/self/mountinfo",
                 "42 1 8:1 / /private/data rw - ext4 /dev/sda1 rw\n"
                 "43 1 8:1 / /public/data rw - ext4 /dev/sda1 rw\n")
        space = SimpleNamespace(f_frsize=1024, f_bsize=1024, f_blocks=1000,
                                f_bfree=300, f_bavail=250)
        with patch("devicescope.disks.os.statvfs", create=True,
                   side_effect=[PermissionError("search permission denied"), space]) as statvfs:
            volumes, notes = read_volumes(self.system)
        self.assertEqual(statvfs.call_count, 2)
        self.assertEqual(len(volumes), 1)
        self.assertEqual(volumes[0]["mount"], "/public/data")
        self.assertEqual(volumes[0]["used_bytes"], 700 * 1024)
        self.assertTrue(any("/private/data" in note for note in notes))

    def test_smart_request_read_only_and_standby_safe(self):
        self.put("/sys/block/nvme0n1/size", "1000")
        self.put("/sys/block/nvme0n1/device/model", "Test NVMe")
        result = CommandResult(0, '{"smart_status":{"passed":true}}')
        with patch.object(self.system, "run", return_value=result) as run:
            report = scan_disks(self.system)
        args = run.call_args.args[0]
        self.assertIn("--nocheck=standby,3", args)
        self.assertEqual(args[-1], "/dev/nvme0n1")
        self.assertEqual(report["disks"][0]["health"], "SMART self-assessment passed")

    def test_remote_mount_does_not_call_statvfs(self):
        self.put("/proc/self/mountinfo", "42 1 0:10 / /net rw - nfs4 server:/share rw\n")
        with patch("devicescope.disks.os.statvfs", create=True) as statvfs:
            volumes, notes = read_volumes(self.system)
        statvfs.assert_not_called()
        self.assertEqual(volumes, [])
        self.assertTrue(any("remote/userspace" in note for note in notes))

    def test_missing_device_link_does_not_mean_virtual(self):
        self.put("/sys/block/unusual0/size", "1000")
        row = scan_disks(self.system)["disks"][0]
        self.assertEqual(row["health"], "Unavailable")


if __name__ == "__main__":
    unittest.main()
