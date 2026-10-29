"""GUI completion/selection regressions that do not need a display server."""
import queue
import threading
import time
import unittest
from collections import deque
from unittest.mock import Mock, patch

from devicescope.gui import DeviceScope, Graph


class GuiLogicTests(unittest.TestCase):
    def app(self):
        app = DeviceScope.__new__(DeviceScope)
        app.closed = False
        app.jobs = set()
        app.system = Mock()
        app.results = queue.Queue()
        app.diagnostic_generation = 0
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

    def test_refresh_resets_a_category_that_no_longer_exists(self):
        for selected, expected in (("USB", "All categories"), ("PCI", "PCI")):
            with self.subTest(selected=selected):
                app = self.app()
                app.previous = None
                app.device_category = Mock()
                app.device_category.get.return_value = selected
                app.device_category.set.side_effect = lambda value: setattr(
                    app.device_category.get, "return_value", value)
                for name in ("category", "device_note", "diagnostic_note",
                             "bluetooth_readout", "device_table"):
                    setattr(app, name, Mock())
                app.device_search = Mock()
                app.device_search.get.return_value = ""
                app.attention, app.changes_only = Mock(), Mock()
                app.attention.get.return_value = app.changes_only.get.return_value = False
                current = {"id": "pci:1", "name": "Current device", "category": "PCI"}
                app._submit = lambda key, task, done: done(task())
                with patch("devicescope.gui.scan_devices", return_value={"devices": [current]}):
                    app.refresh_devices()
                self.assertEqual(expected, app.device_category.get())
                self.assertEqual(["All categories", "PCI"],
                                 app.category.configure.call_args.kwargs["values"])
                self.assertEqual(1, len(app.device_table.set_rows.call_args.args[0]))

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

    def test_stop_refreshes_after_a_list_started_before_or_during_stop(self):
        process = {"pid": 123, "name": "Selected worker", "start_ticks": 456, "uid": 1000}
        for stop_first in (False, True):
            with self.subTest(scan_started_during_stop=stop_first):
                app = self.app()
                app.processes = [process]
                app.process_generation = 0
                app.last_process = 0
                app.process_auto = Mock()
                app.process_auto.get.return_value = False
                app.process_table = Mock()
                app.process_table.selected.return_value = process
                app.process_note, app.stop_result = Mock(), Mock()
                app.stop_button, app.force_button = Mock(), Mock()
                app.filter_processes = Mock()
                list_started, release_list, release_stop = (threading.Event() for _ in range(3))
                calls = []
                def scan(_):
                    calls.append(True)
                    if len(calls) == 1:
                        list_started.set()
                        if not release_list.wait(2):
                            raise RuntimeError("Test did not release the list scan")
                        return {"processes": [process]}
                    return {"processes": []}
                def stop(*_, **__):
                    if not release_stop.wait(2):
                        raise RuntimeError("Test did not release the stop result")
                    return "Process 123 stopped."
                with patch("devicescope.gui.scan_processes", side_effect=scan), \
                     patch("devicescope.gui.stop_process", side_effect=stop):
                    try:
                        if stop_first:
                            app.stop_selected(False)
                            app.refresh_processes()
                        else:
                            app.refresh_processes()
                            app.stop_selected(False)
                        self.assertTrue(list_started.wait(2))
                        release_stop.set()
                        deadline = time.monotonic() + 2
                        while "Stop process" in app.jobs and time.monotonic() < deadline:
                            app._poll()
                            time.sleep(.005)
                        self.assertNotIn("Stop process", app.jobs)
                        self.assertIn("Process list", app.jobs)
                    finally:
                        release_stop.set()
                        release_list.set()
                    self.drain(app)
                self.assertEqual([], app.processes)
                self.assertEqual(2, len(calls))


if __name__ == "__main__":
    unittest.main()
