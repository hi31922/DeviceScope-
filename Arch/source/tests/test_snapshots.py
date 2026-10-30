import copy
import json
import tempfile
import unittest
from pathlib import Path

from devicescope.snapshots import compare_devices, load_snapshot, save_snapshot


def device(identity="pci:0000:00:02.0", **changes):
    result = {"id": identity, "name": "Graphics", "category": "PCI", "device_type": "Physical hardware",
              "driver": "i915", "status": "Driver bound", "needs_attention": False,
              "hardware_ids": ["PCI\\VEN_8086&DEV_1234"], "details": {"Vendor": "8086"},
              "evidence": "Enumerated through PCI sysfs."}
    result.update(changes)
    return result


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "devices.json"

    def test_unicode_roundtrip_excludes_comparison_metadata(self):
        original = device(name="Aygıt — 网卡", previous={"secret": "not persisted"}, change="Changed")
        save_snapshot(self.path, [original])
        loaded = load_snapshot(self.path)
        self.assertEqual(loaded[0]["name"], original["name"])
        self.assertNotIn("previous", loaded[0])
        self.assertNotIn("change", loaded[0])
        self.assertEqual(list(Path(self.directory.name).iterdir()), [self.path])

    def test_escaped_unpaired_surrogates_rejected_before_rendering(self):
        save_snapshot(self.path, [device()])
        original = json.loads(self.path.read_text(encoding="utf-8"))
        for invalid in (chr(0xd800), chr(0xdfff)):
            for row in (device(name=invalid), device(id=invalid),
                        device(hardware_ids=[invalid]), device(driver=invalid),
                        device(details={invalid: "value"}),
                        device(details={"property": invalid})):
                document = dict(original, devices=[row])
                # JSON escapes can represent a lone surrogate even though the
                # resulting Python string cannot be sent to Tk as UTF-8.
                self.path.write_text(json.dumps(document), encoding="utf-8")
                with self.subTest(row=row), self.assertRaisesRegex(ValueError, "Unicode"):
                    load_snapshot(self.path)

    def test_supplementary_unicode_remains_valid(self):
        original = device(name="Controller \U0001f5a5", details={"\U0001f50c": "Connected \U0001f680"})
        save_snapshot(self.path, [original])
        self.assertEqual([original], load_snapshot(self.path))
        # A valid escaped surrogate pair is decoded to one Unicode scalar.
        document = json.loads(self.path.read_text(encoding="utf-8"))
        self.path.write_text(json.dumps(document), encoding="utf-8")
        self.assertEqual([original], load_snapshot(self.path))

    def test_mixed_changes_and_previous_values(self):
        before = [device(), device("gone"), device("stable")]
        current = [device(driver="xe"), device("new"), device("stable")]
        result = {row["id"]: row for row in compare_devices(current, before)}
        self.assertEqual(result[device()["id"]]["change"], "Changed")
        self.assertEqual(result[device()["id"]]["previous"]["driver"], "i915")
        self.assertEqual(result["gone"]["change"], "Removed")
        self.assertEqual(result["new"]["change"], "Added")
        self.assertEqual(result["stable"]["change"], "Unchanged")
        self.assertNotIn("change", current[0])

    def test_dynamic_details_do_not_create_spurious_changes(self):
        self.assertEqual(compare_devices([device(details={"Link speed": "1000"})],
                                         [device(details={"Link speed": "100"})])[0]["change"], "Unchanged")

    def test_network_state_ignored_but_mac_change_detected(self):
        before = device(category="Network", status="down", details={"Interface": "enp3s0", "MAC address": "aa:bb", "Operational state": "down"})
        after = device(category="Network", status="up", details={"Interface": "enp3s0", "MAC address": "aa:bb", "Operational state": "up"})
        self.assertEqual(compare_devices([after], [before])[0]["change"], "Unchanged")
        after["details"]["MAC address"] = "cc:dd"
        self.assertEqual(compare_devices([after], [before])[0]["change"], "Changed")

    def test_stable_module_details_changes_detected(self):
        self.assertEqual(compare_devices([device(details={"Driver module": "new"})],
                                         [device(details={"Driver module": "old"})])[0]["change"], "Changed")

    def test_duplicate_id_rejected(self):
        with self.assertRaisesRegex(ValueError, "unique"):
            save_snapshot(self.path, [device(), device()])
        self.assertFalse(self.path.exists())

    def test_invalid_types_rejected(self):
        for changes in ({"id": ""}, {"needs_attention": 1}, {"hardware_ids": [42]},
                        {"details": []}, {"name": None}, {"driver": {}},
                        {"details": {"broken": float("nan")}}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                save_snapshot(self.path, [device(**changes)])

    def test_windows_or_future_schema_rejected(self):
        save_snapshot(self.path, [device()])
        original = json.loads(self.path.read_text(encoding="utf-8"))
        for changed in ({"format": "devicescope-windows"}, {"schema_version": 2},
                        {"schema_version": True}, {"platform": "windows"},
                        {"created_at": "yesterday"}, {"created_at": "2026-10-27T12:00:00"}):
            doc = dict(original, **changed)
            self.path.write_text(json.dumps(doc), encoding="utf-8")
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                load_snapshot(self.path)

    def test_duplicate_json_keys_and_nan_rejected(self):
        for raw in ('{"format":"devicescope-linux","format":"other"}', '{"x": NaN}', '{"x": Infinity}'):
            self.path.write_text(raw, encoding="utf-8")
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                load_snapshot(self.path)

    def test_size_limit_preserves_existing_export(self):
        save_snapshot(self.path, [device()])
        previous = self.path.read_bytes()
        with self.assertRaises(ValueError):
            save_snapshot(self.path, [device(details={"huge": "x" * (1024 * 1024 + 1)})])
        self.assertEqual(self.path.read_bytes(), previous)

    def test_loaded_size_limit(self):
        with self.path.open("wb") as handle:
            handle.write(b" " * (16 * 1024 * 1024 + 1))
        with self.assertRaisesRegex(ValueError, "16 MiB"):
            load_snapshot(self.path)


if __name__ == "__main__":
    unittest.main()
