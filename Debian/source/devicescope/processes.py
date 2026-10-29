"""Read Linux processes and signal one identity through a retained pidfd."""
from __future__ import annotations

import os
import select
import signal
import sys
from pathlib import Path

from .system import System

_PF_KTHREAD = 0x00200000
_CORE_NAMES = frozenset({
    "init", "systemd", "systemd-logind", "systemd-udevd", "udevd", "login",
    "sshd", "dbus-daemon", "dbus-broker", "dbus-broker-lau", "gdm", "gdm3",
    "gdm-session-wor", "lightdm", "sddm", "X", "Xorg", "Xwayland",
    "gnome-shell", "gnome-session-b", "kwin_wayland", "kwin_x11", "plasmashell",
    "startplasma-way", "xfce4-session", "xfwm4", "cinnamon", "cinnamon-sessi",
    "mate-session", "mate-panel", "marco", "weston", "sway", "Hyprland",
    "pipewire", "pipewire-pulse", "wireplumber", "pulseaudio", "polkitd",
})
_STATES = {
    "R": "Running", "S": "Sleeping", "D": "Disk wait", "Z": "Zombie",
    "T": "Stopped", "t": "Tracing stop", "X": "Dead", "x": "Dead",
    "I": "Idle", "P": "Parked", "K": "Wakekill", "W": "Waking",
}


def parse_stat(text: str) -> dict:
    """Parse comm using its final ')' because comm may itself contain ')' and spaces."""
    left, right = text.find("("), text.rfind(")")
    if left <= 0 or right <= left:
        raise ValueError("Malformed process stat record.")
    fields = text[right + 1:].split()
    if len(fields) < 22:
        raise ValueError("Incomplete process stat record.")
    result = {
        "pid": int(text[:left].strip()), "name": text[left + 1:right],
        "state_code": fields[0], "ppid": int(fields[1]),
        "flags": int(fields[6]), "start_ticks": int(fields[19]),
        "rss_pages": int(fields[21]),
    }
    if result["pid"] <= 0 or result["start_ticks"] < 0:
        raise ValueError("Invalid process identity.")
    return result


def _status(text: str) -> dict:
    result = {}
    for line in text.splitlines():
        key, sep, value = line.partition(":")
        if sep:
            result[key] = value.strip()
    return result


def _uids(status: dict) -> tuple[int, ...]:
    values = tuple(int(value) for value in status.get("Uid", "").split())
    if len(values) != 4 or any(value < 0 for value in values):
        raise ValueError("Process ownership is unavailable.")
    return values


def scan_processes(system: System) -> dict:
    rows, notes = [], []
    try:
        entries = list(system.path("/proc").iterdir())
    except OSError as exc:
        return {"processes": [], "notes": [f"Process inventory unavailable: {exc}"]}
    try:
        page_size = os.sysconf("SC_PAGE_SIZE")
    except (AttributeError, OSError, ValueError):
        page_size = None
    denied = 0
    for entry in entries:
        if not entry.name.isdecimal():
            continue
        base = f"/proc/{entry.name}"
        stat_text = system.read(base + "/stat", 32768)
        if not stat_text:
            denied += 1
            continue
        try:
            stat = parse_stat(stat_text)
            if stat["pid"] != int(entry.name):
                continue
            status = _status(system.read(base + "/status", 65536))
            try:
                uid = _uids(status)[1]
            except ValueError:
                uid = None
            rss = None
            raw_rss = status.get("VmRSS", "").split()
            if len(raw_rss) == 2 and raw_rss[1] == "kB":
                rss = max(0, int(raw_rss[0])) * 1024
            elif page_size is not None:
                rss = max(0, stat["rss_pages"]) * page_size
            cmdline = system.read(base + "/cmdline", 16384).replace("\0", " ").strip()
            # A second stat avoids mixing memory/name data from a reused PID during a scan.
            end = parse_stat(system.read(base + "/stat", 32768))
            if end["pid"] != stat["pid"] or end["start_ticks"] != stat["start_ticks"]:
                continue
            kernel = bool(stat["flags"] & _PF_KTHREAD)
            kind = "Kernel thread" if kernel else ("System process" if uid == 0 else
                                                   "User process" if uid is not None else "Unknown")
            rows.append({
                "pid": stat["pid"], "name": stat["name"], "uid": uid,
                "start_ticks": stat["start_ticks"], "rss_bytes": rss,
                "state": _STATES.get(stat["state_code"], stat["state_code"]),
                "command": cmdline, "kind": kind,
            })
        except (ValueError, OSError):
            # Processes can naturally disappear between any two /proc reads.
            continue
    rows.sort(key=lambda row: (-(row["rss_bytes"] or 0), row["pid"]))
    if denied:
        notes.append(f"{denied} process records vanished or were inaccessible during this scan.")
    notes.append("RAM is resident memory (RSS), including shared pages; do not sum it as system RAM. "
                 "Linux does not expose a reliable application/background grouping through /proc.")
    return {"processes": rows, "notes": notes}


