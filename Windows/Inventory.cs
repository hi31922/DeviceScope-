using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Web.Script.Serialization;

namespace DeviceScope
{
    public sealed class DeviceRow
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string PnpClass { get; set; }
        public string ClassGuid { get; set; }
        public string EnumeratorName { get; set; }
        public string ParentDeviceId { get; set; }
        public string BusTypeGuid { get; set; }
        public uint? NetworkCharacteristics { get; set; }
        public string DeviceType { get; set; }
        public string TypeEvidence { get; set; }
        public string Manufacturer { get; set; }
        public string Status { get; set; }
        public int? ErrorCode { get; set; }
        public string DeviceId { get; set; }
        public string HardwareIds { get; set; }
        public string Service { get; set; }
        public string DriverVersion { get; set; }
        public string DriverDate { get; set; }
        public string DriverProvider { get; set; }
        public string DriverInf { get; set; }
        public string DriverSigned { get; set; }
        public string MacAddress { get; set; }
        public string LinkState { get; set; }
        public string LinkSpeed { get; set; }
        public string ConnectionName { get; set; }
        [ScriptIgnore] public string Comparison { get; set; }
        [ScriptIgnore] public string ChangeDetails { get; set; }
        [ScriptIgnore] public bool IsRemoved { get; set; }
        [ScriptIgnore] public bool HasProblem { get { return (ErrorCode.HasValue && ErrorCode.Value != 0) || !String.IsNullOrEmpty(Status) && ((!ErrorCode.HasValue && Status.IndexOf("code ", StringComparison.OrdinalIgnoreCase) >= 0) || String.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase) || String.Equals(Status, "Degraded", StringComparison.OrdinalIgnoreCase)); } }
    }

    public sealed class SnapshotReport
    {
        public int SchemaVersion { get; set; }
        public string GeneratedAt { get; set; }
        public bool? DriverInventoryAvailable { get; set; }
        public List<DeviceRow> Devices { get; set; }
        public List<string> Warnings { get; set; }
        public SnapshotReport() { Warnings = new List<string>(); }
    }

    public sealed class ComparisonResult
    {
        public List<DeviceRow> Rows { get; set; }
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
    }

    public static class InventoryReader
    {
        private sealed class DriverInfo
        {
            public string Version, Date, Provider, Inf, Signed;
        }
        private sealed class NetworkInfo
        {
            public string Mac, State, Speed, Connection;
        }

        public static SnapshotReport Capture()
        {
            SnapshotReport result = new SnapshotReport { SchemaVersion = 2, GeneratedAt = DateTimeOffset.Now.ToString("o"), DriverInventoryAvailable = true, Devices = new List<DeviceRow>() };
            DeviceMetadataSnapshot metadata = DeviceMetadataReader.Capture();
            result.Warnings.AddRange(metadata.Warnings);
            Dictionary<string, DriverInfo> drivers = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Query("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned FROM Win32_PnPSignedDriver", delegate(ManagementObject item)
                {
                    string id = Value(item, "DeviceID");
                    if (id.Length > 0) drivers[id] = new DriverInfo { Version = Value(item, "DriverVersion"), Date = FormatDate(Value(item, "DriverDate")), Provider = Value(item, "DriverProviderName"), Inf = Value(item, "InfName"), Signed = Value(item, "IsSigned") };
                });
            }
            catch (Exception ex) { result.DriverInventoryAvailable = false; result.Warnings.Add("Driver inventory unavailable: " + ex.Message); }

            Dictionary<string, NetworkInfo> networks = new Dictionary<string, NetworkInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Query("SELECT PNPDeviceID, MACAddress, NetConnectionStatus, Speed, NetConnectionID FROM Win32_NetworkAdapter", delegate(ManagementObject item)
                {
                    string id = Value(item, "PNPDeviceID");
                    if (id.Length > 0) networks[id] = new NetworkInfo { Mac = Value(item, "MACAddress"), State = NetworkState(Value(item, "NetConnectionStatus")), Speed = FormatSpeed(Value(item, "Speed")), Connection = Value(item, "NetConnectionID") };
                });
            }
            catch (Exception ex) { result.Warnings.Add("Network details unavailable: " + ex.Message); }

            Query("SELECT Name, PNPClass, Manufacturer, Status, DeviceID, ConfigManagerErrorCode, HardwareID, Service FROM Win32_PnPEntity", delegate(ManagementObject item)
            {
                string id = Value(item, "DeviceID");
                if (id.Length == 0) return;
                int error;
                int? errorCode = Int32.TryParse(Value(item, "ConfigManagerErrorCode"), out error) ? (int?)error : null;
                DriverInfo driver; NetworkInfo network;
                drivers.TryGetValue(id, out driver); networks.TryGetValue(id, out network);
                string pnpClass = Value(item, "PNPClass");
                string name = Value(item, "Name");
                result.Devices.Add(new DeviceRow
                {
                    Name = name.Length == 0 ? id : name,
                    DeviceId = id, PnpClass = pnpClass,
                    Class = pnpClass.Length == 0 ? "Other" : pnpClass == "Net" ? "Network" : pnpClass,
                    Manufacturer = Value(item, "Manufacturer"), ErrorCode = errorCode,
                    Status = DescribeStatus(Value(item, "Status"), errorCode),
                    HardwareIds = Value(item, "HardwareID"), Service = Value(item, "Service"),
                    DriverVersion = driver == null ? "" : driver.Version,
                    DriverDate = driver == null ? "" : driver.Date,
                    DriverProvider = driver == null ? "" : driver.Provider,
                    DriverInf = driver == null ? "" : driver.Inf,
                    DriverSigned = driver == null ? "" : driver.Signed,
                    MacAddress = network == null ? "" : network.Mac,
                    LinkState = network == null ? "" : network.State,
                    LinkSpeed = network == null ? "" : network.Speed,
                    ConnectionName = network == null ? "" : network.Connection,
                    Comparison = "Not compared"
                });
            });
            foreach (DeviceRow row in result.Devices) DeviceClassifier.Apply(row, metadata.Devices);
            result.Devices = result.Devices.GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return result;
        }

        private static void Query(string query, Action<ManagementObject> use)
        {
            EnumerationOptions options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(25) };
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"), new ObjectQuery(query), options))
            using (ManagementObjectCollection rows = searcher.Get())
                foreach (ManagementObject row in rows) using (row) use(row);
        }

        private static string Value(ManagementBaseObject item, string property)
        {
            object value = item[property];
            string[] array = value as string[];
            return array != null ? String.Join(Environment.NewLine, array) : value == null ? "" : Convert.ToString(value);
        }
        private static string FormatDate(string value) { return value.Length >= 8 ? value.Substring(0, 4) + "-" + value.Substring(4, 2) + "-" + value.Substring(6, 2) : value; }
        private static string FormatSpeed(string value) { ulong speed; return UInt64.TryParse(value, out speed) && speed > 0 && speed < UInt64.MaxValue ? (speed / 1000000.0).ToString("0.#") + " Mbps" : ""; }
        private static string NetworkState(string value)
        {
            string[] states = { "Disconnected", "Connecting", "Connected", "Disconnecting", "Hardware not present", "Hardware disabled", "Hardware malfunction", "Media disconnected", "Authenticating", "Authentication succeeded", "Authentication failed", "Invalid address", "Credentials required" };
            int state; return Int32.TryParse(value, out state) && state >= 0 && state < states.Length ? states[state] : value.Length == 0 ? "Unknown" : "State " + value;
        }
        public static string DescribeStatus(string status, int? code)
        {
            if (!code.HasValue || code.Value == 0) return String.IsNullOrWhiteSpace(status) ? "Unknown" : status;
            switch (code.Value)
            {
                case 10: return "Cannot start (code 10)";
                case 22: return "Disabled (code 22)";
                case 24: return "Not present / not working (code 24)";
                case 28: return "Driver missing (code 28)";
                case 31: return "Driver cannot load (code 31)";
                case 43: return "Windows stopped device (code 43)";
                default: return "Issue (code " + code.Value + ")";
            }
        }
    }

    public static class SnapshotStore
    {
        private const int MaximumBytes = 16 * 1024 * 1024;
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 64 }; }
        public static void Save(string path, SnapshotReport report)
        {
            Validate(report);
            string json = Serializer().Serialize(report);
            byte[] data = new UTF8Encoding(false).GetBytes(json);
            if (data.Length > MaximumBytes) throw new InvalidDataException("Snapshot exceeds the 16 MB limit.");
            string target = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".devicescope-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = null;
            bool committed = false;
            try
            {
                // Write beside the destination and commit only after a complete,
                // flushed write, so an I/O failure cannot truncate an older export.
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (File.Exists(target))
                {
                    backup = Path.Combine(Path.GetDirectoryName(target), ".devicescope-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try { File.Replace(temporary, target, backup); }
                    catch (Exception failure)
                    {
                        // ReplaceFile can fail after renaming the old destination.
                        // Restore it if possible; never discard its recovery copy.
                        if (File.Exists(backup) && !File.Exists(target))
                        {
                            try { File.Move(backup, target); backup = null; }
                            catch (IOException) { throw new IOException("Snapshot export failed. The previous snapshot was kept at: " + backup, failure); }
                            catch (UnauthorizedAccessException) { throw new IOException("Snapshot export failed. The previous snapshot was kept at: " + backup, failure); }
                        }
                        if (!File.Exists(target) && File.Exists(temporary))
                        {
                            string recovery = temporary; temporary = null;
                            throw new IOException("Snapshot export failed. A completed recovery export was kept at: " + recovery, failure);
                        }
                        if (backup != null && File.Exists(backup))
                            throw new IOException("Snapshot export failed. The previous snapshot was kept at: " + backup, failure);
                        throw;
                    }
                }
                else File.Move(temporary, target);
                temporary = null; committed = true;
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (committed && backup != null)
                {
                    try { File.Delete(backup); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        public static SnapshotReport Load(string path)
        {
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Snapshot exceeds the 16 MB limit.");
            SnapshotReport report = Serializer().Deserialize<SnapshotReport>(File.ReadAllText(path, Encoding.UTF8));
            Validate(report);
            if (report.Warnings == null) report.Warnings = new List<string>();
            // Legacy snapshots lack type metadata: classify saved fields only, never
            // attach today's live topology to a historical device record.
            foreach (DeviceRow row in report.Devices) if (String.IsNullOrWhiteSpace(row.DeviceType) || String.IsNullOrWhiteSpace(row.TypeEvidence)) DeviceClassifier.Apply(row);
            return report;
        }
        private static void Validate(SnapshotReport report)
        {
            if (report == null || report.Devices == null || String.IsNullOrWhiteSpace(report.GeneratedAt)) throw new InvalidDataException("Select a DeviceScope inventory snapshot.");
            if (report.SchemaVersion > 2) throw new InvalidDataException("This snapshot needs a newer DeviceScope version.");
            DateTimeOffset generatedAt;
            if (report.SchemaVersion < 0 || !DateTimeOffset.TryParse(report.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out generatedAt)) throw new InvalidDataException("Snapshot contains an invalid version or capture timestamp.");
            if (report.Devices.Any(d => d == null || String.IsNullOrWhiteSpace(d.DeviceId))) throw new InvalidDataException("Snapshot contains an invalid device record.");
            if (report.Devices.GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException("Snapshot contains duplicate device IDs.");
        }
        private static bool Same(string a, string b) { return String.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }
        private static int? StatusCode(DeviceRow row)
        {
            if (row.ErrorCode.HasValue) return row.ErrorCode;
            string status = row.Status ?? "";
            if (Same(status, "OK")) return 0;
            int index = status.IndexOf("code ", StringComparison.OrdinalIgnoreCase);
            int parsed;
            return index >= 0 && Int32.TryParse(new string(status.Substring(index + 5).TakeWhile(Char.IsDigit).ToArray()), out parsed) ? (int?)parsed : null;
        }
        public static ComparisonResult Compare(SnapshotReport current, SnapshotReport previous)
        {
            Dictionary<string, DeviceRow> old = previous.Devices.ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
            ComparisonResult result = new ComparisonResult { Rows = new List<DeviceRow>() };
            bool driversAvailable = current.DriverInventoryAvailable != false && previous.DriverInventoryAvailable != false;
            foreach (DeviceRow row in current.Devices)
            {
                row.IsRemoved = false; row.ChangeDetails = "";
                DeviceRow before;
                if (!old.TryGetValue(row.DeviceId, out before)) { row.Comparison = "New device"; result.Added++; }
                else
                {
                    old.Remove(row.DeviceId);
                    List<string> changes = new List<string>();
                    if (driversAvailable)
                    {
                        if (!Same(row.DriverVersion, before.DriverVersion)) changes.Add("Driver version: " + Display(before.DriverVersion) + " -> " + Display(row.DriverVersion));
                        if (!Same(row.DriverDate, before.DriverDate)) changes.Add("Driver date: " + Display(before.DriverDate) + " -> " + Display(row.DriverDate));
                        if (before.DriverProvider != null && !Same(row.DriverProvider, before.DriverProvider)) changes.Add("Driver provider: " + Display(before.DriverProvider) + " -> " + Display(row.DriverProvider));
                        if (before.DriverInf != null && !Same(row.DriverInf, before.DriverInf)) changes.Add("Driver INF: " + Display(before.DriverInf) + " -> " + Display(row.DriverInf));
                        if (before.DriverSigned != null && !Same(row.DriverSigned, before.DriverSigned)) changes.Add("Driver signing: " + Display(before.DriverSigned) + " -> " + Display(row.DriverSigned));
                    }
                    int? currentCode = StatusCode(row), oldCode = StatusCode(before);
                    bool statusChanged = currentCode.HasValue && oldCode.HasValue
                        ? currentCode.Value != oldCode.Value || (currentCode.Value == 0 && !Same(row.Status, before.Status))
                        : !Same(row.Status, before.Status);
                    if (statusChanged) changes.Add("Status: " + Display(before.Status) + " -> " + Display(row.Status));
                    if (!Same(row.Name, before.Name)) changes.Add("Name: " + Display(before.Name) + " -> " + Display(row.Name));
                    row.ChangeDetails = String.Join(Environment.NewLine, changes);
                    if (!driversAvailable) row.ChangeDetails += (row.ChangeDetails.Length == 0 ? "" : Environment.NewLine) + "Driver changes could not be checked because one scan has no driver inventory.";
                    row.Comparison = changes.Count > 0 ? "Changed" : driversAvailable ? "Unchanged" : "Incomplete";
                    if (changes.Count > 0) result.Changed++;
                }
                result.Rows.Add(row);
            }
            foreach (DeviceRow removed in old.Values)
            {
                // Clone so repeated comparisons cannot mutate the loaded baseline.
                DeviceRow row = Serializer().Deserialize<DeviceRow>(Serializer().Serialize(removed));
                row.Comparison = "Removed"; row.IsRemoved = true;
                row.ChangeDetails = "Present in the snapshot, absent from the current scan. It may be unplugged or no longer enumerated.";
                result.Rows.Add(row); result.Removed++;
            }
            return result;
        }
        private static string Display(string value) { return String.IsNullOrWhiteSpace(value) ? "(unavailable)" : value; }
    }
}
