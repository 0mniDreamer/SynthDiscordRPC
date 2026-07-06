using System;
using System.Linq;
using MelonLoader;
using SynthDiscordRPC.Discord;
using SynthDiscordRPC.Game;

[assembly: MelonInfo(typeof(SynthDiscordRPC.Main), "SynthDiscordRPC", "1.3.0", "OmniDreamer")]
[assembly: MelonGame(null, null)] // universal — loads on both Unity branches

namespace SynthDiscordRPC
{
    /// <summary>
    /// Discord Rich Presence for Synth Riders.
    /// Shows the current song (title / artist / difficulty / elapsed time) on your
    /// Discord profile while playing, and an idle presence while browsing menus.
    /// </summary>
    public class Main : MelonMod
    {
        /// <summary>
        /// Distribution default: create ONE Discord application (named "Synth Riders")
        /// on https://discord.com/developers/applications and paste its Application ID
        /// here before building a release. Application IDs are public — safe to embed.
        /// Users can still override via the ClientId config entry.
        /// </summary>
        private const string DefaultClientId = "1523758767240642680";

        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<string> _clientId;
        private MelonPreferences_Entry<bool> _showSongDetails;
        private MelonPreferences_Entry<string> _menuDetails;
        private MelonPreferences_Entry<string> _menuState;
        private MelonPreferences_Entry<string> _largeImageKey;
        private MelonPreferences_Entry<string> _largeImageText;
        private MelonPreferences_Entry<bool> _debugLogging;
        private MelonPreferences_Entry<bool> _albumArt;
        private MelonPreferences_Entry<bool> _iTunesArt;
        private MelonPreferences_Entry<bool> _timeRemaining;
        private MelonPreferences_Entry<bool> _liveScore;
        private MelonPreferences_Entry<int> _liveScoreInterval;

        private DiscordIpcClient _client;
        private CoverArt.CoverArtResolver _coverArt;
        private int _songSeq; // bumps on every song start/end; guards stale cover lookups

        // Current-song presence state. _songBase is the presence WITHOUT the score
        // suffix; every send composes base + suffix fresh, so suffixes never stack.
        private PresenceData _songBase;
        private bool _inSongPresence;
        private float _songDurationSec;      // 0 = unknown
        private long _lastSentScore = -1;
        private int _lastSentCombo;
        private DateTime _nextScoreCheckUtc;

        private bool _patched;
        private DateTime _nextPatchAttemptUtc = DateTime.MinValue;
        private int _patchAttempts;
        private const int MaxPatchAttempts = 30; // ~60s of retries

        public override void OnInitializeMelon()
        {
            _cfg = MelonPreferences.CreateCategory("SynthDiscordRPC");

            _clientId = _cfg.CreateEntry("ClientId", "",
                description: "Optional override of the built-in Discord Application ID. Leave empty to use the mod's default.");
            _showSongDetails = _cfg.CreateEntry("ShowSongDetails", true,
                description: "Show song title/artist/difficulty. If false, only shows 'Playing a song' (privacy mode).");
            _menuDetails = _cfg.CreateEntry("MenuDetailsText", "In the Menus",
                description: "Top presence line while not in a song.");
            _menuState = _cfg.CreateEntry("MenuStateText", "Browsing songs",
                description: "Second presence line while not in a song.");
            _largeImageKey = _cfg.CreateEntry("LargeImageKey", "logo",
                description: "Art asset key uploaded on your Discord application (or a full https:// image URL). Leave empty for no image.");
            _largeImageText = _cfg.CreateEntry("LargeImageText", "Synth Riders",
                description: "Hover text shown on the large image.");
            _debugLogging = _cfg.CreateEntry("DebugLogging", false,
                description: "Verbose logging incl. one-time Game_InfoProvider and synthriderz-API member dumps.");
            _albumArt = _cfg.CreateEntry("AlbumArt", true,
                description: "Look up custom-song cover art on synthriderz.com and show it as the presence image (logo moves to the corner). OST songs fall back to iTunes/logo.");
            _iTunesArt = _cfg.CreateEntry("ITunesFallback", true,
                description: "When synthriderz has no cover (OST songs), look the song up in the iTunes catalog. Only accepts results whose artist matches, to avoid wrong art.");
            _timeRemaining = _cfg.CreateEntry("TimeRemaining", true,
                description: "Show a live countdown of the song's remaining time (Discord renders it client-side).");
            _liveScore = _cfg.CreateEntry("LiveScore", true,
                description: "Append the live score and combo to the presence, updated periodically.");
            _liveScoreInterval = _cfg.CreateEntry("LiveScoreIntervalSeconds", 20,
                description: "Seconds between live score presence updates. Clamped to 15+ to respect Discord's rate limit.");

            GameHooks.Configure(_debugLogging.Value);

            // Config entry overrides the embedded default; embedded default covers
            // normal users so the mod works out of the box.
            string clientId = (_clientId.Value ?? "").Trim();
            if (clientId.Length == 0 && DefaultClientId.All(char.IsDigit))
                clientId = DefaultClientId;

            if (clientId.Length == 0)
            {
                LoggerInstance.Warning("No Discord Application ID available — Rich Presence is disabled.");
                LoggerInstance.Warning("Either build with DefaultClientId set, or put an Application ID from https://discord.com/developers/applications into [SynthDiscordRPC] ClientId.");
                return;
            }

            _client = new DiscordIpcClient(
                clientId,
                msg => LoggerInstance.Msg($"[Discord] {msg}"),
                msg => LoggerInstance.Warning($"[Discord] {msg}"),
                _debugLogging.Value);
            _client.Start();

            if (_albumArt.Value)
                _coverArt = new CoverArt.CoverArtResolver(_debugLogging.Value, _iTunesArt.Value);

            GameHooks.SongStarted += OnSongStarted;
            GameHooks.SongEnded += OnSongEnded;

            SetMenuPresence();
            LoggerInstance.Msg("SynthDiscordRPC initialised.");
        }

