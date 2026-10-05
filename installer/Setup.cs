using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("QuietKey Setup")]
[assembly: AssemblyVersion("1.1.0.0")]
namespace QuietKeyInstaller
{
    internal static class Setup
    {
        [STAThread] private static int Main(string[] args)
        {
            try {
                if (args.Contains("--quiet")) { InstallerEngine.Install(true, true, message => { }); if (!args.Contains("--no-launch")) InstallerEngine.Launch(); return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new SetupForm()) {
                    if (args.Length == 2 && args[0] == "--preview") {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000); form.Show(); Application.DoEvents();
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                        form.Close(); return 0;
                    }
                    Application.Run(form);
                }
                return 0;
            } catch (Exception e) {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "QuietKey.Setup.error.log"), e.ToString()); } catch { }
                if (!args.Contains("--quiet")) MessageBox.Show(e.Message, "QuietKey setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    internal sealed class SetupForm : Form
    {
        private readonly CheckBox startup, desktop;
        private readonly Button install, cancel;
        private readonly Label status;
        private readonly ProgressBar progress;
        private bool working, complete;
        public SetupForm()
        {
            Text = "QuietKey Setup"; ClientSize = new Size(530, 430); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = new Font("Segoe UI", 10);
            using (var icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)) Icon = (Icon)icon.Clone();
            Controls.Add(new Label { Text = "Install QuietKey", Font = new Font("Segoe UI", 22, FontStyle.Bold), Location = new Point(30, 25), AutoSize = true });
            Controls.Add(new Label { Text = "Sleep and Gaming modes, one shortcut away.\nVersion " + InstallerEngine.Version + " • Installs for your Windows account", Location = new Point(32, 82), Size = new Size(470, 47), ForeColor = Color.FromArgb(87, 100, 116) });
            Controls.Add(new Label { Text = "Installation folder", Location = new Point(32, 148), AutoSize = true });
            Controls.Add(new TextBox { Text = InstallerEngine.DirectoryPath, ReadOnly = true, Location = new Point(32, 173), Size = new Size(463, 26), BackColor = Color.FromArgb(246, 248, 250), BorderStyle = BorderStyle.FixedSingle });
            startup = new CheckBox { Text = "Start QuietKey with Windows", Checked = true, Location = new Point(32, 222), Size = new Size(420, 27) };
            desktop = new CheckBox { Text = "Create a desktop shortcut", Checked = true, Location = new Point(32, 251), Size = new Size(420, 27) };
            Controls.Add(startup); Controls.Add(desktop);
            status = new Label { Text = "Also adds Start menu shortcuts and a Windows uninstaller.\nYour saved profiles are kept when updating.", Location = new Point(32, 295), Size = new Size(465, 45), ForeColor = Color.FromArgb(87, 100, 116) };
            progress = new ProgressBar { Location = new Point(32, 344), Size = new Size(465, 7), Visible = false, Style = ProgressBarStyle.Marquee };
            Controls.Add(status); Controls.Add(progress);
            cancel = new Button { Text = "Cancel", Location = new Point(283, 375), Size = new Size(95, 33) };
            install = new Button { Text = "Install", Location = new Point(390, 375), Size = new Size(107, 33), BackColor = Color.FromArgb(114, 229, 195), FlatStyle = FlatStyle.Flat };
            install.FlatAppearance.BorderSize = 0; Controls.Add(cancel); Controls.Add(install); AcceptButton = install; CancelButton = cancel;
            cancel.Click += (sender, args) => Close();
            install.Click += async (sender, args) => {
                if (complete) { InstallerEngine.Launch(); Close(); return; }
                if (working) return;
                working = true; install.Enabled = cancel.Enabled = startup.Enabled = desktop.Enabled = false; progress.Visible = true;
                bool enableStartup = startup.Checked, createDesktop = desktop.Checked;
                var reporter = new Progress<string>(message => status.Text = message);
                try {
                    await Task.Run(() => InstallerEngine.Install(enableStartup, createDesktop, message => ((IProgress<string>)reporter).Report(message)));
                    complete = true; install.Text = "Open app"; cancel.Text = "Close";
                    status.Text = "QuietKey is installed. Press Ctrl + Alt + Space to open it.\nYou can uninstall it from Windows Settings → Installed apps.";
                } catch (Exception e) { status.Text = "Installation failed: " + e.Message; }
                finally { working = false; progress.Visible = false; install.Enabled = cancel.Enabled = true; }
            };
            FormClosing += (sender, args) => { if (working) args.Cancel = true; };
        }
    }
}
