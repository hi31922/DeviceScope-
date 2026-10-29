# DeviceScope for Arch Linux 0.8.0

A native, English-language Linux desktop utility with six sections in one window. The Windows application is separate.

## Arch download

Copy **DeviceScope-0.8.0-linux-x86_64.run** onto your Arch Linux machine. In the folder containing it:

```sh
sh ./DeviceScope-0.8.0-linux-x86_64.run
```

Alternatively mark it executable in your file manager, or run `chmod +x DeviceScope-0.8.0-linux-x86_64.run`, then launch it. Run as your ordinary desktop user. No system Python, pip, FUSE, web server, internet connection or installation is required. Python and Tcl/Tk are included in the single file. It extracts into a private temporary directory, checks the payload SHA-256 and removes its temporary files on exit.

This folder contains the **x86-64 Arch Linux** release. It needs an Intel/AMD 64-bit CPU and glibc. The bundled runtime targets glibc 2.17 or newer. The Tk desktop requires X11 or **XWayland** inside a Wayland session and readable desktop fonts. The inspected runtime statically includes X11/xcb code; its external ELF dependencies are the glibc family and Linux loader. Hardware support depends on the running Linux kernel and installed drivers.

On systems mounting `/tmp` with `noexec`, choose a private executable temporary directory, e.g. `mkdir -m 700 "$HOME/devicescope-tmp"` then `TMPDIR="$HOME/devicescope-tmp" sh ./DeviceScope-0.8.0-linux-x86_64.run`.

## Features

- **Devices & Drivers:** real kernel PCI/USB vendor/product/subsystem IDs, local `pci.ids`/`usb.ids` product names when installed, network interfaces, Bluetooth adapters, block/input/audio devices and system buses. Driver binding, module alias, sysfs topology, device type and classification evidence remain visible. Search and category/attention filters, JSON snapshot export and previous/current comparisons are included. A hardware-backed object inside a VM can still be emulated; built-in versus external placement is not guessed.
- **Processes & RAM:** actual PID, name, user/system/kernel role, command, state and resident memory from `/proc`. Numeric RAM sorting, search and optional refresh every three seconds. RSS includes shared pages and must not be summed as system RAM. A selected row reveals **Stop Process** (SIGTERM) and **Force Stop** (SIGKILL, confirmed in the UI). Only the selected process is signalled, using a retained pidfd and verified creation ticks and ownership. Core desktop/session processes, kernel threads, other users, root and DeviceScope itself are protected. Process stopping requires Linux 5.3+ with pidfd support; there is no race-prone PID-only fallback. A delivered signal is not reported as a completed exit unless the kernel confirms it.
- **Driver Diagnostics:** actual bound driver/module and `modinfo` metadata when available, declared module dependencies, firmware-file evidence and exact-identity matching against a bounded kernel log. No driver installation, firmware changes, module loading, or automatic privilege elevation. Missing files may be optional; missing binding alone is not declared a broken driver. Linux does **not** have Windows Device Manager codes such as Code 28. The attention filter flags explicit evidence such as kernel-denied USB authorization; it is not a complete missing-driver detector.
- **Live Performance:** real interval CPU activity and `MemAvailable`-based RAM, supported AMD/kernel GPU busy counters, NVIDIA telemetry when the installed driver provides `nvidia-smi`, and labelled hwmon/thermal sensors. The displayed GPU summary is the busiest reported GPU, not an invented sum. Intel GPU load remains unavailable when the kernel has no supported counter. NVIDIA data refreshes at most every five seconds; other UI samples are around one second while visible. Graphs cover 60 seconds and leave gaps for unavailable values. Sensors retain kernel names; thermal zones are not assumed to be CPU cores.
- **Disk Health:** sysfs disk inventory, local filesystem capacity/used/available space, optional read-only `smartctl` JSON showing reported health, temperature, ATA raw attributes and NVMe counters. Health warnings and command/permission failures are distinct. No universal health percentage is invented. SMART is not mapped to guessed underlying disks; USB/RAID bridges, VM storage and ordinary-user permissions may prevent health access. Read-only SMART requests try to skip disks in standby, but transport autodetection may wake some bridges. Known remote/userspace filesystems are skipped to avoid blocking on network storage. Filesystem capacity pools are deduplicated; `Available` excludes privileged reserved blocks.

**Bluetooth:** Blueman is a frontend for **BlueZ**, not a replacement stack. DeviceScope separately checks kernel adapters, the BlueZ process/service/D-Bus owner and installed/running Blueman. It does not assume a manager based on the distribution name, start discovery, pair devices, or change Bluetooth settings.

