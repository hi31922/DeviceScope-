"""Bounded, read-only Linux access shared by collectors; injectable for tests."""
from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class CommandResult:
    code: int | None
    stdout: str = ""
    stderr: str = ""

    @property
    def ok(self):
        return self.code == 0


class System:
    def __init__(self, root: str | Path = "/", commands: bool = True):
        self.root = Path(root)
        self.commands = commands

    def path(self, path: str | Path) -> Path:
        return self.root / str(path).lstrip("/")

    def read(self, path: str | Path, limit: int = 1048576) -> str:
        try:
            with self.path(path).open("r", encoding="utf-8", errors="replace") as handle:
                return handle.read(limit)
        except (OSError, ValueError):
            return ""

    def run(self, args: list[str], timeout: float = 5) -> CommandResult:
        if not self.commands:
            return CommandResult(None, stderr="Commands disabled for this data source.")
        # Never interpret a shell string or look for executables in writable cwd/PATH.
        name = args[0]
        executable = next((str(Path(folder) / name) for folder in
                           ("/usr/bin", "/usr/sbin", "/bin", "/sbin")
                           if os.access(Path(folder) / name, os.X_OK)), None)
        if not executable:
            return CommandResult(None, stderr=f"{name} is not installed.")
        env = dict(os.environ, LC_ALL="C", LANG="C", SYSTEMD_COLORS="0",
                   SYSTEMD_PAGER="cat", PAGER="cat")
        try:
            # Temporary files keep a noisy diagnostic from filling memory or blocking a pipe.
            import tempfile
            with tempfile.TemporaryFile() as out, tempfile.TemporaryFile() as err:
                process = subprocess.Popen([executable, *args[1:]], stdout=out, stderr=err,
                                           stdin=subprocess.DEVNULL, env=env)
                try:
                    process.wait(timeout=timeout)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                    return CommandResult(None, stderr=f"{name} timed out after {timeout:g}s.")
                out.seek(0)
                err.seek(0)
                return CommandResult(process.returncode, out.read(2**20).decode("utf-8", "replace"),
                                     err.read(32768).decode("utf-8", "replace"))
        except OSError as exc:
            return CommandResult(None, stderr=str(exc))


def number(value: str, default=None):
    try:
        return int(value.strip(), 0)
    except (ValueError, TypeError, AttributeError):
        return default


def human_bytes(value):
    if value is None:
        return "Unavailable"
    size = float(value)
    for suffix in ("B", "KiB", "MiB", "GiB", "TiB", "PiB"):
        if abs(size) < 1024 or suffix == "PiB":
            return f"{size:,.1f} {suffix}"
        size /= 1024
