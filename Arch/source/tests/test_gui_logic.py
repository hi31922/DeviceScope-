"""GUI completion/selection regressions that do not need a display server."""
import queue
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


if __name__ == "__main__":
    unittest.main()
