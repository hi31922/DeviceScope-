# Windows compact classic interface verification

Date: 2026-10-30. Application version: 0.8 (unchanged).

The content banner, DeviceScope name, WINDOWS / 0.8 label and tagline were removed.
The standard title is System Information; the existing embedded file/window icon
remains. All six main tabs and nested diagnostics/storage tabs remain in one window.

The default outer window is 700 x 580 physical pixels. SourceInitialized adjusts
WPF logical dimensions using the window's device transform. Native GetWindowRect
confirmed exactly 700 x 580 on the host's 125% desktop (client 545.6 x 426.4 WPF
units). The window is resizable upward. Other scale factors were not exercised.

55 focused native WPF checks passed for geometry, branding removal, six-tab/header
reachability, action/search bounds, inventory filtering/details, selected-process
button visibility, graph presence, lower GPU-engine scroll access, last specification
row access, and all nested diagnostic/storage pages. Fourteen offscreen renders
used labelled fixtures and the actual client size; the final renders were inspected.
23 existing full-report copy tests also passed with an injected clipboard writer.
No user clipboard changes or process termination were performed in this UI check.

All inventory/metadata/classification/diagnostic/performance/disk/specification
reader files, ProcessControl.cs, AssemblyInfo.cs, Build.ps1 and the ICO are
byte-identical to the pre-update versions. Application changes are limited to
the shell, common theme and five view layouts; no hardware values were replaced
by the labelled test fixtures, which exist only in the separate test harness.

Arch, Debian and Debian-backup are excluded from this update.
Previous Windows source/build copies are in the local development audit folder.

Release SHA-256: `4e551ede550285cb6840e7734b47dc5ca6847387bc94e3bf7f09a918d04f7beb`.

## Stable tabs and gray palette update - 2026-10-30

The shared tab template now uses an items-host WrapPanel instead of TabPanel.
Headers wrap in declaration order and their positions do not change on selection.
The fix also applies to nested storage/diagnostic tabs. Beige backgrounds, button
shades, graph grids, section panels and list headers use neutral gray shades.
The 700 x 580 physical default size, font, all six tabs, existing functions and
embedded icon are unchanged. All 55 compact WPF checks passed again; see the
separate local stable-tab regression log for before/after selection checks.
No reader, process control, version, build script, Linux or backup files changed.

Stable-tab regression: the previous binary failed 48 checks; the fixed binary passed all 245 checks. Selection order, repeated selection, unchanged header rectangles and stationary-coordinate hit tests were covered at 400/510/700 logical widths, including nested multirow tabs. Keyboard shortcuts were not separately exercised.

## Release audit - 2026-10-30

Fixed keyboard navigation in the stable wrapping tab strip. Directional cycle
and single-entry Tab navigation are restored; Down/Up stay in the tab headers,
and Tab moves from the selected header to content. No appearance/geometry change.
The original binary failed the focused regression; the fixed binary passed all
30 keyboard checks. Another 55 compact, 245 stationary-header and 112 collector,
specification, copy, pause/race and diagnostic/process checks passed (442 total).
Windows tests ran natively; live read-only host readings succeeded. Clipboard
tests used an isolated writer; process tests targeted disposable test workers.
Only UiTheme.cs and the executable changed in this edition, apart from this note.

## Second release audit - 2026-10-30

Fixed a snapshot-save data-loss bug: an I/O failure after opening an existing
export could truncate it. A byte-range lock on a disposable snapshot reproduced
the failure safely; no user snapshot was used. Save now serializes/validates
first, writes and flushes a unique same-directory temporary file, and replaces
the destination with a rollback backup. Partial replacement failures restore
the old name when possible or report retained recovery paths; no truncate fallback.
The original binary failed the preservation regression. All 13 focused checks
passed on the final build. The existing 53 collector/classification/snapshot checks
also passed during this audit (66 checks total; only recovery-message handling
changed after that broader run, followed by the final 13-check rerun).
Only Inventory.cs snapshot-saving logic and the EXE changed in this edition.
The host filesystem was NTFS. FAT/exFAT and rare partial ReplaceFile failures
were reviewed but not physically exercised; universal power-loss guarantees
are not claimed. Layout, title, font, dimensions, readers and versions are unchanged.
