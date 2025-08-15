using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AzerothReforged.Launcher
{
    public partial class MainWindow : Window
    {
        // ===== COMPILED-IN CONFIG ===== 
        private const string ManifestUrl            = "https://cdn.azerothreforged.xyz/latest.json";
        private const string NewsUrl                = "https://www.azerothreforged.xyz/feed/";
        private const string RealmlistHost          = "login.azerothreforged.xyz";
        private const string ConfigExeName          = "patchmenu.exe";  // resides under InstallDir

        // Launcher self-update channel
        private const string LauncherVersion        = "1.0.0"; // bump when you ship a new launcher
        private const string LauncherManifestUrl    = "https://cdn.azerothreforged.xyz/launcher.json";
        // =================================

        private enum PrimaryMode { Install, Update, Play }
        private PrimaryMode _mode = PrimaryMode.Install;

        private string _installDir = @"C:\Games\Azeroth Reforged"; // overridden by launcher.cfg
        private readonly Updater _updater;

        public MainWindow()
        {
            InitializeComponent();
            LoadOrCreateCfg();
            _updater = new Updater(_installDir);
            Directory.CreateDirectory(_installDir);

            _ = SelfUpdateCheckAsync();   // launcher updates (zip or exe)
            _ = InitializeStateAsync();   // game files
            _ = LoadNewsAsync();
        }

        private async Task SelfUpdateCheckAsync()
        {
            try
            {
                Log($"Checking launcher updates from: {LauncherManifestUrl}");
                var man = await LauncherSelfUpdater.FetchAsync(LauncherManifestUrl, CancellationToken.None);
                if (man == null) { Log("No launcher manifest."); return; }

                if (LauncherSelfUpdater.CompareVersion(man.version, LauncherVersion) > 0)
                {
                    Log($"Launcher update found: {man.version}");
                    var path = await LauncherSelfUpdater.DownloadAsync(man.url, CancellationToken.None);
                    if (!await LauncherSelfUpdater.VerifySha256Async(path, man.sha256, CancellationToken.None))
                    {
                        Log("Launcher update hash mismatch; aborting.");
                        try { File.Delete(path); } catch { }
                        return;
                    }

                    string currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
                    string launcherDir = AppContext.BaseDirectory.TrimEnd('\\'); // {app}\Launcher\

                    if (man.url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Updating launcher (package)...");
                        LauncherSelfUpdater.DeployZipAndRestart(path, launcherDir, currentExe);
                    }
                    else
                    {
                        Log("Updating launcher (exe)...");
                        LauncherSelfUpdater.SwapExeAndRestart(currentExe, path);
                    }
                }
                else
                {
                    Log("Launcher is up to date.");
                }
            }
            catch (Exception ex)
            {
                Log("Launcher update check failed: " + ex.Message);
            }
        }

        private void LoadOrCreateCfg()
        {
            string cfgPath = Path.Combine(AppContext.BaseDirectory, "launcher.cfg");
            if (File.Exists(cfgPath))
            {
                try
                {
                    var line = File.ReadAllLines(cfgPath).FirstOrDefault(l => l.TrimStart().StartsWith("InstallDir", StringComparison.OrdinalIgnoreCase));
                    if (line != null)
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var dir = parts[1].Trim().Trim('"');
                            if (!string.IsNullOrWhiteSpace(dir))
                                _installDir = dir;
                        }
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                try { File.WriteAllText(cfgPath, $"InstallDir={_installDir}\r\n"); } catch { }
            }
        }

        private async Task InitializeStateAsync()
        {
            try
            {
                StatusText.Text = "Checking local files...";
                bool hasWow = File.Exists(Path.Combine(_installDir, "Wow.exe"));

                var manifest = await _updater.FetchManifestAsync(new Uri(ManifestUrl), CancellationToken.None);
                var plan = await _updater.ComputeUpdatePlanAsync(manifest, CancellationToken.None, installMode: !hasWow);

                if (!hasWow) SetMode(PrimaryMode.Install);
                else SetMode(plan.NeedsAny ? PrimaryMode.Update : PrimaryMode.Play);

                StatusText.Text = plan.NeedsAny
                    ? $"Update available ({plan.ToDownloadCount}/{manifest.files.Count} items)"
                    : "Up to date.";
            }
            catch (Exception ex)
            {
                Log("Failed to check manifest: " + ex.Message);
                if (File.Exists(Path.Combine(_installDir, "Wow.exe")))
                    SetMode(PrimaryMode.Play);
                else
                    SetMode(PrimaryMode.Install);
            }
        }

        private void SetMode(PrimaryMode mode)
        {
            _mode = mode;
            BtnPrimary.Content = mode switch
            {
                PrimaryMode.Install => "INSTALL",
                PrimaryMode.Update  => "UPDATE",
                _                   => "PLAY"
            };
        }

        private async void BtnPrimary_Click(object sender, RoutedEventArgs e)
        {
            switch (_mode)
            {
                case PrimaryMode.Play:
                    LaunchGame();
                    break;
                case PrimaryMode.Install:
                case PrimaryMode.Update:
                    await RunUpdateAsync();
                    break;
            }
        }

        private async Task RunUpdateAsync()
        {
            SetButtonsEnabled(false);
            Log("Fetching manifest...");
            try
            {
                bool installMode = _mode == PrimaryMode.Install;
                var manifest = await _updater.FetchManifestAsync(new Uri(ManifestUrl), CancellationToken.None);

                int total = manifest.files.Count;
                int done  = 0;
                MainProgress.Value = 0;

                await foreach (var (file, status) in _updater.UpdateAsync(manifest, installMode, CancellationToken.None))
                {
                    done++;
                    var pct = (int)Math.Round((double)done / Math.Max(1, total) * 100);
                    MainProgress.Value = pct;
                    Log($"{status.ToUpper(),-18} {file.path} ({file.size} bytes)");
                }

                EnsureRealmlist(_installDir, RealmlistHost);
                Log("Realmlist ensured.");

                // Re-evaluate: if now has Wow.exe and nothing pending, flip to PLAY
                bool hasWow = File.Exists(Path.Combine(_installDir, "Wow.exe"));
                var plan = await _updater.ComputeUpdatePlanAsync(manifest, CancellationToken.None, installMode: !hasWow);
                SetMode(!hasWow ? PrimaryMode.Install : (plan.NeedsAny ? PrimaryMode.Update : PrimaryMode.Play));

                StatusText.Text = plan.NeedsAny ? "Update pending." : "Update complete.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Update failed.";
                Log("ERROR: " + ex.Message);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnDonate_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.azerothreforged.xyz/shop/",
                UseShellExecute = true
            });
        }

        private void BtnConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = Path.Combine(_installDir, ConfigExeName);
                if (!File.Exists(exe))
                {
                    Log($"Options app not found: {exe}");
                    MessageBox.Show($"Options app not found:\n{exe}", "Options", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = _installDir
                };
                System.Diagnostics.Process.Start(psi);
                Log("Opened options.");
            }
            catch (Exception ex)
            {
                Log("Failed to open options: " + ex.Message);
            }
        }

        private void LaunchGame()
        {
            var wow = Path.Combine(_installDir, "Wow.exe");
            if (!File.Exists(wow))
            {
                Log("Wow.exe not found in install directory.");
                MessageBox.Show("Wow.exe not found in the install directory.", "Play", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LaunchHelper.LaunchGame(wow);
        }

        private async Task LoadNewsAsync()
        {
            try
            {
                var items = await News.FetchNewsAsync(new Uri(NewsUrl), CancellationToken.None);
                NewsPanel.Children.Clear();

                foreach (var n in items.Take(12))
                {
                    var title = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
                    if (!string.IsNullOrWhiteSpace(n.url))
                    {
                        var link = new Hyperlink(new Run(n.title)) { NavigateUri = new Uri(n.url) };
                        link.RequestNavigate += (_, args) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = args.Uri.AbsoluteUri, UseShellExecute = true });
                        title.Inlines.Add(link);
                    }
                    else title.Text = n.title;

                    var meta = new TextBlock { Text = $"{n.published:yyyy-MM-dd}", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 2) };
                    var sum  = new TextBlock { Text = n.summary, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 0, 0, 12) };

                    NewsPanel.Children.Add(title);
                    NewsPanel.Children.Add(meta);
                    NewsPanel.Children.Add(sum);
                }
            }
            catch (Exception ex)
            {
                NewsPanel.Children.Clear();
                NewsPanel.Children.Add(new TextBlock { Text = "Failed to load news.", Foreground = Brushes.OrangeRed });
                NewsPanel.Children.Add(new TextBlock { Text = ex.Message, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            }
        }

        private async void RefreshNews_Click(object sender, RoutedEventArgs e) => await LoadNewsAsync();

        private void Log(string s)
        {
            LogList.Items.Add(s);
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            StatusText.Text = s;
        }

        private void SetButtonsEnabled(bool enabled)
        {
            BtnDonate.IsEnabled = enabled;
            BtnConfig.IsEnabled = enabled;
            BtnPrimary.IsEnabled = enabled;
        }

        private static void EnsureRealmlist(string installDir, string host)
        {
            try
            {
                var data = Path.Combine(installDir, "Data");
                Directory.CreateDirectory(data);
                var locale = Directory.GetDirectories(data)
                                      .Select(Path.GetFileName)
                                      .FirstOrDefault(d => d != null && (d.Length == 4 || d.Length == 5)) ?? "enUS";
                var path = Path.Combine(data, locale, "realmlist.wtf");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"set realmlist {host}\r\n", Encoding.ASCII);
            }
            catch { /* ignore */ }
        }
    }
}
