# DeviceScope 0.8 System Specifications verification — Debian

Date: 2026-10-29.

The sixth tab shows real OS/edition, processor models, all reported GPUs,
installed memory and DDR/module speed, motherboard, BIOS/UEFI and Secure Boot,
storage models, and display current/supported modes. Collection is read-only and
runs off the GUI event thread. Missing/denied fields remain unavailable; installed
memory is not substituted with OS-usable RAM and MHz is not guessed as MT/s.

Debian WSL: all 102 packaged tests passed with no skips, including nine real Tk
GUI tests, six-tab header reachability at 840/1180 widths, asynchronous specifications
display, the 23 new collector/firmware/display fixture checks and the pidfd integration
case that stops only a disposable test worker. A read-only real WSL specification
capture also succeeded. Native dpkg-deb confirmed package/version/architecture.

Runtime/archive integrity, embedded hashes, packaged source syntax, executable
mode, architecture, Tcl/Tk resources and notices were verified. The Debian package
also passed ar/control/data layout, ownership/modes/links, all 3,506 file hashes,
launcher/icon, and exact portable-runtime/app consistency checks.

Linux GUI tests ran inside WSL, not on physical Arch or Pardus hardware. WSL
firmware and display interfaces describe its virtual environment. Optional dmidecode
can be missing or denied for ordinary users; xrandr requires an exposed X11/XWayland
server. Reported supported display modes are the active driver/server's modes,
not proof of every physical panel capability. No drivers, firmware settings,
Secure Boot settings, or privileges were changed.

Release SHA-256: `299a044e72251da816ad13a039687869a7a8ebcdbaff218b96bfa21ae9850c7b`.

The separate Debian-backup school copy is excluded from this update and remains byte-identical.

## Copy specifications update — 2026-10-29

A Copy specifications button is available only in System Specifications. It copies the complete currently displayed report (all sections, rows, GPU entries, supported modes and collection notes), regardless of scroll position or selected text. It remains disabled before a successful capture and reports clipboard errors without claiming success. Collectors and hardware readings are unchanged. Native Tk tests intercept clipboard writes to preserve the user clipboard while checking the full text passed to the real clipboard API.

Copy-button verification: 102 packaged tests passed in ordinary-user Debian WSL. Native clipboard calls are used in production; tests intercepted writes to preserve user clipboard contents.
