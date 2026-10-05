using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace QuietKey
{
    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
    }
    public interface ICommandRunner { Task<CommandResult> Run(string executable, string arguments); }
    public sealed class HiddenCommandRunner : ICommandRunner
    {
        public async Task<CommandResult> Run(string executable, string arguments)
        {
            var info = new ProcessStartInfo(executable, arguments) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());
                return new CommandResult { ExitCode = process.ExitCode, Output = ((await stdout) + " " + (await stderr)).Trim() };
            }
        }
    }
    public sealed class ShutdownService
    {
        private readonly ICommandRunner runner;
        public ShutdownService(ICommandRunner commandRunner) { runner = commandRunner; }
        public Task<CommandResult> Schedule(string minutes) { return runner.Run(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), TimerInput.Arguments(TimerInput.Seconds(minutes))); }
        public Task<CommandResult> Cancel() { return runner.Run(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/a"); }
        public static string Explain(CommandResult result)
        {
            if (result.ExitCode == 1190) return "Windows already has a shutdown scheduled. Cancel that timer before starting a new one.";
            if (result.ExitCode == 1116) return "There is no pending Windows shutdown to cancel.";
            if (result.ExitCode == 5 || result.ExitCode == 1314) return "Windows did not grant permission to shut down this PC.";
            return string.IsNullOrWhiteSpace(result.Output) ? "Windows returned error " + result.ExitCode + "." : result.Output;
        }
    }
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        public static string Command(string executable)
        {
            if (executable.IndexOf('"') >= 0 || !Path.IsPathRooted(executable)) throw new ArgumentException("An absolute application path is required.");
            string command = "\"" + executable + "\" --background";
            if (command.Length > 260) throw new ArgumentException("The app path is too long for Windows startup. Install QuietKey in a shorter path.");
            return command;
        }
        public static bool Enabled()
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey))
            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                if (run == null || run.GetValue("QuietKey") == null) return false;
                byte[] data = approved == null ? null : approved.GetValue("QuietKey") as byte[];
                return data == null || data.Length == 0 || data[0] == 2 || data[0] == 6;
            }
        }
        public static void Set(bool enabled, string executable)
        {
            string command = enabled ? Command(executable) : null;
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) run.SetValue("QuietKey", command, RegistryValueKind.String);
                else run.DeleteValue("QuietKey", false);
            }
            // Clear only this application's cached disabled state when the user explicitly enables it.
            if (enabled) using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                if (approved != null) approved.DeleteValue("QuietKey", false);
            }
        }
    }
    public static class BrightnessService
    {
        public static Dictionary<string, int> Read()
        {
            var brightness = new Dictionary<string, int>();
            using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
            using (ManagementObjectCollection results = search.Get())
                foreach (ManagementObject monitor in results) using (monitor)
                    brightness[Convert.ToString(monitor["InstanceName"])] = Convert.ToInt32(monitor["CurrentBrightness"]);
            if (brightness.Count == 0) throw new NotSupportedException("This display does not expose Windows brightness control. External monitors may need their own controls.");
            return brightness;
        }
        public static void Set(string instance, int value)
        {
            if (value < 0 || value > 100) throw new ArgumentOutOfRangeException("value");
            using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
            using (ManagementObjectCollection results = search.Get())
            {
                foreach (ManagementObject monitor in results) using (monitor)
                {
                    if (Convert.ToString(monitor["InstanceName"]) != instance) continue;
                    using (ManagementBaseObject input = monitor.GetMethodParameters("WmiSetBrightness"))
                    {
                        input["Timeout"] = (uint)0;
                        input["Brightness"] = (byte)value;
                        using (ManagementBaseObject output = monitor.InvokeMethod("WmiSetBrightness", input, null)) {
                            if (output != null && output.Properties["ReturnValue"] != null && output["ReturnValue"] != null && Convert.ToUInt32(output["ReturnValue"]) != 0)
                                throw new IOException("Windows rejected the brightness change.");
                        }
                    }
                    // Some ASUS drivers successfully write brightness but return no response object.
                    for (int retry = 0; retry < 5; retry++) {
                        int actual;
                        if (Read().TryGetValue(instance, out actual) && Math.Abs(actual - value) <= 1) return;
                        System.Threading.Thread.Sleep(100);
                    }
                    throw new IOException("The display did not confirm the requested brightness.");
                }
            }
            throw new NotSupportedException("The saved display is no longer connected.");
        }
    }
    public sealed class AsusFanService : IDisposable
    {
        // ASUS' firmware preset endpoint. No custom fan curves or raw RPM control.
        private const uint ModeDevice = 0x00120075;
        private SafeFileHandle handle;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        public AsusFanService()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            {
                string manufacturer = key == null ? "" : Convert.ToString(key.GetValue("SystemManufacturer"));
                if (manufacturer.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) < 0) return;
            }
            handle = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        }
        private uint NativeCall(bool write, int mode)
        {
            if (handle == null || handle.IsInvalid) throw new NotSupportedException("ASUS fan mode control is unavailable. The ASUS System Control Interface driver is required.");
            byte[] input = new byte[16];
            byte[] method = System.Text.Encoding.ASCII.GetBytes(write ? "DEVS" : "DSTS");
            Array.Copy(method, input, 4);
            Array.Copy(BitConverter.GetBytes(8U), 0, input, 4, 4);
            Array.Copy(BitConverter.GetBytes(ModeDevice), 0, input, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)mode), 0, input, 12, 4);
            byte[] output = new byte[16];
            uint count;
            if (!DeviceIoControl(handle, 0x0022240C, input, (uint)input.Length, output, (uint)output.Length, out count, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The ASUS driver rejected the fan mode request.");
            if (count < 4) throw new IOException("The ASUS driver returned an incomplete response.");
            return BitConverter.ToUInt32(output, 0);
        }
        public void CheckSupport()
        {
            uint result = NativeCall(false, 0);
            // DSTS reports support flags here, not the active preset. A common response is 0x10003.
            if (result == 0xFFFFFFFE || (result & 0x10000) == 0)
                throw new NotSupportedException("This ASUS model does not expose Silent / Performance / Turbo presets.");
        }
        public void Set(int mode)
        {
            if (mode < 0 || mode > 2) throw new ArgumentOutOfRangeException("mode");
            CheckSupport(); // Do not write to an unsupported endpoint.
            if (mode == 1 && System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Online)
                throw new InvalidOperationException("Connect the laptop to AC power before choosing Turbo.");
            if (NativeCall(true, mode) != 1) throw new IOException("The ASUS firmware did not accept this fan preset.");
        }
        public static int Mode(string name)
        {
            switch (name) { case "Performance": return 0; case "Turbo": return 1; case "Silent": return 2; default: throw new ArgumentException("Unknown fan mode."); }
        }
        public static string Name(int mode) { return mode == 0 ? "Performance" : mode == 1 ? "Turbo" : "Silent"; }
        public void Dispose() { if (handle != null) handle.Dispose(); }
    }
    public sealed class ActionReport
    {
        public readonly List<string> Applied = new List<string>();
        public readonly List<string> Failed = new List<string>();
        public string Text() { return string.Join("\n", Applied.Select(s => "✓ " + s).Concat(Failed.Select(s => "• " + s))); }
    }
    public static class BootClock
    {
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        public static DateTime StartedUtc() { return DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64()); }
    }
}