def stop_process(row: dict, force: bool = False) -> str:
    """Stop exactly the selected process; never fall back to PID-only kill().

    Access checks are intentionally conservative even when the caller has more privileges.
    A pidfd keeps the signal tied to its original process after PID reuse.
    """
    if sys.platform != "linux":
        raise RuntimeError("Process stopping is available only on Linux.")
    if not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
        raise RuntimeError("Safe process stopping needs Linux 5.3+ and Python pidfd support. "
                           "No PID-only fallback is used.")
    pid, start = row.get("pid"), row.get("start_ticks")
    if type(pid) is not int or type(start) is not int or pid <= 1 or start <= 0:
        raise ValueError("The selected process has no safe, complete identity.")
    caller = os.getuid()
    if caller == 0 or os.geteuid() != caller:
        raise PermissionError("Run DeviceScope as an ordinary user to stop your own processes.")
    if pid == os.getpid():
        raise PermissionError("DeviceScope cannot stop itself.")
    if row.get("uid") != caller:
        raise PermissionError("Only processes owned by the current user can be stopped.")
    if row.get("kind") == "Kernel thread" or row.get("name") in _CORE_NAMES:
        raise PermissionError("Kernel and core desktop/session processes are protected.")
    try:
        descriptor = os.pidfd_open(pid, 0)
    except ProcessLookupError:
        raise ProcessLookupError("The selected process has already exited.") from None
    except OSError as exc:
        raise RuntimeError(f"Cannot obtain a safe process handle: {exc}") from exc
    try:
        system = System()
        try:
            current = parse_stat(system.read(f"/proc/{pid}/stat", 32768))
            owners = _uids(_status(system.read(f"/proc/{pid}/status", 65536)))
        except ValueError:
            raise ProcessLookupError("The process exited or its identity is no longer readable.") from None
        if current["pid"] != pid or current["start_ticks"] != start:
            raise ProcessLookupError("The selected PID was reused; no signal was sent.")
        if current["flags"] & _PF_KTHREAD or current["name"] in _CORE_NAMES:
            raise PermissionError("Kernel and core desktop/session processes are protected.")
        if any(owner != caller for owner in owners):
            raise PermissionError("The process is not exclusively owned by the current user.")
        if current["state_code"] in {"Z", "X", "x"}:
            return f"Process {pid} has already exited; its parent has not reaped it yet."
        chosen_signal = signal.SIGKILL if force else signal.SIGTERM
        signal.pidfd_send_signal(descriptor, chosen_signal, None, 0)
        readable, _, _ = select.select([descriptor], [], [], 1.0)
        if readable:
            return f"Process {pid} stopped."
        if force:
            return f"SIGKILL sent to process {pid}; the kernel has not reported its exit yet."
        return (f"Stop requested for process {pid}, but it is still running. "
                "Refresh to check, or use Force Stop if needed.")
    except ProcessLookupError:
        raise ProcessLookupError("The selected process exited or changed identity; no other process was stopped.") from None
    finally:
        os.close(descriptor)
