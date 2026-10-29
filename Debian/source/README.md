# DeviceScope for Debian and Pardus

Package: **devicescope_0.8.0-2_amd64.deb** (Debian package revision 2; application version 0.8.0). This installs the same reviewed Linux
application as the portable `.run`, with an application-menu shortcut and the
`devicescope` command. It includes Python, Tcl/Tk, the app and its regression
tests. No system Python packages, drivers or services are installed by this app.

## Install and open

Copy the `.deb` file onto the Debian/Pardus computer. Open it with the normal
package installer, or open a terminal in that folder and run:

```sh
sudo apt install ./devicescope_0.8.0-2_amd64.deb
```

Installation requires administrator authorization. After installation, open
**DeviceScope** from the application menu, or type `devicescope` in a terminal.
Run the application as your ordinary desktop user.

The package contains the runtime and its only required package dependency is
`libc6 (>= 2.17)`, normally part of an installed amd64 Debian/Pardus system.
An X11 desktop or XWayland and readable fonts are required to show the GUI.
Optional tools are not installed automatically. SMART information, temperatures,
GPU counters and kernel logs depend on actual hardware and access permissions;
missing access is shown as unavailable.

This is an **amd64 / x86-64 Intel or AMD** package. It does not run on ARM or
32-bit systems. The school's exact Pardus release has not been identified or
tested. Debian ancestry alone does not establish complete hardware compatibility.

For a school demonstration, copy the portable `.run` file to the USB as well.
It can run without installation or administrator rights when the desktop allows
execution. A restricted school account may disallow installation or execution;
use the school's authorized account and normal software policy.

## Check it on the destination

```sh
devicescope --version
devicescope --self-test
devicescope
```

The GUI checks need a display. The pidfd integration test needs an ordinary
Linux user and supported kernel; it stops only a disposable worker created by
the test. A headless read-only report is also available:

```sh
devicescope --report > devicescope-report.json
```

Reports and snapshots include hardware identifiers. They stay local unless you
choose to share them.

To inspect the package before installing, use `dpkg-deb --info` and
`dpkg-deb --contents` followed by the filename. Remove the installed package with
`sudo apt remove devicescope`. Exported snapshots are not package-owned files.

## Source and rebuild

The Debian release folder includes its own `source/` directory. Open a terminal
in that source directory and build both artifacts with:

```sh
python3 build.py
python3 packaging/build_deb.py dist/DeviceScope-0.8.0-linux-x86_64.run
```

The builder cross-packages the verified payload using the documented Debian ar
and gzip-tar format. It runs no Linux executables on the build host. The package
uses `/opt/devicescope`, `/usr/bin/devicescope`, an app-menu entry and its icon.
There are no package maintainer scripts, privilege rules, autostart entries,
automatic updates or background services.

Pardus installation documentation:
https://belge.pardus.org.tr/pages/viewpage.action?pageId=10028699

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
Display queries optionally use `x11-xserver-utils` on Debian/Pardus.

## Copy specifications update — 2026-10-29

A Copy specifications button is available only in System Specifications. It copies the complete currently displayed report (all sections, rows, GPU entries, supported modes and collection notes), regardless of scroll position or selected text. It remains disabled before a successful capture and reports clipboard errors without claiming success. Collectors and hardware readings are unchanged. Native Tk tests intercept clipboard writes to preserve the user clipboard while checking the full text passed to the real clipboard API.
