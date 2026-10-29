"""Native single-window Linux UI. All collectors run off the Tk event thread."""
from __future__ import annotations

import copy
import json
import queue
import threading
import time
import tkinter as tk
from collections import deque
from tkinter import filedialog, messagebox, ttk

from . import __version__
from .devices import scan_devices
from .diagnostics import diagnose
from .disks import scan_disks
from .performance import PerformanceSampler
from .processes import scan_processes, stop_process
from .snapshots import compare_devices, load_snapshot, save_snapshot
from .system import System, human_bytes
from .specifications import capture_specifications

BG = "#edf2f8"
INK = "#18263b"
MUTED = "#53647a"
BLUE = "#2563eb"


def percent(value):
    return "Unavailable" if value is None else f"{value:.1f}%"


def detail_text(row):
    parts = []
    for key, value in row.items():
        if key in ("previous", "details"):
            continue
        if isinstance(value, (list, tuple)):
            value = "\n    ".join(str(item) for item in value)
        parts.append(f"{key.replace('_', ' ').title()}: {value if value is not None else 'Unavailable'}")
    if row.get("details"):
        parts.append("\nSystem properties")
        parts.extend(f"{key}: {value}" for key, value in row["details"].items())
    if row.get("previous"):
        parts.append("\nPrevious snapshot\n" + json.dumps(row["previous"], ensure_ascii=False, indent=2))
    return "\n".join(parts)


