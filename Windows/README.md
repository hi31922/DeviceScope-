# DeviceScope

Windows 10/11 x64 desktop app. Close the older version and run `DeviceScope-0.8.exe`. All six main sections open inside the same window. No local web server or internet connection is required.

## System Specifications (Windows 0.8)

A sixth tab shows OS name/edition ID/version/build, full processor model names,
all reported display adapters and manufacturers, installed RAM and per-module
DDR type/capacity/speed, motherboard model/manufacturer, BIOS/UEFI version/vendor,
boot firmware mode, Windows-reported Secure Boot state, disk models/capacity, and
monitors with current resolution/refresh plus reported supported modes. Values
are selectable and cards scroll inside the same window. Collection runs in the
background on first opening the tab or on Refresh specifications.

Read-only WMI data is supplemented by SetupAPI display adapters, firmware-backed
installed-memory and raw SMBIOS queries, BIOS registry values and Windows display
APIs. RAM totals are installed memory rather than OS-usable RAM; incomplete module
capacities are not summed as a complete total. SMBIOS memory speeds are shown in
MT/s. A WMI-only configured clock fallback remains labelled MHz, without guessing
a conversion. Unsupported, OEM-placeholder or denied fields are Unavailable.
Display refresh is the integer rate reported by Windows' display driver; remote
and virtual displays may describe the session instead of a physical monitor.
No administrator elevation, Secure Boot changes, drivers or services are used.

The feature passed 24 focused checks covering RAM parser bounds/unknowns/extended
sizes and units, multi-GPU identity joins, display modes, OEM placeholders, native
ABI layout, real read-only host capture, six tabs, compact layout and selectable
wrapped values. The development host returned both integrated/discrete GPUs,
installed module details and current/supported display modes through actual OS
APIs. Two offscreen WPF renders of the new tab were inspected at 1040 and 840 pixels.
The prior five tabs also passed their 35 component checks with ten offscreen renders.
These reads do not establish all-machine hardware/provider compatibility.

## Windows 0.7 interface

The Windows edition now shares the Linux edition's navy header, light canvas, flat tabs/buttons, white tables, dark text and blue selection colors. Device and diagnostics lists sit above adjustable detail panels. Live Performance uses CPU/RAM/GPU/temperature cards in a scrollable 2x2 layout and retains the Windows GPU-engine table. The smaller default window is 1040x720; the minimum is 840x620.

This update retains the native Windows WPF application and its existing inventory, diagnostic, performance, SMART and selected-process control implementations. It does not substitute Linux data sources. `UiTheme.cs` contains the shared WPF theme; the icon remains in the EXE/title bar, not the content header. Other platform releases are separate.

The supplied computer/magnifier artwork is embedded into the EXE at seven icon sizes and used as its file/title-bar icon. It is not displayed in the app's content header. The ICO is included for rebuilding; the EXE needs no sidecar image file.

## Devices & Drivers

- Device inventory with manufacturer, category, status, real hardware IDs and device instance ID.
- Driver provider, version, date, INF file and signing status.
- Network connection name, MAC address, link state and link speed.
- Search, category filter and Needs attention filter.
- Device Type appears beside each device in Devices & Drivers and Driver Diagnostics: Physical hardware, Audio endpoint, System component, Virtual device, Software component, or Unknown. Search also matches these English type labels.
- Classification uses actual Windows setup classes/GUIDs, enumerator/parent-chain metadata and declared network Characteristics flags. It never identifies types by product names such as Realtek, NVIDIA or Hyper-V.
- Classification basis in the details explains the evidence. Driver rows describe the associated device's type; the driver file itself is still software.
- Built-in/external placement is not guessed. Integrated hardware is still physical, system components can be hardware-backed, and Windows-reported PCI/USB enumeration does not prove bare-metal hardware inside a VM. ROOT/HID/Bluetooth names alone do not prove physical hardware; inconclusive evidence stays Unknown.
- Export JSON saves the full scan, regardless of active search filters.
- Compare snapshot shows new, changed and removed devices. Select a changed device to see previous/current values. Changes only filters that comparison.
- Older DeviceScope snapshots remain supported. Device scans run in the background; failures keep the previous successful inventory visible.

## Processes & RAM

- Running applications and background processes, PID, window title, RAM and private committed memory.
- Numeric memory sorting and search by name, PID, type or window title.
- System physical RAM usage. Auto-refresh every three seconds while this tab is visible; it can be paused.
- RAM means the process working set, including shared physical pages. Private memory means committed private bytes, some of which may be paged out. Neither value is identical to Task Manager's default memory column; shared working sets must not be summed as system RAM usage.
- Application/background classification depends on whether Windows exposes a main window. It is a practical indication, not a definitive application grouping.
- Some protected or already-exited processes may show Unavailable. The rest of the list continues to work.
- Left-click a process row to reveal **Stop Process**. Clicking it force-terminates only that selected process; unsaved work in that application can be lost.
- The selected PID and creation time are verified against one retained Windows process handle. A recycled PID or changing selection cannot redirect the action to another process.
- DeviceScope refuses its own process, Windows core components and critical processes. Other protected/elevated processes may return Access denied. No privilege changes or automatic elevation are used.
- Stop results remain visible independently of automatic list refresh. A successfully stopped row is removed immediately; a sample started before termination cannot restore that stale row.

## Driver Diagnostics