        public override void OnUpdate()
        {
            if (_client == null) return;

            // GameControlManager may not be resolvable until interop assemblies settle,
            // so patching retries from here rather than assuming init-time availability.
            if (!_patched && _patchAttempts < MaxPatchAttempts && DateTime.UtcNow >= _nextPatchAttemptUtc)
            {
                _patchAttempts++;
                _patched = GameHooks.TryPatch(HarmonyInstance);
                if (!_patched)
                {
                    _nextPatchAttemptUtc = DateTime.UtcNow.AddSeconds(2);
                    if (_patchAttempts == MaxPatchAttempts)
                        LoggerInstance.Warning("Could not resolve GameControlManager after repeated attempts; presence will stay on the menu state. Enable DebugLogging and check the type names.");
                }
            }

            GameHooks.Update();
            LiveStatsTick();
        }

        public override void OnApplicationQuit()
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
        }

        // ------------------------------------------------------------------

        private void OnSongStarted(SongInfo info)
        {
            if (_client == null) return;
            int seq = ++_songSeq;

            string details;
            string state;

            if (_showSongDetails.Value)
            {
                details = string.IsNullOrWhiteSpace(info.Name) ? "Playing a song" : info.Name;

                string author = string.IsNullOrWhiteSpace(info.Author) ? null : $"by {info.Author}";
                string diff = string.IsNullOrWhiteSpace(info.Difficulty) ? null : $"[{info.Difficulty}]";
                state = author != null && diff != null ? $"{author} {diff}"
                      : author ?? diff ?? "Riding the rails";
            }
            else
            {
                details = "Playing a song";
                state = "Riding the rails";
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _songDurationSec = 0f;
            if (_timeRemaining.Value && GameHooks.TryReadDurationSeconds(out float dur))
                _songDurationSec = dur;

            _songBase = new PresenceData
            {
                Details = details,
                State = state,
                StartTimestampMs = nowMs,
                EndTimestampMs = _songDurationSec > 0 ? nowMs + (long)(_songDurationSec * 1000) : (long?)null,
                LargeImageKey = _largeImageKey.Value,
                LargeText = _largeImageText.Value
            };

            _inSongPresence = true;
            _lastSentScore = -1;
            _lastSentCombo = 0;
            _nextScoreCheckUtc = DateTime.UtcNow.AddSeconds(5); // first check shortly after start

            // Send immediately with the logo; upgrade to cover art when the lookup lands.
            ComposeAndSendSongPresence();

            if (_coverArt != null && _showSongDetails.Value)
            {
                _coverArt.ResolveAsync(info.Name, info.Author, coverUrl =>
                {
                    // Runs on a threadpool thread — SetPresence is thread-safe, and we
                    // drop the result if the song changed while the lookup was in flight.
                    if (coverUrl == null || seq != _songSeq || _client == null) return;

                    var baseP = _songBase;
                    if (baseP == null) return;
                    baseP.LargeImageKey = coverUrl;
                    baseP.SmallImageKey = _largeImageKey.Value;
                    baseP.SmallText = _largeImageText.Value;
                    ComposeAndSendSongPresence();
                });
            }
        }

        private void OnSongEnded()
        {
            _songSeq++;
            _inSongPresence = false;
            _songBase = null;
            SetMenuPresence();
        }

        /// <summary>
        /// Periodic live-score tick, called from OnUpdate. Cheap: two DateTime compares
        /// per frame; actual reflection reads happen at most once per interval.
        /// </summary>
        private void LiveStatsTick()
        {
            if (!_inSongPresence || !_liveScore.Value || _client == null) return;

            var now = DateTime.UtcNow;
            if (now < _nextScoreCheckUtc) return;

            int interval = Math.Max(15, _liveScoreInterval.Value);
            _nextScoreCheckUtc = now.AddSeconds(interval);

            if (!GameHooks.TryReadScore(out long score, out int combo)) return;
            if (score == _lastSentScore && combo == _lastSentCombo) return;

            _lastSentScore = score;
            _lastSentCombo = combo;
            ComposeAndSendSongPresence();
        }

        /// <summary>Compose base presence + score suffix + resynced countdown, and send.</summary>
        private void ComposeAndSendSongPresence()
        {
            var baseP = _songBase;
            if (baseP == null || _client == null) return;

            var p = baseP.Clone();

            if (_liveScore.Value && _lastSentScore >= 0 && _showSongDetails.Value)
            {
                string suffix = " • " + _lastSentScore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " pts";
                if (_lastSentCombo > 1) suffix += $" • {_lastSentCombo}x";
                p.State = (p.State ?? "") + suffix; // Sanitize() in the client enforces the 128-char cap
            }

            // Resync the countdown from actual play position when readable — this
            // self-corrects drift from pauses (Discord's countdown never stops).
            if (_timeRemaining.Value && _songDurationSec > 0 &&
                GameHooks.TryReadPlayTimeSeconds(_songDurationSec, out float playSec))
            {
                float remaining = Math.Max(0f, _songDurationSec - playSec);
                p.EndTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(remaining * 1000);
            }

            _client.SetPresence(p);
        }

        private void SetMenuPresence()
        {
            _client?.SetPresence(new PresenceData
            {
                Details = _menuDetails.Value,
                State = _menuState.Value,
                LargeImageKey = _largeImageKey.Value,
                LargeText = _largeImageText.Value
            });
        }
    }
}
