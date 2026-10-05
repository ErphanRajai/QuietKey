using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace QuietKey
{
    public sealed class Settings
    {
        public int Version { get; set; }
        public string Hotkey { get; set; }
        public Profile SleepProfile { get; set; }
        public Profile GamingProfile { get; set; }
        public string ActiveMode { get; set; }
        public DateTime? ModeBootUtc { get; set; }
        public bool ModeHasWarnings { get; set; }
        public DateTime? ShutdownUtc { get; set; }
        public DateTime? TimerBootUtc { get; set; }
        public RestorePoint Restore { get; set; }
        public Settings() { Version = 2; Hotkey = "Space"; SleepProfile = new Profile(); GamingProfile = Profile.Gaming(); ActiveMode = "None"; }
        public void Validate()
        {
            if (!Hotkeys.IsValid(Hotkey)) Hotkey = "Space";
            if (SleepProfile == null) SleepProfile = new Profile();
            if (GamingProfile == null) GamingProfile = Profile.Gaming();
            if (ActiveMode != "Sleep" && ActiveMode != "Gaming") ActiveMode = "None";
            SleepProfile.Validate();
            GamingProfile.Validate();
            if (Restore != null) Restore.Validate();
            Version = 2;
        }
    }
    public sealed class Profile
    {
        public bool ChangeBrightness { get; set; }
        public int Brightness { get; set; }
        public string NightLight { get; set; }
        public string FanMode { get; set; }
        public string PowerMode { get; set; }
        public bool EnableGameMode { get; set; }
        public Profile() { ChangeBrightness = true; Brightness = 25; NightLight = "On"; FanMode = "Silent"; PowerMode = "Efficiency"; }
        public static Profile Gaming() { return new Profile { Brightness = 100, NightLight = "Off", FanMode = "Turbo", PowerMode = "Performance", EnableGameMode = true }; }
        public void Validate()
        {
            Brightness = Math.Max(0, Math.Min(100, Brightness));
            if (NightLight != "On" && NightLight != "Off" && NightLight != "Keep") NightLight = "Keep";
            if (FanMode != "Silent" && FanMode != "Performance" && FanMode != "Turbo" && FanMode != "Keep") FanMode = "Keep";
            if (PowerMode != "Efficiency" && PowerMode != "Balanced" && PowerMode != "Performance" && PowerMode != "Keep") PowerMode = "Keep";
        }
        public string Summary()
        {
            return (ChangeBrightness ? Brightness + "% brightness" : "Keep brightness") + "  ·  " +
                (NightLight == "Keep" ? "Keep Night Light" : "Night Light " + NightLight.ToLowerInvariant()) + "  ·  " +
                (FanMode == "Keep" ? "Keep fan mode" : FanMode);
        }
    }
    public sealed class RestorePoint
    {
        public Dictionary<string, int> Brightness { get; set; }
        public bool? NightLightOn { get; set; }
        public Dictionary<string, bool> NightLightStates { get; set; }
        public PowerState Power { get; set; }
        public int? FanMode { get; set; }
        public bool GameModeCaptured { get; set; }
        public int? GameModeValue { get; set; }
        public RestorePoint() { Brightness = new Dictionary<string, int>(); }
        public void Validate() { if (Brightness == null) Brightness = new Dictionary<string, int>(); }
        public bool HasChanges() { return (Brightness != null && Brightness.Count > 0) || NightLightOn.HasValue || (NightLightStates != null && NightLightStates.Count > 0) || Power != null || FanMode.HasValue || GameModeCaptured; }
    }
    public sealed class PowerState
    {
        public string Plan { get; set; }
        public string Overlay { get; set; }
        public string PlanName { get; set; }
    }
    public static class Hotkeys
    {
        public static readonly string[] Keys = { "Space", "Q", "K", "S" };
        public static bool IsValid(string key) { return Array.IndexOf(Keys, key) >= 0; }
        public static uint VirtualKey(string key)
        {
            if (!IsValid(key)) throw new ArgumentException("Unsupported shortcut key.");
            return key == "Space" ? 0x20U : (uint)key[0];
        }
        public static string Label(string key) { return "Ctrl + Alt + " + key; }
    }
    public static class TimerInput
    {
        public static int Seconds(string text)
        {
            int minutes;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < 1 || minutes > 10080)
                throw new ArgumentException("Enter a whole number from 1 to 10,080 minutes.");
            return checked(minutes * 60);
        }
        public static string Arguments(int seconds)
        {
            if (seconds < 60 || seconds > 604800) throw new ArgumentOutOfRangeException("seconds");
            return "/s /t " + seconds.ToString(CultureInfo.InvariantCulture);
        }
    }
    public sealed class SettingsStore
    {
        public readonly string DirectoryPath;
        public readonly string FilePath;
        public string LoadWarning { get; private set; }
        public SettingsStore(string directory)
        {
            DirectoryPath = directory;
            FilePath = Path.Combine(directory, "settings.json");
        }
        public Settings Load()
        {
            if (!File.Exists(FilePath)) return new Settings();
            try { return Read(FilePath); }
            catch
            {
                try
                {
                    Settings result = Read(FilePath + ".bak");
                    LoadWarning = "Settings were recovered from a backup.";
                    return result;
                }
                catch { LoadWarning = "Saved settings could not be read. Defaults are in use."; return new Settings(); }
            }
        }
        private Settings Read(string path)
        {
            Settings data = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
            if (data == null) throw new InvalidDataException("Empty settings.");
            data.Validate();
            return data;
        }
        public void Save(Settings data)
        {
            data.Validate();
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
    }
}
