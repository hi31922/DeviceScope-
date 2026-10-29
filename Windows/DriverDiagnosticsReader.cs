using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace DeviceScope
{
    public sealed class DriverDependencyRow
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
    }
    public sealed class DeviceEventRow
    {
        public DateTime? Time { get; set; }
        public string TimeText { get { return Time.HasValue ? Time.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown"; } }
        public string Log { get; set; }
        public string Provider { get; set; }
        public int EventId { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
    }
    public sealed class DriverDiagnosticResult
    {
        public List<DriverDependencyRow> Dependencies { get; set; }
        public List<DeviceEventRow> Events { get; set; }
        public List<string> Notes { get; set; }
        public int EventsExamined { get; set; }
        public string DependencyScope { get; set; }
        public DriverDiagnosticResult() { Dependencies = new List<DriverDependencyRow>(); Events = new List<DeviceEventRow>(); Notes = new List<string>(); }
    }

    public static class DriverDiagnosticsReader
    {
        public const int EventDays = 30;
        public const int EventsPerLog = 100;
        private const string ServiceRoot = @"SYSTEM\CurrentControlSet\Services";
        public static DriverDiagnosticResult Read(DeviceRow device, CancellationToken cancellation)
        {
            if (device == null) throw new ArgumentNullException("device");
            DriverDiagnosticResult result = new DriverDiagnosticResult();
            cancellation.ThrowIfCancellationRequested(); ReadDependencies(device.Service, result, cancellation);
            cancellation.ThrowIfCancellationRequested(); ReadEvents(device.DeviceId, result, cancellation);
            return result;
        }
        public static string Advice(int? code)
        {
            if (!code.HasValue) return "Windows did not provide a numeric device problem code. Inspect the reported status and matching device events; missing information is not proof that the device is healthy.";
            switch (code.Value)
            {
                case 0: return "Windows reports no Configuration Manager problem code for this device. This does not test every function of the hardware or driver.";
                case 10: return "Windows could not start the device. Check matching events for a more specific cause and verify the driver package matches this exact hardware and Windows version.";
                case 14: return "Windows reports that a restart is needed to finish configuring this device. Save your work before restarting Windows.";
                case 22: return "This device is disabled. If it should be in use, inspect it in Device Manager before enabling it. This code is different from a missing driver.";
                case 24: return "Windows reports that the device is absent, not working correctly, or incompletely installed. Check the physical connection and matching events; the code alone does not prove a hardware failure.";
                case 28: return "Code 28: Windows reports that the drivers for this device are not installed. Use the exact Hardware IDs below and your PC/device model to identify the matching package. Check Windows Update and the PC or hardware manufacturer's official support page for your Windows version and system type. Chipset or device packages may be needed, but Code 28 alone does not identify which package. DeviceScope does not download or install drivers automatically.";
                case 31: return "Windows could not load the drivers required for this device. Check the matching events and use the correct package from the PC or hardware manufacturer.";
                case 32: return "The driver service is disabled. Inspect service registration and dependency entries here; a disabled service is not the same as a missing dependency. DeviceScope does not change startup settings.";
                case 39: return "Windows could not load this driver; the code does not identify the exact missing or damaged file. Inspect matching events and the manufacturer's package before making changes.";
                case 43: return "The device reported a problem and Windows stopped it. This can have multiple causes; inspect device events, connections and the manufacturer's driver guidance rather than assuming a specific hardware failure.";
                case 45: return "Windows reports that this device is not currently connected. It may be a previously installed or unplugged device.";
                case 51: return "Windows reports that this device is waiting for another device or set of devices to start. Declared service dependencies are one useful check, not a complete model of Plug and Play dependencies.";
                case 52: return "Windows cannot verify this driver's digital signature. Obtain a correctly signed package from the hardware manufacturer. DeviceScope does not disable signature enforcement.";
                case 54: return "The device failed and is undergoing a reset. Inspect recent matching events; if the state persists, follow the hardware manufacturer's guidance.";
                case 56: return "Windows is still setting up class configuration for this device. Inspect matching events and related installation activity.";
                default: return "Windows reports device problem code " + code.Value + ". Inspect the exact status, hardware IDs and matching events. This code alone cannot name a particular missing file or package.";
            }
        }
        private static void ReadDependencies(string serviceName, DriverDiagnosticResult result, CancellationToken cancellation)
        {
            result.DependencyScope = "Checks directly declared Windows service/load-order-group dependencies only. It does not scan arbitrary DLL imports or prove every driver/package dependency is satisfied.";
            if (String.IsNullOrWhiteSpace(serviceName)) { result.DependencyScope += " Windows did not report a driver service, so no service dependency declaration can be inspected."; return; }
            if (!ValidServiceName(serviceName)) { result.DependencyScope += " The service name is not a valid registry subkey name."; return; }
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey services = machine.OpenSubKey(ServiceRoot, false))
                {
                    if (services == null) { result.DependencyScope += " Services registry is unavailable."; return; }
                    using (RegistryKey service = services.OpenSubKey(serviceName, false))
                    {
                        if (service == null) { result.Dependencies.Add(new DriverDependencyRow { Kind = "Driver service", Name = serviceName, State = "Registration missing", Detail = "Windows reported this service name, but its Services registry key was not found." }); return; }
                        result.Dependencies.Add(new DriverDependencyRow { Kind = "Driver service", Name = serviceName, State = RegisteredState(service), Detail = "Driver service registration exists. A stopped or demand-start driver is not automatically a missing dependency." });
                        List<string> groups = new List<string>();
                        foreach (string dependency in RegistryStrings(service.GetValue("DependOnService", null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (dependency.StartsWith("+", StringComparison.Ordinal)) groups.Add(dependency.Substring(1));
                            else ReadServiceDependency(services, dependency, result);
                        }
                        groups.AddRange(RegistryStrings(service.GetValue("DependOnGroup", null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
                        foreach (string group in groups.Where(g => !String.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase)) { cancellation.ThrowIfCancellationRequested(); ReadGroupDependency(services, group, result, cancellation); }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                if (!ExpectedReadError(ex)) throw;
                result.DependencyScope += " Dependency inspection unavailable: " + ex.Message;
            }
        }
        private static void ReadServiceDependency(RegistryKey services, string name, DriverDiagnosticResult result)
        {
            if (!ValidServiceName(name)) { result.Dependencies.Add(new DriverDependencyRow { Kind = "Service dependency", Name = name, State = "Unknown", Detail = "The declared name is not a valid service registry subkey." }); return; }
            try
            {
                using (RegistryKey dependency = services.OpenSubKey(name, false))
                    result.Dependencies.Add(new DriverDependencyRow { Kind = "Service dependency", Name = name, State = dependency == null ? ServiceDependencyState(false, null) : RegisteredState(dependency), Detail = dependency == null ? "This explicitly declared service dependency has no Services registry key." : "The dependency is registered. Its running state was not tested; demand-start services may legitimately be stopped." });
            }
            catch (Exception ex) { if (!ExpectedReadError(ex)) throw; result.Dependencies.Add(new DriverDependencyRow { Kind = "Service dependency", Name = name, State = "Unavailable", Detail = ex.Message }); }
        }
        private static void ReadGroupDependency(RegistryKey services, string group, DriverDiagnosticResult result, CancellationToken cancellation)
        {
            int members = 0, denied = 0;
            foreach (string name in services.GetSubKeyNames())
            {
                cancellation.ThrowIfCancellationRequested();
                try { using (RegistryKey candidate = services.OpenSubKey(name, false)) if (candidate != null && String.Equals(Convert.ToString(candidate.GetValue("Group")), group, StringComparison.OrdinalIgnoreCase)) members++; }
                catch (Exception ex) { if (!ExpectedReadError(ex)) throw; denied++; }
            }
            result.Dependencies.Add(new DriverDependencyRow
            {
                Kind = "Load-order group", Name = group,
                State = GroupDependencyState(members, denied),
                Detail = members > 0 ? "At least one installed service belongs to this group. This does not prove a member has started successfully." : denied > 0 ? "Some service keys were inaccessible, so absence cannot be established." : "No service Group value matched this declaration. This is not a complete group-startup or device-dependency diagnosis."
            });
        }
        public static string ServiceDependencyState(bool registrationExists, int? startMode) { return !registrationExists ? "Registration missing" : startMode == 4 ? "Registered, disabled" : "Registered"; }
        public static string GroupDependencyState(int members, int inaccessible) { return members > 0 ? members + " registered member(s)" : inaccessible > 0 ? "Membership unknown" : "No registered members found"; }
        private static string RegisteredState(RegistryKey service) { object start = service.GetValue("Start"); return ServiceDependencyState(true, start is int ? (int?)start : null); }
        private static bool ValidServiceName(string value) { return !String.IsNullOrWhiteSpace(value) && value.IndexOfAny(new[] { '\\', '/' }) < 0; }
        private static IEnumerable<string> RegistryStrings(object value)
        {
            string[] array = value as string[];
            IEnumerable<string> values = array ?? (value is string ? new[] { (string)value } : new string[0]);
            return values.Where(s => !String.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(256);
        }
        private static bool ExpectedReadError(Exception ex) { return ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException || ex is System.ComponentModel.Win32Exception; }
        private static void ReadEvents(string deviceId, DriverDiagnosticResult result, CancellationToken cancellation)
        {
            if (String.IsNullOrWhiteSpace(deviceId)) { result.Notes.Add("Device instance ID unavailable; events cannot be correlated safely."); return; }
            string timeFilter = "TimeCreated[timediff(@SystemTime) <= 2592000000]";
            foreach (string log in new[] { "System", "Microsoft-Windows-Kernel-PnP/Configuration" })
            {
                cancellation.ThrowIfCancellationRequested();
                string providers = log == "System" ? " and (Provider[@Name='Microsoft-Windows-Kernel-PnP'] or Provider[@Name='Microsoft-Windows-UserPnp'] or Provider[@Name='Microsoft-Windows-DriverFrameworks-UserMode'])" : "";
                EventLogQuery query = new EventLogQuery(log, PathType.LogName, "*[System[" + timeFilter + providers + "]]") { ReverseDirection = true };
                int examined = 0;
                try
                {
                    using (EventLogReader reader = new EventLogReader(query))
                    using (cancellation.Register(delegate { try { reader.CancelReading(); } catch (EventLogException) { } catch (ObjectDisposedException) { } }))
                    {
                        while (examined < EventsPerLog)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            using (EventRecord record = reader.ReadEvent(TimeSpan.FromSeconds(2)))
                            {
                                if (record == null) break;
                                examined++; result.EventsExamined++;
                                string xml = record.ToXml(); if (!MatchesDeviceXml(xml, deviceId)) continue;
                                string message; try { message = record.FormatDescription(); } catch (EventLogException) { message = null; }
                                if (String.IsNullOrWhiteSpace(message)) message = "Localized event message unavailable. Recorded event data:" + Environment.NewLine + ExtractEventData(xml);
                                string level; try { level = record.LevelDisplayName; } catch (EventLogException) { level = record.Level.HasValue ? "Level " + record.Level.Value : "Unknown"; }
                                result.Events.Add(new DeviceEventRow { Time = record.TimeCreated, Log = log, Provider = record.ProviderName, EventId = record.Id, Level = level, Message = message });
                            }
                        }
                    }
                    result.Notes.Add(log + ": examined " + examined + (examined == EventsPerLog ? " newest matching-provider events (scan limit reached)" : " matching-provider events") + " from the last " + EventDays + " days.");
                }
                catch (Exception ex)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!(ex is EventLogException) && !ExpectedReadError(ex)) throw;
                    result.Notes.Add(log + ": unavailable or access denied: " + ex.Message);
                }
            }
            result.Events = result.Events.OrderByDescending(row => row.Time).ToList();
        }
        public static bool MatchesDeviceXml(string xml, string deviceId)
        {
            if (String.IsNullOrWhiteSpace(xml) || String.IsNullOrWhiteSpace(deviceId)) return false;
            foreach (XmlNode value in DataNodes(ReadXml(xml))) if (String.Equals(value.InnerText.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public static string ExtractEventData(string xml)
        {
            StringBuilder result = new StringBuilder();
            foreach (XmlNode value in DataNodes(ReadXml(xml)))
            {
                string name = value.Attributes != null && value.Attributes["Name"] != null ? value.Attributes["Name"].Value : value.LocalName;
                result.Append(name).Append(": ").AppendLine(value.InnerText);
                if (result.Length > 6000) { result.Length = 6000; result.AppendLine("..."); break; }
            }
            return result.ToString();
        }
        private static XmlNodeList DataNodes(XmlDocument document) { return document.SelectNodes("//*[local-name()='EventData']/*[local-name()='Data'] | //*[local-name()='UserData']//*[not(*)]"); }
        private static XmlDocument ReadXml(string xml)
        {
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (StringReader text = new StringReader(xml)) using (XmlReader reader = XmlReader.Create(text, settings)) document.Load(reader);
            return document;
        }
    }
}
