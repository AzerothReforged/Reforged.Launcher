// ============================================
// File: src/AzerothReforged.Launcher/Updater.cs
// ============================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzerothReforged.Launcher
{
    public sealed record Manifest(string version, string minLauncherVersion, string clientBuild, string baseUrl, List<ManifestFile> files);
    public sealed record ManifestFile(string path, long size, string sha256, string? torrent = null);

    public class Updater
    {
        private readonly string _installDir;
        private readonly HttpClient _http;

        public Updater(string installDir)
        {
            _installDir = installDir;
            _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        }

        public async Task<Manifest> FetchManifestAsync(Uri manifestUrl, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(manifestUrl, ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var m = await JsonSerializer.DeserializeAsync<Manifest>(s, cancellationToken: ct);
            return m ?? throw new InvalidOperationException("Invalid manifest JSON");
        }

        public async IAsyncEnumerable<(ManifestFile file, string status)> UpdateAsync(Manifest manifest, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var f in manifest.files)
            {
                string full = Path.Combine(_installDir, f.path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);

                bool need = true;
                if (File.Exists(full))
                {
                    var localHash = await HashFileAsync(full, ct);
                    need = !localHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase);
                }

                if (!need) { yield return (f, "ok"); continue; }

                var url = new Uri(new Uri(manifest.baseUrl), f.path.Replace('\\', '/'));
                string tmp = full + ".part";

                long existing = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using (var fs = new FileStream(tmp, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await resp.Content.CopyToAsync(fs, ct);

                var tmpHash = await HashFileAsync(tmp, ct);
                if (!tmpHash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    yield return (f, "hash_mismatch");
                    continue;
                }

                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);
                yield return (f, "updated");
            }
        }

        static async Task<string> HashFileAsync(string path, CancellationToken ct)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash);
        }
    }
}
