using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuietKeyInstaller
{
    internal static class InstallerEngine
    {
        public const string Version = "1.1.0";
        public static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietKey", "app"));
        public static readonly string AppPath = Path.Combine(DirectoryPath, "QuietKey.exe");
        public static readonly string UninstallerPath = Path.Combine(DirectoryPath, "QuietKey.Uninstall.exe");
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuietKey";
        public static string Quote(string path) { if (path.IndexOf('"') >= 0) throw new ArgumentException("Invalid application path."); return "\"" + path + "\""; }
        private static string OwnedFile(string name)
        {
            string result = Path.GetFullPath(Path.Combine(DirectoryPath, name));
            if (!string.Equals(Path.GetDirectoryName(result), DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected application file path.");
            return result;
        }
        public static void StopApp()
        {
            foreach (Process process in Process.GetProcessesByName("QuietKey")) using (process) {
                string path;
                try { path = process.MainModule.FileName; } catch { continue; }
                if (!string.Equals(Path.GetFullPath(path), AppPath, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill();
                if (!process.WaitForExit(10000)) throw new IOException("QuietKey is still running. Close it from the tray and retry.");
            }
        }
        private static void Extract(string resource, string name)
        {
            string path = OwnedFile(name);
            string pending = OwnedFile(name + ".new");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) {
                if (source == null) throw new IOException("The setup package is incomplete.");
                using (FileStream target = File.Create(pending)) { source.CopyTo(target); target.Flush(true); }
            }
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
        public static void Install(bool startup, bool desktop, Action<string> progress)
        {
            Directory.CreateDirectory(DirectoryPath);
            progress("Closing the previous QuietKey version…"); StopApp();
            progress("Installing QuietKey " + Version + "…");
            Extract("QuietKey.Payload.App.exe", "QuietKey.exe");
            Extract("QuietKey.Payload.Uninstall.exe", "QuietKey.Uninstall.exe");
            progress("Adding Windows startup and shortcuts…");
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                string command = Quote(AppPath) + " --background";
                if (command.Length > 260) throw new IOException("The application path is too long for Windows startup.");
                if (startup) run.SetValue("QuietKey", command); else run.DeleteValue("QuietKey", false);
            }
            if (startup) using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true)) if (approved != null) approved.DeleteValue("QuietKey", false);
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QuietKey");
            Directory.CreateDirectory(programs);
            Shortcut(Path.Combine(programs, "QuietKey.lnk"), AppPath, "QuietKey: Ctrl + Alt + Space");
            Shortcut(Path.Combine(programs, "Uninstall QuietKey.lnk"), UninstallerPath, "Uninstall QuietKey");
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "QuietKey.lnk");
            if (desktop) Shortcut(desktopLink, AppPath, "QuietKey: Ctrl + Alt + Space"); else RemoveShortcut(desktopLink, AppPath);
            progress("Registering QuietKey in Windows Installed apps…");
            using (RegistryKey uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
                uninstall.SetValue("DisplayName", "QuietKey");
                uninstall.SetValue("DisplayVersion", Version);
                uninstall.SetValue("Publisher", "QuietKey");
                uninstall.SetValue("InstallLocation", DirectoryPath);
                uninstall.SetValue("DisplayIcon", AppPath + ",0");
                uninstall.SetValue("UninstallString", Quote(UninstallerPath));
                uninstall.SetValue("QuietUninstallString", Quote(UninstallerPath) + " --quiet");
                uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                uninstall.SetValue("EstimatedSize", (int)((new FileInfo(AppPath).Length + new FileInfo(UninstallerPath).Length) / 1024), RegistryValueKind.DWord);
            }
            string legacy = OwnedFile("Uninstall.ps1"); if (File.Exists(legacy)) File.Delete(legacy);
            progress("Installed. Your saved profiles were preserved.");
        }
        public static void Launch() { Process.Start(new ProcessStartInfo(AppPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = DirectoryPath }); }
        public static void Uninstall(Action<string> progress)
        {
            StopApp();
            progress("Restoring any active mode…");
            if (File.Exists(AppPath)) {
                string report = Path.Combine(Path.GetTempPath(), "QuietKey.Restore." + Guid.NewGuid().ToString("N") + ".txt");
                using (Process process = Process.Start(new ProcessStartInfo(AppPath, "--restore-mode " + Quote(report)) { UseShellExecute = false, CreateNoWindow = true })) {
                    if (!process.WaitForExit(30000)) throw new IOException("Restoring the active mode took too long. Switch the mode off in QuietKey and retry.");
                    if (process.ExitCode != 0) throw new IOException("Some settings could not be restored. Open QuietKey, switch its mode off, and retry uninstall. " + (File.Exists(report) ? File.ReadAllText(report) : ""));
                }
                if (File.Exists(report)) File.Delete(report);
            }
            progress("Removing startup and shortcuts…");
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (run != null) run.DeleteValue("QuietKey", false);
            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true)) if (approved != null) approved.DeleteValue("QuietKey", false);
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "QuietKey.lnk"), AppPath);
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QuietKey");
            RemoveShortcut(Path.Combine(programs, "QuietKey.lnk"), AppPath);
            RemoveShortcut(Path.Combine(programs, "Uninstall QuietKey.lnk"), UninstallerPath);
            if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs);
            progress("Removing application files…");
            foreach (string name in new[] { "QuietKey.exe", "QuietKey.Uninstall.exe", "Uninstall.ps1" }) { string path = OwnedFile(name); if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any()) Directory.Delete(DirectoryPath);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            progress("QuietKey was removed. Your saved profiles remain in LocalAppData\\QuietKey.");
        }
        private static void Shortcut(string path, string target, string description)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(path);
            try { link.TargetPath = target; link.WorkingDirectory = DirectoryPath; link.Description = description; link.IconLocation = target + ",0"; link.Save(); }
            finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        }
        private static void RemoveShortcut(string path, string target)
        {
            if (!File.Exists(path)) return;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic link = shell.CreateShortcut(path);
            bool owned;
            try { owned = string.Equals((string)link.TargetPath, target, StringComparison.OrdinalIgnoreCase); }
            finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
            if (owned) File.Delete(path);
        }
    }
}
