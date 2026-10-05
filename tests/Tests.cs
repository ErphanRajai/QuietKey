using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuietKey;

internal sealed class RecordingRunner : ICommandRunner
{
    public string Executable;
    public string Arguments;
    public int Calls;
    public CommandResult Result = new CommandResult { ExitCode = 0, Output = "" };
    public Task<CommandResult> Run(string executable, string arguments) { Calls++; Executable = executable; Arguments = arguments; return Task.FromResult(Result); }
}
internal sealed class FakeModeHardware : IModeHardware
{
    public bool OnAcPower { get; set; }
    public int Brightness = 72;
    public bool Night;
    public int Fan;
    public int? Game = 0;
    public string Overlay = Guid.Empty.ToString();
    public string Plan = PowerService.BalancedPlan.ToString();
    public bool FailBrightness, FailNight, FailFan, FailPower, FailGame;
    public int Writes;
    public FakeModeHardware() { OnAcPower = true; }
    public Dictionary<string, int> ReadBrightness() { return new Dictionary<string, int> { { "display", Brightness } }; }
    public void SetBrightness(string display, int value) { if (FailBrightness) throw new IOException("Brightness failed"); Writes++; Brightness = value; }
    public Dictionary<string, bool> ReadNightLight() { return new Dictionary<string, bool> { { "shared", Night }, { "deviceperdevice", Night } }; }
    public void SetNightLight(bool value) { if (FailNight) throw new IOException("Night Light failed"); Writes++; Night = value; }
    public void RestoreNightLight(string key, bool value) { SetNightLight(value); }
    public void CheckFan() { }
    public void SetFan(int value) { if (FailFan) throw new IOException("Fan failed"); Writes++; Fan = value; }
    public PowerState ReadPower() { return new PowerState { Plan = Plan, Overlay = Overlay, PlanName = "Balanced" }; }
    public void SetPower(string value) { if (FailPower) throw new IOException("Power failed"); Writes++; Overlay = PowerService.Overlay(value).ToString(); Brightness = 0; }
    public void RestorePower(PowerState value) { if (FailPower) throw new IOException("Power failed"); Writes++; Plan = value.Plan; Overlay = value.Overlay; Brightness = 0; }
    public int? ReadGameMode() { return Game; }
    public void SetGameMode(int? value) { if (FailGame) throw new IOException("Game mode failed"); Writes++; Game = value; }
}
internal static class Tests
{
    private static int count;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { count++; return; } catch (InvalidDataException) { count++; return; } catch (NotSupportedException) { count++; return; }
        throw new Exception(message);
    }
    private static byte[] Hex(string source) { return source.Split('-').Select(value => Convert.ToByte(value, 16)).ToArray(); }
    private static byte[] State(BondStruct inner, ulong timestamp)
    {
        var wrapper = new BondStruct(); wrapper.Set(1, 11, BondReader.ByteList(BondReader.Marshal(inner)));
        var container = new BondStruct(); container.Set(0, 6, BondReader.Varint(timestamp)); container.Fields.Add(new BondField { Id = 1, Type = 10, Children = wrapper });
        var outer = new BondStruct(); outer.Fields.Add(new BondField { Id = 0, Type = 10, Children = new BondStruct() }); outer.Fields.Add(new BondField { Id = 1, Type = 10, Children = container });
        return BondReader.Marshal(outer);
    }
    private static int Main()
    {
        try
        {
            Assert(TimerInput.Seconds("1") == 60, "Minimum delay");
            Assert(TimerInput.Seconds("30") == 1800, "Minutes convert to seconds");
            Assert(TimerInput.Seconds("10080") == 604800, "Maximum delay");
            foreach (string invalid in new[] { "", "0", "-1", "1.5", "10081", "999999999999", "30 & shutdown /s", "1e3", "+5" }) Reject(() => TimerInput.Seconds(invalid), "Invalid minutes were accepted");
            var runner = new RecordingRunner();
            var shutdown = new ShutdownService(runner);
            shutdown.Schedule("30").GetAwaiter().GetResult();
            Assert(runner.Arguments == "/s /t 1800", "Shutdown arguments");
            Assert(runner.Executable == Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "Absolute system executable");
            shutdown.Cancel().GetAwaiter().GetResult();
            Assert(runner.Arguments == "/a", "Cancellation arguments");
            int calls = runner.Calls;
            Reject(() => shutdown.Schedule("0").GetAwaiter().GetResult(), "Zero scheduled a shutdown");
            Assert(runner.Calls == calls, "Invalid input reached the command runner");
            runner.Result = new CommandResult { ExitCode = 1190, Output = "" };
            Assert(shutdown.Schedule("30").GetAwaiter().GetResult().ExitCode == 1190, "Windows errors must be returned");
            Assert(ShutdownService.Explain(runner.Result).Contains("already"), "Existing shutdown message");
            Assert(StartupService.Command(@"C:\Test Folder\QuietKey.exe") == '"' + @"C:\Test Folder\QuietKey.exe" + '"' + " --background", "Startup path quoting");
            Reject(() => StartupService.Command("relative.exe"), "Relative startup accepted");
            Assert(Hotkeys.VirtualKey("Space") == 0x20, "Space virtual key");
            Reject(() => Hotkeys.VirtualKey("F12"), "Reserved key accepted");
            Assert(AsusFanService.Mode("Performance") == 0 && AsusFanService.Mode("Turbo") == 1 && AsusFanService.Mode("Silent") == 2, "ASUS preset mapping");
            // Real off-state bytes captured from this Windows 11 installation, used only in memory.
            byte[] off = Hex("43-42-01-00-0A-02-01-00-2A-06-96-B0-8F-D6-06-2A-2B-0E-13-43-42-01-00-D0-0A-02-C6-14-A5-A7-B7-A8-B4-9D-D5-EE-01-00-00-00-00");
            Assert(!NightLightCodec.IsOn(off), "Night Light off parse");
            DateTime now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            byte[] on = NightLightCodec.WithState(off, true, now);
            Assert(NightLightCodec.IsOn(on), "Night Light enables");
            Assert(!NightLightCodec.IsOn(NightLightCodec.WithState(on, false, now)), "Night Light disables");
            byte[] deviceOff = Hex("43-42-01-00-0A-02-01-00-2A-06-E9-87-E6-D5-06-2A-2B-0E-14-43-42-01-00-D0-0A-02-C6-14-9C-81-F2-84-D2-CA-D3-EE-01-01-00-00-00-00");
            Assert(!NightLightCodec.IsOn(deviceOff), "Windows 25H2 per-device off state");
            byte[] deviceOn = NightLightCodec.WithState(deviceOff, true, now);
            Assert(NightLightCodec.IsOn(deviceOn), "Windows 25H2 per-device enable");
            Assert(!NightLightCodec.IsOn(NightLightCodec.WithState(deviceOn, false, now)), "Windows 25H2 per-device disable");
            BondStruct deviceEnvelope = BondReader.Decode(deviceOn);
            BondStruct deviceInner = BondReader.Decode(BondReader.GetByteList(deviceEnvelope.Find(1, 10).Children.Find(1, 10).Children.Find(1, 11).Value));
            Assert(deviceInner.Stop == 1 && deviceInner.Derived != null && deviceInner.Derived.Stop == 0, "Per-device inheritance markers preserved");
            Assert(off.SequenceEqual(Hex("43-42-01-00-0A-02-01-00-2A-06-96-B0-8F-D6-06-2A-2B-0E-13-43-42-01-00-D0-0A-02-C6-14-A5-A7-B7-A8-B4-9D-D5-EE-01-00-00-00-00")), "Original data mutated");
            var inner = new BondStruct(); inner.Set(50, 6, BondReader.Varint(12345));
            byte[] source = State(inner, 9000000000UL);
            BondStruct changed = BondReader.Decode(NightLightCodec.WithState(source, true, now));
            BondStruct payloadContainer = changed.Find(1, 10).Children;
            Assert(new BondReader(payloadContainer.Find(0, 6).Value).Number() == 9000000002UL, "Timestamp must advance beyond existing future timestamps");
            BondStruct changedInner = BondReader.Decode(BondReader.GetByteList(payloadContainer.Find(1, 10).Children.Find(1, 11).Value));
            Assert(new BondReader(changedInner.Find(50, 6).Value).Number() == 12345, "Unknown fields must survive");
            inner.Set(30, 2, new byte[] { 0 });
            Reject(() => NightLightCodec.WithState(State(inner, 1), true, now), "Unusable Night Light was written");
            Reject(() => NightLightCodec.WithState(new byte[] { 1, 2, 3 }, true, now), "Unknown format was written");
            Reject(() => NightLightCodec.WithState(off.Take(off.Length - 1).ToArray(), true, now), "Truncated data was written");
            Reject(() => NightLightCodec.WithState(off.Concat(new byte[] { 0 }).ToArray(), true, now), "Trailing data was written");
            byte[] corrupt = (byte[])off.Clone(); corrupt[17] = 255;
            Reject(() => NightLightCodec.WithState(corrupt, true, now), "Malformed list was written");
            string directory = Path.Combine(Path.GetTempPath(), "QuietKey.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new SettingsStore(directory);
                var data = new Settings { Hotkey = "K", ShutdownUtc = now, TimerBootUtc = now.AddMinutes(-60), Restore = new RestorePoint { NightLightOn = false } };
                data.Restore.Brightness["display"] = 65;
                data.SleepProfile.Brightness = 37;
                store.Save(data);
                Settings loaded = store.Load();
                Assert(loaded.Hotkey == "K" && loaded.SleepProfile.Brightness == 37, "Profile persistence");
                Assert(loaded.ShutdownUtc == now && loaded.Restore.NightLightOn == false && loaded.Restore.Brightness["display"] == 65, "Restore and countdown persistence");
                data.SleepProfile.Brightness = 88; store.Save(data);
                File.WriteAllText(store.FilePath, "broken json");
                loaded = store.Load();
                Assert(loaded.SleepProfile.Brightness == 37 && store.LoadWarning != null, "Corrupt settings backup recovery");
                File.WriteAllText(store.FilePath + ".bak", "broken backup");
                loaded = store.Load(); Assert(loaded.Hotkey == "Space", "Corrupt backups return defaults");
                ModeChecks(directory);
            }
            finally
            {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
            Console.WriteLine("PASS: " + count + " checks. No shutdown, brightness, Night Light, fan, or startup settings were changed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ModeChecks(string directory)
    {
        var store = new SettingsStore(directory);
        var settings = new Settings();
        var hardware = new FakeModeHardware();
        var service = new ModeService(settings, store, hardware);
        ActionReport report = service.Enable("Sleep");
        Assert(report.Failed.Count == 0 && settings.ActiveMode == "Sleep", "Sleep toggle on");
        Assert(hardware.Brightness == 25 && hardware.Night && hardware.Fan == 2, "Sleep applies display and fan settings after power changes");
        Assert(store.Load().ActiveMode == "Sleep", "Active switch state persists");
        report = service.Enable("Gaming");
        Assert(report.Failed.Count == 0 && settings.ActiveMode == "Gaming", "Switching modes is exclusive");
        Assert(hardware.Brightness == 100 && !hardware.Night && hardware.Fan == 1 && hardware.Game == 1, "Gaming applies maximum normal presets");
        Assert(hardware.Overlay == PowerService.Overlay("Performance").ToString(), "Best performance overlay");
        report = service.Disable();
        Assert(report.Failed.Count == 0 && settings.ActiveMode == "None" && settings.Restore == null, "Off clears completed restoration");
        Assert(hardware.Brightness == 72 && !hardware.Night && hardware.Fan == 0 && hardware.Game == 0 && hardware.Overlay == Guid.Empty.ToString(), "Off restores initial settings including brightness after plan changes");
        service.Enable("Sleep"); hardware.OnAcPower = false;
        int writes = hardware.Writes;
        report = service.Enable("Gaming");
        Assert(report.Failed.Count > 0 && settings.ActiveMode == "Sleep" && writes == hardware.Writes, "Gaming refuses battery without disabling Sleep or changing hardware");
        hardware.OnAcPower = true; service.Disable();
        hardware.FailNight = true;
        report = service.Enable("Sleep");
        Assert(settings.ActiveMode == "Sleep" && settings.ModeHasWarnings && report.Failed.Count == 1, "Partial apply shows a warning");
        report = service.Disable();
        Assert(report.Failed.Count > 0 && settings.Restore != null && settings.ActiveMode == "Sleep", "Failed Off retains restoration for retry");
        int failedSwitchWrites = hardware.Writes;
        report = service.Enable("Gaming");
        Assert(settings.ActiveMode == "Sleep" && report.Failed.Count > 0 && hardware.Writes == failedSwitchWrites, "Cannot switch modes while restoration is incomplete");
        hardware.FailNight = false; service.Disable();
        Assert(settings.ActiveMode == "None" && settings.Restore == null, "Retry Off completes restoration");
        hardware.FailBrightness = hardware.FailNight = hardware.FailFan = hardware.FailPower = true;
        report = service.Enable("Sleep");
        Assert(settings.ActiveMode == "None" && report.Applied.Count == 0, "An entirely failed mode does not show On");
        hardware.FailBrightness = hardware.FailNight = hardware.FailFan = hardware.FailPower = false; service.Disable();
        hardware.Game = null; service.Enable("Gaming"); service.Disable();
        Assert(hardware.Game == null, "Unset Game Mode value is restored by removal");
        hardware.FailGame = true; report = service.Enable("Gaming");
        Assert(settings.ModeHasWarnings && report.Failed.Count == 1, "A failed Game Mode setting is reported");
        hardware.FailGame = false; service.Disable();
        string blockedDirectory = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blockedDirectory, "blocked");
        var blockedSettings = new Settings(); var blockedHardware = new FakeModeHardware();
        try { new ModeService(blockedSettings, new SettingsStore(blockedDirectory), blockedHardware).Enable("Sleep"); throw new Exception("Expected settings failure"); }
        catch (IOException) { Assert(blockedHardware.Writes == 0 && blockedSettings.ActiveMode == "None", "Settings failure prevents hardware changes"); }
    }
}
