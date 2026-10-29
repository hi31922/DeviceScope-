# DeviceScope 0.8 System Specifications verification — Windows

Date: 2026-10-29.

The sixth tab shows real OS/edition, processor models, all reported GPUs,
installed memory and DDR/module speed, motherboard, BIOS/UEFI and Secure Boot,
storage models, and display current/supported modes. Collection is read-only and
runs off the GUI event thread. Missing/denied fields remain unavailable; installed
memory is not substituted with OS-usable RAM and MHz is not guessed as MT/s.

Windows: 24 focused specifications checks passed, including native SMBIOS
parsing bounds, partial/unknown modules, DDR types/extended speeds, multiple GPUs,
refresh values and display ABI. A real host capture returned all eight sections.
Both normal and compact new-tab WPF renders were inspected. The 35 existing WPF
UI component checks also passed. Existing readers/views, process guards and theme
are byte-identical; only integration, versions and the new reader/view were added.

Release SHA-256: `01a4c67d226c6bca6644535e737a4963a9593ceaf83a9bcbdd79ad249c41a2a9`.

The separate Debian-backup school copy is excluded from this update and remains byte-identical.

## Copy specifications update — 2026-10-29

The tab-local Copy specifications button copies the complete cached report, including all GPUs, off-screen rows, display modes and collection notes, using the native persistent Windows clipboard. Copying is disabled until a report loads; clipboard failures are reported honestly. The previous displayed report remains copyable during refresh. Twenty-three focused checks passed with an isolated clipboard writer so the user clipboard was not overwritten during verification. Other Windows application source and collectors are unchanged.

Copy-button verification: 23 focused Windows checks passed using an isolated writer. Native clipboard calls are used in production; tests intercepted writes to preserve user clipboard contents.

## Bug audit — 2026-10-29

Fixed stale Driver Diagnostics dependency/event notes and loading status after
the selected device is cleared by filtering. The regression failed against the
previous binary and passed after the fix. No readers, theme or features changed.
147 Windows assertions passed across collector, specifications, copy, WPF layout,
performance pause/race and diagnostic/process tests. Live read-only host capture
succeeded. Process termination checks used only newly created disposable workers;
clipboard tests intercepted writes to preserve the user's clipboard.

