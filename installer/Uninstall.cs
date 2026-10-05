using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Uninstall QuietKey")]
[assembly: AssemblyVersion("1.1.0.0")]
namespace QuietKeyInstaller
{
    internal static class UninstallProgram
    {
        [STAThread] private static int Main(string[] args)
        {
            bool quiet = args.Contains("--quiet");
            try {
                // Run a temporary copy so the installed uninstaller can be removed without a shell cleanup script.
                if (!args.Contains("--relocated")) {
                    string temporary = Path.Combine(Path.GetTempPath(), "QuietKey.Uninstall." + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Assembly.GetExecutingAssembly().Location, temporary);
                    using (Process process = Process.Start(new ProcessStartInfo(temporary, "--relocated" + (quiet ? " --quiet" : "")) { UseShellExecute = false, CreateNoWindow = true })) {
                        // The installed executable must exit before the temporary copy removes it.
                    }
                    return 0;
                }
                System.Threading.Thread.Sleep(500);
                Application.EnableVisualStyles();
                if (!quiet && MessageBox.Show("Remove QuietKey from this account?\n\nActive modes will be restored, and your saved profiles will be kept. Any Windows shutdown timer continues until cancelled with shutdown /a.", "Uninstall QuietKey", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                InstallerEngine.Uninstall(message => { });
                if (!quiet) MessageBox.Show("QuietKey was removed. Your saved profiles were kept.", "QuietKey", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            } catch (Exception e) {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "QuietKey.Uninstall.error.log"), e.ToString()); } catch { }
                if (!quiet) MessageBox.Show(e.Message, "Could not uninstall QuietKey", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
