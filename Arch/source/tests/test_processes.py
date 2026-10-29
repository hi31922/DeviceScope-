import contextlib
import os
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from devicescope.processes import parse_stat, scan_processes, stop_process
from devicescope.system import System


def stat_record(pid=123, name="test process)", start=456, flags=0, rss=42, state="S"):
    fields = ["0"] * 50
    fields[0], fields[1], fields[6] = state, "10", str(flags)
    fields[19], fields[21] = str(start), str(rss)
    return f"{pid} ({name}) " + " ".join(fields)


def row(**overrides):
    result = {"pid": 123, "name": "test process)", "uid": 1000,
              "start_ticks": 456, "kind": "User process"}
    result.update(overrides)
    return result


class ProcessScanTests(unittest.TestCase):
    def test_stat_parentheses_and_field_offsets(self):
        parsed = parse_stat(stat_record(name="a ) b ( c)", flags=0x200000, rss=13))
        self.assertEqual(parsed["name"], "a ) b ( c)")
        self.assertEqual(parsed["start_ticks"], 456)
        self.assertEqual(parsed["rss_pages"], 13)
        self.assertEqual(parsed["flags"], 0x200000)

    def test_malformed_stat(self):
        for text in ("", "123 missing", "123 (x) S 1", "-1 (x) " + "0 " * 30):
            with self.subTest(text=text), self.assertRaises(ValueError):
                parse_stat(text)

    def test_scan_real_values_from_fixture(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory) / "proc" / "123"
            base.mkdir(parents=True)
            (base / "stat").write_text(stat_record(), encoding="utf-8")
            (base / "status").write_text("Uid:\t1000\t1000\t1000\t1000\nVmRSS:\t27 kB\n", encoding="utf-8")
            (base / "cmdline").write_bytes(b"/usr/bin/example\0--argument\0")
            result = scan_processes(System(directory, commands=False))
            self.assertEqual(len(result["processes"]), 1)
            proc = result["processes"][0]
            self.assertEqual(proc["rss_bytes"], 27 * 1024)
            self.assertEqual(proc["uid"], 1000)
            self.assertEqual(proc["kind"], "User process")
            self.assertEqual(proc["command"], "/usr/bin/example --argument")
            self.assertEqual(proc["state"], "Sleeping")

    def test_unreadable_proc_is_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            result = scan_processes(System(directory, commands=False))
            self.assertEqual(result["processes"], [])
            self.assertIn("unavailable", result["notes"][0])

    def test_kernel_thread_is_metadata_based(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory) / "proc" / "123"
            base.mkdir(parents=True)
            (base / "stat").write_text(stat_record(flags=0x200000), encoding="utf-8")
            (base / "status").write_text("Uid:\t0\t0\t0\t0\n", encoding="utf-8")
            result = scan_processes(System(directory, commands=False))
            self.assertEqual(result["processes"][0]["kind"], "Kernel thread")


class ProcessStopTests(unittest.TestCase):
    def fake_environment(self, stat=None, owners="1000\t1000\t1000\t1000", exited=True):
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(mock.patch("devicescope.processes.sys.platform", "linux"))
        stack.enter_context(mock.patch.object(os, "getuid", return_value=1000, create=True))
        stack.enter_context(mock.patch.object(os, "geteuid", return_value=1000, create=True))
        stack.enter_context(mock.patch.object(os, "getpid", return_value=999))
        self.open = stack.enter_context(mock.patch.object(os, "pidfd_open", return_value=77, create=True))
        self.send = stack.enter_context(mock.patch.object(signal, "pidfd_send_signal", create=True))
        stack.enter_context(mock.patch.object(signal, "SIGKILL", 9, create=True))
        self.close = stack.enter_context(mock.patch.object(os, "close"))
        stack.enter_context(mock.patch("devicescope.processes.select.select", return_value=([77] if exited else [], [], [])))
        self.read = stack.enter_context(mock.patch.object(System, "read", side_effect=lambda path, limit:
            (stat or stat_record()) if path.endswith("/stat") else "Uid:\t" + owners + "\n"))
        return stack

    def test_sigterm_sent_only_to_retained_selected_handle(self):
        self.fake_environment()
        self.assertEqual(stop_process(row()), "Process 123 stopped.")
        self.open.assert_called_once_with(123, 0)
        self.send.assert_called_once_with(77, signal.SIGTERM, None, 0)
        self.close.assert_called_once_with(77)

    def test_force_uses_sigkill_only_when_requested(self):
        self.fake_environment()
        stop_process(row(), force=True)
        self.send.assert_called_once_with(77, 9, None, 0)

    def test_ignored_sigterm_does_not_claim_stopped(self):
        self.fake_environment(exited=False)
        message = stop_process(row())
        self.assertIn("still running", message)
        self.assertNotIn("stopped.", message)

    def test_reused_pid_sends_nothing_and_closes(self):
        self.fake_environment(stat=stat_record(start=457))
        with self.assertRaises(ProcessLookupError):
            stop_process(row())
        self.send.assert_not_called()
        self.close.assert_called_once_with(77)

    def test_root_and_other_owners_protected(self):
        self.fake_environment()
        for overrides in ({"uid": 0}, {"uid": 1001}, {"pid": 1}, {"pid": 999},
                          {"kind": "Kernel thread"}, {"name": "gnome-shell"}):
            with self.subTest(overrides=overrides), self.assertRaises((ValueError, PermissionError)):
                stop_process(row(**overrides))
        self.send.assert_not_called()

    def test_ownership_change_after_selection_sends_nothing(self):
        self.fake_environment(owners="1000\t0\t0\t0")
        with self.assertRaises(PermissionError):
            stop_process(row())
        self.send.assert_not_called()
        self.close.assert_called_once_with(77)

    def test_live_core_name_change_after_selection_is_protected(self):
        self.fake_environment(stat=stat_record(name="systemd"))
        with self.assertRaises(PermissionError):
            stop_process(row())
        self.send.assert_not_called()

    def test_no_unsafe_fallback_when_pidfd_unavailable(self):
        with mock.patch("devicescope.processes.sys.platform", "linux"), \
             mock.patch("devicescope.processes.os", object()):
            with self.assertRaisesRegex(RuntimeError, "pidfd"):
                stop_process(row())


@unittest.skipUnless(sys.platform == "linux" and hasattr(os, "getuid") and os.getuid() != 0
                     and hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal"),
                     "Needs an ordinary Linux user with pidfd support.")
class ProcessLinuxIntegrationTests(unittest.TestCase):
    def test_only_selected_disposable_worker_stops(self):
        workers = [subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
                   for _ in range(2)]
        try:
            rows = {item["pid"]: item for item in scan_processes(System())["processes"]}
            self.assertIn(workers[0].pid, rows)
            message = stop_process(rows[workers[0].pid])
            self.assertIn("stopped", message)
            workers[0].wait(timeout=5)
            self.assertIsNone(workers[1].poll())
        finally:
            for worker in workers:
                if worker.poll() is None:
                    worker.terminate()
                worker.wait(timeout=5)


if __name__ == "__main__":
    unittest.main()