All input fields and white tables use dark text. The supplied computer/magnifier artwork is used only as the window icon, not in the content header.

## Optional system tools

The app works without these tools; associated details say unavailable if absent or denied. Install them only if you want the extra information, using Arch's package manager:

| Distribution | Optional packages |
| --- | --- |
| Arch | `smartmontools`, `kmod`, `hwdata` |

Names and ownership of the ID database packages vary by release. `journalctl`/`systemctl`/`busctl` are used when systemd is present, with readable kernel-ring-buffer fallback for events. `nvidia-smi` comes from the appropriate official NVIDIA driver, not an app download. Do not run the whole UI as root to obtain SMART or log access. Restricted information is explicitly unavailable.

## Arch WSL and verification

Inside your Arch terminal, from this project's Arch release folder:

```sh
sh ./DeviceScope-0.8.0-linux-x86_64.run --self-test
sh ./DeviceScope-0.8.0-linux-x86_64.run --report > devicescope-report.json
sh ./DeviceScope-0.8.0-linux-x86_64.run
```

`--self-test` includes fixture/parser regressions and, on an ordinary Linux account with pidfd support, stops only a disposable test worker and verifies its sibling remains alive. It never selects an existing user application. GUI checks require a display and skip in a headless session. `--report` reads actual data without requiring a display; it contains device identifiers. WSL reports its virtual Linux environment, not a complete Windows physical-hardware inventory. Physical PCI/USB, Bluetooth, SMART and sensors must also be checked on a real Linux installation.

Development checks and limitations are recorded in `VERIFICATION.md`. The Codex Windows session was denied access to the WSL service even after Arch was installed. No native Arch hardware validation is claimed on that basis.

## Source and build

All project source, tests and the portable packager are in `source/`. Python 3.11+ is required to rebuild the package (the app source needs 3.10+). There are no pip dependencies:

```sh
cd source
python3 -m unittest discover -s tests -v
python3 build.py
```

The packager downloads the pinned official Astral Python Build Standalone archive only if it is missing and verifies its fixed SHA-256 from `packaging/runtime.json`. `python3 build.py --offline` uses a previously downloaded archive. It cross-packages the already built Linux runtime; it does not compile or execute Linux ELF files on Windows. Generated releases and their SHA-256 sidecars appear under `dist/`. The `.run` payload contains the tests, app source in a zip archive, runtime, and third-party license notices. See `THIRD_PARTY.md`.

The source can also run with system Python/Tk using `python3 -m devicescope`. For a different architecture, use the same application source and an appropriate runtime; changing the filename does not change CPU compatibility.

The app performs no network requests at runtime. Device inventory is read-only. Process signalling occurs only through the selected-process action. Snapshots use a Linux-specific format and do not import Windows snapshots as if the schemas were identical.

## Primary references

- [Linux proc counters](https://docs.kernel.org/filesystems/proc.html)
- [Kernel hwmon sensor interface](https://docs.kernel.org/hwmon/sysfs-interface.html)
- [AMD GPU telemetry](https://docs.kernel.org/gpu/amdgpu/driver-misc.html)
- [NVIDIA SMI](https://docs.nvidia.com/deploy/nvidia-smi/index.html)
- [Blueman and the BlueZ API](https://github.com/blueman-project/blueman)
- [Python Build Standalone runtime requirements](https://docs.astral.sh/python-build-standalone/running/)

## System Specifications

The sixth tab reads OS name/edition, full processor models, all detected PCI/DRM GPUs,
installed-memory modules and DDR type/speeds, motherboard, BIOS/UEFI firmware and
Secure Boot state, storage models, and current/reported supported display modes.
Refresh runs in the background. Missing information stays unavailable. Memory
clock MHz and transfer rate MT/s are retained as reported, and OS-usable memory
is separate from firmware-reported installed RAM. WSL data describes the VM, not
the Windows host's complete firmware or physical monitor capabilities.

For richer specification data, the optional `dmidecode` tool reads memory module
information when firmware permissions allow it. No sudo is invoked by DeviceScope.
Display queries optionally use `xorg-xrandr` on Arch.

## Copy specifications update — 2026-10-29

A Copy specifications button is available only in System Specifications. It copies the complete currently displayed report (all sections, rows, GPU entries, supported modes and collection notes), regardless of scroll position or selected text. It remains disabled before a successful capture and reports clipboard errors without claiming success. Collectors and hardware readings are unchanged. Native Tk tests intercept clipboard writes to preserve the user clipboard while checking the full text passed to the real clipboard API.
