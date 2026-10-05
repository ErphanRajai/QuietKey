using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuietKey
{
    public sealed class UiController : IDisposable
    {
        private readonly Application app;
        private readonly Settings settings;
        private readonly SettingsStore store;
        private readonly ShutdownService shutdown;
        private readonly bool preview;
        private readonly Window window;
        private readonly DispatcherTimer tick;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem cancelTray;
        private bool exiting;
        private bool busy;
        private bool syncingSwitches;
        private string editingMode = "Sleep";
        public GlobalHotkey Hotkey;
        private T Find<T>(string name) where T : class { return window.FindName(name) as T; }
        private Button Button(string name) { return Find<Button>(name); }
        public UiController(Application application, Settings data, SettingsStore settingsStore, ShutdownService shutdownService, bool isPreview)
        {
            app = application; settings = data; store = settingsStore; shutdown = shutdownService; preview = isPreview;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuietKey.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            window.PreviewKeyDown += (sender, args) => { if (args.Key == Key.Escape) { window.Hide(); args.Handled = true; } };
            window.Closing += (sender, args) => { if (!exiting) { args.Cancel = true; window.Hide(); } };
            window.SizeChanged += (sender, args) => {
                if (preview || !window.IsVisible) return;
                Rect work = SystemParameters.WorkArea;
                window.Top = Math.Max(work.Top + 8, Math.Min(window.Top, work.Bottom - window.ActualHeight - 8));
            };
            Find<Grid>("Header").MouseLeftButtonDown += (sender, args) => {
                if (args.OriginalSource is TextBlock || args.OriginalSource is Grid || args.OriginalSource is Border) {
                    DependencyObject current = args.OriginalSource as DependencyObject;
                    while (current != null) { if (current is Button) return; current = VisualTreeHelper.GetParent(current); }
                    if (args.ButtonState == MouseButtonState.Pressed) window.DragMove();
                }
            };
            Button("HideButton").Click += (sender, args) => window.Hide();
            Button("TimerCard").Click += (sender, args) => {
                if (settings.ShutdownUtc.HasValue) { Status("A shutdown timer is already scheduled. Use Cancel before starting another.", false); return; }
                Page("TimerPage"); Find<TextBox>("MinutesInput").Focus(); Find<TextBox>("MinutesInput").SelectAll();
            };
            Button("EditProfileButton").Click += (sender, args) => { LoadProfile("Sleep"); Page("ProfilePage"); };
            Button("EditGamingButton").Click += (sender, args) => { LoadProfile("Gaming"); Page("ProfilePage"); };
            Button("SleepCard").Click += (sender, args) => Find<ToggleButton>("SleepToggle").IsChecked = Find<ToggleButton>("SleepToggle").IsChecked != true;
            Button("GamingCard").Click += (sender, args) => Find<ToggleButton>("GamingToggle").IsChecked = Find<ToggleButton>("GamingToggle").IsChecked != true;
            Find<ToggleButton>("SleepToggle").Checked += async (sender, args) => { if (!syncingSwitches) await SetMode("Sleep", true); };
            Find<ToggleButton>("SleepToggle").Unchecked += async (sender, args) => { if (!syncingSwitches) await SetMode("Sleep", false); };
            Find<ToggleButton>("GamingToggle").Checked += async (sender, args) => { if (!syncingSwitches) await SetMode("Gaming", true); };
            Find<ToggleButton>("GamingToggle").Unchecked += async (sender, args) => { if (!syncingSwitches) await SetMode("Gaming", false); };
            Button("RestoreButton").Click += async (sender, args) => await SetMode(settings.ActiveMode, false);
            Button("SettingsButton").Click += (sender, args) => { LoadSettings(); Page("SettingsPage"); };
            foreach (string back in new[] { "TimerBack", "ProfileBack", "SettingsBack" }) Button(back).Click += (sender, args) => Page("HomePage");
            foreach (int number in new[] { 15, 30, 60, 90 }) {
                int minutes = number;
                Button("Minutes" + number).Click += (sender, args) => { Find<TextBox>("MinutesInput").Text = minutes.ToString(); Find<TextBox>("MinutesInput").Focus(); };
            }
            Button("StartTimerButton").Click += async (sender, args) => await StartTimer();
            Find<TextBox>("MinutesInput").KeyDown += async (sender, args) => { if (args.Key == Key.Enter) { args.Handled = true; await StartTimer(); } };
            Button("CancelTimerButton").Click += async (sender, args) => await CancelTimer();
            Button("SaveProfileButton").Click += async (sender, args) => await SaveProfile();
            Button("SaveSettingsButton").Click += (sender, args) => SaveSettings();
            Button("NightLightSettingsButton").Click += (sender, args) => OpenSettings("ms-settings:nightlight");
            Button("OpenStartupSettingsButton").Click += (sender, args) => OpenSettings("ms-settings:startupapps");
            Find<Slider>("BrightnessSlider").ValueChanged += (sender, args) => Find<TextBlock>("BrightnessValue").Text = ((int)args.NewValue) + "%";
            Find<CheckBox>("BrightnessEnabled").Checked += (sender, args) => Find<Slider>("BrightnessSlider").IsEnabled = true;
            Find<CheckBox>("BrightnessEnabled").Unchecked += (sender, args) => Find<Slider>("BrightnessSlider").IsEnabled = false;
            Refresh();
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (sender, args) => { if (!busy) RefreshTimer(); };
            if (!preview) tick.Start();
        }
        private void Page(string name)
        {
            foreach (string page in new[] { "HomePage", "TimerPage", "ProfilePage", "SettingsPage" }) Find<StackPanel>(page).Visibility = page == name ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("StatusPanel").Visibility = Visibility.Collapsed;
        }
        public void Status(string message, bool error)
        {
            Find<TextBlock>("StatusText").Text = message;
            Find<TextBlock>("StatusText").Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#F0C4AA" : "#D3DFEE"));
            Find<Border>("StatusPanel").Visibility = Visibility.Visible;
        }
        public void Show()
        {
            if (!window.IsVisible) {
                Rect work = SystemParameters.WorkArea;
                window.MaxHeight = Math.Max(300, work.Height - 32);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = work.Left + (work.Width - window.Width) / 2;
                window.Top = work.Top + Math.Max(16, (work.Height - Math.Max(window.ActualHeight, 440)) / 2);
                window.Show();
            }
            window.Activate();
            window.Focus();
        }
        public void Toggle() { if (window.IsVisible && window.IsActive) window.Hide(); else Show(); }
        private void SetBusy(bool state)
        {
            busy = state;
            foreach (string name in new[] { "StartTimerButton", "CancelTimerButton", "SleepCard", "GamingCard", "RestoreButton", "EditProfileButton", "EditGamingButton", "SaveProfileButton", "SaveSettingsButton", "SettingsButton", "TimerCard", "TimerBack", "ProfileBack", "SettingsBack" }) Button(name).IsEnabled = !state;
            Find<ToggleButton>("SleepToggle").IsEnabled = !state;
            Find<ToggleButton>("GamingToggle").IsEnabled = !state;
            if (cancelTray != null) cancelTray.Enabled = !state;
            window.Cursor = state ? Cursors.Wait : Cursors.Arrow;
        }
        private async Task StartTimer()
        {
            if (busy) return;
            if (settings.ShutdownUtc.HasValue) { Status("A shutdown is already scheduled. Cancel it before starting another.", true); return; }
            string text = Find<TextBox>("MinutesInput").Text.Trim();
            int seconds;
            try { seconds = TimerInput.Seconds(text); } catch (Exception e) { Status(e.Message, true); return; }
            SetBusy(true);
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                CommandResult result = await shutdown.Schedule(text);
                if (result.ExitCode != 0) { Status(ShutdownService.Explain(result), true); return; }
                settings.ShutdownUtc = deadline;
                settings.TimerBootUtc = BootClock.StartedUtc();
                Page("HomePage");
                Refresh();
                try { store.Save(settings); Status("Shutdown scheduled. You can cancel it here or from the tray.", false); }
                catch (Exception e) { Status("Windows scheduled the shutdown, but the timer could not be saved: " + e.Message + " You can still cancel it here.", true); }
            }
            catch (Exception e) { Status("Could not schedule shutdown: " + e.Message, true); }
            finally { SetBusy(false); }
        }
        private async Task CancelTimer()
        {
            if (busy) return;
            SetBusy(true);
            try
            {
                CommandResult result = await shutdown.Cancel();
                if (result.ExitCode != 0 && result.ExitCode != 1116) { Show(); Status(ShutdownService.Explain(result), true); return; }
                settings.ShutdownUtc = null; settings.TimerBootUtc = null;
                Refresh();
                Show();
                try { store.Save(settings); Status(result.ExitCode == 1116 ? "Windows has no shutdown pending. The timer display has been cleared." : "Shutdown timer cancelled.", false); }
                catch (Exception e) { Status("The shutdown was cancelled, but settings could not be saved: " + e.Message, true); }
            }
            catch (Exception e) { Show(); Status("Could not cancel shutdown: " + e.Message, true); }
            finally { SetBusy(false); }
        }
        private async Task SetMode(string mode, bool enabled)
        {
            if (busy) { Refresh(); return; }
            SetBusy(true);
            Status(enabled ? "Enabling " + mode + " mode…" : "Restoring your previous settings…", false);
            try
            {
                ActionReport report = await Task.Run(() => {
                    var service = new ModeService(settings, store, new WindowsModeHardware(store.DirectoryPath));
                    return enabled ? service.Enable(mode) : service.Disable();
                });
                Refresh();
                Status(report.Text(), report.Failed.Count > 0);
            }
            catch (Exception e) { Refresh(); Status("The profile could not be completed: " + e.Message, true); Program.Log(e); }
            finally { SetBusy(false); }
        }
        private void LoadProfile(string mode)
        {
            editingMode = mode;
            Profile profile = mode == "Gaming" ? settings.GamingProfile : settings.SleepProfile;
            Find<TextBlock>("ProfileTitle").Text = "Your " + mode.ToLowerInvariant() + " mode";
            Find<TextBlock>("ProfileHint").Text = "Choose what changes when you enable " + mode + " mode.";
            Button("SaveProfileButton").Content = "Save " + mode.ToLowerInvariant() + " mode";
            Find<CheckBox>("BrightnessEnabled").IsChecked = profile.ChangeBrightness;
            Find<Slider>("BrightnessSlider").Value = profile.Brightness;
            Select("NightLightChoice", profile.NightLight);
            Select("FanChoice", profile.FanMode);
            Select("PowerChoice", profile.PowerMode);
            Find<CheckBox>("EnableGameMode").Visibility = mode == "Gaming" ? Visibility.Visible : Visibility.Collapsed;
            Find<CheckBox>("EnableGameMode").IsChecked = profile.EnableGameMode;
        }
        private void Select(string name, string tag)
        {
            ComboBox choice = Find<ComboBox>(name);
            choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag);
        }
        private string Choice(string name) { return Convert.ToString(((ComboBoxItem)Find<ComboBox>(name).SelectedItem).Tag); }
        private async Task SaveProfile()
        {
            Profile previous = editingMode == "Gaming" ? settings.GamingProfile : settings.SleepProfile;
            var profile = new Profile { ChangeBrightness = Find<CheckBox>("BrightnessEnabled").IsChecked == true, Brightness = (int)Find<Slider>("BrightnessSlider").Value, NightLight = Choice("NightLightChoice"), FanMode = Choice("FanChoice"), PowerMode = Choice("PowerChoice"), EnableGameMode = editingMode == "Gaming" && Find<CheckBox>("EnableGameMode").IsChecked == true };
            if (editingMode == "Gaming") settings.GamingProfile = profile; else settings.SleepProfile = profile;
            try { store.Save(settings); Refresh(); Page("HomePage"); Status(editingMode + " mode saved. Use its switch to enable it.", false); }
            catch (Exception e) { if (editingMode == "Gaming") settings.GamingProfile = previous; else settings.SleepProfile = previous; Status("Could not save the profile: " + e.Message, true); return; }
            if (settings.ActiveMode == editingMode) await SetMode(editingMode, true);
        }
        private void LoadSettings()
        {
            Find<CheckBox>("StartupEnabled").IsChecked = preview || StartupService.Enabled();
            Select("HotkeyChoice", settings.Hotkey);
        }
        private void SaveSettings()
        {
            string next = Choice("HotkeyChoice");
            string previous = settings.Hotkey;
            bool oldStartup = StartupService.Enabled();
            if (Hotkey != null && !Hotkey.Set(next)) { Status("That shortcut is already in use. Choose a different one.", true); return; }
            try
            {
                StartupService.Set(Find<CheckBox>("StartupEnabled").IsChecked == true, Program.Executable);
                settings.Hotkey = next;
                store.Save(settings);
                Refresh(); Page("HomePage"); Status("Startup and shortcut settings saved.", false);
            }
            catch (Exception e)
            {
                settings.Hotkey = previous;
                if (Hotkey != null) Hotkey.Set(previous);
                try { StartupService.Set(oldStartup, Program.Executable); } catch { }
                Status("Could not save settings: " + e.Message, true);
            }
        }
        private void Refresh()
        {
            Find<TextBlock>("ProfileDescription").Text = settings.SleepProfile.Summary();
            Find<TextBlock>("GamingDescription").Text = settings.GamingProfile.Summary();
            syncingSwitches = true;
            Find<ToggleButton>("SleepToggle").IsChecked = settings.ActiveMode == "Sleep";
            Find<ToggleButton>("GamingToggle").IsChecked = settings.ActiveMode == "Gaming";
            syncingSwitches = false;
            Find<TextBlock>("SleepState").Text = settings.ActiveMode == "Sleep" ? (settings.ModeHasWarnings ? "On · partial" : "On") : "Off";
            Find<TextBlock>("GamingState").Text = settings.ActiveMode == "Gaming" ? (settings.ModeHasWarnings ? "On · partial" : "On") : "Off";
            Find<TextBlock>("ShortcutLabel").Text = Hotkeys.Label(settings.Hotkey);
            bool enabled = preview || StartupService.Enabled();
            Find<System.Windows.Shapes.Ellipse>("StartupDot").Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(enabled ? "#72E5C3" : "#8594A9"));
            Find<System.Windows.Shapes.Ellipse>("StartupDot").ToolTip = enabled ? "Windows startup is enabled" : "Windows startup is disabled";
            Button("RestoreButton").Visibility = settings.Restore != null && settings.Restore.HasChanges() ? Visibility.Visible : Visibility.Collapsed;
            RefreshTimer();
        }
        private void RefreshTimer()
        {
            bool active = settings.ShutdownUtc.HasValue;
            if (active && settings.ShutdownUtc.Value <= DateTime.UtcNow) {
                settings.ShutdownUtc = null; settings.TimerBootUtc = null; active = false;
                if (!preview) try { store.Save(settings); } catch (Exception e) { Program.Log(e); }
            }
            Find<Border>("CountdownPanel").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("TimerDescription").Text = active ? "Shutdown is scheduled. Cancel it below." : "Shut down after a set number of minutes.";
            if (active) {
                TimeSpan remaining = settings.ShutdownUtc.Value - DateTime.UtcNow;
                string text = remaining.TotalHours >= 1 ? ((int)remaining.TotalHours) + ":" + remaining.Minutes.ToString("00") + ":" + remaining.Seconds.ToString("00") : remaining.Minutes.ToString("00") + ":" + remaining.Seconds.ToString("00");
                Find<TextBlock>("CountdownText").Text = text + " remaining";
                Find<TextBlock>("DeadlineText").Text = "Shutdown at " + settings.ShutdownUtc.Value.ToLocalTime().ToString("ddd, h:mm tt");
                if (tray != null) tray.Text = "QuietKey · " + text + " until shutdown";
            } else if (tray != null) tray.Text = "QuietKey · " + Hotkeys.Label(settings.Hotkey);
        }
        private void OpenSettings(string uri)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception e) { Status("Could not open Windows Settings: " + e.Message, true); }
        }
        public void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open QuietKey", null, (sender, args) => Show());
            menu.Items.Add("Toggle sleep mode", null, async (sender, args) => { Show(); Page("HomePage"); await SetMode("Sleep", settings.ActiveMode != "Sleep"); });
            menu.Items.Add("Toggle gaming mode", null, async (sender, args) => { Show(); Page("HomePage"); await SetMode("Gaming", settings.ActiveMode != "Gaming"); });
            cancelTray = (Forms.ToolStripMenuItem)menu.Items.Add("Cancel pending shutdown", null, async (sender, args) => await CancelTimer());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Settings", null, (sender, args) => { Show(); LoadSettings(); Page("SettingsPage"); });
            menu.Items.Add("Exit", null, (sender, args) => {
                if (busy) { Show(); Status("Wait for the current action to finish before exiting.", false); return; }
                exiting = true; app.Shutdown();
            });
            tray = new Forms.NotifyIcon { Text = "QuietKey", Icon = TrayIcon.Create(), ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (sender, args) => { if (args.Button == Forms.MouseButtons.Left) Toggle(); };
            RefreshTimer();
        }
        public void Balloon(string title, string message) { if (tray != null) tray.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info); }
        public void Dispose()
        {
            exiting = true;
            tick.Stop();
            if (tray != null) { tray.Visible = false; var icon = tray.Icon; tray.ContextMenuStrip.Dispose(); tray.Dispose(); icon.Dispose(); }
            window.Close();
        }
        public static void Smoke(string directory)
        {
            Directory.CreateDirectory(directory);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = new Settings();
            using (var ui = new UiController(app, settings, new SettingsStore(directory), null, true))
            {
                ui.window.WindowStartupLocation = WindowStartupLocation.Manual;
                ui.window.Left = -10000; ui.window.Top = -10000;
                ui.window.Show();
                ui.Capture(directory, "home");
                ui.Page("TimerPage"); ui.Capture(directory, "timer");
                ui.LoadProfile("Sleep"); ui.Page("ProfilePage"); ui.Capture(directory, "profile");
                ui.LoadProfile("Gaming"); ui.Page("ProfilePage"); ui.Capture(directory, "gaming-profile");
                settings.ActiveMode = "Sleep"; ui.Refresh(); ui.Page("HomePage"); ui.Capture(directory, "sleep-on");
                settings.ActiveMode = "Gaming"; ui.Refresh(); ui.Capture(directory, "gaming-on");
                settings.ActiveMode = "None";
                ui.LoadSettings(); ui.Page("SettingsPage"); ui.Capture(directory, "settings");
                settings.ShutdownUtc = DateTime.UtcNow.AddMinutes(30);
                settings.Restore = new RestorePoint { NightLightOn = false };
                ui.Refresh(); ui.Page("HomePage"); ui.Capture(directory, "countdown");
                settings.ShutdownUtc = null; ui.Refresh();
                ui.Status("✓ Brightness set to 25%\n✓ Night Light turned on\n✓ Fan preset set to Silent", false);
                ui.Capture(directory, "applied");
                using (var icon = TrayIcon.Create()) using (var output = File.Create(Path.Combine(directory, "QuietKey.ico"))) icon.Save(output);
                bool conflict = false;
                using (var first = new GlobalHotkey(() => { }))
                using (var second = new GlobalHotkey(() => { }))
                {
                    string available = Hotkeys.Keys.FirstOrDefault(key => first.Set(key));
                    if (available == null) throw new Exception("No test shortcut could be registered.");
                    conflict = !second.Set(available);
                    if (!conflict) throw new Exception("Hotkey conflict was not detected.");
                }
                File.WriteAllText(Path.Combine(directory, "result.txt"), "Nine UI views loaded and rendered; native hotkey registration, conflict detection, and cleanup passed. No shutdown commands or startup changes were made.");
            }
            app.Shutdown();
        }
        private void Capture(string directory, string name)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            var animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            animationTimer.Tick += (sender, args) => { animationTimer.Stop(); frame.Continue = false; };
            animationTimer.Start(); Dispatcher.PushFrame(frame);
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            var root = (FrameworkElement)window;
            var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5), (int)Math.Ceiling(root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(stream);
        }
    }
    internal static class TrayIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        public static System.Drawing.Icon Create()
        {
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(114, 229, 195))) graphics.FillEllipse(brush, 1, 1, 30, 30);
                using (var font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold))
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(16, 46, 40))) graphics.DrawString("Q", font, brush, 4, 2);
                IntPtr native = bitmap.GetHicon();
                try { using (var icon = System.Drawing.Icon.FromHandle(native)) return (System.Drawing.Icon)icon.Clone(); }
                finally { DestroyIcon(native); }
            }
        }
    }
}
