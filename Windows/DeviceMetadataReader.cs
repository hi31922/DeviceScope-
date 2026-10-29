using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeviceScope
{
    public sealed class DeviceMetadata
    {
        public string DeviceId { get; set; }
        public string PnpClass { get; set; }
        public string ClassGuid { get; set; }
        public string EnumeratorName { get; set; }
        public string ParentDeviceId { get; set; }
        public string HardwareIds { get; set; }
        public string BusTypeGuid { get; set; }
        public uint? NetworkCharacteristics { get; set; }
        public uint? Capabilities { get; set; }
    }

    public sealed class DeviceMetadataSnapshot
    {
        public Dictionary<string, DeviceMetadata> Devices { get; set; }
        public List<string> Warnings { get; set; }
        public DeviceMetadataSnapshot()
        {
            Devices = new Dictionary<string, DeviceMetadata>(StringComparer.OrdinalIgnoreCase);
            Warnings = new List<string>();
        }
    }

    // Reads Windows' registered device properties. No friendly-name heuristics,
    // registry/device writes, WMI calls or privilege changes are used here.
    public static class DeviceMetadataReader
    {
        private const uint AllClasses = 0x00000004;
        private const uint HardwareIdProperty = 0x00000001;
        private const uint ClassProperty = 0x00000007;
        private const uint CapabilitiesProperty = 0x0000000F;
        private const uint BusTypeGuidProperty = 0x00000013;
        private const uint EnumeratorProperty = 0x00000016;
        private const uint GlobalScope = 1, DriverSoftwareKey = 2, QueryValue = 1;
        private static readonly Guid NetworkClass = new Guid("4d36e972-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo data, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint needed);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo data, uint scope, uint profile, uint keyType, uint desiredAccess);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(uint devInst, StringBuilder id, uint length, uint flags);

        public static DeviceMetadataSnapshot Capture()
        {
            DeviceMetadataSnapshot snapshot = new DeviceMetadataSnapshot();
            IntPtr set = IntPtr.Zero;
            try
            {
                // Do not restrict to present devices: an inventory/snapshot can
                // contain disconnected but still registered device instances.
                set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, AllClasses);
                if (set == new IntPtr(-1))
                {
                    snapshot.Warnings.Add("Native device metadata unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    return snapshot;
                }
                for (uint index = 0; ; index++)
                {
                    DeviceInfo data = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, index, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) snapshot.Warnings.Add("Native device metadata enumeration stopped: " + new Win32Exception(error).Message);
                        break;
                    }
                    string id = InstanceId(set, ref data);
                    if (String.IsNullOrWhiteSpace(id)) continue;
                    uint type;
                    string pnpClass = StringValue(Property(set, ref data, ClassProperty, out type));
                    byte[] hardwareIds = Property(set, ref data, HardwareIdProperty, out type);
                    byte[] capabilities = Property(set, ref data, CapabilitiesProperty, out type);
                    uint? capabilityFlags = type == 4 && capabilities != null && capabilities.Length >= 4 ? (uint?)BitConverter.ToUInt32(capabilities, 0) : null;
                    byte[] bus = Property(set, ref data, BusTypeGuidProperty, out type);
                    string busType = bus != null && bus.Length == 16 ? new Guid(bus).ToString("B") : "";
                    DeviceMetadata metadata = new DeviceMetadata
                    {
                        DeviceId = id,
                        PnpClass = pnpClass,
                        ClassGuid = data.ClassGuid == Guid.Empty ? "" : data.ClassGuid.ToString("B"),
                        EnumeratorName = StringValue(Property(set, ref data, EnumeratorProperty, out type)),
                        ParentDeviceId = ParentId(data.DevInst),
                        HardwareIds = MultiStringValue(hardwareIds),
                        BusTypeGuid = busType,
                        Capabilities = capabilityFlags,
                        NetworkCharacteristics = data.ClassGuid == NetworkClass || String.Equals(pnpClass, "Net", StringComparison.OrdinalIgnoreCase) ? ReadNetworkCharacteristics(set, ref data) : null
                    };
                    snapshot.Devices[id] = metadata;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is DllNotFoundException) && !(ex is EntryPointNotFoundException) && !(ex is Win32Exception) && !(ex is SecurityException) && !(ex is UnauthorizedAccessException)) throw;
                snapshot.Warnings.Add("Native device metadata unavailable: " + ex.Message);
            }
            finally { if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set); }
            return snapshot;
        }

        private static string InstanceId(IntPtr set, ref DeviceInfo data)
        {
            uint required;
            StringBuilder id = new StringBuilder(4096);
            if (SetupDiGetDeviceInstanceId(set, ref data, id, (uint)id.Capacity, out required)) return id.ToString();
            if (required <= id.Capacity || required > 65536) return "";
            id = new StringBuilder((int)required);
            return SetupDiGetDeviceInstanceId(set, ref data, id, (uint)id.Capacity, out required) ? id.ToString() : "";
        }

        private static byte[] Property(IntPtr set, ref DeviceInfo data, uint property, out uint type)
        {
            uint required;
            type = 0;
            SetupDiGetDeviceRegistryProperty(set, ref data, property, out type, null, 0, out required);
            if (required == 0 || required > 1024 * 1024) return null;
            byte[] buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out type, buffer, (uint)buffer.Length, out required)) return null;
            if (required > buffer.Length) return null;
            if (required != buffer.Length) Array.Resize(ref buffer, (int)required);
            return buffer;
        }

        private static string StringValue(byte[] value)
        {
            return value == null || value.Length < 2 ? "" : Encoding.Unicode.GetString(value, 0, value.Length - value.Length % 2).TrimEnd('\0');
        }
        private static string MultiStringValue(byte[] value)
        {
            string text = StringValue(value);
            return String.Join(Environment.NewLine, text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ParentId(uint devInst)
        {
            uint parent, length;
            if (CM_Get_Parent(out parent, devInst, 0) != 0 || CM_Get_Device_ID_Size(out length, parent, 0) != 0 || length == 0 || length > 65535) return "";
            StringBuilder id = new StringBuilder((int)length + 1);
            return CM_Get_Device_ID(parent, id, (uint)id.Capacity, 0) == 0 ? id.ToString() : "";
        }

        private static uint? ReadNetworkCharacteristics(IntPtr set, ref DeviceInfo data)
        {
            IntPtr nativeKey = SetupDiOpenDevRegKey(set, ref data, GlobalScope, 0, DriverSoftwareKey, QueryValue);
            if (nativeKey == IntPtr.Zero || nativeKey == new IntPtr(-1)) return null;
            using (SafeRegistryHandle handle = new SafeRegistryHandle(nativeKey, true))
            {
                try
                {
                    using (RegistryKey key = RegistryKey.FromHandle(handle, RegistryView.Registry64))
                    {
                        object value = key.GetValue("Characteristics", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (value is int) return unchecked((uint)(int)value);
                        if (value is uint) return (uint)value;
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is UnauthorizedAccessException) && !(ex is SecurityException) && !(ex is IOException)) throw;
                    return null;
                }
            }
        }
    }
}
