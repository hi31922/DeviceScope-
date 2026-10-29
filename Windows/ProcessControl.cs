using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeviceScope
{
    public sealed class ProcessIdentity
    {
        public int Id { get; set; }
        public long StartTimeUtcTicks { get; set; }
        public string Name { get; set; }
    }

    public sealed class ProcessStopResult
    {
        public bool Success { get; set; }
        public bool AlreadyExited { get; set; }
        public bool TerminationRequested { get; set; }
        public string Message { get; set; }
    }

    public static class ProcessController
    {
        private const uint QueryLimitedInformation = 0x1000, Terminate = 0x0001, Synchronize = 0x00100000;
        private const uint WaitObject0 = 0, WaitTimeout = 258, WaitFailed = 0xFFFFFFFF;
        private static readonly int SelfId = CurrentProcessId();
        // Defense in depth for Windows components that must never be stopped here.
        // IsProcessCritical is also checked on the exact opened handle, not by name.
        private static readonly HashSet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System",
            "smss", "csrss", "wininit", "services", "lsass", "lsaiso", "winlogon", "svchost"
        };

        private sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private ProcessHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low, High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(ProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(ProcessHandle process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessCritical(ProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(ProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(ProcessHandle handle, uint milliseconds);

        private static int CurrentProcessId() { using (Process self = Process.GetCurrentProcess()) return self.Id; }

        public static string ProtectionReason(int processId, string name)
        {
            if (processId <= 4) return "Windows system processes cannot be stopped here.";
            if (processId == SelfId) return "DeviceScope cannot stop itself.";
            if (ProtectedNames.Contains(name ?? "")) return "This Windows system component is protected in DeviceScope.";
            return null;
        }

        // Name and creation time come from the same native handle. A recycled PID
        // therefore cannot pair an old displayed executable name with a new token.
        public static ProcessIdentity ReadIdentity(int processId)
        {
            if (processId <= 0) return null;
            using (ProcessHandle handle = OpenProcess(QueryLimitedInformation, false, processId))
            {
                if (handle.IsInvalid) return null;
                long ticks;
                if (!TryReadCreationTime(handle, out ticks)) return null;
                string name = ReadImageName(handle);
                if (name == null) return null;
                return new ProcessIdentity { Id = processId, StartTimeUtcTicks = ticks, Name = name };
            }
        }

        public static ProcessStopResult StopSelected(int processId, long expectedStartTimeUtcTicks)
        {
            string earlyProtection = ProtectionReason(processId, null);
            if (earlyProtection != null) return Failure(earlyProtection);
            if (expectedStartTimeUtcTicks <= 0) return Failure("Process identity is unavailable. Refresh and select the process again.");
            // One handle is retained for identity, protection, termination and wait.
            // No PID-based reopening occurs after validation, and no privileges are enabled.
            using (ProcessHandle handle = OpenProcess(Terminate | QueryLimitedInformation | Synchronize, false, processId))
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == 87 ? Exited() : Failure("Cannot open the selected process: " + Error(error));
                }
                uint state = WaitForSingleObject(handle, 0);
                if (state == WaitObject0) return Exited();
                if (state == WaitFailed) return Failure("Cannot check process state: " + Error(Marshal.GetLastWin32Error()));
                long actualTicks;
                if (!TryReadCreationTime(handle, out actualTicks)) return Failure("Cannot verify the selected process identity: " + Error(Marshal.GetLastWin32Error()));
                if (actualTicks != expectedStartTimeUtcTicks) return Failure("That process exited and its PID was reused. Refresh and select the new process explicitly.");
                string name = ReadImageName(handle);
                if (name == null) return Failure("Cannot verify the executable identity. The process was not stopped.");
                string protection = ProtectionReason(processId, name);
                if (protection != null) return Failure(protection);
                bool critical;
                if (!IsProcessCritical(handle, out critical)) return Failure("Cannot verify Windows critical-process protection: " + Error(Marshal.GetLastWin32Error()));
                if (critical) return Failure("Windows marks this as a critical process. DeviceScope will not stop it.");
                if (!TerminateProcess(handle, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (WaitForSingleObject(handle, 0) == WaitObject0) return Exited();
                    return Failure("Windows refused to stop the process: " + Error(error));
                }
                uint exitState = WaitForSingleObject(handle, 5000);
                if (exitState == WaitObject0) return new ProcessStopResult { Success = true, TerminationRequested = true, Message = "Process stopped." };
                return new ProcessStopResult { TerminationRequested = true, Message = exitState == WaitTimeout ? "Stop requested; Windows has not finished terminating the process yet." : "Stop requested; exit confirmation failed: " + Error(Marshal.GetLastWin32Error()) };
            }
        }

        private static bool TryReadCreationTime(ProcessHandle process, out long ticks)
        {
            FileTime created, exited, kernel, user; ticks = 0;
            if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) return false;
            long fileTime = ((long)created.High << 32) | created.Low;
            ticks = DateTime.FromFileTimeUtc(fileTime).Ticks; return true;
        }
        private static string ReadImageName(ProcessHandle process)
        {
            StringBuilder path = new StringBuilder(32768); int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? Path.GetFileNameWithoutExtension(path.ToString()) : null;
        }
        private static ProcessStopResult Failure(string message) { return new ProcessStopResult { Message = message }; }
        private static ProcessStopResult Exited() { return new ProcessStopResult { Success = true, AlreadyExited = true, Message = "The selected process has already exited." }; }
        private static string Error(int code) { return new Win32Exception(code).Message + " (" + code + ")"; }
    }
}
