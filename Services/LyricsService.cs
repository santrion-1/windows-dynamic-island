using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DynamicIsland.Services
{
    public class LyricLine
    {
        public double TimeSeconds { get; set; }
        public string Text { get; set; } = "";
    }

    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateFastIpv4HttpClient();
        private static readonly ConcurrentDictionary<string, List<LyricLine>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> InFlight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> LastFailedTime = new(StringComparer.OrdinalIgnoreCase);

        public static event Action? LyricsLoaded;

        private static HttpClient CreateFastIpv4HttpClient()
        {
            // Force IPv4 resolution so dual-stack domains like lrclib.net respond in ~400ms
            // instead of waiting 11+ seconds for dead IPv6 routes to time out on Windows
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(6),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    if (addresses.Length == 0)
                        addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DynamicIslandDesktop/1.0 (https://github.com)");
            return client;
        }

        public static (string CurrentLine, string NextLine) GetCurrentLyrics(string title, string artist, double positionSec, double durationSec)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                string.Equals(title, "No Media Playing", StringComparison.OrdinalIgnoreCase))
            {
                return ("Play your favorite track", "Synced lyrics will appear here");
            }

            string key = $"{title.Trim()}|{(artist ?? "").Trim()}";

            if (Cache.TryGetValue(key, out var lines) && lines.Count > 0)
            {
                return PickLines(lines, positionSec);
            }

            // Retry if not currently in flight and cooldown (6s) has passed since any failure
            bool canRetry = !LastFailedTime.TryGetValue(key, out DateTime failedAt) ||
                            (DateTime.UtcNow - failedAt).TotalSeconds >= 6;

            if (canRetry && InFlight.TryAdd(key, true))
            {
                _ = FetchLyricsAsync(key, title, artist ?? "", durationSec);
            }

            return ($"♪ {title}", string.IsNullOrWhiteSpace(artist) ? "Searching for lyrics..." : $"{artist} · Loading lyrics...");
        }

        private static (string CurrentLine, string NextLine) PickLines(List<LyricLine> lines, double posSec)
        {
            if (lines == null || lines.Count == 0)
                return ("♪ Instrumental", "");

            int activeIdx = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (posSec + 0.35 >= lines[i].TimeSeconds)
                    activeIdx = i;
                else
                    break;
            }

            string current = lines[activeIdx].Text;
            string next = (activeIdx + 1 < lines.Count) ? lines[activeIdx + 1].Text : "♪ · · ·";
            return (current, next);
        }

        private static async Task FetchLyricsAsync(string key, string title, string artist, double durationSec)
        {
            try
            {
                string cleanArtist = CleanArtistName(artist);
                string cleanTitle = CleanTrackTitle(title);

                // 1. Try fast exact /api/get lookup first
                var parsed = await TryGetExactAsync(title, cleanArtist, durationSec);
                if (parsed == null && cleanTitle != title)
                {
                    parsed = await TryGetExactAsync(cleanTitle, cleanArtist, durationSec);
                }

                // 2. Fallback to /api/search?q=...
                if (parsed == null)
                {
                    parsed = await TrySearchAsync($"{cleanTitle} {cleanArtist}".Trim(), durationSec);
                }
                if (parsed == null && !string.IsNullOrWhiteSpace(cleanTitle))
                {
                    parsed = await TrySearchAsync(cleanTitle, durationSec);
                }

                if (parsed != null && parsed.Count > 0)
                {
                    Cache[key] = parsed;
                    LastFailedTime.TryRemove(key, out _);
                    LyricsLoaded?.Invoke();
                    return;
                }

                // Track genuinely has no lyrics in LRCLIB
                Cache[key] = new List<LyricLine>
                {
                    new LyricLine { TimeSeconds = 0, Text = $"♪ {title}" },
                    new LyricLine { TimeSeconds = 4, Text = string.IsNullOrWhiteSpace(artist) ? "Lyrics not available for this track" : $"{artist} (Instrumental / No Lyrics)" }
                };
                LyricsLoaded?.Invoke();
            }
            catch
            {
                // Network error: allow automatic retry after 6 seconds
                LastFailedTime[key] = DateTime.UtcNow;
            }
            finally
            {
                InFlight.TryRemove(key, out _);
            }
        }

        private static async Task<List<LyricLine>?> TryGetExactAsync(string track, string artist, double durationSec)
        {
            try
            {
                string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(artist)}";
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return ExtractLyricsFromItem(doc.RootElement, durationSec);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<LyricLine>?> TrySearchAsync(string query, double durationSec)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            // Prefer syncedLyrics first
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("syncedLyrics", out var syncedProp) &&
                    syncedProp.ValueKind == JsonValueKind.String)
                {
                    string? lrc = syncedProp.GetString();
                    if (!string.IsNullOrWhiteSpace(lrc))
                    {
                        var lines = ParseLrc(lrc);
                        if (lines.Count > 0) return lines;
                    }
                }
            }

            // Otherwise fallback to plainLyrics on first match
            return ExtractLyricsFromItem(doc.RootElement[0], durationSec);
        }

        private static List<LyricLine>? ExtractLyricsFromItem(JsonElement item, double durationSec)
        {
            if (item.TryGetProperty("syncedLyrics", out var syncedProp) &&
                syncedProp.ValueKind == JsonValueKind.String)
            {
                string? lrc = syncedProp.GetString();
                if (!string.IsNullOrWhiteSpace(lrc))
                {
                    var parsed = ParseLrc(lrc);
                    if (parsed.Count > 0) return parsed;
                }
            }

            if (item.TryGetProperty("plainLyrics", out var plainProp) &&
                plainProp.ValueKind == JsonValueKind.String)
            {
                string? plain = plainProp.GetString();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    var plainLines = ParsePlain(plain, durationSec > 10 ? durationSec : 200);
                    if (plainLines.Count > 0) return plainLines;
                }
            }

            return null;
        }

        private static string CleanArtistName(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            // Strip " - Topic" from YouTube Music or take primary artist before comma/dash
            string s = Regex.Replace(artist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase).Trim();
            int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0) s = s.Substring(0, dashIdx).Trim();
            int commaIdx = s.IndexOf(',');
            if (commaIdx > 0) s = s.Substring(0, commaIdx).Trim();
            return s;
        }

        private static string CleanTrackTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string s = Regex.Replace(title, @"\s*[\(\[](feat\.?|ft\.?|official|lyric|video|audio|remaster|live).*?[\)\]]", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*-\s*(remastered|single|version|live).*$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static readonly Regex LrcRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)", RegexOptions.Compiled);

        private static List<LyricLine> ParseLrc(string lrc)
        {
            var result = new List<LyricLine>();
            var rawLines = lrc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in rawLines)
            {
                var m = LrcRegex.Match(raw);
                if (m.Success)
                {
                    int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double frac = 0;
                    if (m.Groups[3].Success)
                    {
                        string fStr = m.Groups[3].Value;
                        frac = int.Parse(fStr, CultureInfo.InvariantCulture) / Math.Pow(10, fStr.Length);
                    }
                    string text = m.Groups[4].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(new LyricLine
                        {
                            TimeSeconds = min * 60 + sec + frac,
                            Text = text
                        });
                    }
                }
            }
            result.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            return result;
        }

        private static List<LyricLine> ParsePlain(string plain, double totalSec)
        {
            var result = new List<LyricLine>();
            var lines = plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return result;
            double step = Math.Max(3.5, totalSec / (lines.Length + 1));
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (!string.IsNullOrEmpty(t))
                {
                    result.Add(new LyricLine
                    {
                        TimeSeconds = i * step,
                        Text = t
                    });
                }
            }
            return result;
        }
    }
}
