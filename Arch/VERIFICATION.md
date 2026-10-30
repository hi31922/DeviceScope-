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

Release SHA-256: `799c1a3c0a9ca42e81245e0c9c866207d8a01145f7e026b6a8e9a64a9e0d9e57`.

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


## In-content branding removal - 2026-10-30

Only the five header-frame/branding-label construction lines in gui.py were
removed. Window title, default 1180x800 size, minimum 840x620, styles, controls,
icon, six tabs, collectors and interactions are unchanged. Comparative native
Tk checks passed for both editions (20 checks total). Packaged tests passed:
Arch 102 tests with the existing root-account integration skip; Debian 102 tests
with no skips. Archive and payload/source consistency checks passed.
No Windows or Debian-backup files were included. Package/app versions remain
unchanged; updating an already installed Debian copy requires apt --reinstall.

## Release audit - 2026-10-30

Fixed two snapshot-comparison races: an old import could restore a comparison
after Clear comparison, and a newer file choice was silently discarded while
an import was running (including when the earlier file failed to load). Generation
guards reject obsolete results and queue only the newest requested file.
Three deterministic regressions failed before and passed after the fix.
All 105 packaged tests passed with no skips in Arch WSL under an ordinary
non-root account, including the selected-only disposable-worker integration test.
Package integrity and exact packaged/source application correspondence passed.
Application changes are confined to comparison-loading logic; existing size,
layout, styling, branding removal, readers and process controls are unchanged.
WSL does not establish physical sensor, SMART transport or firmware compatibility.

## Second release audit - 2026-10-30

Fixed three Linux snapshot/export bugs. A successful inventory containing zero
devices can now be exported as a valid empty snapshot; an initial failed scan
still cannot export. Export requests made while a write is active now show an
in-progress message before opening another Save dialog, avoiding silently discarded
destinations. Snapshot validation now rejects lone Unicode surrogates in text
or keys before Tk rendering; valid supplementary Unicode remains supported.
Both export regressions and 12 malformed-Unicode subcases failed before the fixes.
The final package passed all 110 tests under ordinary-user WSL with no skips.
The independent .deb/payload/source checks and real Debian report also passed.
Application changes are confined to export state/guards and snapshot validation;
there is no layout, size, style, branding, collector or process-control change.
Linux results are under WSL, not physical sensor/SMART/firmware validation.
The package/application versions are unchanged; use apt --reinstall to replace
an installed Debian copy. No installation or GitHub upload was performed here.

## Public release packaging - 2026-10-30

The MIT application notice is now included as app/LICENSE. Third-party notices
remain intact. Debian also installs its application notice in /usr/share/doc.
All application modules/tests match the previously audited build byte for byte.
The licensed final packages passed all 110 tests per edition under ordinary-user
WSL. Archive layout, notices, hashes and packaged/source consistency passed.
This packaging update does not change the GUI, sizes, readers or functionality.