class DataTable(ttk.Frame):
    def __init__(self, parent, columns):
        super().__init__(parent)
        self.columns = columns
        self.rows = {}
        self.order = []
        self.sort_column = None
        self.reverse = False
        self.tree = ttk.Treeview(self, columns=[c[0] for c in columns], show="headings", selectmode="browse")
        for key, title, width in columns:
            self.tree.heading(key, text=title, command=lambda k=key: self.sort(k))
            self.tree.column(key, width=width, minwidth=65, stretch=True)
        vertical = ttk.Scrollbar(self, orient="vertical", command=self.tree.yview)
        horizontal = ttk.Scrollbar(self, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal.grid(row=1, column=0, sticky="ew")
        self.rowconfigure(0, weight=1)
        self.columnconfigure(0, weight=1)
        self.formatters = {}

    def selected(self):
        selection = self.tree.selection()
        return self.rows.get(selection[0]) if selection else None

    def set_rows(self, rows, identity="id"):
        selection = self.tree.selection()
        selected_id = selection[0] if selection else None
        view = self.tree.yview()
        self.rows = {str(row[identity]): row for row in rows}
        self.order = list(self.rows)
        self.tree.delete(*self.tree.get_children())
        for iid, row in self.rows.items():
            values = []
            for key, _, _ in self.columns:
                value = row.get(key)
                values.append(self.formatters[key](value) if key in self.formatters
                              else "Unavailable" if value is None else str(value))
            self.tree.insert("", "end", iid=iid, values=values)
        if self.sort_column:
            self._sort()
        if selected_id in self.rows:
            self.tree.selection_set(selected_id)
            self.tree.focus(selected_id)
        if view:
            self.tree.yview_moveto(view[0])

    def sort(self, key):
        self.reverse = not self.reverse if self.sort_column == key else False
        self.sort_column = key
        self._sort()

    def _sort(self):
        def sort_value(iid):
            value = self.rows[iid].get(self.sort_column)
            if value is None:
                return (1, 0, "")
            if isinstance(value, (float, int)):
                return (0, 0, value)
            return (0, 1, str(value).casefold())
        known = [iid for iid in self.rows if self.rows[iid].get(self.sort_column) is not None]
        missing = [iid for iid in self.rows if self.rows[iid].get(self.sort_column) is None]
        ordered = sorted(known, key=sort_value, reverse=self.reverse) + missing
        for index, iid in enumerate(ordered):
            self.tree.move(iid, "", index)


class Readout(ttk.Frame):
    def __init__(self, parent, height=9):
        super().__init__(parent)
        self.text = tk.Text(self, wrap="word", height=height, state="disabled", bg="white", fg=INK,
                            insertbackground=INK, relief="flat", padx=14, pady=10, font=("TkDefaultFont", 10))
        scrollbar = ttk.Scrollbar(self, command=self.text.yview)
        self.text.configure(yscrollcommand=scrollbar.set)
        self.text.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")

    def set(self, text):
        self.text.configure(state="normal")
        self.text.delete("1.0", "end")
        self.text.insert("1.0", text)
        self.text.configure(state="disabled")


class Graph(tk.Canvas):
    def __init__(self, parent, title, color, unit="%"):
        super().__init__(parent, bg="white", height=160, highlightthickness=1, highlightbackground="#dbe3ef")
        self.title, self.color, self.unit = title, color, unit
        self.history = deque(maxlen=61)
        self.bind("<Configure>", lambda _: self.redraw())

    def add(self, value, timestamp=None):
        self.history.append((time.monotonic() if timestamp is None else timestamp, value))
        self.redraw()

    def redraw(self):
        self.delete("all")
        width, height = self.winfo_width(), self.winfo_height()
        if width < 30:
            return
        last = self.history[-1][1] if self.history else None
        label = "Unavailable" if last is None else f"{last:.1f}{self.unit}"
        self.create_text(14, 16, anchor="w", text=self.title, fill=MUTED, font=("TkDefaultFont", 10))
        self.create_text(width-14, 16, anchor="e", text=label, fill=INK, font=("TkDefaultFont", 13, "bold"))
        left, right, top, bottom = 14, width-14, 42, height-22
        minimum = min(0, min((v for _, v in self.history if v is not None), default=0))
        maximum = max(100, max((v for _, v in self.history if v is not None), default=100))
        for fraction in (0, .5, 1):
            y = top + fraction * (bottom-top)
            self.create_line(left, y, right, y, fill="#edf2f8")
        self.create_text(left, height-10, text="60 seconds", anchor="w", fill=MUTED, font=("TkDefaultFont", 8))
        if not self.history:
            return
        end = self.history[-1][0]
        points = []
        previous_time = None
        def flush():
            if len(points) >= 4:
                self.create_line(*points, fill=self.color, width=2)
            points.clear()
        for stamp, value in self.history:
            if stamp < end-60 or value is None or (previous_time is not None and stamp-previous_time > 3):
                flush()
            if stamp >= end-60 and value is not None:
                x = right-(end-stamp)/60*(right-left)
                y = bottom-(value-minimum)/(maximum-minimum)*(bottom-top)
                points.extend((x, y))
                self.create_oval(x-1, y-1, x+1, y+1, fill=self.color, outline="")
            previous_time = stamp
        flush()


class DeviceScope:
    def __init__(self, root, system=None):
        self.root = root
        self.system = system or System()
        self.closed = False
        self.after_ids = set()
        self.jobs = set()
        self.results = queue.Queue()
        self.devices = []
        self.previous = None
        self.processes = []
        self.disk_data = {}
        self.specification_data = None
        self.sensors = {}
        self.sampler = PerformanceSampler(self.system)
        self.diagnostic_generation = 0
        self.process_generation = 0
        self.last_process = self.last_disk = 0
        self.last_performance = 0
        self._theme()
        root.title(f"DeviceScope {__version__} | Linux")
        root.geometry("1180x800")
        root.minsize(840, 620)
        root.configure(bg=BG)
        try:
            import importlib.resources
            image_data = importlib.resources.files("devicescope").joinpath("icon.png").read_bytes()
            import base64
            icon = tk.PhotoImage(data=base64.b64encode(image_data))
            self.window_icon = icon.subsample(max(1, icon.width() // 64))
            root.iconphoto(True, self.window_icon)
        except (OSError, tk.TclError):
            pass
        root.protocol("WM_DELETE_WINDOW", self.close)
        header = tk.Frame(root, bg="#10213a", padx=22, pady=16)
        header.pack(fill="x")
        tk.Label(header, text="DeviceScope", font=("TkDefaultFont", 23, "bold"), fg="white", bg="#10213a").pack(side="left")
        tk.Label(header, text=f"LINUX  /  {__version__}", font=("TkDefaultFont", 10, "bold"), fg="#9fc4ff", bg="#10213a", padx=18).pack(side="left")
        tk.Label(header, text="Know what your computer is running.", fg="#bdcbe1", bg="#10213a").pack(side="right")
        self.tabs = ttk.Notebook(root, padding=12, style="Main.TNotebook")
        self.tabs.pack(fill="both", expand=True)
        self.device_tab = self._tab("Devices & Drivers")
        self.process_tab = self._tab("Processes & RAM")
        self.diagnostic_tab = self._tab("Driver Diagnostics")
        self.performance_tab = self._tab("Live Performance")
        self.disk_tab = self._tab("Disk Health")
        self.specification_tab = self._tab("System Specifications")
        self._devices_ui()
        self._process_ui()
        self._diagnostic_ui()
        self._performance_ui()
        self._disk_ui()
        self._specifications_ui()
        self.tabs.bind("<Configure>", self._fit_tab_strip)
        self.footer = tk.StringVar(value="Local data only. Reading Linux hardware inventory...")
        ttk.Label(root, textvariable=self.footer, padding=(18, 7), foreground=MUTED).pack(fill="x")
        self.tabs.bind("<<NotebookTabChanged>>", lambda _: self.tick(reschedule=False))
        self._schedule(50, self._poll)
        self._schedule(100, self.refresh_devices)
        self._schedule(1000, self.tick)

    def _theme(self):
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure(".", background=BG, foreground=INK, font=("TkDefaultFont", 10))
        style.configure("TNotebook", background=BG, borderwidth=0)
        style.configure("TNotebook.Tab", padding=(15, 10), background="#dfe7f2")
        style.map("TNotebook.Tab", background=[("selected", "white")], foreground=[("selected", BLUE)])
        style.configure("TButton", padding=(12, 7), background="#dfe8f5")
        style.map("TButton", background=[("active", "#c9dcf6")])
        style.configure("TEntry", fieldbackground="white", foreground="#111827", padding=6)
        style.configure("TCombobox", fieldbackground="white", foreground="#111827", padding=4)
        style.map("TCombobox", fieldbackground=[("readonly", "white")], foreground=[("readonly", "#111827")])
        style.configure("Treeview", background="white", fieldbackground="white", foreground="#111827", rowheight=29, borderwidth=0)
        style.configure("Treeview.Heading", background="#e0e8f3", foreground=INK, padding=7, font=("TkDefaultFont", 10, "bold"))
        style.map("Treeview", background=[("selected", "#c9dfff")], foreground=[("selected", "#10213a")])
        style.configure("Title.TLabel", font=("TkDefaultFont", 14, "bold"))
        style.configure("Note.TLabel", foreground=MUTED)

    def _tab(self, name):
        frame = ttk.Frame(self.tabs, padding=10)
        self.tabs.add(frame, text=name)
        return frame

    def _note(self, parent, variable):
        label = ttk.Label(parent, textvariable=variable, style="Note.TLabel", wraplength=1040, padding=(0, 7))
        label.pack(fill="x")
        parent.bind("<Configure>", lambda event: label.configure(wraplength=max(300, event.width-30)), add=True)
        return label

    def _submit(self, key, task, done):
        if self.closed or key in self.jobs:
            return False
        self.jobs.add(key)
        def worker():
            try:
                self.results.put((key, done, task(), None))
            except Exception as exc:
                self.results.put((key, done, None, exc))
        threading.Thread(target=worker, name=f"DeviceScope-{key}", daemon=True).start()
        return True

    def _schedule(self, delay, callback):
        if self.closed:
            return
        identifier = None
        def dispatch():
            self.after_ids.discard(identifier)
            if not self.closed:
                callback()
        identifier = self.root.after(delay, dispatch)
        self.after_ids.add(identifier)

    def _poll(self):
        if self.closed:
            return
        try:
            while True:
                key, done, result, error = self.results.get_nowait()
                self.jobs.discard(key)
                if error is not None:
                    self.footer.set(f"{key}: {error}. Previous successful data was kept.")
                else:
                    try:
                        done(result)
                    except Exception as exc:
                        self.footer.set(f"Could not display {key}: {exc}")
        except queue.Empty:
            pass
        self._schedule(60, self._poll)

    def _devices_ui(self):
        bar = ttk.Frame(self.device_tab)
        bar.pack(fill="x")
        ttk.Button(bar, text="Refresh devices", command=self.refresh_devices).pack(side="left")
        ttk.Button(bar, text="Export snapshot", command=self.export).pack(side="left", padx=6)
        ttk.Button(bar, text="Compare snapshot", command=self.compare).pack(side="left")
        ttk.Button(bar, text="Clear comparison", command=self.clear_comparison).pack(side="left", padx=6)
        self.device_search = tk.StringVar()
        ttk.Entry(bar, textvariable=self.device_search, width=23).pack(side="right")
        ttk.Label(bar, text="Search ").pack(side="right")
        filters = ttk.Frame(self.device_tab, padding=(0, 8))
        filters.pack(fill="x")
        self.device_category = tk.StringVar(value="All categories")
        self.category = ttk.Combobox(filters, textvariable=self.device_category, state="readonly", width=24, values=["All categories"])
        self.category.pack(side="left")
        self.attention = tk.BooleanVar()
        self.changes_only = tk.BooleanVar()
        ttk.Checkbutton(filters, text="Needs attention", variable=self.attention, command=self.filter_devices).pack(side="left", padx=12)
        ttk.Checkbutton(filters, text="Changes only", variable=self.changes_only, command=self.filter_devices).pack(side="left")
        self.device_note = tk.StringVar(value="Scanning actual sysfs device records...")
        self._note(self.device_tab, self.device_note)
        split = ttk.Panedwindow(self.device_tab, orient="vertical")
        split.pack(fill="both", expand=True)
        self.device_table = DataTable(split, [("name", "Device", 260), ("category", "Category", 110), ("device_type", "Device Type", 145), ("driver", "Bound driver", 120), ("status", "Status", 190), ("change", "Change", 85)])
        self.device_details = Readout(split, height=10)
        self.device_details.set("Select a device to view its real hardware IDs, sysfs path and classification evidence.")
        split.add(self.device_table, weight=3)
        split.add(self.device_details, weight=2)
        self.device_table.tree.bind("<<TreeviewSelect>>", self._select_device)
        self.device_search.trace_add("write", lambda *_: self.filter_devices())
        self.category.bind("<<ComboboxSelected>>", lambda _: self.filter_devices())

    def refresh_devices(self):
        self.footer.set("Reading Linux hardware inventory in the background...")
        def done(report):
            self.devices = report["devices"]
            categories = ["All categories"]+sorted({str(r.get("category", "Unknown")) for r in self.devices})
            self.category.configure(values=categories)
            if self.device_category.get() not in categories:
                self.device_category.set("All categories")
            self.filter_devices()
            self.diagnostic_table.set_rows(self.devices)
            bt = report.get("bluetooth", {})
            notes = report.get("notes", [])
            self.device_note.set(f"{len(self.devices)} devices. " + " ".join(notes))
            self.diagnostic_note.set("Select a device. Linux reports driver binding and kernel messages; Windows Device Manager codes do not apply here.")
            self.bluetooth_readout.set("Bluetooth stack and frontend\n\n" + detail_text(bt))
            self.footer.set(f"Inventory updated at {time.strftime('%H:%M:%S')}. Data stays on this computer.")
        self._submit("Device inventory", lambda: scan_devices(self.system), done)

    def filter_devices(self):
        rows = compare_devices(self.devices, self.previous) if self.previous is not None else [dict(r, change="") for r in self.devices]
        query = self.device_search.get().strip().casefold()
        category = self.device_category.get()
        filtered = [r for r in rows
                    if (not query or query in json.dumps(r, ensure_ascii=False).casefold())
                    and (category == "All categories" or r.get("category") == category)
                    and (not self.attention.get() or r.get("needs_attention"))
                    and (not self.changes_only.get() or r.get("change") in ("Added", "Changed", "Removed"))]
        self.device_table.set_rows(filtered)

    def _select_device(self, _=None):
        row = self.device_table.selected()
        self.device_details.set(detail_text(row) if row else
                                "Select a device to view its real hardware IDs, sysfs path and classification evidence.")

    def export(self):
        if not self.devices:
            self.footer.set("Wait for a successful scan before exporting.")
            return
        path = filedialog.asksaveasfilename(parent=self.root, title="Export device snapshot", defaultextension=".json", filetypes=[("JSON snapshot", "*.json")])
        if path:
            devices = copy.deepcopy(self.devices)
            self._submit("Snapshot export", lambda: save_snapshot(path, devices), lambda _: self.footer.set("Device snapshot saved. It contains hardware identifiers; processes were not exported."))

    def compare(self):
        path = filedialog.askopenfilename(parent=self.root, title="Compare Linux device snapshot", filetypes=[("JSON snapshot", "*.json")])
        if path:
            def done(previous):
                self.previous = previous
                self.filter_devices()
                self.footer.set("Snapshot comparison loaded. Select a changed device for previous values.")
            self._submit("Snapshot import", lambda: load_snapshot(path), done)

    def clear_comparison(self):
        self.previous = None
        self.changes_only.set(False)
        self.filter_devices()

    def _process_ui(self):
        bar = ttk.Frame(self.process_tab)
        bar.pack(fill="x")
        ttk.Button(bar, text="Refresh processes", command=self.refresh_processes).pack(side="left")
        self.process_auto = tk.BooleanVar(value=True)
        ttk.Checkbutton(bar, text="Refresh every 3 seconds", variable=self.process_auto).pack(side="left", padx=12)
        self.process_search = tk.StringVar()
        ttk.Entry(bar, textvariable=self.process_search, width=28).pack(side="right")
        ttk.Label(bar, text="Search ").pack(side="right")
        self.process_note = tk.StringVar(value="Select a process to reveal the stop buttons. Only your own user processes can be stopped.")
        self._note(self.process_tab, self.process_note)
        self.process_table = DataTable(self.process_tab, [("name", "Process", 210), ("pid", "PID", 80), ("kind", "Type", 140), ("rss_bytes", "RAM (RSS)", 120), ("state", "State", 80), ("command", "Command", 370)])
        self.process_table.formatters["rss_bytes"] = human_bytes
        self.process_table.sort_column, self.process_table.reverse = "rss_bytes", True
        self.process_table.pack(fill="both", expand=True)
        self.process_actions = ttk.Frame(self.process_tab, padding=(0, 9))
        self.process_selected = tk.StringVar()
        ttk.Label(self.process_actions, textvariable=self.process_selected).pack(side="left", fill="x", expand=True)
        self.stop_button = ttk.Button(self.process_actions, text="Stop Process", command=lambda: self.stop_selected(False))
        self.stop_button.pack(side="left")
        self.force_button = ttk.Button(self.process_actions, text="Force Stop", command=lambda: self.stop_selected(True))
        self.force_button.pack(side="left", padx=7)
        self.stop_result = tk.StringVar(value="Stop Process sends SIGTERM. Force Stop sends SIGKILL; unsaved work can be lost. No automatic elevation.")
        self.stop_result_label = self._note(self.process_tab, self.stop_result)
        self.process_table.tree.bind("<<TreeviewSelect>>", lambda _: self._select_process())
        self.process_search.trace_add("write", lambda *_: self.filter_processes())

    def refresh_processes(self):
        generation = self.process_generation
        def done(report):
            if generation != self.process_generation:
                self.last_process = 0
                self.refresh_processes()
                return
            self.processes = report["processes"]
            self.filter_processes()
            self.process_note.set(f"{len(self.processes)} processes. RSS includes shared pages; do not sum it as total system RAM. " + " ".join(report.get("notes", [])))
        if self._submit("Process list", lambda: scan_processes(self.system), done):
            self.last_process = time.monotonic()

    def filter_processes(self):
        query = self.process_search.get().strip().casefold()
        rows = []
        for process in self.processes:
            if not query or query in f"{process.get('pid')} {process.get('name')} {process.get('command')} {process.get('kind')}".casefold():
                rows.append(dict(process, id=f"{process['pid']}:{process['start_ticks']}"))
        self.process_table.set_rows(rows)
        self._select_process()

    def _select_process(self):
        row = self.process_table.selected()
        if row:
            self.process_selected.set(f"Selected: {row['name']}  |  PID {row['pid']}")
            self.process_actions.pack(fill="x", before=self.stop_result_label)
        else:
            self.process_actions.pack_forget()

    def stop_selected(self, force):
        row = self.process_table.selected()
        if not row or "Stop process" in self.jobs:
            return
        target = copy.deepcopy(row)
        if force and not messagebox.askyesno("Force Stop", f"Force-stop {target['name']} (PID {target['pid']})?\nUnsaved work in this process will be lost.", parent=self.root):
            return
        self.process_generation += 1
        self.stop_button.configure(state="disabled")
        self.force_button.configure(state="disabled")
        self.stop_result.set(f"Stopping {target['name']} (PID {target['pid']})...")
        def task():
            try:
                return True, stop_process(target, force=force)
            except (OSError, RuntimeError, ValueError) as exc:
                return False, str(exc)
        def done(result):
            success, message = result
            # Invalidate lists that started while the signal/exit check ran too.
            self.process_generation += 1
            self.stop_result.set(message)
            self.stop_button.configure(state="normal")
            self.force_button.configure(state="normal")
            # A delivered SIGTERM is not proof of exit. A fresh /proc scan decides
            # which rows disappear; force-stop remains available if it was ignored.
            self.last_process = 0
            self.refresh_processes()
        self._submit("Stop process", task, done)

    def _diagnostic_ui(self):
        bar = ttk.Frame(self.diagnostic_tab)
        bar.pack(fill="x")
        ttk.Button(bar, text="Refresh devices", command=self.refresh_devices).pack(side="left")
        ttk.Button(bar, text="Inspect selected", command=self.inspect_selected).pack(side="left", padx=7)
        self.diagnostic_note = tk.StringVar(value="Select a device to inspect binding, module dependencies and recent kernel messages.")
        self._note(self.diagnostic_tab, self.diagnostic_note)
        split = ttk.Panedwindow(self.diagnostic_tab, orient="vertical")
        split.pack(fill="both", expand=True)
        self.diagnostic_table = DataTable(split, [("name", "Device", 280), ("device_type", "Device Type", 160), ("driver", "Bound driver", 150), ("status", "Status", 320)])
        details_tabs = ttk.Notebook(split)
        self.diagnostic_readout = Readout(details_tabs, 13)
        self.bluetooth_readout = Readout(details_tabs, 13)
        details_tabs.add(self.diagnostic_readout, text="Selected device")
        details_tabs.add(self.bluetooth_readout, text="Bluetooth stack")
        split.add(self.diagnostic_table, weight=2)
        split.add(details_tabs, weight=3)
        self.diagnostic_table.tree.bind("<<TreeviewSelect>>", lambda _: self.inspect_selected())

    def inspect_selected(self):
        row = self.diagnostic_table.selected()
        if not row:
            self.diagnostic_generation += 1
            self.diagnostic_readout.set("Select a current device to inspect its driver and kernel messages.")
            return
        self.diagnostic_generation += 1
        generation = self.diagnostic_generation
        self.diagnostic_readout.set(f"Inspecting {row['name']}...\nReading declared dependencies and bounded kernel log; no changes are made.")
        def done(report):
            if generation != self.diagnostic_generation:
                return
            self.diagnostic_readout.set(report["summary"] + "\n\n" + detail_text(report.get("details", {}))
                                        + "\n\nRecent matching kernel messages\n" + report.get("events", "Unavailable")
                                        + "\n\n" + "\n".join(report.get("notes", [])))
        # One live inspection at a time, then inspect the newest selection if it changed.
        def task():
            try:
                return diagnose(self.system, copy.deepcopy(row)), None
            except Exception as exc:
                return None, exc
        def completed(result):
            report, error = result
            if error is None:
                done(report)
            elif generation == self.diagnostic_generation:
                self.diagnostic_readout.set(f"Could not inspect {row['name']}: {error}")
            if generation != self.diagnostic_generation:
                self.inspect_selected()
        self._submit("Driver inspection", task, completed)

    def _performance_ui(self):
        canvas = tk.Canvas(self.performance_tab, bg=BG, highlightthickness=0)
        scroll = ttk.Scrollbar(self.performance_tab, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)
        body = ttk.Frame(canvas)
        window = canvas.create_window((0, 0), window=body, anchor="nw")
        canvas.bind("<Configure>", lambda event: canvas.itemconfigure(window, width=event.width))
        body.bind("<Configure>", lambda _: canvas.configure(scrollregion=canvas.bbox("all")))
        self.performance_note = tk.StringVar(value="Real CPU, RAM, supported GPU load and readable sensors. Missing readings remain unavailable.")
        self._note(body, self.performance_note)
        grid = ttk.Frame(body)
        grid.pack(fill="x")
        grid.columnconfigure((0, 1), weight=1)
        self.cpu_graph = Graph(grid, "CPU", BLUE)
        self.ram_graph = Graph(grid, "RAM", "#7c3aed")
        self.gpu_graph = Graph(grid, "GPU", "#059669")
        self.temperature_graph = Graph(grid, "Selected sensor", "#e08716", " °C")
        for index, graph in enumerate((self.cpu_graph, self.ram_graph, self.gpu_graph, self.temperature_graph)):
            graph.grid(row=index//2, column=index%2, sticky="nsew", padx=4, pady=4)
        sensor_bar = ttk.Frame(body, padding=(0, 8))
        sensor_bar.pack(fill="x")
        ttk.Label(sensor_bar, text="Temperature sensor ").pack(side="left")
        self.sensor_choice = tk.StringVar()
        self.sensor_combo = ttk.Combobox(sensor_bar, textvariable=self.sensor_choice, state="readonly", width=60)
        self.sensor_combo.pack(side="left", fill="x", expand=True)
        self.sensor_combo.bind("<<ComboboxSelected>>", lambda _: self.temperature_graph.history.clear())
        self.performance_readout = Readout(body, 7)
        self.performance_readout.pack(fill="both", expand=True)

    def refresh_performance(self):
        if "Live performance" in self.jobs:
            return
        now = time.monotonic()
        if now - self.last_performance > 3:
            # Returning to the tab must not label a many-minute CPU average as
            # a current one-second reading. Prime it again and leave a gap.
            self.sampler.previous_cpu = None
        self.last_performance = now
        def done(report):
            stamp = time.monotonic()
            self.cpu_graph.add(report.get("cpu_percent"), stamp)
            self.ram_graph.add(report.get("ram_percent"), stamp)
            gpu_loads = [g["utilization"] for g in report.get("gpus", []) if g.get("utilization") is not None]
            self.gpu_graph.add(max(gpu_loads) if gpu_loads else None, stamp)
            self.sensors = {f"{s['name']} [{s['id']}]": s for s in report.get("sensors", [])}
            choices = list(self.sensors)
            self.sensor_combo.configure(values=choices)
            if self.sensor_choice.get() not in self.sensors:
                self.sensor_choice.set(choices[0] if choices else "No readable sensor")
                self.temperature_graph.history.clear()
            selected = self.sensors.get(self.sensor_choice.get(), {})
            self.temperature_graph.add(selected.get("celsius"), stamp)
            notes = report.get("notes", [])
            self.performance_note.set(" ".join(notes) or "Live measurements; graphs cover the last 60 seconds. No sensor values are estimated.")
            lines = [f"RAM: {human_bytes(report.get('ram_used'))} used / {human_bytes(report.get('ram_total'))}"]
            for gpu in report.get("gpus", []):
                lines.append(f"{gpu['name']}: {percent(gpu.get('utilization'))}  |  VRAM {human_bytes(gpu.get('memory_used'))} / {human_bytes(gpu.get('memory_total'))}")
            for sensor in report.get("sensors", []):
                lines.append(f"{sensor['name']}: {sensor['celsius']:.1f} °C" if sensor.get("celsius") is not None else f"{sensor['name']}: Unavailable")
            self.performance_readout.set("\n".join(lines))
        self._submit("Live performance", self.sampler.sample, done)

    def _disk_ui(self):
        bar = ttk.Frame(self.disk_tab)
        bar.pack(fill="x")
        ttk.Button(bar, text="Refresh disks", command=self.refresh_disks).pack(side="left")
        self.disk_auto = tk.BooleanVar(value=False)
        ttk.Checkbutton(bar, text="Refresh every 30 seconds", variable=self.disk_auto).pack(side="left", padx=12)
        self.disk_note = tk.StringVar(value="SMART is optional and may require privileges. Missing access is not a healthy result.")
        self._note(self.disk_tab, self.disk_note)
        split = ttk.Panedwindow(self.disk_tab, orient="vertical")
        split.pack(fill="both", expand=True)
        self.disk_table = DataTable(split, [("name", "Disk", 100), ("model", "Model", 260), ("size_bytes", "Capacity", 130), ("health", "Reported health", 220), ("temperature_c", "Temperature °C", 125)])
        self.disk_table.formatters["size_bytes"] = human_bytes
        self.disk_readout = Readout(split, 8)
        self.volume_table = DataTable(split, [("mount", "Mounted filesystem", 260), ("filesystem", "Type", 110), ("total_bytes", "Capacity", 140), ("used_bytes", "Used", 140), ("available_bytes", "Available", 140), ("percent", "Used %", 100)])
        for key in ("total_bytes", "used_bytes", "available_bytes"):
            self.volume_table.formatters[key] = human_bytes
        self.volume_table.formatters["percent"] = percent
        split.add(self.disk_table, weight=2)
        split.add(self.disk_readout, weight=2)
        split.add(self.volume_table, weight=2)
        self.disk_table.tree.bind("<<TreeviewSelect>>", lambda _: self._select_disk())

    def refresh_disks(self):
        def done(report):
            self.disk_data = report
            self.disk_table.set_rows(report["disks"])
            self.volume_table.set_rows([dict(v, id=f"{v['mount']}:{index}") for index, v in enumerate(report["volumes"])])
            self.disk_note.set(" ".join(report.get("notes", [])) or "Raw device-reported health; no universal health percentage is invented.")
            self._select_disk()
        if self._submit("Disk health", lambda: scan_disks(self.system), done):
            self.last_disk = time.monotonic()

    def _select_disk(self):
        row = self.disk_table.selected()
        self.disk_readout.set(detail_text(row) if row else "Select a disk to view available SMART data and collection notes. Filesystem space appears below.")

    def _fit_tab_strip(self, _=None):
        # Keep the additional section reachable at the existing minimum size.
        compact = self.tabs.winfo_width() < 1050
        if compact != getattr(self, "_compact_tabs", None):
            self._compact_tabs = compact
            ttk.Style(self.root).configure("Main.TNotebook.Tab",
                padding=(5 if compact else 15, 10),
                font=("TkDefaultFont", 9 if compact else 10))

    def _specifications_ui(self):
        bar = ttk.Frame(self.specification_tab)
        bar.pack(fill="x")
        ttk.Button(bar, text="Refresh specifications", command=self.refresh_specifications).pack(side="left")
        self.copy_specifications_button = ttk.Button(bar, text="Copy specifications", command=self.copy_specifications, state="disabled")
        self.copy_specifications_button.pack(side="left", padx=(8, 0))
        ttk.Label(bar, text="System Specifications", style="Title.TLabel", padding=(14, 0)).pack(side="left")
        self.specification_note = tk.StringVar(value="Read-only system and firmware information. Unsupported or restricted readings remain unavailable.")
        self._note(self.specification_tab, self.specification_note)
        self.specification_readout = Readout(self.specification_tab, height=22)
        self.specification_readout.pack(fill="both", expand=True)
        self.specification_readout.set("Open this section to read operating system, CPU, all detected GPUs, installed memory, motherboard, firmware, storage and display specifications.")

    def copy_specifications(self):
        if self.specification_data is None:
            self.specification_note.set("Wait for specifications to finish loading before copying.")
            return False
        try:
            # Text.get reads the entire report, regardless of scroll or selection.
            text = self.specification_readout.text.get("1.0", "end-1c")
            self.root.clipboard_clear()
            self.root.clipboard_append(text)
        except tk.TclError as exc:
            self.specification_note.set(f"Could not copy specifications: {exc}")
            return False
        self.specification_note.set("All specifications copied to clipboard.")
        return True

    def refresh_specifications(self):
        def done(report):
            self.specification_data = report
            parts = []
            for section in report.get("sections", []):
                lines = [section["title"]]
                lines.extend(f"{row['label']}: {row.get('value') or 'Unavailable'}"
                             for row in section.get("rows", []))
                parts.append("\n".join(lines))
            notes = report.get("notes", [])
            if notes:
                parts.append("Collection notes\n" + "\n".join(notes))
            self.specification_readout.set("\n\n".join(parts))
            self.copy_specifications_button.configure(state="normal")
            self.specification_note.set("Updated " + time.strftime("%H:%M:%S") + ". Values come from OS, firmware and device records; missing fields are not estimated.")
        self._submit("System specifications", lambda: capture_specifications(self.system), done)

    def tick(self, reschedule=True):
        if self.closed:
            return
        selected = self.tabs.index(self.tabs.select())
        now = time.monotonic()
        if selected == 1 and (not self.processes or (self.process_auto.get() and now-self.last_process >= 3)):
            self.refresh_processes()
        elif selected == 3:
            self.refresh_performance()
        elif selected == 4 and (not self.disk_data or (self.disk_auto.get() and now-self.last_disk >= 30)):
            self.refresh_disks()
        elif selected == 5 and self.specification_data is None:
            self.refresh_specifications()
        if reschedule:
            self._schedule(1000, self.tick)

    def close(self):
        if self.closed:
            return
        self.closed = True
        for identifier in tuple(self.after_ids):
            try:
                self.root.after_cancel(identifier)
            except tk.TclError:
                pass
        self.after_ids.clear()
        self.root.destroy()


def launch(system=None):
    root = tk.Tk()
    DeviceScope(root, system)
    root.mainloop()
