"""GUI completion/selection regressions that do not need a display server."""
import queue
import threading
import tempfile
from pathlib import Path
import time
import unittest
from collections import deque
from unittest.mock import Mock, patch

from devicescope.gui import DeviceScope, Graph
from devicescope.snapshots import load_snapshot


class GuiLogicTests(unittest.TestCase):
    def app(self):
        app = DeviceScope.__new__(DeviceScope)
        app.closed = False
        app.jobs = set()
        app.system = Mock()
        app.results = queue.Queue()
        app.diagnostic_generation = 0
        app.comparison_generation = 0
        app.pending_comparison = None
        app.diagnostic_table = Mock()
        app.diagnostic_readout = Mock()
        app.footer = Mock()
        app._schedule = Mock()
        return app

    def drain(self, app):
        deadline = time.monotonic() + 2
        while app.jobs and time.monotonic() < deadline:
            app._poll()
            time.sleep(.005)
        self.assertFalse(app.jobs, "Diagnostic worker did not finish")

    def test_failed_inspection_still_runs_latest_selection(self):
        app = self.app()
        first = {"id": "a", "name": "First device"}
        latest = {"id": "b", "name": "Latest device"}
        app.diagnostic_table.selected.return_value = first
        with patch("devicescope.gui.diagnose", side_effect=[
                OSError("Device disappeared"), {"summary": "Latest result"}]) as diagnose:
            app.inspect_selected()
            app.diagnostic_table.selected.return_value = latest
            app.inspect_selected()
            self.drain(app)
            self.assertEqual(2, diagnose.call_count)
        self.assertIn("Latest result", app.diagnostic_readout.set.call_args.args[0])
        self.assertFalse(app.jobs)

    def test_failed_current_inspection_replaces_loading_message(self):
        app = self.app()
        app.diagnostic_table.selected.return_value = {"id": "a", "name": "First device"}
        with patch("devicescope.gui.diagnose", side_effect=OSError("Device disappeared")):
            app.inspect_selected()
            self.drain(app)
        message = app.diagnostic_readout.set.call_args.args[0]
        self.assertIn("Device disappeared", message)
        self.assertNotIn("Inspecting First device...", message)

    def test_clearing_device_selection_clears_old_hardware_details(self):
        app = self.app()
        app.device_table = Mock()
        app.device_details = Mock()
        app.device_table.selected.return_value = {"name": "Old device", "hardware_ids": ["PCI:1234"]}
        app._select_device()
        self.assertIn("PCI:1234", app.device_details.set.call_args.args[0])
        app.device_table.selected.return_value = None
        app._select_device()
        self.assertNotIn("PCI:1234", app.device_details.set.call_args.args[0])

    def test_temperature_graph_distinguishes_negative_from_zero(self):
        graph = Mock()
        graph.winfo_width.return_value = 400
        graph.winfo_height.return_value = 160
        graph.title, graph.color, graph.unit = "Temperature", "orange", " °C"
        graph.history = deque([(0, -20), (1, 0), (2, 20)])
        Graph.redraw(graph)
        heights = [call.args[1] for call in graph.create_oval.call_args_list]
        self.assertEqual(3, len(heights))
        self.assertGreater(heights[0], heights[1])
        self.assertGreater(heights[1], heights[2])


    def test_clear_comparison_invalidates_an_inflight_import(self):
        app = self.app()
        app.root, app.changes_only, app.filter_devices = Mock(), Mock(), Mock()
        app.previous = [{"id": "existing baseline"}]
        started, release = threading.Event(), threading.Event()
        def load(_):
            started.set()
            if not release.wait(2):
                raise RuntimeError("Test did not release snapshot import")
            return [{"id": "cleared baseline"}]
        with patch("devicescope.gui.filedialog.askopenfilename", return_value="fixture.json"), \
             patch("devicescope.gui.load_snapshot", side_effect=load):
            try:
                app.compare()
                self.assertTrue(started.wait(2))
                app.clear_comparison()
            finally:
                release.set()
            self.drain(app)
        self.assertIsNone(app.previous, "A cleared comparison must not return when its earlier import finishes")

    def test_latest_compare_selection_wins_while_first_import_is_pending(self):
        self._check_latest_comparison(False)

    def test_failed_old_import_still_loads_latest_comparison(self):
        self._check_latest_comparison(True)

    def _check_latest_comparison(self, first_fails):
        app = self.app()
        app.root, app.changes_only, app.filter_devices = Mock(), Mock(), Mock()
        app.previous = None
        started, release = threading.Event(), threading.Event()
        first, latest = [{"id": "first"}], [{"id": "latest"}]
        calls = []
        def load(path):
            calls.append(path)
            if path == "first.json":
                started.set()
                if not release.wait(2):
                    raise RuntimeError("Test did not release snapshot import")
                if first_fails:
                    raise OSError("First file disappeared")
                return first
            return latest
        with patch("devicescope.gui.filedialog.askopenfilename", side_effect=["first.json", "latest.json"]), \
             patch("devicescope.gui.load_snapshot", side_effect=load):
            try:
                app.compare()
                self.assertTrue(started.wait(2))
                app.compare()
            finally:
                release.set()
            self.drain(app)
        self.assertEqual(["first.json", "latest.json"], calls, "The newest requested baseline must not be silently dropped")
        self.assertEqual(latest, app.previous)
        self.assertEqual(1, app.filter_devices.call_count, "An obsolete import must not briefly replace the latest selection")


    def empty_inventory_app(self):
        app = self.app()
        app.root, app.filter_devices = Mock(), Mock()
        app.devices = []
        app.inventory_loaded = False
        app.category, app.device_category = Mock(), Mock()
        app.device_category.get.return_value = "All categories"
        app.device_note, app.diagnostic_note, app.bluetooth_readout = Mock(), Mock(), Mock()
        return app

    def test_successful_empty_inventory_can_be_exported(self):
        app = self.empty_inventory_app()
        captured = {}
        def submit(key, task, done):
            captured["done"] = done
            return True
        with patch.object(app, "_submit", side_effect=submit):
            app.refresh_devices()
        captured["done"]({"devices": [], "notes": [], "bluetooth": {}})
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "empty.json"
            with patch("devicescope.gui.filedialog.asksaveasfilename", return_value=str(target)):
                app.export()
                self.drain(app)
            self.assertTrue(target.is_file(), "A successful zero-device scan must be exportable")
            self.assertEqual([], load_snapshot(target))

    def test_failed_initial_inventory_still_blocks_export(self):
        app = self.empty_inventory_app()
        with patch("devicescope.gui.scan_devices", side_effect=OSError("No inventory read")):
            app.refresh_devices()
            self.drain(app)
        with patch("devicescope.gui.filedialog.asksaveasfilename") as dialog:
            app.export()
        dialog.assert_not_called()
        self.assertIn("Wait for a successful scan", app.footer.set.call_args.args[0])

    def test_busy_export_reports_pending_write_before_another_save_dialog(self):
        app = self.empty_inventory_app()
        app.inventory_loaded = True
        app.devices = [{"id": "fixture"}]
        started, release = threading.Event(), threading.Event()
        calls = []
        def save(path, devices):
            calls.append(path)
            started.set()
            if not release.wait(2):
                raise RuntimeError("Test did not release snapshot export")
        with patch("devicescope.gui.filedialog.asksaveasfilename", side_effect=["first.json", "second.json"]) as dialog, \
             patch("devicescope.gui.save_snapshot", side_effect=save):
            try:
                app.export()
                self.assertTrue(started.wait(2))
                app.export()
                self.assertEqual(1, dialog.call_count, "Do not open a second Save dialog whose destination will be discarded")
                self.assertIn("in progress", app.footer.set.call_args.args[0])
            finally:
                release.set()
                self.drain(app)
        self.assertEqual(["first.json"], calls)


if __name__ == "__main__":
    unittest.main()
