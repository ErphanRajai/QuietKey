using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("QuietKey")]
[assembly: AssemblyDescription("A small Windows tray app for evening shortcuts")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace QuietKey
{
    public sealed class GlobalHotkey : IDisposable
    {
        private readonly HwndSource source;
        private readonly Action toggle;
        private int currentId;
        private string currentKey;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        public GlobalHotkey(Action action)
        {
            toggle = action;
            source = new HwndSource(new HwndSourceParameters("QuietKey.Hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            source.AddHook(OnMessage);
        }
        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0312 && wparam.ToInt32() == currentId) { handled = true; toggle(); }
            return IntPtr.Zero;
        }
        public bool Set(string key)
        {
            if (currentKey == key && currentId != 0) return true;
            int nextId = currentId == 1 ? 2 : 1;
            if (!RegisterHotKey(source.Handle, nextId, 0x4003, Hotkeys.VirtualKey(key))) return false;
            if (currentId != 0) UnregisterHotKey(source.Handle, currentId);
            currentId = nextId;
            currentKey = key;
            return true;
        }
        public void Dispose()
        {
            if (currentId != 0) UnregisterHotKey(source.Handle, currentId);
            source.RemoveHook(OnMessage);
            source.Dispose();
        }
    }
    public static class Program
    {
        public static readonly string DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietKey");
        public static readonly string Executable = Assembly.GetExecutingAssembly().Location;
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--diagnostics") { Diagnostics(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--ui-smoke") { UiController.Smoke(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--mode-test") { return ModeTest(args[1]); }
                if (args.Length == 2 && args[0] == "--restore-mode") {
                    var restoreStore = new SettingsStore(DataPath);
                    ActionReport report = new ModeService(restoreStore.Load(), restoreStore, new WindowsModeHardware(DataPath)).Disable();
                    File.WriteAllText(args[1], report.Text());
                    return report.Failed.Count == 0 ? 0 : 1;
                }
                if (args.Length > 0 && args[0] != "--background") throw new ArgumentException("Unknown command-line option.");
                string sid = WindowsIdentity.GetCurrent().User.Value;
                bool first;
                using (var mutex = new Mutex(true, @"Local\QuietKey.Instance." + sid, out first))
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QuietKey.Show." + sid))
                {
                    if (!first) { if (args.Length == 0) showEvent.Set(); return 0; }
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var store = new SettingsStore(DataPath);
                    Settings settings = store.Load();
                    if (settings.ActiveMode != "None" && (!settings.ModeBootUtc.HasValue || Math.Abs((settings.ModeBootUtc.Value - BootClock.StartedUtc()).TotalSeconds) > 15)) {
                        settings.ActiveMode = "None"; settings.ModeBootUtc = null; store.Save(settings);
                    }
                    if (settings.ShutdownUtc.HasValue && (settings.ShutdownUtc.Value <= DateTime.UtcNow || !settings.TimerBootUtc.HasValue || Math.Abs((settings.TimerBootUtc.Value - BootClock.StartedUtc()).TotalSeconds) > 15))
                    { settings.ShutdownUtc = null; settings.TimerBootUtc = null; store.Save(settings); }
                    using (var ui = new UiController(app, settings, store, new ShutdownService(new HiddenCommandRunner()), false))
                    using (var hotkey = new GlobalHotkey(ui.Toggle))
                    {
                        ui.Hotkey = hotkey;
                        bool registered = hotkey.Set(settings.Hotkey);
                        if (!registered) ui.Status("The shortcut " + Hotkeys.Label(settings.Hotkey) + " is in use. Choose a different key in Settings. The tray icon still opens QuietKey.", true);
                        else if (store.LoadWarning != null) ui.Status(store.LoadWarning, true);
                        ui.CreateTray();
                        if (!registered) ui.Balloon("Shortcut unavailable", "Open QuietKey from the tray and choose another shortcut in Settings.");
                        RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) => {
                            if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(ui.Show));
                        }, null, Timeout.Infinite, false);
                        app.DispatcherUnhandledException += (sender, eventArgs) => {
                            Log(eventArgs.Exception);
                            ui.Status("Something went wrong: " + eventArgs.Exception.Message, true);
                            eventArgs.Handled = true;
                        };
                        app.Exit += (sender, eventArgs) => wait.Unregister(null);
                        if (args.Length == 0) ui.Show();
                        app.Run();
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception e)
            {
                Log(e);
                if (args.Length == 2 && (args[0] == "--diagnostics" || args[0] == "--ui-smoke"))
                {
                    try { string path = args[0] == "--ui-smoke" ? Path.Combine(args[1], "error.txt") : args[1] + ".error.txt"; File.WriteAllText(path, e.ToString()); } catch { }
                }
                else Forms.MessageBox.Show("QuietKey could not start.\n\n" + e.Message, "QuietKey", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                return 1;
            }
        }
        public static void Log(Exception error)
        {
            try { Directory.CreateDirectory(DataPath); File.AppendAllText(Path.Combine(DataPath, "errors.log"), DateTime.UtcNow.ToString("o") + " " + error + Environment.NewLine); } catch { }
        }
        private static void Diagnostics(string outputPath)
        {
            var data = new System.Collections.Generic.Dictionary<string, object>();
            data["Executable"] = Executable;
            data["StartupEnabled"] = StartupService.Enabled();
            data["WindowsVersion"] = Environment.OSVersion.Version.ToString();
            try { data["Brightness"] = BrightnessService.Read(); } catch (Exception e) { data["BrightnessError"] = e.Message; }
            try { data["NightLightOn"] = new NightLightService().Read(); } catch (Exception e) { data["NightLightError"] = e.Message; }
            try { data["NightLightStates"] = new NightLightService().ReadStates(); } catch { }
            try { data["Power"] = PowerService.Read(); } catch (Exception e) { data["PowerError"] = e.Message; }
            try { using (var fan = new AsusFanService()) { fan.CheckSupport(); data["AsusFanPresetsSupported"] = true; } } catch (Exception e) { data["FanError"] = e.Message; }
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(data));
        }
        private static int ModeTest(string directory)
        {
            Directory.CreateDirectory(directory);
            var store = new SettingsStore(directory);
            var settings = new Settings();
            var hardware = new WindowsModeHardware(directory);
            var service = new ModeService(settings, store, hardware);
            var results = new System.Collections.Generic.Dictionary<string, object>();
            int exit = 0;
            try {
                results["Before"] = Snapshot(hardware);
                foreach (string mode in new[] { "Sleep", "Gaming" }) {
                    ActionReport enabled = service.Enable(mode);
                    results[mode + "OnReport"] = enabled.Text();
                    results[mode + "OnState"] = Snapshot(hardware);
                    if (enabled.Failed.Count > 0 || settings.ActiveMode != mode) exit = 1;
                    Profile profile = mode == "Sleep" ? settings.SleepProfile : settings.GamingProfile;
                    if (hardware.ReadBrightness().Any(pair => pair.Value != profile.Brightness) || hardware.ReadNightLight().Any(pair => pair.Value != (profile.NightLight == "On")) || hardware.ReadPower().Overlay != PowerService.Overlay(profile.PowerMode).ToString()) {
                        results[mode + "ValidationError"] = "The final hardware state did not match the profile."; exit = 1;
                    }
                    ActionReport disabled = service.Disable();
                    results[mode + "OffReport"] = disabled.Text();
                    results[mode + "OffState"] = Snapshot(hardware);
                    if (disabled.Failed.Count > 0 || settings.ActiveMode != "None") exit = 1;
                }
            } catch (Exception e) { results["Error"] = e.ToString(); exit = 1; }
            finally {
                if (settings.Restore != null) { try { results["FinalRestore"] = service.Disable().Text(); } catch (Exception e) { results["RestoreError"] = e.ToString(); exit = 1; } }
                File.WriteAllText(Path.Combine(directory, "hardware-results.json"), new JavaScriptSerializer().Serialize(results));
            }
            return exit;
        }
        private static object Snapshot(IModeHardware hardware)
        {
            return new { Brightness = hardware.ReadBrightness(), NightLight = hardware.ReadNightLight(), Power = hardware.ReadPower(), GameMode = hardware.ReadGameMode() };
        }
    }
}