- Error codes, status and exact hardware/device-instance IDs in one section; Code 28 devices appear first when the Needs attention filter is enabled.
- Code 28 means Windows reports that drivers are not installed. Guidance points to Windows Update and the PC/hardware manufacturer's official support, without guessing which package is required.
- Declared driver-service dependencies are inspected from read-only Windows service registration. Missing registration, disabled service and unknown load-order-group membership are distinguished. This is not an arbitrary DLL-import scan or a complete driver-package dependency resolver.
- Device Events reads up to 100 recent relevant events in each of System and Kernel-PnP/Configuration from the last 30 days. It matches exact device-instance IDs in recorded XML payloads, not similar device names. No match in this bounded scan is not proof there were no events.
- Refresh devices updates the shared inventory. Refresh selected rechecks the selected device's declared dependencies and matching events.

## Live Performance

- CPU and physical RAM use real Windows system measurements; GPU uses Windows GPU Engine performance counters. Graphs cover the last 60 seconds and update around once per second while visible.
- GPU per-process readings are grouped by physical engine. The busiest engine across GPUs is shown rather than adding unrelated engines into a misleading total. Engine identities and percentages remain visible in a table.
- On systems with multiple processor groups, CPU timing covers the process's processor group; the measurement note identifies that limitation.
- Temperature readings are sampled separately around every 10 seconds. ACPI thermal zones retain their actual identity and are not assumed to be CPU cores. Existing LibreHardwareMonitor/OpenHardwareMonitor WMI providers can also supply named sensors if already running; DeviceScope installs nothing.
- Select a readable sensor for its temperature graph. Unsupported, inaccessible or missing readings stay unavailable and leave graph gaps instead of becoming fake 0% or 0 C readings. GPU load requires supported WDDM counters.

## Disk Health

- Physical-disk health is shown as reported by the Windows Storage provider. Associated storage reliability counters can expose temperature, wear and error counters where supported.
- The physical-disk-to-reliability association is used; records are not joined by guessed disk numbers. Win32 inventory is clearly labelled as a fallback and is not treated as SMART health.
- Volume space shows actual local filesystem capacity, used/free space and usage bars. Logical volumes are not assumed to correspond one-to-one to physical disks.
- ATA SMART provider instances, raw bytes, normalized attributes and available thresholds are shown separately. Raw attributes are vendor-defined; no universal health percentage is invented. NVMe, USB and RAID devices may not expose legacy ATA SMART.
- Storage refresh runs in the background. Optional 30-second automatic refresh operates while this tab is visible. Provider access failures and unsupported values are shown explicitly.

White input and table areas use black text, including selected cells.

Device inventory is read-only. Process termination is performed only when the user selects one process and clicks Stop Process. The app does not change drivers, registry values or device settings. Reports stay local and contain device identifiers; process lists are not included in device snapshots.

## Source and build

`DeviceScope.cs` contains the window and `UiTheme.cs` supplies the shared control styles; `Inventory.cs` handles devices/snapshots; `DeviceMetadataReader.cs` reads native device properties, and `DeviceClassifier.cs` derives the evidence-backed type. `ProcessesView.cs` and `ProcessControl.cs` handle processes and selected termination. The DriverDiagnostics, Performance and DiskHealth Reader/View pairs implement the other sections. `SystemSpecificationsReader.cs` and `SystemSpecificationsView.cs` implement the specification tab. `Build.ps1` builds `DeviceScope-0.8.exe` with the Windows .NET Framework C# compiler and embedded `DeviceScope.ico`; no downloaded packages are needed. Close DeviceScope before rebuilding. The build also supports folder paths containing commas, such as this project's current folder.

## Verification and limits

The Windows 0.7 visual update passed 35 focused WPF component checks and generated ten offscreen layout renders covering all five pages at normal and compact content sizes. Checks include filtering, selected device details, selected-only process-stop controls without terminating any process, error-code filtering, black text in white fields/tables, the embedded icon, all four graphs and nested tab selection. Render data was explicitly labelled as layout fixtures. The seven inventory/metadata/classifier/process-control/diagnostic/performance/storage implementation files and the icon were byte-for-byte unchanged. Main inventory/filter/detail/export/compare handlers and the other views' refresh/apply/shutdown methods were also retained. The Windows screen-capture helper timed out, so direct desktop capture/interaction was not verified during this visual update. Existing hardware-provider availability limits below still apply.

Code 28 guidance, exact event matching, dependency classification, SMART parsing, unknown-value handling, graphs/layout, snapshot compatibility and single-process stopping have component checks. CPU, GPU, RAM and local volume space were read successfully in the development environment. Physical-disk/SMART WMI access was denied there; hardware-specific health, sensor and driver-event availability must still be checked on the destination PC. No clean bill of health is inferred from denied access.

The 0.6 classification rules passed focused read-only checks for class/GUID roles, physical/virtual network flags, conflicting metadata, HID/ROOT uncertainty, physical and virtual ancestry, cycles and JSON preservation. Native metadata was also read successfully on the development PC. The broader combined regression-test executable was blocked by Bitdefender during this update, so a complete new regression run could not finish; no protection settings were changed.

Reference documentation: [Windows system CPU timing](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes), [Microsoft's GPU-engine aggregation explanation](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/), [storage reliability association](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisktostoragereliabilitycounter), [storage reliability counters](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter).

## Copy specifications update — 2026-10-29

The tab-local Copy specifications button copies the complete cached report, including all GPUs, off-screen rows, display modes and collection notes, using the native persistent Windows clipboard. Copying is disabled until a report loads; clipboard failures are reported honestly. The previous displayed report remains copyable during refresh. Twenty-three focused checks passed with an isolated clipboard writer so the user clipboard was not overwritten during verification. Other Windows application source and collectors are unchanged.
