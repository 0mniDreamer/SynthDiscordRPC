using System;
using System.Linq;
using MelonLoader;
using SynthDiscordRPC.Discord;
using SynthDiscordRPC.Game;

[assembly: MelonInfo(typeof(SynthDiscordRPC.Main), "SynthDiscordRPC", "1.1.0", "OmniDreamer")]
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

        private DiscordIpcClient _client;
        private CoverArt.CoverArtResolver _coverArt;
        private int _songSeq; // bumps on every song start/end; guards stale cover lookups

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
                description: "Look up custom-song cover art on synthriderz.com and show it as the presence image (logo moves to the corner). OST songs fall back to the logo.");

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
                _coverArt = new CoverArt.CoverArtResolver(_debugLogging.Value);

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

            var presence = new PresenceData
            {
                Details = details,
                State = state,
                StartTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LargeImageKey = _largeImageKey.Value,
                LargeText = _largeImageText.Value
            };

            // Send immediately with the logo; upgrade to cover art when the lookup lands.
            _client.SetPresence(presence);

            if (_coverArt != null && _showSongDetails.Value)
            {
                _coverArt.ResolveAsync(info.Name, info.Author, coverUrl =>
                {
                    // Runs on a threadpool thread — SetPresence is thread-safe, and we
                    // drop the result if the song changed while the lookup was in flight.
                    if (coverUrl == null || seq != _songSeq || _client == null) return;

                    var withArt = presence.Clone();
                    withArt.LargeImageKey = coverUrl;
                    withArt.SmallImageKey = _largeImageKey.Value;
                    withArt.SmallText = _largeImageText.Value;
                    _client.SetPresence(withArt);
                });
            }
        }

        private void OnSongEnded()
        {
            _songSeq++;
            SetMenuPresence();
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
