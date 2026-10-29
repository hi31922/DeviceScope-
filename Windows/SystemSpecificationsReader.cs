using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DeviceScope
{
    public sealed class SpecificationProperty
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }
    public sealed class SpecificationSection
    {
        public string Title { get; set; }
        public List<SpecificationProperty> Rows { get; set; }
        public SpecificationSection(string title) { Title = title; Rows = new List<SpecificationProperty>(); }
        public void Add(string label, string value) { Rows.Add(new SpecificationProperty { Label = label, Value = SystemSpecificationsReader.Clean(value) }); }
    }
    public sealed class SystemSpecificationsReport
    {
        public List<SpecificationSection> Sections { get; set; }
        public List<string> Notes { get; set; }
        public SystemSpecificationsReport() { Sections = new List<SpecificationSection>(); Notes = new List<string>(); }
    }
    public sealed class MemorySpecification
    {
        public string Locator { get; set; }
        public string Manufacturer { get; set; }
        public string PartNumber { get; set; }
        public ulong? Capacity { get; set; }
        public string MemoryType { get; set; }
        public uint? RatedTransferRate { get; set; }
        public uint? ConfiguredTransferRate { get; set; }
        public uint? ConfiguredClockMhz { get; set; }
    }
    public sealed class GpuSpecification
    {
        public string Id { get; set; }
        public string Model { get; set; }
        public string Manufacturer { get; set; }
    }

    public static class SystemSpecificationsReader
    {
        private static readonly Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFirmwareType(out uint type);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetSystemFirmwareTable(uint provider, uint table, [Out] byte[] buffer, uint size);
        [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo data, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, Extra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DisplayMode settings, uint flags);

        public static string Clean(string text)
        {
            text = (text ?? "").Replace("\0", "").Trim();
            if (text.Length == 0 || new[] { "To be filled by O.E.M.", "Default string", "Not Specified", "Unknown", "None", "Type2Version", "System Version", "System Product Name", "System Manufacturer" }.Contains(text, StringComparer.OrdinalIgnoreCase)) return "Unavailable";
            return text;
        }
        private static string V(Dictionary<string, string> row, string name) { string result; return row.TryGetValue(name, out result) ? result : ""; }
        private static ulong? Positive(string text) { ulong value; return UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0 ? (ulong?)value : null; }
        public static string Bytes(ulong? value) { return value.HasValue ? String.Format(CultureInfo.InvariantCulture, "{0:N2} GiB ({1:N0} bytes)", value.Value / 1073741824.0, value.Value) : "Unavailable"; }
        private static string RegistryValue(string key, string name)
        {
            try { using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) using (RegistryKey entry = machine.OpenSubKey(key, false)) return entry == null ? "" : Convert.ToString(entry.GetValue(name), CultureInfo.InvariantCulture); }
            catch (Exception) { return ""; }
        }
        private static List<Dictionary<string, string>> Query(string cls, string[] properties, SystemSpecificationsReport report)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            try
            {
                EnumerationOptions options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(5), Rewindable = false };
                using (ManagementObjectSearcher search = new ManagementObjectSearcher("root\\cimv2", "SELECT " + String.Join(",", properties) + " FROM " + cls, options))
                using (ManagementObjectCollection found = search.Get())
                    foreach (ManagementObject item in found)
                        using (item)
                        {
                            Dictionary<string, string> row = new Dictionary<string, string>();
                            foreach (string property in properties) { try { row[property] = Convert.ToString(item[property], CultureInfo.InvariantCulture); } catch (ManagementException) { row[property] = ""; } }
                            rows.Add(row);
                        }
            }
            catch (Exception ex) { report.Notes.Add(cls + " unavailable: " + ex.Message); }
            return rows;
        }
        private static SpecificationSection Section(SystemSpecificationsReport report, string title) { SpecificationSection section = new SpecificationSection(title); report.Sections.Add(section); return section; }

        public static string MemoryTypeName(uint type)
        {
            switch (type)
            {
                case 0x12: return "DDR"; case 0x13: return "DDR2"; case 0x14: return "DDR2 FB-DIMM"; case 0x18: return "DDR3";
                case 0x1A: return "DDR4"; case 0x1B: return "LPDDR"; case 0x1C: return "LPDDR2"; case 0x1D: return "LPDDR3"; case 0x1E: return "LPDDR4";
                case 0x20: return "HBM"; case 0x21: return "HBM2"; case 0x22: return "DDR5"; case 0x23: return "LPDDR5"; case 0x24: return "HBM3";
                case 0: case 1: case 2: return "Unavailable"; default: return "SMBIOS memory type " + type.ToString(CultureInfo.InvariantCulture);
            }
        }
        private static ushort U16(byte[] bytes, int position) { return (ushort)(bytes[position] | bytes[position + 1] << 8); }
        private static uint U32(byte[] bytes, int position) { return (uint)(bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16 | bytes[position + 3] << 24); }
        private static string SmbiosString(byte[] data, int start, int end, int index)
        {
            if (index == 0) return "Unavailable";
            int current = 1;
            while (start < end)
            {
                int next = start; while (next < end && data[next] != 0) next++;
                if (current == index) return Clean(Encoding.UTF8.GetString(data, start, next - start));
                current++; start = next + 1;
            }
            return "Unavailable";
        }
        public static List<MemorySpecification> ParseMemoryTable(byte[] raw)
        {
            List<MemorySpecification> modules = new List<MemorySpecification>();
            if (raw == null || raw.Length < 8) return modules;
            uint size = U32(raw, 4);
            if (size > raw.Length - 8) return modules;
            int end = 8 + (int)size, position = 8;
            while (position + 4 <= end)
            {
                int length = raw[position + 1];
                if (length < 4 || position + length > end) return new List<MemorySpecification>();
                int strings = position + length, terminator = strings;
                while (terminator + 1 < end && !(raw[terminator] == 0 && raw[terminator + 1] == 0)) terminator++;
                if (terminator + 1 >= end) return new List<MemorySpecification>();
                if (raw[position] == 17 && length >= 0x15)
                {
                    ushort memorySize = U16(raw, position + 0x0C);
                    if (memorySize != 0)
                    {
                        MemorySpecification module = new MemorySpecification { Locator = SmbiosString(raw, strings, terminator, raw[position + 0x10]), MemoryType = MemoryTypeName(raw[position + 0x12]), Manufacturer = "Unavailable", PartNumber = "Unavailable" };
                        if (memorySize == 0x7FFF && length >= 0x20) { uint extended = U32(raw, position + 0x1C) & 0x7FFFFFFF; if (extended > 0) module.Capacity = (ulong)extended * 1048576; }
                        else if (memorySize != 0xFFFF && memorySize != 0x7FFF)
                        {
                            ulong capacity = (ulong)(memorySize & 0x7FFF) * ((memorySize & 0x8000) != 0 ? 1024UL : 1048576UL);
                            if (capacity > 0) module.Capacity = capacity;
                        }
                        if (length >= 0x17) { uint value = U16(raw, position + 0x15); if (value == 0xFFFF && length >= 0x58) value = U32(raw, position + 0x54); if (value > 0 && value != 0xFFFF) module.RatedTransferRate = value; }
                        if (length >= 0x22) { uint value = U16(raw, position + 0x20); if (value == 0xFFFF && length >= 0x5C) value = U32(raw, position + 0x58); if (value > 0 && value != 0xFFFF) module.ConfiguredTransferRate = value; }
                        if (length > 0x17) module.Manufacturer = SmbiosString(raw, strings, terminator, raw[position + 0x17]);
                        if (length > 0x1A) module.PartNumber = SmbiosString(raw, strings, terminator, raw[position + 0x1A]);
                        modules.Add(module);
                    }
                }
                if (raw[position] == 127) return modules;
                position = terminator + 2;
            }
            return position == end ? modules : new List<MemorySpecification>();
        }
        public static ulong? CompleteMemoryTotal(IList<MemorySpecification> modules)
        {
            if (modules.Count == 0 || modules.Any(module => !module.Capacity.HasValue)) return null;
            ulong total = 0; foreach (MemorySpecification module in modules) { if (UInt64.MaxValue - total < module.Capacity.Value) return null; total += module.Capacity.Value; }
            return total > 0 ? (ulong?)total : null;
        }
        public static SpecificationSection MemorySection(IList<MemorySpecification> modules, ulong? installed)
        {
            SpecificationSection section = new SpecificationSection("Memory");
            section.Add("Total installed RAM", Bytes(installed ?? CompleteMemoryTotal(modules)));
            section.Add("DDR / memory type", modules.Count == 0 ? "Unavailable" : String.Join(" / ", modules.Select(module => module.MemoryType).Distinct()));
            if (modules.Count == 0) section.Add("Memory speed", "Unavailable");
            for (int i = 0; i < modules.Count; i++)
            {
                MemorySpecification module = modules[i]; string prefix = "Module " + (i + 1) + " / ";
                section.Add(prefix + "Slot", module.Locator); section.Add(prefix + "Capacity", Bytes(module.Capacity));
                section.Add(prefix + "Type", module.MemoryType); section.Add(prefix + "Manufacturer", module.Manufacturer); section.Add(prefix + "Part number", module.PartNumber);
                section.Add(prefix + "Configured transfer rate", module.ConfiguredTransferRate.HasValue ? module.ConfiguredTransferRate.Value + " MT/s" : "Unavailable");
                section.Add(prefix + "Rated transfer rate", module.RatedTransferRate.HasValue ? module.RatedTransferRate.Value + " MT/s" : "Unavailable");
                if (module.ConfiguredClockMhz.HasValue) section.Add(prefix + "Configured clock (WMI reported)", module.ConfiguredClockMhz.Value + " MHz");
            }
            return section;
        }

        private static string DeviceProperty(IntPtr set, ref DeviceInfo data, uint property)
        {
            byte[] buffer = new byte[16384]; uint type, needed;
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out type, buffer, (uint)buffer.Length, out needed) || needed > buffer.Length) return "";
            return Encoding.Unicode.GetString(buffer, 0, (int)needed).TrimEnd('\0');
        }
        private static List<GpuSpecification> NativeGpus(SystemSpecificationsReport report)
        {
            List<GpuSpecification> result = new List<GpuSpecification>(); Guid cls = DisplayClass;
            IntPtr set = SetupDiGetClassDevs(ref cls, null, IntPtr.Zero, 2);
            if (set == new IntPtr(-1)) { report.Notes.Add("Present display-adapter inventory is unavailable from SetupAPI."); return result; }
            try
            {
                for (uint i = 0; i < 1024; i++)
                {
                    DeviceInfo data = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                    StringBuilder id = new StringBuilder(4096); uint needed;
                    string identity = SetupDiGetDeviceInstanceId(set, ref data, id, (uint)id.Capacity, out needed) ? id.ToString() : "SetupAPI display adapter " + i;
                    string model = DeviceProperty(set, ref data, 12); if (String.IsNullOrWhiteSpace(model)) model = DeviceProperty(set, ref data, 0);
                    result.Add(new GpuSpecification { Id = identity, Model = model, Manufacturer = DeviceProperty(set, ref data, 11) });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
        public static List<GpuSpecification> MergeGpus(IEnumerable<GpuSpecification> native, IEnumerable<GpuSpecification> wmi)
        {
            Dictionary<string, GpuSpecification> result = new Dictionary<string, GpuSpecification>(StringComparer.OrdinalIgnoreCase);
            int unknown = 0;
            foreach (GpuSpecification gpu in native.Concat(wmi))
            {
                string key = String.IsNullOrWhiteSpace(gpu.Id) ? "unidentified:" + (++unknown) : gpu.Id;
                GpuSpecification existing;
                if (result.TryGetValue(key, out existing))
                {
                    if (Clean(gpu.Model) != "Unavailable") existing.Model = gpu.Model;
                    if (Clean(gpu.Manufacturer) != "Unavailable") existing.Manufacturer = gpu.Manufacturer;
                }
                else result[key] = new GpuSpecification { Id = gpu.Id, Model = gpu.Model, Manufacturer = gpu.Manufacturer };
            }
            return result.Values.ToList();
        }
        public static string DisplayModeText(uint width, uint height, uint frequency)
        {
            if (width == 0 || height == 0) return "Unavailable";
            return width + " × " + height + (frequency > 1 ? " @ " + frequency + " Hz" : " (refresh rate unavailable)");
        }
        private static void ReadDisplays(SpecificationSection section, SystemSpecificationsReport report)
        {
            int displayed = 0;
            for (uint i = 0; i < 256; i++)
            {
                DisplayDevice adapter = new DisplayDevice { Size = (uint)Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
                bool desktop = (adapter.Flags & 1) != 0;
                List<DisplayDevice> monitors = new List<DisplayDevice>();
                for (uint j = 0; j < 256; j++)
                {
                    DisplayDevice monitor = new DisplayDevice { Size = (uint)Marshal.SizeOf(typeof(DisplayDevice)) };
                    if (!EnumDisplayDevices(adapter.Name, j, ref monitor, 0)) break;
                    monitors.Add(monitor);
                }
                if (monitors.Count == 0 && !desktop) continue;
                if (monitors.Count == 0) monitors.Add(new DisplayDevice { Name = adapter.Name, Description = adapter.Description, Flags = 1 });
                DisplayMode current = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
                string currentText = desktop && EnumDisplaySettingsEx(adapter.Name, -1, ref current, 0) ? DisplayModeText(current.Width, current.Height, current.Frequency) : "Unavailable";
                List<string> modes = new List<string>();
                for (int modeIndex = 0; modeIndex < 8192; modeIndex++)
                {
                    DisplayMode mode = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
                    if (!EnumDisplaySettingsEx(adapter.Name, modeIndex, ref mode, 0)) break;
                    string text = DisplayModeText(mode.Width, mode.Height, mode.Frequency);
                    if (text != "Unavailable" && !modes.Contains(text)) modes.Add(text);
                    if (modeIndex == 8191) report.Notes.Add("Display mode enumeration reached its safety limit for " + adapter.Name + ".");
                }
                foreach (DisplayDevice monitor in monitors)
                {
                    string prefix = "Display " + (++displayed) + " / ";
                    section.Add(prefix + "Name", monitor.Description); section.Add(prefix + "Output", adapter.Name);
                    bool active = desktop && (monitor.Flags & 1) != 0;
                    section.Add(prefix + "State", active ? "Active desktop display" : "Detected / inactive");
                    section.Add(prefix + "Current resolution / refresh", active ? currentText : "Unavailable (inactive)");
                    section.Add(prefix + "Reported supported modes", modes.Count == 0 ? "Unavailable" : String.Join("\n", modes));
                }
            }
            if (displayed == 0) section.Add("Displays", "Unavailable");
            report.Notes.Add("Display modes and integer refresh rates are reported by Windows' display driver; remote/virtual displays may describe a session rather than a physical panel.");
        }

        public static SystemSpecificationsReport Capture()
        {
            SystemSpecificationsReport report = new SystemSpecificationsReport();
            SpecificationSection os = Section(report, "Operating System");
            Dictionary<string, string> system = Query("Win32_OperatingSystem", new[] { "Caption", "Version", "BuildNumber", "OSArchitecture" }, report).FirstOrDefault() ?? new Dictionary<string, string>();
            const string windows = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            os.Add("Operating system", V(system, "Caption").Length > 0 ? V(system, "Caption") : RegistryValue(windows, "ProductName"));
            os.Add("Edition ID", RegistryValue(windows, "EditionID")); os.Add("Version", V(system, "Version"));
            string build = V(system, "BuildNumber"); if (build.Length == 0) build = RegistryValue(windows, "CurrentBuildNumber");
            string revision = RegistryValue(windows, "UBR"); os.Add("Build", build + (build.Length > 0 && revision.Length > 0 ? "." + revision : ""));
            os.Add("Architecture", V(system, "OSArchitecture").Length > 0 ? V(system, "OSArchitecture") : Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");

            SpecificationSection processor = Section(report, "Processor");
            List<Dictionary<string, string>> cpus = Query("Win32_Processor", new[] { "Name", "Manufacturer", "DeviceID" }, report);
            if (cpus.Count == 0)
            {
                string name = RegistryValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
                processor.Add("Full model name", name); processor.Add("Manufacturer", RegistryValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "VendorIdentifier"));
                report.Notes.Add("If processor WMI access is unavailable, the registry fallback identifies the first reported processor only.");
            }
            for (int i = 0; i < cpus.Count; i++) { processor.Add("Processor " + (i + 1) + " / Full model", V(cpus[i], "Name")); processor.Add("Processor " + (i + 1) + " / Manufacturer", V(cpus[i], "Manufacturer")); }

            SpecificationSection graphics = Section(report, "Graphics Adapters");
            List<GpuSpecification> gpus = MergeGpus(NativeGpus(report), Query("Win32_VideoController", new[] { "Name", "AdapterCompatibility", "PNPDeviceID" }, report).Select(row => new GpuSpecification { Id = V(row, "PNPDeviceID"), Model = V(row, "Name"), Manufacturer = V(row, "AdapterCompatibility") }));
            graphics.Add("Detected adapters", gpus.Count == 0 ? "Unavailable" : gpus.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < gpus.Count; i++) { string prefix = "GPU " + (i + 1) + " / "; graphics.Add(prefix + "Model", gpus[i].Model); graphics.Add(prefix + "Manufacturer", gpus[i].Manufacturer); graphics.Add(prefix + "Device identity", gpus[i].Id); }
            report.Notes.Add("Graphics lists reported display-class adapters, including virtual/basic adapters when Windows exposes them. Names depend on installed drivers.");

            List<MemorySpecification> modules = new List<MemorySpecification>();
            try
            {
                uint size = GetSystemFirmwareTable(0x52534D42, 0, null, 0);
                if (size >= 8 && size <= 16 * 1024 * 1024) { byte[] data = new byte[size]; uint read = GetSystemFirmwareTable(0x52534D42, 0, data, size); if (read == size) modules = ParseMemoryTable(data); }
            }
            catch (Exception ex) { report.Notes.Add("SMBIOS memory data unavailable: " + ex.Message); }
            if (modules.Count == 0)
                foreach (Dictionary<string, string> row in Query("Win32_PhysicalMemory", new[] { "Capacity", "SMBIOSMemoryType", "ConfiguredClockSpeed", "DeviceLocator", "Manufacturer", "PartNumber" }, report))
                {
                    ulong? clock = Positive(V(row, "ConfiguredClockSpeed")), type = Positive(V(row, "SMBIOSMemoryType"));
                    modules.Add(new MemorySpecification { Capacity = Positive(V(row, "Capacity")), Locator = V(row, "DeviceLocator"), MemoryType = type.HasValue && type <= UInt32.MaxValue ? MemoryTypeName((uint)type.Value) : "Unavailable", Manufacturer = V(row, "Manufacturer"), PartNumber = V(row, "PartNumber"), ConfiguredClockMhz = clock.HasValue && clock <= UInt32.MaxValue ? (uint?)clock.Value : null });
                }
            ulong? installed = null; ulong installedKb;
            if (GetPhysicallyInstalledSystemMemory(out installedKb) && installedKb > 0 && installedKb <= UInt64.MaxValue / 1024) installed = installedKb * 1024;
            report.Sections.Add(MemorySection(modules, installed));
            report.Notes.Add("Installed RAM comes from Windows' firmware-backed installed-memory API or a complete module inventory, not available/usable RAM. SMBIOS speeds are MT/s; fallback WMI clock values retain their documented MHz unit without guessing a conversion.");

            SpecificationSection board = Section(report, "Motherboard"); const string biosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
            Dictionary<string, string> baseboard = Query("Win32_BaseBoard", new[] { "Manufacturer", "Product", "Version" }, report).FirstOrDefault() ?? new Dictionary<string, string>();
            board.Add("Manufacturer", V(baseboard, "Manufacturer").Length > 0 ? V(baseboard, "Manufacturer") : RegistryValue(biosKey, "BaseBoardManufacturer"));
            board.Add("Model", V(baseboard, "Product").Length > 0 ? V(baseboard, "Product") : RegistryValue(biosKey, "BaseBoardProduct"));
            board.Add("Version", V(baseboard, "Version").Length > 0 ? V(baseboard, "Version") : RegistryValue(biosKey, "BaseBoardVersion"));

            SpecificationSection firmware = Section(report, "BIOS / UEFI Firmware");
            firmware.Add("Manufacturer", RegistryValue(biosKey, "BIOSVendor")); firmware.Add("Version", RegistryValue(biosKey, "BIOSVersion")); firmware.Add("Release date", RegistryValue(biosKey, "BIOSReleaseDate"));
            uint firmwareType; bool hasFirmwareType = GetFirmwareType(out firmwareType);
            firmware.Add("Boot firmware mode", hasFirmwareType ? (firmwareType == 2 ? "UEFI" : firmwareType == 1 ? "Legacy BIOS" : "Unavailable") : "Unavailable");
            string secureBoot = RegistryValue(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            firmware.Add("Secure Boot (Windows reported)", hasFirmwareType && firmwareType == 1 ? "Not applicable (Legacy BIOS)" : secureBoot == "1" ? "Enabled" : secureBoot == "0" ? "Disabled" : "Unavailable");

            SpecificationSection storage = Section(report, "Storage");
            List<Dictionary<string, string>> disks = Query("Win32_DiskDrive", new[] { "Model", "Manufacturer", "Size", "DeviceID", "InterfaceType" }, report);
            if (disks.Count == 0) storage.Add("Disk models", "Unavailable");
            for (int i = 0; i < disks.Count; i++) { string prefix = "Disk " + (i + 1) + " / "; storage.Add(prefix + "Model", V(disks[i], "Model")); storage.Add(prefix + "Manufacturer", V(disks[i], "Manufacturer")); storage.Add(prefix + "Capacity", Bytes(Positive(V(disks[i], "Size")))); storage.Add(prefix + "Device", V(disks[i], "DeviceID")); storage.Add(prefix + "Reported interface", V(disks[i], "InterfaceType")); }

            ReadDisplays(Section(report, "Displays"), report);
            return report;
        }
    }
}
