using System;
using System.Collections.Generic;

namespace DeviceScope
{
    public sealed class DeviceTypeClassification
    {
        public string DeviceType { get; set; }
        public string Evidence { get; set; }
    }

    // Windows metadata-derived categories, not a claim about bare-metal topology.
    // Functional roles (audio endpoint/software/system) take precedence over transport.
    public static class DeviceClassifier
    {
        private const string AudioEndpointGuid = "c166523c-fe0c-4a94-a586-f1a80cfbbf3e";
        private const string SoftwareComponentGuid = "5c4c3332-344d-483c-8739-259e934c9cc8";
        private const string SoftwareDeviceGuid = "62f9c741-b25a-46ce-b54c-9bccce08b6f2";
        private const string ComputerGuid = "4d36e966-e325-11ce-bfc1-08002be10318";
        private const string SystemGuid = "4d36e97d-e325-11ce-bfc1-08002be10318";
        private const string NetworkGuid = "4d36e972-e325-11ce-bfc1-08002be10318";
        private static bool Same(string left, string right) { return String.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase); }
        private static bool ClassIs(DeviceMetadata node, string name, string guid)
        {
            Guid parsed, expected;
            return Same(node.PnpClass, name) || (!String.IsNullOrEmpty(guid) && Guid.TryParse(node.ClassGuid, out parsed) && Guid.TryParse(guid, out expected) && parsed == expected);
        }
        private static string Enumerator(DeviceMetadata node)
        {
            if (!String.IsNullOrWhiteSpace(node.EnumeratorName)) return node.EnumeratorName.Trim().ToUpperInvariant();
            string id = node.DeviceId ?? ""; int separator = id.IndexOf('\\');
            return separator > 0 ? id.Substring(0, separator).ToUpperInvariant() : "";
        }
        private static bool IsNetwork(DeviceMetadata node) { return ClassIs(node, "Net", NetworkGuid) || Same(node.PnpClass, "Network"); }
        private static DeviceTypeClassification Result(string type, string evidence) { return new DeviceTypeClassification { DeviceType = type, Evidence = evidence }; }
        private static DeviceMetadata RowMetadata(DeviceRow row)
        {
            return new DeviceMetadata { DeviceId = row.DeviceId, PnpClass = String.IsNullOrWhiteSpace(row.PnpClass) ? row.Class : row.PnpClass, ClassGuid = row.ClassGuid, EnumeratorName = row.EnumeratorName, ParentDeviceId = row.ParentDeviceId, BusTypeGuid = row.BusTypeGuid, HardwareIds = row.HardwareIds, NetworkCharacteristics = row.NetworkCharacteristics };
        }
        private static DeviceMetadata Node(DeviceRow row, IDictionary<string, DeviceMetadata> metadata)
        {
            DeviceMetadata native;
            return metadata != null && row.DeviceId != null && metadata.TryGetValue(row.DeviceId, out native) ? native : RowMetadata(row);
        }
        private static DeviceTypeClassification ExplicitNetworkType(DeviceMetadata node)
        {
            if (!IsNetwork(node) || !node.NetworkCharacteristics.HasValue) return null;
            uint kind = node.NetworkCharacteristics.Value & 7;
            if (kind == 0) return null;
            if ((kind & (kind - 1)) != 0) return Result("Unknown", "The network driver reports conflicting physical/virtual/software-enumerated Characteristics flags (0x" + node.NetworkCharacteristics.Value.ToString("X") + ").");
            if (kind == 1) return Result("Virtual device", "Windows network driver registration sets NCF_VIRTUAL (Characteristics 0x" + node.NetworkCharacteristics.Value.ToString("X") + ").");
            if (kind == 2) return Result("Virtual device", "Windows network driver registration declares a software-enumerated adapter (NCF_SOFTWARE_ENUMERATED, Characteristics 0x" + node.NetworkCharacteristics.Value.ToString("X") + ").");
            return Result("Physical hardware", "Windows network driver registration sets NCF_PHYSICAL (Characteristics 0x" + node.NetworkCharacteristics.Value.ToString("X") + "). This does not distinguish built-in from external hardware.");
        }
        private static DeviceTypeClassification VirtualTransport(DeviceMetadata node)
        {
            string enumerator = Enumerator(node);
            if (enumerator == "VMBUS") return Result("Virtual device", "The device is enumerated on Windows VMBus, the Hyper-V synthetic-device bus.");
            if (enumerator == "SWD" || enumerator == "SWC" || ClassIs(node, "SoftwareDevice", SoftwareDeviceGuid) || ClassIs(node, "SoftwareComponent", SoftwareComponentGuid))
                return Result("Virtual device", "The parent device is software-enumerated (" + (enumerator.Length == 0 ? node.PnpClass : enumerator) + ").");
            DeviceTypeClassification network = ExplicitNetworkType(node);
            return network != null && network.DeviceType != "Physical hardware" ? network : null;
        }
        private static bool HardwareTransport(DeviceMetadata node)
        {
            string bus = Enumerator(node);
            // HID and SCSI alone are insufficient: both support virtual devices.
            // Bluetooth protocol nodes are likewise not assumed to be a physical radio.
            return bus == "PCI" || bus == "USB" || bus == "USBSTOR" || bus == "HDAUDIO" || bus == "INTELAUDIO" || bus == "I2C" || bus == "SPI" || bus == "SD" || bus == "PCMCIA" || bus == "1394";
        }
        public static DeviceTypeClassification Classify(DeviceRow row, IDictionary<string, DeviceMetadata> metadata = null)
        {
            if (row == null) return Result("Unknown", "No device metadata is available.");
            DeviceMetadata node = Node(row, metadata);
            if (ClassIs(node, "AudioEndpoint", AudioEndpointGuid)) return Result("Audio endpoint", "Windows assigns the AudioEndpoint setup class. This is a logical speaker/microphone endpoint, not the underlying audio controller.");
            if (ClassIs(node, "SoftwareComponent", SoftwareComponentGuid) || ClassIs(node, "SoftwareDevice", SoftwareDeviceGuid)) return Result("Software component", "Windows assigns the " + (node.PnpClass ?? "software device") + " setup class (" + node.ClassGuid + ").");
            if (ClassIs(node, "Computer", ComputerGuid) || ClassIs(node, "System", SystemGuid) || Same(node.PnpClass, "Firmware")) return Result("System component", "Windows setup class identifies a computer, system or firmware component. This describes its role; such a component can still be backed by physical hardware.");
            if (Same(node.PnpClass, "NetClient") || Same(node.PnpClass, "NetService") || Same(node.PnpClass, "NetTrans") || Same(node.PnpClass, "AudioProcessingObject")) return Result("Software component", "Windows setup class " + node.PnpClass + " identifies a software service, transport or processing component.");
            DeviceTypeClassification networkType = ExplicitNetworkType(node);
            if (networkType != null && networkType.DeviceType != "Physical hardware") return networkType;
            string enumerator = Enumerator(node);
            if (enumerator == "SWD" || enumerator == "SWC") return Result("Software component", "Windows reports software enumeration via " + enumerator + "; no more specific endpoint class was reported.");
            if (enumerator == "VMBUS") return VirtualTransport(node);

            List<DeviceMetadata> ancestry = new List<DeviceMetadata>();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DeviceMetadata parent = node;
            if (!String.IsNullOrEmpty(node.DeviceId)) visited.Add(node.DeviceId);
            bool incompleteAncestry = false, invalidAncestry = false;
            for (int depth = 0; !String.IsNullOrWhiteSpace(parent.ParentDeviceId); depth++)
            {
                if (depth >= 32 || !visited.Add(parent.ParentDeviceId)) { invalidAncestry = true; break; }
                DeviceMetadata next;
                if (metadata == null || !metadata.TryGetValue(parent.ParentDeviceId, out next)) { incompleteAncestry = true; break; }
                ancestry.Add(next); parent = next;
                DeviceTypeClassification virtualParent = VirtualTransport(parent);
                if (virtualParent != null) return Result(virtualParent.DeviceType, "Parent " + parent.DeviceId + ": " + virtualParent.Evidence);
            }
            if (networkType != null) return networkType;
            if (invalidAncestry) return Result("Unknown", "The reported parent chain contains a cycle or exceeds the traversal limit; no physical/virtual inference is made.");
            if (HardwareTransport(node)) return Result("Physical hardware", "Windows enumerates this device on " + enumerator + ". Hardware-enumerated means the transport Windows sees; emulation inside a VM is not excluded, and built-in/external placement is not inferred.");
            foreach (DeviceMetadata ancestor in ancestry)
            {
                if (HardwareTransport(ancestor)) return Result("Physical hardware", "The recorded device parent chain reaches " + Enumerator(ancestor) + " device " + ancestor.DeviceId + ". This is Windows-reported hardware transport, not a built-in/external or bare-metal guarantee.");
            }
            if (incompleteAncestry) return Result("Unknown", "The full parent chain is unavailable. Device class/name alone cannot prove physical or virtual hardware.");
            return Result("Unknown", "Windows metadata does not provide enough evidence for these categories. ROOT, HID, ACPI or a product name alone is not proof of physical or virtual hardware.");
        }
        public static void Apply(DeviceRow row, IDictionary<string, DeviceMetadata> metadata = null)
        {
            DeviceMetadata native;
            if (metadata != null && row.DeviceId != null && metadata.TryGetValue(row.DeviceId, out native))
            {
                row.ClassGuid = native.ClassGuid; row.EnumeratorName = native.EnumeratorName; row.ParentDeviceId = native.ParentDeviceId; row.BusTypeGuid = native.BusTypeGuid; row.NetworkCharacteristics = native.NetworkCharacteristics;
                if (String.IsNullOrWhiteSpace(row.PnpClass)) { row.PnpClass = native.PnpClass; row.Class = Same(native.PnpClass, "Net") ? "Network" : String.IsNullOrWhiteSpace(native.PnpClass) ? "Other" : native.PnpClass; }
            }
            DeviceTypeClassification classification = Classify(row, metadata);
            row.DeviceType = classification.DeviceType; row.TypeEvidence = classification.Evidence;
        }
    }
}
