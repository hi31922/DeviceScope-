# DeviceScope 0.8 System Specifications verification — Arch

Date: 2026-10-29.

The sixth tab shows real OS/edition, processor models, all reported GPUs,
installed memory and DDR/module speed, motherboard, BIOS/UEFI and Secure Boot,
storage models, and display current/supported modes. Collection is read-only and
runs off the GUI event thread. Missing/denied fields remain unavailable; installed
memory is not substituted with OS-usable RAM and MHz is not guessed as MT/s.

Arch WSL: 100 packaged tests completed, OK (skipped=1). The sole skip is the
ordinary-user pidfd integration test because this distribution's default account
is root. Real Tk GUI and the new specification fixture tests passed. The existing
Arch application logic was retained while adding the new tab and collector.

Runtime/archive integrity, embedded hashes, packaged source syntax, executable
mode, architecture, Tcl/Tk resources and notices were verified. 

Linux GUI tests ran inside WSL, not on physical Arch hardware. WSL
firmware and display interfaces describe its virtual environment. Optional dmidecode
can be missing or denied for ordinary users; xrandr requires an exposed X11/XWayland
server. Reported supported display modes are the active driver/server's modes,
not proof of every physical panel capability. No drivers, firmware settings,
Secure Boot settings, or privileges were changed.

Release SHA-256: `b3384b28a08014cf2b9a351ee5d29b44d121002e1f813a1341367dc9d495668e`.

Other release folders and the separate school backup were excluded from this Arch update.

## Copy specifications update — 2026-10-29

A Copy specifications button is available only in System Specifications. It copies the complete currently displayed report (all sections, rows, GPU entries, supported modes and collection notes), regardless of scroll position or selected text. It remains disabled before a successful capture and reports clipboard errors without claiming success. Collectors and hardware readings are unchanged. Native Tk tests intercept clipboard writes to preserve the user clipboard while checking the full text passed to the real clipboard API.

Copy-button verification: 100 packaged tests completed in Arch WSL, with the root-only pidfd integration skip. Native clipboard calls are used in production; tests intercepted writes to preserve user clipboard contents.

## Bug audit — 2026-10-29

Fixed an obsolete device-category filter hiding refreshed inventory and stale
process scans surviving stop operations with automatic refresh disabled. Both
fixes already existed in Debian and were ported without changing collectors.
The two new GUI regressions failed before and passed after the fix.
The rebuilt .run completed 102 packaged tests in Arch WSL, OK (skipped=1);
the skip is the ordinary-user pidfd integration test because Arch runs as root.
Archive integrity, executable modes, source syntax, runtime resources and exact
packaged/source GUI correspondence passed. Testing is under WSL, not physical Arch.

