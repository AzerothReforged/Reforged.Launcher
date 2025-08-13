using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;

namespace AzerothReforged.Launcher
{
    public partial class MainWindow : Window
    {
        // ====== EDIT THESE IF NEEDED ======
        private const string InstallDir = @"C:\Games\Azeroth Reforged";
        private const string ManifestUrl = "https://cdn.azerothreforged.xyz/manifest/latest.json";
        private const string NewsUrl = "https://azerothreforged.xyz/rss";
        private const string RealmlistHost = "login.azerothreforged.xyz"; // change if different
        private const string ConfigExeRelative = @"Tools\ConfigApp.exe";   // "" to disable Options button
        // ===================================

        private readonly Updater _updater;

        public MainWindow()
        {
            InitializeComponent();
            _updater = new Updater(InstallDir);
            Directory.CreateDirectory(InstallDir);
            _ = LoadNewsAsync();
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            Log("Fetching manifest...");
            try
            {
                using var cts = new CancellationTokenSource();
                var manifest = await _updater.FetchManifestAsync(new Uri(ManifestUrl), cts.Token);

                int total = manifest.files.Count;
                int done = 0;
                MainProgress.Value = 0;

                await foreach (var (file, status) in _updater.UpdateAsync(manifest, cts.Token))
                {
                    done++;
                    var pct = (int)Math.Round((double)done / Math.Max(1, total) * 100);
                    MainProgress.Value = pct;
                    Log($"{status.ToUpper(),-14} {file.path} ({file.size} bytes)");
                }

                EnsureRealmlist(InstallDir, RealmlistHost);
                Log("Realmlist ensured.");
                StatusText.Text = $"Update complete.";
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

        private void BtnConfig_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ConfigExeRelative)) return;
            var path = Path.Combine(InstallDir, ConfigExeRelative);
            LaunchHelper.LaunchConfig(path);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            var wow = Path.Combine(InstallDir, "Wow.exe");
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
                    var title = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
                    if (!string.IsNullOrWhiteSpace(n.url))
                    {
                        var link = new Hyperlink(new Run(n.title)) { NavigateUri = new Uri(n.url) };
                        link.RequestNavigate += (_, args) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = args.Uri.AbsoluteUri, UseShellExecute = true });
                        title.Inlines.Add(link);
                    }
                    else title.Text = n.title;

                    var meta = new TextBlock { Text = $"{n.published:yyyy-MM-dd}", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 2) };
                    var sum = new TextBlock { Text = n.summary, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gainsboro, Margin = new Thickness(0, 0, 0, 12) };

                    NewsPanel.Children.Add(title);
                    NewsPanel.Children.Add(meta);
                    NewsPanel.Children.Add(sum);
                }
            }
            catch (Exception ex)
            {
                NewsPanel.Children.Clear();
                NewsPanel.Children.Add(new TextBlock { Text = "Failed to load news.", Foreground = System.Windows.Media.Brushes.OrangeRed });
                NewsPanel.Children.Add(new TextBlock { Text = ex.Message, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap });
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
            BtnUpdate.IsEnabled = enabled;
            BtnConfig.IsEnabled = string.IsNullOrWhiteSpace(ConfigExeRelative) ? false : enabled;
            BtnPlay.IsEnabled = enabled;
        }

        private static void EnsureRealmlist(string installDir, string host)
        {
            try
            {
                var data = Path.Combine(installDir, "Data");
                Directory.CreateDirectory(data);
                var locale = Directory.GetDirectories(data).Select(Path.GetFileName).FirstOrDefault(d => d != null && (d.Length == 4 || d.Length == 5)) ?? "enUS";
                var path = Path.Combine(data, locale, "realmlist.wtf");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"set realmlist {host}\r\n", Encoding.ASCII);
            }
            catch { /* ignore */ }
        }
    }
}
