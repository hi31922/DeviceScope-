"""Real Tk component checks; no existing desktop applications are controlled."""
import os
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import tkinter as tk

from devicescope.gui import DeviceScope
from devicescope.system import System


@unittest.skipUnless(sys.platform == "win32" or os.environ.get("DISPLAY"), "Needs an X11/XWayland display for real Tk checks.")
class GuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            probe = tk.Tk()
            probe.withdraw()
            probe.destroy()
        except tk.TclError as exc:
            if sys.platform == "win32":
                raise unittest.SkipTest(f"Windows QA Tcl runtime cannot initialize: {exc}")
            raise

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = tk.Tk()
        self.root.geometry("1180x800+30000+30000")
        self.app = DeviceScope(self.root, System(self.temp.name, commands=False))
        self.errors = []
        self.root.report_callback_exception = lambda *args: self.errors.append(args)
        self.root.update()

    def tearDown(self):
        if not self.app.closed:
            self.app.close()
        self.temp.cleanup()

    def pump(self, duration=.2):
        until = time.monotonic() + duration
        while time.monotonic() < until:
            self.root.update()
            time.sleep(.01)

    def test_all_tabs_and_compact_window_do_not_raise(self):
        for size in ("1180x800+30000+30000", "840x620+30000+30000"):
            self.root.geometry(size)
            for tab in range(6):
                self.app.tabs.select(tab)
                self.pump()
        self.assertEqual([], self.errors)
        self.assertEqual(6, len(self.app.tabs.tabs()))

    def test_selection_keeps_start_time_identity_and_controls_visibility(self):
        process = {"pid": 55555, "name": "fixture", "uid": 1000, "start_ticks": 111,
                   "rss_bytes": 2**20, "state": "Sleeping", "command": "fixture", "kind": "User process"}
        self.app.processes = [process]
        self.app.filter_processes()
        self.app.process_table.tree.selection_set("55555:111")
        self.pump()
        self.assertEqual(111, self.app.process_table.selected()["start_ticks"])
        self.app.processes = [dict(process, start_ticks=222)]
        self.app.filter_processes()
        self.pump()
        self.assertIsNone(self.app.process_table.selected())
        self.assertEqual("", self.app.process_actions.winfo_manager())

    def test_pending_sigterm_does_not_remove_still_running_process(self):
        process = {"pid": 55555, "name": "fixture", "uid": 1000, "start_ticks": 111,
                   "rss_bytes": 2**20, "state": "Sleeping", "command": "fixture", "kind": "User process"}
        self.app.processes = [process]
        self.app.filter_processes()
        self.app.process_table.tree.selection_set("55555:111")
        self.root.update()
        with patch("devicescope.gui.stop_process", return_value="Stop requested, but it is still running."), \
             patch("devicescope.gui.scan_processes", return_value={"processes": [process], "notes": []}):
            self.app.stop_selected(False)
            self.pump(.4)
        self.assertEqual([process], self.app.processes)
        self.assertIn("still running", self.app.stop_result.get())
        self.assertEqual([], self.errors)

    def test_numeric_ram_sort(self):
        rows = [{"id": "a", "name": "small", "rss_bytes": 900},
                {"id": "b", "name": "large", "rss_bytes": 10000},
                {"id": "c", "name": "unreadable", "rss_bytes": None}]
        self.app.process_table.set_rows(rows)
        self.assertEqual("b", self.app.process_table.tree.get_children()[0])
        self.assertEqual("c", self.app.process_table.tree.get_children()[-1])

    def test_close_cancels_owned_timers(self):
        pending = set(self.app.after_ids)
        self.assertTrue(pending)
        self.app.close()
        remaining = set(self.root.tk.call("after", "info"))
        self.assertFalse(pending & remaining)
        self.assertFalse(self.app.after_ids)

    def test_specifications_load_and_all_six_headers_fit(self):
        report = {"sections": [
            {"title": "Processor", "rows": [{"label": "Full model", "value": "Fixture CPU model"}]},
            {"title": "Graphics", "rows": [{"label": "GPU 1", "value": "Fixture GPU A"},
                                              {"label": "GPU 2", "value": "Fixture GPU B"}]},
            {"title": "Memory", "rows": [{"label": "DDR type", "value": "Unavailable"}]}
        ], "notes": ["Fixture: firmware access denied."]}
        with patch("devicescope.gui.capture_specifications", return_value=report):
            self.app.tabs.select(self.app.specification_tab)
            self.pump(.3)
        text = self.app.specification_readout.text.get("1.0", "end")
        for expected in ("Fixture CPU model", "Fixture GPU A", "Fixture GPU B",
                         "DDR type: Unavailable", "firmware access denied"):
            self.assertIn(expected, text)
        for width in (840, 1180):
            self.root.geometry(f"{width}x800+30000+30000")
            self.pump(.15)
            visible = set()
            for y in (16, 24, 32, 40, 48, 56):
                for x in range(1, self.app.tabs.winfo_width(), 3):
                    try:
                        visible.add(self.app.tabs.index(f"@{x},{y}"))
                    except tk.TclError:
                        pass
            self.assertEqual(set(range(6)), visible, f"Hidden tab header at width {width}")
        self.assertEqual([], self.errors)

    def test_copy_specs_is_tab_local_and_disabled_before_loading(self):
        from tkinter import ttk
        def copy_buttons(widget):
            found = []
            for child in widget.winfo_children():
                if isinstance(child, ttk.Button) and child.cget("text") == "Copy specifications":
                    found.append(child)
                found.extend(copy_buttons(child))
            return found
        self.assertTrue(self.app.copy_specifications_button.instate(["disabled"]))
        for tab in (self.app.device_tab, self.app.process_tab, self.app.diagnostic_tab,
                    self.app.performance_tab, self.app.disk_tab):
            self.assertEqual([], copy_buttons(tab))
        self.assertEqual([self.app.copy_specifications_button], copy_buttons(self.app.specification_tab))
        with patch.object(self.root, "clipboard_clear") as clear:
            self.assertFalse(self.app.copy_specifications())
        clear.assert_not_called()

    def test_copy_specs_copies_all_sections_offscreen_rows_and_notes(self):
        titles = ("Operating System", "Processor", "Graphics", "Memory", "Motherboard",
                  "Firmware", "Storage", "Display")
        report = {"sections": [{"title": title,
                    "rows": [{"label": f"Field {index}", "value": f"Fixture {title} value {index}"}
                             for index in range(30)]} for title in titles],
                  "notes": ["Fixture: firmware permission unavailable.", "Ünicode collection note."]}
        report["sections"][2]["rows"].extend([
            {"label": "GPU 1 model", "value": "Fixture integrated GPU"},
            {"label": "GPU 2 model", "value": "Fixture discrete GPU"}])
        report["sections"][-1]["rows"].append(
            {"label": "Last reported supported mode", "value": "7680x4320 @ 60 Hz"})
        with patch("devicescope.gui.capture_specifications", return_value=report):
            self.app.refresh_specifications()
            self.pump(.2)
        self.assertFalse(self.app.copy_specifications_button.instate(["disabled"]))
        readout = self.app.specification_readout.text
        readout.tag_add("sel", "1.0", "1.1")
        readout.yview_moveto(1)
        expected = readout.get("1.0", "end-1c")
        # Use real Tk controls but intercept clipboard writes to preserve the
        # user's host clipboard, including image/custom formats shared via WSLg.
        with patch.object(self.root, "clipboard_clear") as clear, \
             patch.object(self.root, "clipboard_append") as append:
            self.app.copy_specifications_button.invoke()
        clear.assert_called_once_with()
        append.assert_called_once_with(expected)
        for value in (*titles, "Fixture integrated GPU", "Fixture discrete GPU",
                      "7680x4320 @ 60 Hz", "Ünicode collection note."):
            self.assertIn(value, expected)
        self.assertIn("copied", self.app.specification_note.get())
        self.assertEqual([], self.errors)

    def test_copy_specs_failure_does_not_claim_success_and_refresh_updates_copy(self):
        def report(value):
            return {"sections": [{"title": "Processor", "rows": [{"label": "Model", "value": value}]}], "notes": []}
        with patch("devicescope.gui.capture_specifications", return_value=report("Old fixture")):
            self.app.refresh_specifications()
            self.pump(.2)
        with patch.object(self.root, "clipboard_clear", side_effect=tk.TclError("clipboard busy")):
            self.assertFalse(self.app.copy_specifications())
        self.assertIn("Could not copy", self.app.specification_note.get())
        with patch("devicescope.gui.capture_specifications", return_value=report("New fixture")):
            self.app.refresh_specifications()
            self.pump(.2)
        with patch.object(self.root, "clipboard_clear"), patch.object(self.root, "clipboard_append") as append:
            self.assertTrue(self.app.copy_specifications())
        self.assertIn("New fixture", append.call_args.args[0])
        self.assertNotIn("Old fixture", append.call_args.args[0])

    def test_removed_device_category_does_not_hide_refreshed_inventory(self):
        self.app.device_category.set("Former category")
        report = {"devices": [{"id": "fixture-new", "name": "Current device",
                              "category": "Current category"}], "notes": [], "bluetooth": {}}
        with patch.object(self.app, "_submit", side_effect=lambda key, task, done: done(report)):
            self.app.refresh_devices()
        self.assertEqual("All categories", self.app.device_category.get())
        self.assertEqual(["fixture-new"], list(self.app.device_table.rows))

    def test_stop_rejects_scans_started_before_and_during_signal_check(self):
        process = {"pid": 55555, "name": "fixture", "uid": 1000, "start_ticks": 111,
                   "rss_bytes": 2**20, "state": "Sleeping", "command": "fixture", "kind": "User process"}
        self.app.process_auto.set(False)
        self.app.processes = [process]
        self.app.filter_processes()
        self.app.process_table.tree.selection_set("55555:111")
        self.root.update()
        pending = {}
        def submit(key, task, done):
            if key in pending:
                return False
            pending[key] = done
            self.app.jobs.add(key)
            return True
        def complete(key, result):
            done = pending.pop(key)
            self.app.jobs.discard(key)
            done(result)
        with patch.object(self.app, "_submit", side_effect=submit):
            self.app.refresh_processes()  # Started before the stop request.
            self.app.stop_selected(False)
            complete("Process list", {"processes": [process], "notes": []})
            self.assertIn("Process list", pending, "Stale scan must retry even with auto-refresh off")
            # A replacement scan can start while stop_process waits for exit.
            complete("Stop process", (True, "Fixture process exited."))
            complete("Process list", {"processes": [process], "notes": []})
            self.assertIn("Process list", pending, "Scan started during stop must also be rejected")
            complete("Process list", {"processes": [], "notes": []})
        self.assertEqual([], self.app.processes)
        self.assertEqual((), self.app.process_table.tree.get_children())
        self.assertFalse(self.app.stop_button.instate(["disabled"]))
