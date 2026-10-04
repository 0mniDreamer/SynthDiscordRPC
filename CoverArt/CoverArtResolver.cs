using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MelonLoader;

namespace SynthDiscordRPC.CoverArt
{
  
    /// Resolves album/cover art URLs for custom songs via the synthriderz.com API,
    /// so Discord can display per-song art (Discord requires a public URL — it cannot
    /// show local images, and custom song covers live inside local .synth files).

    /// Probe-first notes:
    ///  - The API base (https://synthriderz.com/api/beatmaps) is confirmed in use by
    ///    community tooling. The exact search syntax and cover field name are best
    ///    guesses pending verification — so this class probes CANDIDATE field names
    ///    (same discipline as game-member candidates) and, with DebugLogging on,
    ///    dumps the real JSON keys of the first item it sees.
    ///  - Verify/adjust with:  curl "https://synthriderz.com/api/beatmaps?limit=1"

    /// All I/O is on background threads (HttpClient + Task), mirroring the
    /// download-on-background / marshal-nothing pattern: the completion callback only
    /// calls DiscordIpcClient.SetPresence, which is thread-safe by design.

    /// OST songs are not on synthriderz — lookups miss and the caller keeps the
    /// default logo. Misses are cached too, so each song costs at most one query.
 
    public sealed class CoverArtResolver
    {
        private const string ApiBase = "https://synthriderz.com";

        // nestjsx/crud style filter (synthriderz is a NestJS API). {title}/{artist}
        // are replaced with URL-escaped values. If verification shows a different
        // syntax, this is the one line to change.
        private const string SearchUrlTemplate =
            ApiBase + "/api/beatmaps?limit=1&s={\"$and\":[{\"title\":{\"$contL\":\"{title}\"}},{\"artist\":{\"$contL\":\"{artist}\"}}]}";

        // Candidate JSON property names for the cover image (probe in order).
        private static readonly string[] CoverFieldCandidates =
            { "cover_url", "cover", "image_url", "image", "artwork_url", "albumArt", "art_url" };

        // iTunes Search API — keyless, ~20 calls/min (our disk cache means one call
        // per song ever). OST tracks are licensed commercial music, so coverage is
        // near-total there; also catches customs of licensed songs synthriderz missed.
        private const string ITunesSearchTemplate =
            "https://itunes.apple.com/search?media=music&entity=song&limit=5&term={term}";

        private readonly bool _iTunesEnabled;

        private readonly HttpClient _http;
        private readonly string _cacheFile;
        private readonly Dictionary<string, string> _cache; // key -> url ("" = known miss)
        private readonly object _cacheLock = new object();
        private readonly bool _debug;
        private bool _keysDumped;

        public CoverArtResolver(bool debug, bool iTunesEnabled)
        {
            _debug = debug;
            _iTunesEnabled = iTunesEnabled;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SynthDiscordRPC/1.1 (+MelonLoader mod)");

            string dir = Path.Combine(GetUserDataDir(), "SynthDiscordRPC");
            try { Directory.CreateDirectory(dir); } catch { }
            _cacheFile = Path.Combine(dir, "covercache.json");
            _cache = LoadCache();
        }

        private static string GetUserDataDir()
        {
            try { return MelonLoader.Utils.MelonEnvironment.UserDataDirectory; }
            catch { return Path.Combine(Directory.GetCurrentDirectory(), "UserData"); }
        }

        /// Resolve a cover URL for (title, artist) on a background thread.
        /// Invokes onResolved exactly once with the URL, or null if unavailable.
        /// onResolved may run on a threadpool thread — callers must only do
        /// thread-safe work in it (DiscordIpcClient.SetPresence is fine).

        public void ResolveAsync(string title, string artist, Action<string> onResolved)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                onResolved(null);
                return;
            }

