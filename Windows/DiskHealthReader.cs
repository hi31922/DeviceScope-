using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace DeviceScope
{
    public sealed class DiskHealthDetail { public string Label { get; set; } public string Value { get; set; } }
    public sealed class PhysicalDiskHealth
    {
        public string Id { get; set; } public string Name { get; set; } public string MediaType { get; set; } public string BusType { get; set; }
        public string Health { get; set; } public string Capacity { get; set; } public string Temperature { get; set; } public string Source { get; set; }
        public List<DiskHealthDetail> Details { get; set; }
        public PhysicalDiskHealth() { Details = new List<DiskHealthDetail>(); }
    }
    public sealed class DiskVolumeUsage
    {
        public string Name { get; set; } public string FileSystem { get; set; } public string Total { get; set; } public string Free { get; set; }
        public string Used { get; set; } public string Status { get; set; } public bool HasUsage { get; set; } public double UsedPercent { get; set; }
    }
    public sealed class SmartAttributeRow
    {
        public int Id { get; set; } public string Attribute { get; set; } public int Current { get; set; } public int Worst { get; set; }
        public string Threshold { get; set; } public string RawHex { get; set; } public string RawDecimal { get; set; } public ulong RawValue { get; set; }
    }
    public sealed class DiskSmartInstance
    {
        public string InstanceName { get; set; } public string Prediction { get; set; } public string DataStatus { get; set; }
        public string RawPayloadHex { get; set; } public List<SmartAttributeRow> Attributes { get; set; }
        public DiskSmartInstance() { Attributes = new List<SmartAttributeRow>(); Prediction = "Prediction unavailable"; DataStatus = "Raw SMART data unavailable"; }
        public override string ToString() { return InstanceName; }
    }
    public sealed class DiskHealthSnapshot
    {
        public DateTime CapturedAt { get; set; } public List<PhysicalDiskHealth> Disks { get; set; } public List<DiskVolumeUsage> Volumes { get; set; }
        public List<DiskSmartInstance> SmartInstances { get; set; } public List<string> Warnings { get; set; }
        public DiskHealthSnapshot() { Disks = new List<PhysicalDiskHealth>(); Volumes = new List<DiskVolumeUsage>(); SmartInstances = new List<DiskSmartInstance>(); Warnings = new List<string>(); }
    }

    public static class DiskHealthReader
    {
        private const string StorageScope = "root\\Microsoft\\Windows\\Storage";
        private sealed class ScanBudget
        {
            public readonly Stopwatch Watch = Stopwatch.StartNew(); public readonly CancellationToken Token; public bool Expired;
            public ScanBudget(CancellationToken token) { Token = token; }
            public bool Continue() { Token.ThrowIfCancellationRequested(); if (Watch.Elapsed.TotalSeconds < 24) return true; Expired = true; return false; }
        }
        public static DiskHealthSnapshot Capture(CancellationToken token)
        {
            DiskHealthSnapshot snapshot = new DiskHealthSnapshot { CapturedAt = DateTime.Now };
            ScanBudget budget = new ScanBudget(token);
            try { snapshot.Volumes = CaptureVolumes(); }
            catch (Exception ex) { snapshot.Warnings.Add("Local volumes: " + Explain(ex)); }
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (Dictionary<string, object> row in Query(StorageScope, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk", budget, 32))
                {
                    PhysicalDiskHealth disk = new PhysicalDiskHealth
                    {
                        Id = Text(row, "DeviceId"), Name = Text(row, "FriendlyName"), MediaType = MediaName(Number(row, "MediaType")), BusType = BusName(Number(row, "BusType")),
                        Health = HealthName(Number(row, "HealthStatus")), Capacity = FormatBytes(Unsigned(row, "Size")), Temperature = "Not reported", Source = "Windows Storage provider"
                    };
                    if (String.IsNullOrWhiteSpace(disk.Name)) disk.Name = "Physical disk " + disk.Id;
                    Detail(disk, "Data source", "MSFT_PhysicalDisk (Windows Storage provider)"); Detail(disk, "Provider device ID", disk.Id);
                    Detail(disk, "Health", disk.Health); Detail(disk, "Operational status codes", Text(row, "OperationalStatus"));
                    Detail(disk, "Capacity", disk.Capacity); Detail(disk, "Media / bus", disk.MediaType + " / " + disk.BusType);
                    if (budget.Continue()) ReadReliability(row, disk, budget); else Detail(disk, "Reliability counters", "Not read: scan time budget reached");
                    snapshot.Disks.Add(disk);
                }
                if (snapshot.Disks.Count == 0) snapshot.Warnings.Add("Windows Storage provider returned no physical disks.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { snapshot.Warnings.Add("Physical-disk health: " + Explain(ex)); }
            // Win32 is a separately labelled fallback. Its disk numbers are not
            // guessed to be Windows Storage device IDs, and Status is not SMART.
            if (snapshot.Disks.Count == 0 && budget.Continue())
            {
                try
                {
                    foreach (Dictionary<string, object> row in Query("root\\CIMV2", "SELECT Index, Model, InterfaceType, Size, Status, PNPDeviceID FROM Win32_DiskDrive", budget, 32))
                    {
                        PhysicalDiskHealth disk = new PhysicalDiskHealth { Id = Text(row, "Index"), Name = Text(row, "Model"), MediaType = "Not reported", BusType = Text(row, "InterfaceType"), Health = "SMART health unavailable", Capacity = FormatBytes(Unsigned(row, "Size")), Temperature = "Not reported", Source = "Win32 inventory fallback" };
                        Detail(disk, "Data source", "Win32_DiskDrive inventory fallback"); Detail(disk, "Windows disk index", disk.Id); Detail(disk, "PNP device ID", Text(row, "PNPDeviceID"));
                        Detail(disk, "Win32 status (not SMART health)", Text(row, "Status")); Detail(disk, "Health / temperature", "Storage reliability provider unavailable; no health percentage is inferred."); snapshot.Disks.Add(disk);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Warnings.Add("Disk inventory fallback: " + Explain(ex)); }
            }
            ReadSmart(snapshot, budget);
            if (budget.Expired) snapshot.Warnings.Add("Scan time budget reached; some devices or data sources were not read.");
            snapshot.CapturedAt = DateTime.Now; return snapshot;
        }
        private static void ReadReliability(Dictionary<string, object> physical, PhysicalDiskHealth disk, ScanBudget budget)
        {
            try
            {
                // Authoritative Windows association, not a guessed disk number.
                string query = "ASSOCIATORS OF {" + Text(physical, "__RELPATH") + "} WHERE AssocClass = MSFT_PhysicalDiskToStorageReliabilityCounter ResultClass = MSFT_StorageReliabilityCounter";
                List<Dictionary<string, object>> counters = Query(StorageScope, query, budget, 3);
                if (counters.Count != 1) { Detail(disk, "Reliability counters", counters.Count == 0 ? "Not exposed by this device/driver, or no associated counter record" : "Multiple counter records: not merged into one device reading"); return; }
                Dictionary<string, object> values = counters[0]; disk.Temperature = TemperatureDisplay(Number(values, "Temperature"));
                Detail(disk, "Temperature", disk.Temperature); Detail(disk, "Temperature limit (reported)", TemperatureDisplay(Number(values, "TemperatureMax")));
                long? wear = Number(values, "Wear"); Detail(disk, "Wear indicator (lifetime used)", wear.HasValue ? wear.Value + "% reported; 100% is the estimated wear limit" : "Not reported");
                Detail(disk, "Power-on hours", Text(values, "PowerOnHours")); Detail(disk, "Uncorrected read errors", Text(values, "ReadErrorsUncorrected")); Detail(disk, "Uncorrected write errors", Text(values, "WriteErrorsUncorrected"));
                Detail(disk, "Total read / write errors", Display(Text(values, "ReadErrorsTotal")) + " / " + Display(Text(values, "WriteErrorsTotal")));
                Detail(disk, "Maximum read / write latency (ms)", Display(Text(values, "ReadLatencyMax")) + " / " + Display(Text(values, "WriteLatencyMax")));
                Detail(disk, "Reliability source", "MSFT_PhysicalDiskToStorageReliabilityCounter association");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Detail(disk, "Reliability counters", Explain(ex)); }
        }
        public static List<DiskVolumeUsage> CaptureVolumes()
        {
            List<DiskVolumeUsage> result = new List<DiskVolumeUsage>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                DiskVolumeUsage volume = new DiskVolumeUsage { Name = drive.Name, FileSystem = "Unavailable", Total = "Unavailable", Free = "Unavailable", Used = "Unavailable", Status = "Not ready" };
                try
                {
                    // Do not make network filesystem calls or spin up optical media.
                    DriveType type = drive.DriveType; if (type != DriveType.Fixed && type != DriveType.Removable && type != DriveType.Ram) continue;
                    if (drive.IsReady)
                    {
                        long total = drive.TotalSize, free = drive.TotalFreeSpace;
                        if (total > 0 && free >= 0 && free <= total) { volume.HasUsage = true; volume.UsedPercent = 100.0 * (total - free) / total; volume.Total = FormatBytes((ulong)total); volume.Free = FormatBytes((ulong)free); volume.Used = FormatBytes((ulong)(total - free)) + " (" + volume.UsedPercent.ToString("0.0") + "%)"; }
                        volume.FileSystem = drive.DriveFormat; volume.Status = type.ToString() + " volume";
                    }
                }
                catch (Exception ex) { volume.Status = Explain(ex); }
                result.Add(volume);
            }
            return result;
        }
        private static void ReadSmart(DiskHealthSnapshot snapshot, ScanBudget budget)
        {
            Dictionary<string, DiskSmartInstance> instances = new Dictionary<string, DiskSmartInstance>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, byte[]> thresholds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (budget.Continue())
            {
                try
                {
                    foreach (Dictionary<string, object> row in Query("root\\WMI", "SELECT InstanceName, Active, PredictFailure FROM MSStorageDriver_FailurePredictStatus", budget, 64))
                    {
                        string name = Text(row, "InstanceName"); if (name.Length == 0) continue;
                        DiskSmartInstance instance = SmartInstance(instances, name); bool? active = Boolean(row, "Active"), failure = Boolean(row, "PredictFailure");
                        instance.Prediction = active == false ? "Provider inactive; prediction unavailable" : failure == true ? "FAILURE PREDICTED by the device" : failure == false ? "No failure predicted (not a health guarantee)" : "Prediction unavailable";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Warnings.Add("SMART prediction: " + Explain(ex)); }
            }
            if (budget.Continue())
            {
                try
                {
                    foreach (Dictionary<string, object> row in Query("root\\WMI", "SELECT InstanceName, Active, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", budget, 64))
                    { string name = Text(row, "InstanceName"); if (name.Length > 0 && Boolean(row, "Active") != false) thresholds[name] = Bytes(row, "VendorSpecific"); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Warnings.Add("SMART thresholds: " + Explain(ex)); }
            }
            if (budget.Continue())
            {
                try
                {
                    foreach (Dictionary<string, object> row in Query("root\\WMI", "SELECT InstanceName, Active, VendorSpecific FROM MSStorageDriver_FailurePredictData", budget, 64))
                    {
                        string name = Text(row, "InstanceName"); if (name.Length == 0) continue;
                        DiskSmartInstance instance = SmartInstance(instances, name); byte[] payload = Bytes(row, "VendorSpecific"), limits; thresholds.TryGetValue(name, out limits);
                        instance.RawPayloadHex = payload == null ? "Unavailable" : BitConverter.ToString(payload).Replace('-', ' ');
                        if (Boolean(row, "Active") == false) { instance.DataStatus = "Provider inactive; raw bytes retained without interpretation"; continue; }
                        instance.Attributes = ParseAtaAttributes(payload, limits);
                        instance.DataStatus = instance.Attributes.Count > 0 ? "Common ATA layout interpretation of vendor-specific bytes; values and thresholds are device-dependent." : "No supported ATA attribute layout; raw provider bytes remain available.";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Warnings.Add("SMART raw data: " + Explain(ex)); }
            }
            snapshot.SmartInstances = instances.Values.OrderBy(item => item.InstanceName).ToList();
            if (instances.Count == 0) snapshot.Warnings.Add("No readable ATA SMART instances. NVMe, USB and RAID devices may not expose this legacy interface; this does not establish disk health.");
        }
        // Common ATA layout: 30 twelve-byte records after a two-byte revision.
        // Vendor-specific raw values never become a fabricated health score.
        public static List<SmartAttributeRow> ParseAtaAttributes(byte[] data, byte[] thresholds)
        {
            List<SmartAttributeRow> result = new List<SmartAttributeRow>(); if (data == null || data.Length < 362) return result;
            Dictionary<int, byte> limits = new Dictionary<int, byte>();
            if (thresholds != null && thresholds.Length >= 362) for (int offset = 2; offset + 12 <= 362; offset += 12) if (thresholds[offset] != 0) limits[thresholds[offset]] = thresholds[offset + 1];
            HashSet<int> seen = new HashSet<int>();
            for (int offset = 2; offset + 12 <= 362; offset += 12)
            {
                int id = data[offset]; if (id == 0 || !seen.Add(id)) continue; ulong raw = 0; for (int j = 0; j < 6; j++) raw |= (ulong)data[offset + 5 + j] << (8 * j); byte limit;
                result.Add(new SmartAttributeRow { Id = id, Attribute = "0x" + id.ToString("X2") + " (vendor-defined)", Current = data[offset + 3], Worst = data[offset + 4], Threshold = limits.TryGetValue(id, out limit) ? limit.ToString(CultureInfo.InvariantCulture) : "Unavailable", RawHex = BitConverter.ToString(data, offset + 5, 6).Replace('-', ' '), RawDecimal = raw.ToString(CultureInfo.InvariantCulture), RawValue = raw });
            }
            return result;
        }
        private static DiskSmartInstance SmartInstance(Dictionary<string, DiskSmartInstance> items, string name) { DiskSmartInstance instance; if (!items.TryGetValue(name, out instance)) { instance = new DiskSmartInstance { InstanceName = name }; items.Add(name, instance); } return instance; }
        private static List<Dictionary<string, object>> Query(string scope, string query, ScanBudget budget, int maximum)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(); if (!budget.Continue()) return result;
            EnumerationOptions options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) };
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options))
            using (ManagementObjectCollection items = searcher.Get()) foreach (ManagementObject item in items) using (item)
            {
                budget.Token.ThrowIfCancellationRequested(); Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties) values[property.Name] = property.Value;
                values["__RELPATH"] = item.Path.RelativePath; result.Add(values); if (result.Count >= maximum || !budget.Continue()) break;
            }
            return result;
        }
        private static string Text(Dictionary<string, object> row, string key) { object value; if (!row.TryGetValue(key, out value) || value == null) return ""; Array array = value as Array; return array == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Join(", ", array.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))); }
        private static long? Number(Dictionary<string, object> row, string key) { long value; return Int64.TryParse(Text(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? (long?)value : null; }
        private static ulong? Unsigned(Dictionary<string, object> row, string key) { ulong value; return UInt64.TryParse(Text(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? (ulong?)value : null; }
        private static bool? Boolean(Dictionary<string, object> row, string key) { bool value; return System.Boolean.TryParse(Text(row, key), out value) ? (bool?)value : null; }
        private static byte[] Bytes(Dictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? value as byte[] : null; }
        private static void Detail(PhysicalDiskHealth disk, string label, string value) { disk.Details.Add(new DiskHealthDetail { Label = label, Value = Display(value) }); }
        private static string Display(string value) { return String.IsNullOrWhiteSpace(value) ? "Not reported" : value; }
        public static string HealthName(long? code) { if (!code.HasValue) return "Not reported"; switch (code.Value) { case 0: return "Healthy (Windows-reported)"; case 1: return "Warning"; case 2: return "Unhealthy"; case 5: return "Unknown"; default: return "Provider health code " + code.Value; } }
        public static string TemperatureDisplay(long? value) { if (!value.HasValue) return "Not reported"; return value.Value > 0 && value.Value <= 125 ? value.Value + " °C" : "Unusable / unknown reading (reported " + value.Value + ")"; }
        public static string FormatBytes(ulong? value) { return value.HasValue ? (value.Value / 1073741824.0).ToString("N1", CultureInfo.CurrentCulture) + " GiB" : "Not reported"; }
        private static string MediaName(long? value) { return value == 3 ? "HDD" : value == 4 ? "SSD" : value == 5 ? "Storage-class memory" : "Unspecified"; }
        private static string BusName(long? value) { string[] names = { "Unknown", "SCSI", "ATAPI", "ATA", "IEEE 1394", "SSA", "Fibre Channel", "USB", "RAID", "iSCSI", "SAS", "SATA", "SD", "MMC", "Reserved", "File-backed virtual", "Storage Spaces", "NVMe" }; return value.HasValue && value.Value >= 0 && value.Value < names.Length ? names[value.Value] : "Unknown"; }
        private static string Explain(Exception ex)
        {
            ManagementException management = ex as ManagementException;
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || management != null && management.ErrorCode == ManagementStatus.AccessDenied) return "Access denied by Windows/provider";
            if (management != null && (management.ErrorCode == ManagementStatus.InvalidClass || management.ErrorCode == ManagementStatus.InvalidNamespace || management.ErrorCode == ManagementStatus.NotSupported)) return "Not supported or not exposed by this Windows/provider";
            return "Unavailable: " + ex.Message;
        }
    }
}
