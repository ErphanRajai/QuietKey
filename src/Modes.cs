using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QuietKey
{
    public static class PowerService
    {
        public static readonly Guid BalancedPlan = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid scheme);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code, "Windows could not change the power configuration."); }
        public static Guid Overlay(string mode)
        {
            switch (mode) {
                case "Performance": return new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
                case "Efficiency": return new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
                case "Balanced": return Guid.Empty;
                default: throw new ArgumentException("Unknown Windows power mode.");
            }
        }
        public static PowerState Read()
        {
            IntPtr pointer;
            Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
            Guid plan;
            try { plan = (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); } finally { LocalFree(pointer); }
            Guid overlay; Check(PowerGetActualOverlayScheme(out overlay));
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, null, ref size);
            string name = plan.ToString();
            if (size > 0 && size < 65536) { byte[] buffer = new byte[size]; if (PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0) name = Encoding.Unicode.GetString(buffer).TrimEnd('\0'); }
            return new PowerState { Plan = plan.ToString(), Overlay = overlay.ToString(), PlanName = name };
        }
        public static void Set(string mode)
        {
            Guid plan = BalancedPlan;
            Check(PowerSetActiveScheme(IntPtr.Zero, ref plan));
            Guid overlay = Overlay(mode);
            Check(PowerSetActiveOverlayScheme(overlay));
            if (Read().Overlay != overlay.ToString()) throw new IOException("Windows did not retain the requested power mode.");
        }
        public static void Restore(PowerState state)
        {
            Guid plan = new Guid(state.Plan);
            Check(PowerSetActiveScheme(IntPtr.Zero, ref plan));
            Check(PowerSetActiveOverlayScheme(new Guid(state.Overlay)));
        }
        public static int FanRestoreMode(PowerState state)
        {
            string name = state == null ? "" : state.PlanName ?? "";
            if (name.IndexOf("silent", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (name.IndexOf("turbo", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0; // Normal Performance preset when the previous firmware mode cannot be queried.
        }
    }
    public interface IModeHardware
    {
        bool OnAcPower { get; }
        Dictionary<string, int> ReadBrightness();
        void SetBrightness(string display, int value);
        Dictionary<string, bool> ReadNightLight();
        void SetNightLight(bool value);
        void RestoreNightLight(string key, bool value);
        void CheckFan();
        void SetFan(int value);
        PowerState ReadPower();
        void SetPower(string value);
        void RestorePower(PowerState value);
        int? ReadGameMode();
        void SetGameMode(int? value);
    }
    public sealed class WindowsModeHardware : IModeHardware
    {
        private readonly string backupDirectory;
        public WindowsModeHardware(string directory) { backupDirectory = directory; }
        public bool OnAcPower { get { return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online; } }
        public Dictionary<string, int> ReadBrightness() { return BrightnessService.Read(); }
        public void SetBrightness(string display, int value) { BrightnessService.Set(display, value); }
        public Dictionary<string, bool> ReadNightLight() { return new NightLightService().ReadStates(); }
        public void SetNightLight(bool value) { new NightLightService().Set(value, backupDirectory); }
        public void RestoreNightLight(string key, bool value) { new NightLightService().SetState(key, value, backupDirectory); }
        public void CheckFan() { using (var fan = new AsusFanService()) fan.CheckSupport(); }
        public void SetFan(int value) { using (var fan = new AsusFanService()) fan.Set(value); }
        public PowerState ReadPower() { return PowerService.Read(); }
        public void SetPower(string value) { PowerService.Set(value); }
        public void RestorePower(PowerState value) { PowerService.Restore(value); }
        public int? ReadGameMode() { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar")) { object value = key == null ? null : key.GetValue("AutoGameModeEnabled"); return value == null ? (int?)null : Convert.ToInt32(value); } }
        public void SetGameMode(int? value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar")) {
                if (value.HasValue) key.SetValue("AutoGameModeEnabled", value.Value, RegistryValueKind.DWord);
                else key.DeleteValue("AutoGameModeEnabled", false);
            }
        }
    }
    public sealed class ModeService
    {
        private readonly Settings settings;
        private readonly SettingsStore store;
        private readonly IModeHardware hardware;
        public ModeService(Settings data, SettingsStore settingsStore, IModeHardware backend) { settings = data; store = settingsStore; hardware = backend; }
        public ActionReport Enable(string name)
        {
            if (name != "Sleep" && name != "Gaming") throw new ArgumentException("Unknown mode.");
            var report = new ActionReport();
            if (name == "Gaming" && !hardware.OnAcPower) { report.Failed.Add("Connect the laptop to AC power before enabling Gaming mode."); return report; }
            if (settings.ActiveMode != "None" && settings.ActiveMode != name) {
                report = Disable();
                if (report.Failed.Count > 0) { report.Failed.Add("The other mode could not be fully switched off. Retry Off before switching modes."); return report; }
                report = new ActionReport();
            }
            Profile profile = name == "Gaming" ? settings.GamingProfile : settings.SleepProfile;
            RestorePoint restore = settings.Restore ?? new RestorePoint(); restore.Validate();
            Dictionary<string, int> displays = null;
            bool nightReady = false, fanReady = false, powerReady = false, gameReady = false;
            if (profile.ChangeBrightness) try {
                displays = hardware.ReadBrightness();
                foreach (var display in displays) if (!restore.Brightness.ContainsKey(display.Key)) restore.Brightness[display.Key] = display.Value;
            } catch (Exception e) { report.Failed.Add("Brightness: " + e.Message); }
            if (profile.NightLight != "Keep") try {
                var states = hardware.ReadNightLight();
                if (restore.NightLightStates == null) restore.NightLightStates = new Dictionary<string, bool>();
                foreach (var state in states) if (!restore.NightLightStates.ContainsKey(state.Key)) restore.NightLightStates[state.Key] = restore.NightLightOn.HasValue && !state.Key.EndsWith("perdevice", StringComparison.OrdinalIgnoreCase) ? restore.NightLightOn.Value : state.Value;
                restore.NightLightOn = null; nightReady = true;
            } catch (Exception e) { report.Failed.Add("Night Light: " + e.Message); }
            if (profile.PowerMode != "Keep" || profile.FanMode != "Keep") try {
                if (restore.Power == null) restore.Power = hardware.ReadPower();
                powerReady = profile.PowerMode != "Keep";
            } catch (Exception e) { report.Failed.Add("Windows power: " + e.Message); }
            if (profile.FanMode != "Keep") try {
                hardware.CheckFan();
                if (!restore.FanMode.HasValue) restore.FanMode = PowerService.FanRestoreMode(restore.Power);
                fanReady = true;
            } catch (Exception e) { report.Failed.Add("Fan preset: " + e.Message); }
            if (profile.EnableGameMode) try {
                if (!restore.GameModeCaptured) { restore.GameModeValue = hardware.ReadGameMode(); restore.GameModeCaptured = true; }
                gameReady = true;
            } catch (Exception e) { report.Failed.Add("Windows Game Mode: " + e.Message); }
            settings.Restore = restore.HasChanges() ? restore : null;
            store.Save(settings); // Never alter hardware until its restoration data is durable.
            if (fanReady) Try(report, "Fan preset set to " + profile.FanMode, "Fan preset", () => hardware.SetFan(AsusFanService.Mode(profile.FanMode)));
            if (nightReady) Try(report, "Night Light turned " + profile.NightLight.ToLowerInvariant(), "Night Light", () => hardware.SetNightLight(profile.NightLight == "On"));
            if (gameReady) Try(report, "Windows Game Mode enabled", "Windows Game Mode", () => hardware.SetGameMode(1));
            if (powerReady) Try(report, profile.PowerMode == "Performance" ? "Windows Best performance enabled" : profile.PowerMode == "Efficiency" ? "Windows Best power efficiency enabled" : "Windows Balanced power mode enabled", "Windows power", () => hardware.SetPower(profile.PowerMode));
            // Changing a power plan can restore that plan's brightness. Apply brightness last.
            if (displays != null) foreach (var display in displays) Try(report, "Brightness set to " + profile.Brightness + "%", "Brightness", () => hardware.SetBrightness(display.Key, profile.Brightness));
            if (report.Applied.Count > 0) { settings.ActiveMode = name; settings.ModeBootUtc = BootClock.StartedUtc(); }
            else if (report.Failed.Count == 0) report.Failed.Add("This profile keeps every setting unchanged. Edit it before enabling the mode.");
            settings.ModeHasWarnings = report.Failed.Count > 0;
            store.Save(settings); Log(name + " on", report);
            return report;
        }
        public ActionReport Disable()
        {
            var report = new ActionReport();
            RestorePoint restore = settings.Restore;
            if (restore == null) { settings.ActiveMode = "None"; settings.ModeBootUtc = null; store.Save(settings); report.Applied.Add("Modes switched off"); return report; }
            if (restore.NightLightStates != null) foreach (var state in restore.NightLightStates.ToArray()) Try(report, "Previous Night Light state restored", "Night Light", () => { hardware.RestoreNightLight(state.Key, state.Value); restore.NightLightStates.Remove(state.Key); });
            if (restore.NightLightOn.HasValue) Try(report, "Previous Night Light state restored", "Night Light", () => { hardware.SetNightLight(restore.NightLightOn.Value); restore.NightLightOn = null; });
            if (restore.FanMode.HasValue) Try(report, "Fan returned to " + AsusFanService.Name(restore.FanMode.Value), "Fan preset", () => { hardware.SetFan(restore.FanMode.Value); restore.FanMode = null; });
            if (restore.GameModeCaptured) Try(report, "Previous Windows Game Mode setting restored", "Windows Game Mode", () => { hardware.SetGameMode(restore.GameModeValue); restore.GameModeCaptured = false; });
            if (restore.Power != null) Try(report, "Previous Windows power configuration restored", "Windows power", () => { hardware.RestorePower(restore.Power); restore.Power = null; });
            foreach (var display in restore.Brightness.ToArray()) Try(report, "Previous brightness restored", "Brightness", () => { hardware.SetBrightness(display.Key, display.Value); restore.Brightness.Remove(display.Key); });
            if (!restore.HasChanges()) { settings.Restore = null; settings.ActiveMode = "None"; settings.ModeBootUtc = null; }
            settings.ModeHasWarnings = report.Failed.Count > 0;
            store.Save(settings); Log("Modes off", report);
            return report;
        }
        private static void Try(ActionReport report, string success, string label, Action action)
        {
            try { action(); report.Applied.Add(success); } catch (Exception e) { report.Failed.Add(label + ": " + e.Message); }
        }
        private void Log(string action, ActionReport report)
        {
            try { File.AppendAllText(Path.Combine(store.DirectoryPath, "actions.log"), DateTime.UtcNow.ToString("o") + " " + action + Environment.NewLine + report.Text() + Environment.NewLine); } catch { }
        }
    }
}