            string key = CacheKey(title, artist);
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out string cached))
                {
                    onResolved(string.IsNullOrEmpty(cached) ? null : cached);
                    return;
                }
            }

            Task.Run(async () =>
            {
                string url = null;
                try
                {
                    url = await QuerySynthriderz(title, artist).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_debug) MelonLogger.Warning($"[CoverArt] synthriderz lookup failed for '{title}': {ex.Message}");
                }

                // OST songs (and licensed customs synthriderz missed) — licensed
                // commercial music, so the iTunes catalog usually has the art.
                // Skipped when the game gave us no artist: matching on title alone
                // risks showing the wrong song's art, and no image beats wrong image.
                if (url == null && _iTunesEnabled && !string.IsNullOrWhiteSpace(artist))
                {
                    try
                    {
                        url = await QueryITunes(title, artist).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (_debug) MelonLogger.Warning($"[CoverArt] iTunes lookup failed for '{title}': {ex.Message}");
                    }
                }

                lock (_cacheLock)
                {
                    _cache[key] = url ?? "";
                    SaveCache();
                }

                if (_debug) MelonLogger.Msg($"[CoverArt] '{title}' -> {(url ?? "(no cover, using logo)")}");
                onResolved(url);
            });
        }

        private async Task<string> QuerySynthriderz(string title, string artist)
        {
            string requestUrl = SearchUrlTemplate
                .Replace("{title}", Uri.EscapeDataString(title.Trim()))
                .Replace("{artist}", Uri.EscapeDataString((artist ?? "").Trim()));

            string body = await _http.GetStringAsync(requestUrl).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            JsonElement item = FindFirstItem(doc.RootElement);
            if (item.ValueKind != JsonValueKind.Object) return null;

            if (_debug && !_keysDumped)
            {
                _keysDumped = true;
                var keys = new List<string>();
                foreach (var p in item.EnumerateObject()) keys.Add(p.Name);
                MelonLogger.Msg("[CoverArt] beatmap item keys: " + string.Join(", ", keys));
            }

            foreach (string field in CoverFieldCandidates)
            {
                if (item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    string url = MakeAbsolute(v.GetString());
                    if (url != null) return url;
                }
            }
            return null;
        }

      
        /// iTunes Search API lookup. Response shape: {"resultCount":N,"results":[...]}
        /// with "artworkUrl100" and "artistName" per result. We take the first result
        /// whose artist actually matches ours, and upscale the artwork URL
        /// (100x100 -> 512x512; Apple serves arbitrary sizes on the same path).

        private async Task<string> QueryITunes(string title, string artist)
        {
            string term = Uri.EscapeDataString($"{artist.Trim()} {title.Trim()}");
            string requestUrl = ITunesSearchTemplate.Replace("{term}", term);

            string body = await _http.GetStringAsync(requestUrl).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return null;

            string wantArtist = NormalizeForMatch(artist);

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("artistName", out var artistEl) ||
                    artistEl.ValueKind != JsonValueKind.String)
                    continue;

                // Guard against fuzzy-match serving the wrong song's art:
                // require artist-name overlap. No image beats wrong image.
                string gotArtist = NormalizeForMatch(artistEl.GetString());
                if (gotArtist.Length == 0 || wantArtist.Length == 0) continue;
                if (!gotArtist.Contains(wantArtist) && !wantArtist.Contains(gotArtist)) continue;

                if (item.TryGetProperty("artworkUrl100", out var artEl) &&
                    artEl.ValueKind == JsonValueKind.String)
                {
                    string url = artEl.GetString();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    url = url.Replace("100x100", "512x512");
                    if (url.Length <= 256) return url;
                }
            }
            return null;
        }

        ///Lowercase, alphanumerics only — tolerant artist comparison.
        private static string NormalizeForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// Handle both response shapes: a raw JSON array, or {"data":[...]}.
        private static JsonElement FindFirstItem(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength() > 0 ? root[0] : default;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
                return data.GetArrayLength() > 0 ? data[0] : default;

            return default;
        }

        private static string MakeAbsolute(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (url.StartsWith("//")) url = "https:" + url;
            else if (url.StartsWith("/")) url = ApiBase + url;
            else if (!url.StartsWith("http://") && !url.StartsWith("https://")) return null;

            // Discord's asset/url field tops out around 256 chars.
            return url.Length <= 256 ? url : null;
        }

        private static string CacheKey(string title, string artist)
            => (title ?? "").Trim().ToLowerInvariant() + "|" + (artist ?? "").Trim().ToLowerInvariant();

        // ------------------------------------------------------------------
        // Disk cache (same pattern as the emote-metadata cache)
        // ------------------------------------------------------------------

        private Dictionary<string, string> LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFile))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CoverArt] Cache load failed (starting fresh): {ex.Message}");
            }
            return new Dictionary<string, string>();
        }

        private void SaveCache()
        {
            try
            {
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex)
            {
                if (_debug) MelonLogger.Warning($"[CoverArt] Cache save failed: {ex.Message}");
            }
        }
    }
}
