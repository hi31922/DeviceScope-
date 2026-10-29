using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeviceScope
{
    public sealed class GpuEngineReading
    {
        public string Engine { get; set; }
        public double Percent { get; set; }
    }
    public sealed class PerformanceSample
    {
        public DateTime TimestampUtc { get; set; }
        public double? CpuPercent { get; set; }
        public double? GpuPercent { get; set; }
        public double? RamPercent { get; set; }
        public ulong TotalRamBytes { get; set; }
        public ulong AvailableRamBytes { get; set; }
        public string CpuNote { get; set; }
        public string GpuNote { get; set; }
        public string RamNote { get; set; }
        public List<GpuEngineReading> GpuEngines { get; set; }
    }
    public sealed class TemperatureReading
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public double Celsius { get; set; }
        public string DisplayName { get { return Name + " (" + Source + ")"; } }
    }
    public sealed class TemperatureSnapshot
    {
        public DateTime TimestampUtc { get; set; }
        public List<TemperatureReading> Readings { get; set; }
        public List<string> Notes { get; set; }
    }

    public sealed class PerformanceReader : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Ticks { get { return ((ulong)High << 32) | Low; } } }
        [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [StructLayout(LayoutKind.Explicit)] private struct PdhValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
        [StructLayout(LayoutKind.Sequential)] private struct PdhItem { public IntPtr Name; public PdhValue Counter; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
        private readonly object gate = new object();
        private IntPtr gpuQuery, gpuCounter;
        private bool disposed, cpuPrimed, gpuPrimed;
        private ulong previousIdle, previousKernel, previousUser;
        private DateTime gpuRetryUtc = DateTime.MinValue;
        private string gpuError = "Waiting for GPU counters";

        public static double? CpuFromDeltas(ulong idle, ulong kernel, ulong user)
        {
            double total = (double)kernel + user;
            if (total <= 0 || idle > total) return null;
            return Math.Max(0, Math.Min(100, 100.0 * (total - idle) / total));
        }
        public static List<GpuEngineReading> AggregateGpuEngines(IEnumerable<KeyValuePair<string, double>> counters)
        {
            Dictionary<string, double> engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> sample in counters)
            {
                Match match = Regex.Match(sample.Key ?? "", @"^pid_\d+_(luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+_eng_\d+_engtype_.+)$", RegexOptions.IgnoreCase);
                if (!match.Success || Double.IsNaN(sample.Value) || Double.IsInfinity(sample.Value) || sample.Value < 0) continue;
                string key = match.Groups[1].Value;
                double old; engines.TryGetValue(key, out old); engines[key] = old + sample.Value;
            }
            return engines.Select(pair => new GpuEngineReading { Engine = pair.Key, Percent = Math.Min(100, pair.Value) }).OrderByDescending(row => row.Percent).ToList();
        }
        public PerformanceSample Capture()
        {
            PerformanceSample sample = new PerformanceSample { TimestampUtc = DateTime.UtcNow, GpuEngines = new List<GpuEngineReading>() };
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("PerformanceReader");
                FileTime idle, kernel, user;
                if (GetSystemTimes(out idle, out kernel, out user))
                {
                    if (cpuPrimed && idle.Ticks >= previousIdle && kernel.Ticks >= previousKernel && user.Ticks >= previousUser)
                        sample.CpuPercent = CpuFromDeltas(idle.Ticks - previousIdle, kernel.Ticks - previousKernel, user.Ticks - previousUser);
                    sample.CpuNote = sample.CpuPercent.HasValue ? (GetActiveProcessorGroupCount() > 1 ? "CPU time for this process's processor group (multi-group system)" : "System CPU time over the sampling interval") : "Collecting the first interval...";
                    previousIdle = idle.Ticks; previousKernel = kernel.Ticks; previousUser = user.Ticks; cpuPrimed = true;
                }
                else sample.CpuNote = "CPU unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                MemoryStatus memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 && memory.AvailablePhysical <= memory.TotalPhysical)
                {
                    sample.TotalRamBytes = memory.TotalPhysical; sample.AvailableRamBytes = memory.AvailablePhysical;
                    sample.RamPercent = 100.0 * (memory.TotalPhysical - memory.AvailablePhysical) / memory.TotalPhysical;
                    sample.RamNote = String.Format(CultureInfo.CurrentCulture, "{0:N1} / {1:N1} GiB physical RAM used", (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0, memory.TotalPhysical / 1073741824.0);
                }
                else sample.RamNote = "Physical memory information unavailable";
                ReadGpu(sample);
            }
            return sample;
        }

        private void ReadGpu(PerformanceSample sample)
        {
            const uint MoreData = 0x800007D2;
            if (gpuQuery == IntPtr.Zero)
            {
                if (DateTime.UtcNow < gpuRetryUtc) { sample.GpuNote = gpuError; return; }
                uint open = PdhOpenQuery(null, IntPtr.Zero, out gpuQuery);
                if (open != 0) { FailGpu(open); sample.GpuNote = gpuError; return; }
                uint add = PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuCounter);
                if (add != 0) { FailGpu(add); sample.GpuNote = gpuError; return; }
            }
            uint collect = PdhCollectQueryData(gpuQuery);
            if (collect != 0) { FailGpu(collect); sample.GpuNote = gpuError; return; }
            if (!gpuPrimed) { gpuPrimed = true; sample.GpuNote = "Collecting the first GPU interval..."; return; }
            uint bytes = 0, count;
            uint status = PdhGetFormattedCounterArray(gpuCounter, 0x00000200 | 0x00008000, ref bytes, out count, IntPtr.Zero);
            if (status != MoreData || bytes == 0 || bytes > 16 * 1024 * 1024)
            {
                sample.GpuNote = "GPU counters have no usable samples (0x" + status.ToString("X8") + "). Requires a supported WDDM driver."; return;
            }
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                status = PdhGetFormattedCounterArray(gpuCounter, 0x00000200 | 0x00008000, ref bytes, out count, buffer);
                if (status != 0) { sample.GpuNote = "GPU sample unavailable (0x" + status.ToString("X8") + ")"; return; }
                int itemSize = Marshal.SizeOf(typeof(PdhItem));
                if ((ulong)count * (uint)itemSize > bytes) { sample.GpuNote = "Invalid GPU counter array"; return; }
                List<KeyValuePair<string, double>> values = new List<KeyValuePair<string, double>>();
                for (int i = 0; i < count; i++)
                {
                    PdhItem item = (PdhItem)Marshal.PtrToStructure(IntPtr.Add(buffer, i * itemSize), typeof(PdhItem));
                    if (item.Counter.Status <= 1 && item.Name != IntPtr.Zero) values.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(item.Name), item.Counter.Value));
                }
                sample.GpuEngines = AggregateGpuEngines(values);
                if (sample.GpuEngines.Count > 0)
                {
                    sample.GpuPercent = sample.GpuEngines[0].Percent;
                    sample.GpuNote = "Busiest engine across GPUs; per-process counters grouped by physical engine";
                }
                else sample.GpuNote = "No valid GPU-engine readings; unavailable is not 0%";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private void FailGpu(uint status)
        {
            if (gpuQuery != IntPtr.Zero) PdhCloseQuery(gpuQuery);
            gpuQuery = gpuCounter = IntPtr.Zero; gpuPrimed = false; gpuRetryUtc = DateTime.UtcNow.AddSeconds(30);
            gpuError = "GPU counter unavailable / access denied (0x" + status.ToString("X8") + "). Retrying after 30 seconds; requires a supported WDDM driver.";
        }
        public void ResetIntervals()
        {
            lock (gate) { cpuPrimed = false; gpuPrimed = false; }
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; if (gpuQuery != IntPtr.Zero) PdhCloseQuery(gpuQuery); gpuQuery = gpuCounter = IntPtr.Zero; }
        }
    }

    public static class TemperatureReader
    {
        public static double? AcpiToCelsius(double tenthsKelvin)
        {
            double temperature = tenthsKelvin / 10.0 - 273.15;
            return tenthsKelvin <= 0 || !Plausible(temperature) ? (double?)null : temperature;
        }
        private static bool Plausible(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= -100 && value <= 200; }
        public static TemperatureSnapshot Capture()
        {
            TemperatureSnapshot result = new TemperatureSnapshot { TimestampUtc = DateTime.UtcNow, Readings = new List<TemperatureReading>(), Notes = new List<string>() };
            foreach (string provider in new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" })
            {
                try
                {
                    Query("root\\" + provider, "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType='Temperature'", delegate(ManagementObject sensor)
                    {
                        if (sensor["Value"] == null) return;
                        double temperature = Convert.ToDouble(sensor["Value"], CultureInfo.InvariantCulture);
                        if (Plausible(temperature)) result.Readings.Add(new TemperatureReading { Id = provider + ":" + Convert.ToString(sensor["Identifier"]), Name = Convert.ToString(sensor["Name"]), Source = provider, Celsius = temperature });
                    });
                }
                catch (Exception ex) { result.Notes.Add(provider + ": " + ex.Message); }
            }
            try
            {
                Query("root\\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", delegate(ManagementObject sensor)
                {
                    if (sensor["CurrentTemperature"] == null) return;
                    double? temperature = AcpiToCelsius(Convert.ToDouble(sensor["CurrentTemperature"], CultureInfo.InvariantCulture));
                    if (temperature.HasValue) result.Readings.Add(new TemperatureReading { Id = "ACPI:" + Convert.ToString(sensor["InstanceName"]), Name = Convert.ToString(sensor["InstanceName"]), Source = "ACPI thermal zone; not necessarily CPU", Celsius = temperature.Value });
                });
            }
            catch (Exception ex) { result.Notes.Add("ACPI thermal zones: " + ex.Message); }
            if (result.Readings.Count == 0) result.Notes.Insert(0, "No readable temperature sensors. This PC may not expose them, or the provider may require permission. Optional monitor providers are read only if already running; nothing is installed.");
            return result;
        }
        private static void Query(string scope, string query, Action<ManagementObject> inspect)
        {
            EnumerationOptions options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(2) };
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options))
            using (ManagementObjectCollection rows = searcher.Get())
                foreach (ManagementObject row in rows) using (row) inspect(row);
        }
    }
}
