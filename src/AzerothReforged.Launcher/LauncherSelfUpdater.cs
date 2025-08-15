using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzerothReforged.Launcher
{
    public sealed record LauncherManifest(string version, string url, string sha256);

    public static class LauncherSelfUpdater
    {
        public static int CompareVersion(string a, string b)
        {
            Version va = Parse(a), vb = Parse(b);
            return va.CompareTo(vb);
            static Version Parse(string s) => Version.TryParse(s, out var v) ? v : new Version(0, 0, 0, 0);
        }

        public static async Task<LauncherManifest?> FetchAsync(string manifestUrl, CancellationToken ct)
        {
            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
            using var resp = await http.GetAsync(manifestUrl, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, cancellationToken: ct);
        }

        public static async Task<string> DownloadAsync(string url, CancellationToken ct)
        {
            string fileName = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "ARLauncherUpdate.zip" : "ARLauncher.new.exe";
            string tmp = Path.Combine(Path.GetTempPath(), fileName);

            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
            using var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs, ct);

            return tmp;
        }

        public static async Task<bool> VerifySha256Async(string path, string expectedHex, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = await sha.ComputeHashAsync(fs, ct);
            var hex = Convert.ToHexString(bytes);
            return hex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Update when manifest url points to an EXE (single file).
        /// </summary>
        public static void SwapExeAndRestart(string currentExe, string newExePath)
        {
            // wait a beat -> move -> restart
            string cmd = $@"/C ping 127.0.0.1 -n 2 >nul & move /Y ""{newExePath}"" ""{currentExe}"" & start """" ""{currentExe}""";
            var psi = new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Environment.Exit(0);
        }

        /// <summary>
        /// Update when manifest url points to a ZIP package. Extracts to temp and mirrors to target dir.
        /// Skips launcher.cfg so user settings persist.
        /// </summary>
        public static void DeployZipAndRestart(string zipPath, string targetDir, string currentExe)
        {
            string extractDir = Path.Combine(Path.GetTempPath(), "ARLauncherUpdate");
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
            Directory.CreateDirectory(extractDir);

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // build robust robocopy command
            // /E copy subdirs, /R:3 retry 3, /W:1 wait 1s, /NFL/NDL/NP quiet logs, skip launcher.cfg
            string robocopyArgs = $@"/C ping 127.0.0.1 -n 2 >nul & robocopy ""{extractDir}"" ""{targetDir}"" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP /XF launcher.cfg & start """" ""{currentExe}""";
            var psi = new ProcessStartInfo("cmd.exe", robocopyArgs)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Environment.Exit(0);
        }
    }
}
