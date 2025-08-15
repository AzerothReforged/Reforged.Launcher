using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AzerothReforged.Launcher
{
    // Manifest models
    public sealed record Manifest(
        string version,
        string minLauncherVersion,
        string clientBuild,
        string baseUrl,
        List<ManifestFile> files
    );

    public sealed record ManifestFile(
        string path,
        long size,
        string sha256,
        string? torrent = null  // optional magnet or .torrent URL (used for ZIPs)
    );

    public sealed record UpdatePlan(int ToDownloadCount, List<ManifestFile> ToDownload)
    {
        public bool NeedsAny => ToDownloadCount > 0;
    }

    public class Updater
    {
        private readonly string _installDir;
        private readonly HttpClient _http;

        // Persistence for applied ZIP packages
        private string MetaDir        => Path.Combine(_installDir, ".arlauncher");
        private string AppliedLogPath => Path.Combine(MetaDir, "applied.log");

        // Legacy stamp compatibility (older versions created per-sha stamp files)
        private string StampDir       => Path.Combine(MetaDir, "stamps");

        private readonly HashSet<string> _appliedZips = new(StringComparer.OrdinalIgnoreCase);

        public Updater(string installDir)
        {
            _installDir = installDir;

            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });

            Directory.CreateDirectory(_installDir);
            Directory.CreateDirectory(MetaDir);
            Directory.CreateDirectory(StampDir);

            LoadAppliedLog();
            MigrateLegacyStamps();   // <- make sure this method exists (defined below)
        }

        // ---- public API -----------------------------------------------------

        public async Task<Manifest> FetchManifestAsync(Uri manifestUrl, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(manifestUrl, ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var m = await JsonSerializer.DeserializeAsync<Manifest>(s, cancellationToken: ct);
            return m ?? throw new InvalidOperationException("Invalid manifest JSON");
        }

        /// <summary>
        /// installMode=true => ZIP packages are considered and extracted.
        /// installMode=false => ZIP packages are ignored (only loose files update).
        /// </summary>
        public async Task<UpdatePlan> ComputeUpdatePlanAsync(Manifest manifest, CancellationToken ct, bool installMode = false)
        {
            var need = new List<ManifestFile>();
            foreach (var f in manifest.files)
            {
                if (IsIgnored(f.path))
                    continue;

                if (IsZip(f.path))
                {
                    if (!installMode)
                        continue; // only install-time ZIPs

                    if (!HasApplied(f.sha256))
                        need.Add(f);

                    continue;
                }

                string full = Path.Combine(_installDir, f.path);
                if (!File.Exists(full) || !await HashMatchesAsync(full, f.sha256, ct))
                    need.Add(f);
            }
            return new UpdatePlan(need.Count, need);
        }

        /// <summary>
        /// installMode=true => ZIPs will download/extract; otherwise only loose files are processed.
        /// </summary>
        public async IAsyncEnumerable<(ManifestFile file, string status)> UpdateAsync(
            Manifest manifest,
            bool installMode,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var f in manifest.files)
            {
                if (IsIgnored(f.path))
                {
                    yield return (f, "skipped(ignored)");
                    continue;
                }

                if (IsZip(f.path))
                {
                    if (!installMode)
                    {
                        yield return (f, "skipped(zip-noninstall)");
                        continue;
                    }

                    if (HasApplied(f.sha256))
                    {
                        yield return (f, "ok(zip-applied)");
                        continue;
                    }

                    var url = BuildFileUrl(manifest.baseUrl, f.path);
                    string tmpZip  = GetTempPathFor(f.path);
                    string tmpPart = tmpZip + ".part";

                    // Prefer torrent if provided and aria2c is available; else HTTP
                    bool usedTorrent = false;
                    if (!string.IsNullOrWhiteSpace(f.torrent) && TryGetAria2(out string aria))
                    {
                        if (File.Exists(tmpZip)) File.Delete(tmpZip);
                        usedTorrent = await DownloadViaAria2Async(aria, f.torrent!, tmpZip, ct);
                        if (!usedTorrent) { if (File.Exists(tmpZip)) File.Delete(tmpZip); }
                    }

                    if (!usedTorrent)
                    {
                        if (File.Exists(tmpPart)) File.Delete(tmpPart);
                        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            resp.EnsureSuccessStatusCode();
                            await using var fs = new FileStream(tmpPart, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                            await resp.Content.CopyToAsync(fs, ct);
                        }

                        var zipHash = await HashFileAsync(tmpPart, ct);
                        if (!zipHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(tmpPart);
                            yield return (f, "hash_mismatch(zip)");
                            continue;
                        }

                        if (File.Exists(tmpZip)) File.Delete(tmpZip);
                        File.Move(tmpPart, tmpZip);
                    }
                    else
                    {
                        var zipHash = await HashFileAsync(tmpZip, ct);
                        if (!zipHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(tmpZip); } catch { }
                            yield return (f, "hash_mismatch(zip)");
                            continue;
                        }
                    }

                    // Extract into install root (preserve internal paths), overwrite files
                    try
                    {
#if NET6_0_OR_GREATER
                        ZipFile.ExtractToDirectory(tmpZip, _installDir, overwriteFiles: true);
#else
                        using var za = ZipFile.OpenRead(tmpZip);
                        foreach (var entry in za.Entries)
                        {
                            string destPath = Path.Combine(_installDir, entry.FullName);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            if (string.IsNullOrEmpty(entry.Name))
                                continue; // folder entry
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
#endif
                    }
                    finally
                    {
                        try { File.Delete(tmpZip); } catch { /* ignore */ }
                    }

                    RecordApplied(f.sha256);
                    WriteLegacyStamp(f.sha256); // optional back-compat

                    yield return (f, "extracted");
                    continue;
                }

                // Regular (non-zip) file handling with resume & hash validation
                string fullPath = Path.Combine(_installDir, f.path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                bool need = !File.Exists(fullPath) || !await HashMatchesAsync(fullPath, f.sha256, ct);
                if (!need)
                {
                    yield return (f, "ok");
                    continue;
                }

                var fileUrl = BuildFileUrl(manifest.baseUrl, f.path);
                string tmp = fullPath + ".part";

                long existing = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
                using (var req = new HttpRequestMessage(HttpMethod.Get, fileUrl))
                {
                    if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using (var fs = new FileStream(tmp, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, true))
                        await resp.Content.CopyToAsync(fs, ct);
                }

                var tmpHash = await HashFileAsync(tmp, ct);
                if (!tmpHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    yield return (f, "hash_mismatch");
                    continue;
                }

                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(tmp, fullPath);

                yield return (f, "updated");
            }
        }

        // ---- helpers --------------------------------------------------------

        private static bool IsZip(string path)
            => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        private static bool IsIgnored(string path)
        {
            path = path.Replace('\\', '/');
            if (Regex.IsMatch(path, @"^Data/[a-z]{2}[A-Z]{2}/realmlist\.wtf$"))
                return true;
            if (path.StartsWith("Launcher/", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private Uri BuildFileUrl(string baseUrl, string relPath)
        {
            var baseUri = new Uri(FixBase(baseUrl));
            return new Uri(baseUri, relPath.Replace('\\', '/'));
            static string FixBase(string b) => b.EndsWith("/") ? b : (b + "/");
        }

        private string GetTempPathFor(string rel)
        {
            string safe = rel.Replace('\\', '_').Replace('/', '_');
            return Path.Combine(Path.GetTempPath(), "AR_" + safe);
        }

        private static async Task<bool> HashMatchesAsync(string path, string expectedSha, CancellationToken ct)
        {
            var h = await HashFileAsync(path, ct);
            return h.Equals(expectedSha, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> HashFileAsync(string path, CancellationToken ct)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash);
        }

        // ---------- applied.log management ----------

        private void LoadAppliedLog()
        {
            _appliedZips.Clear();
            try
            {
                if (File.Exists(AppliedLogPath))
                {
                    foreach (var line in File.ReadAllLines(AppliedLogPath))
                    {
                        var s = line.Trim().ToUpperInvariant();
                        if (s.Length >= 40) _appliedZips.Add(s);
                    }
                }
            }
            catch { /* ignore */ }
        }

        private void RecordApplied(string sha256)
        {
            try
            {
                Directory.CreateDirectory(MetaDir);
                var s = sha256.ToUpperInvariant();
                if (_appliedZips.Add(s))
                    File.AppendAllText(AppliedLogPath, s + Environment.NewLine);
            }
            catch { /* ignore */ }
        }

        private bool HasApplied(string sha256)
        {
            var s = sha256.ToUpperInvariant();
            if (_appliedZips.Contains(s))
                return true;

            if (HasLegacyStamp(s))
            {
                RecordApplied(s);
                return true;
            }
            return false;
        }

        // ---------- legacy stamp compat ----------

        private bool HasLegacyStamp(string sha256Upper)
        {
            string path = Path.Combine(StampDir, sha256Upper + ".stamp");
            return File.Exists(path);
        }

        private void WriteLegacyStamp(string sha256)
        {
            try
            {
                Directory.CreateDirectory(StampDir);
                string path = Path.Combine(StampDir, sha256.ToUpperInvariant() + ".stamp");
                File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
            }
            catch { /* ignore */ }
        }

        private void MigrateLegacyStamps()
        {
            try
            {
                if (!Directory.Exists(StampDir)) return;
                foreach (var file in Directory.GetFiles(StampDir, "*.stamp"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var s = name.Trim().ToUpperInvariant();
                        if (s.Length >= 40 && !_appliedZips.Contains(s))
                            RecordApplied(s);
                    }
                }
            }
            catch { /* ignore */ }
        }

        // ---------- torrent via aria2c (optional) ----------

        private static bool TryGetAria2(out string aria2Path)
        {
            string baseDir = AppContext.BaseDirectory;
            string p = Path.Combine(baseDir, "aria2c.exe");
            aria2Path = p;
            return File.Exists(p);
        }

        private static async Task<bool> DownloadViaAria2Async(string aria2, string torrentOrMagnet, string outPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = aria2,
                Arguments = $"--check-certificate=false --allow-overwrite=true --dir=\"{Path.GetDirectoryName(outPath)}\" --out=\"{Path.GetFileName(outPath)}\" \"{torrentOrMagnet}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            while (!proc.HasExited)
                await Task.Delay(200, ct);

            return proc.ExitCode == 0 && File.Exists(outPath);
        }
    }
}
