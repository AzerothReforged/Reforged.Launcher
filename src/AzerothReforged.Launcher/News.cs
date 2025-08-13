// ============================================
// File: src/AzerothReforged.Launcher/News.cs
// ============================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeHollow.FeedReader;

namespace AzerothReforged.Launcher
{
    public record NewsItem(string title, string summary, DateTime published, string? url);

    public static class News
    {
        public static async Task<List<NewsItem>> FetchNewsAsync(Uri url, CancellationToken ct)
        {
            var feed = await FeedReader.ReadAsync(url.ToString());
            var items = feed.Items.Select(i => new NewsItem(
                title: i.Title ?? "(untitled)",
                summary: Truncate(StripHtml(i.Description ?? i.Content ?? string.Empty), 300),
                published: CoerceDate(i),
                url: i.Link
            ))
            .OrderByDescending(x => x.published)
            .Take(15)
            .ToList();

            return items;
        }

        private static DateTime CoerceDate(FeedItem i)
        {
            if (i.PublishingDate.HasValue) return i.PublishingDate.Value;
            if (!string.IsNullOrWhiteSpace(i.PublishingDateString) &&
                DateTime.TryParse(i.PublishingDateString, out var dt)) return dt;
            return DateTime.UtcNow;
        }

        private static string StripHtml(string html) => Regex.Replace(html, "<.*?>", string.Empty).Trim();
        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
