using System;
using System.Collections.Generic;
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
        string? torrent = null
    );

    public sealed record UpdatePlan(int ToDownloadCount, List<ManifestFile> ToDownload)
    {
        public bool NeedsAny => ToDownloadCount > 0;
    }

    public class Updater
    {
        private readonly string _installDir;
        private readonly HttpClient _http;

        // We store extraction stamps here so zip packages aren’t re-applied every run
        // when their contents are already in place.
        private string StampDir => Path.Combine(_installDir, ".arlauncher", "stamps");

        public Updater(string installDir)
        {
            _installDir = installDir;

            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });

            Directory.CreateDirectory(_installDir);
            Directory.CreateDirectory(StampDir);
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

        public async Task<UpdatePlan> ComputeUpdatePlanAsync(Manifest manifest, CancellationToken ct)
        {
            var need = new List<ManifestFile>();
            foreach (var f in manifest.files)
            {
                if (IsIgnored(f.path))
                    continue;

                if (IsZip(f.path))
                {
                    // For zip packages, we use a stamp keyed by the archive hash.
                    if (!HasStamp(f.sha256))
                        need.Add(f);

                    continue;
                }

                string full = Path.Combine(_installDir, f.path);
                if (!File.Exists(full) || !await HashMatchesAsync(full, f.sha256, ct))
                    need.Add(f);
            }
            return new UpdatePlan(need.Count, need);
        }

        public async IAsyncEnumerable<(ManifestFile file, string status)> UpdateAsync(
            Manifest manifest,
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
                    // Download archive to temp, verify, extract, stamp.
                    var url = BuildFileUrl(manifest.baseUrl, f.path);
                    string tmpZip = GetTempPathFor(f.path);
                    string tmpPart = tmpZip + ".part";

                    // Simple fresh download (could do ranges as needed)
                    if (File.Exists(tmpPart)) File.Delete(tmpPart);
                    using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var fs = new FileStream(tmpPart, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                        await resp.Content.CopyToAsync(fs, ct);
                    }

                    // Validate hash against manifest (the hash is for the ZIP file itself)
                    var zipHash = await HashFileAsync(tmpPart, ct);
                    if (!zipHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmpPart);
                        yield return (f, "hash_mismatch(zip)");
                        continue;
                    }

                    // Move to final temp .zip
                    if (File.Exists(tmpZip)) File.Delete(tmpZip);
                    File.Move(tmpPart, tmpZip);

                    // Extract into install root (preserve internal paths), overwrite files
                    try
                    {
                        ZipFile.ExtractToDirectory(tmpZip, _installDir, overwriteFiles: true);
                    }
                    catch
                    {
                        // Some older frameworks don’t have overwriteFiles parameter; fallback:
                        // Manual extract per entry with overwrite
                        using var za = ZipFile.OpenRead(tmpZip);
                        foreach (var entry in za.Entries)
                        {
                            string destPath = Path.Combine(_installDir, entry.FullName);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            if (string.IsNullOrEmpty(entry.Name))
                                continue; // folder entry

                            // Overwrite
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }
                    finally
                    {
                        try { File.Delete(tmpZip); } catch { /* ignore */ }
                    }

                    // Write stamp so this exact archive hash isn’t re-applied next runs
                    WriteStamp(f.sha256);

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
            // realmlist is managed locally by the launcher to avoid hash churn
            if (Regex.IsMatch(path, @"^Data/[a-z]{2}[A-Z]{2}/realmlist\.wtf$"))
                return true;

            // Never touch launcher files via the GAME updater
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

        private bool HasStamp(string sha256)
        {
            string path = Path.Combine(StampDir, sha256.ToUpper() + ".stamp");
            return File.Exists(path);
        }

        private void WriteStamp(string sha256)
        {
            try
            {
                Directory.CreateDirectory(StampDir);
                string path = Path.Combine(StampDir, sha256.ToUpper() + ".stamp");
                File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
            }
            catch { /* ignore */ }
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
    }
}
