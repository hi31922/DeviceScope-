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

Release SHA-256: `9107d0d629f6bf1e7e954b82710bdc1676b82eb437c8aed893eaa292fdefa02e`.

The separate Debian-backup school copy is excluded from this update and remains byte-identical.

## Copy specifications update — 2026-10-29

A Copy specifications button is available only in System Specifications. It copies the complete currently displayed report (all sections, rows, GPU entries, supported modes and collection notes), regardless of scroll position or selected text. It remains disabled before a successful capture and reports clipboard errors without claiming success. Collectors and hardware readings are unchanged. Native Tk tests intercept clipboard writes to preserve the user clipboard while checking the full text passed to the real clipboard API.

Copy-button verification: 102 packaged tests passed in ordinary-user Debian WSL. Native clipboard calls are used in production; tests intercepted writes to preserve user clipboard contents.

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

Fixed the same two snapshot-comparison races as Arch: a delayed import could
restore a cleared comparison, and a newer file selection could be ignored while
another import was running. Obsolete successes/errors are ignored and only the
newest pending file is loaded next. Three regressions failed before the fix.
All 105 tests in the extracted final .deb passed in ordinary-user Debian WSL;
the real read-only report passed too. Independent Debian archive verification
passed all 3506 file hashes, launcher/icon metadata and exact portable payload.
No install or privilege change was made during tests. Current package/app versions
remain unchanged; use apt --reinstall when updating an already installed copy.
Existing size, UI, styling, branding removal, readers and process controls remain.
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
