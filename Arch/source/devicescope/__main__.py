"""Entry point; --report works without a display or Tk import."""
import argparse
import json
import platform
import sys

from . import __version__


def main():
    parser = argparse.ArgumentParser(description="DeviceScope Linux desktop hardware utility")
    parser.add_argument("--version", action="version", version=f"DeviceScope Linux {__version__}")
    parser.add_argument("--report", action="store_true", help="Print a real read-only device/disk/performance report as JSON; no GUI")
    parser.add_argument("--self-test", action="store_true", help="Run packaged regression tests (includes stopping only a disposable test worker)")
    args = parser.parse_args()
    if sys.platform != "linux":
        parser.exit(1, "This edition reads Linux /proc and /sys and must run on Linux. Use the Windows edition on Windows.\n")
    from .system import System
    system = System()
    if args.self_test:
        import importlib
        import pkgutil
        import unittest
        try:
            import devicescope_tests as test_package
        except ImportError:
            parser.exit(1, "Use the bundled .run file for --self-test, or run python3 -m unittest discover -s tests from source.\n")
        suite = unittest.TestSuite()
        for entry in pkgutil.iter_modules(test_package.__path__):
            if entry.name.startswith("test_"):
                suite.addTests(unittest.defaultTestLoader.loadTestsFromModule(
                    importlib.import_module(f"devicescope_tests.{entry.name}")))
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        raise SystemExit(0 if result.wasSuccessful() else 1)
    if args.report:
        import time
        from .devices import scan_devices
        from .disks import scan_disks
        from .performance import PerformanceSampler
        from .specifications import capture_specifications
        sampler = PerformanceSampler(system)
        sampler.sample()
        time.sleep(.25)
        report = {"version": __version__, "platform": platform.platform(), "inventory": scan_devices(system),
                  "performance": sampler.sample(), "storage": scan_disks(system),
                  "specifications": capture_specifications(system)}
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return
    try:
        from .gui import launch
        launch(system)
    except ImportError as exc:
        parser.exit(1, f"Cannot load the desktop UI: {exc}\nUse the bundled .run file, which includes Python and Tcl/Tk.\n")
    except Exception as exc:
        # Display a useful terminal diagnostic for X11/XWayland permission or unavailable display.
        if exc.__class__.__name__ == "TclError":
            parser.exit(1, f"Cannot open the desktop: {exc}\nRun inside your graphical desktop. Wayland sessions need XWayland. Do not use sudo.\n")
        raise


if __name__ == "__main__":
    main()
