using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SynthDiscordRPC.Game
{
    public sealed class SongInfo
    {
        public string Name = "";
        public string Author = "";
        public string Difficulty = "";
    }

  
    /// Hooks the game entirely via reflection (per-assembly exact type lookup + candidate
    /// member names) so no game/Unity assemblies are referenced — the same DLL runs
    /// on the Unity 2021.3.45f2 branch and the Unity 6000.3.13 branch.

    /// Patch points (verified in prior SynthRidersWebsocketMod work):
    ///   GameControlManager.Awake                 -> song scene loading (postfix)
    ///   GameControlManager.OnLevelFinishedLoading -> song metadata is populated by
    ///       SetSongStatusData() around here; the reliable point to read it (postfix)
    ///   GameControlManager.ReturnToMenu          -> song end / back to menu (prefix)

    /// Metadata source: Game_InfoProvider singleton, probed with the same candidate
    /// member-name sets the websocket mod uses across both branches.

    public static class GameHooks
    {
        public static event Action<SongInfo> SongStarted;
        public static event Action SongEnded;

        // IL2CPP-prefixed names probed first, then fallbacks — never hardcode one form.
        private static readonly string[] GcmTypeNames =
        {
            "Il2Cpp.GameControlManager",
            "GameControlManager",
            "Il2CppSynth.GameControlManager",
            "Synth.GameControlManager"
        };

        private static readonly string[] InfoProviderTypeNames =
        {
            "Il2Cpp.Game_InfoProvider",
            "Game_InfoProvider",
            "Il2CppSynth.Game_InfoProvider",
            "Synth.Game_InfoProvider"
        };

        // Candidate member names covering naming differences across Unity branches
        // (same sets proven in SynthRidersWebsocketMod).
        private static readonly string[] SongNameCandidates =
            { "_name", "_songName", "_title", "_trackName", "name", "songName", "title", "trackName" };
        private static readonly string[] SongAuthorCandidates =
            { "_author", "_artist", "_mapper", "_beatMapper", "author", "artist", "mapper" };
        private static readonly string[] DifficultyCandidates =
            { "CurrentDifficulty", "_currentDifficulty", "_difficulty", "difficulty", "Difficulty" };
        private static readonly string[] SingletonCandidates =
            { "s_instance", "Instance", "_instance", "instance" };

        private static Type _infoProviderType;
        private static Type _gcmType;
        private static bool _debug;
        private static bool _membersDumped;

        private static bool _inSong;
        private static bool _songStartFired;

        // Deferred metadata read: OnLevelFinishedLoading is the right neighbourhood,
        // but we retry briefly in case SetSongStatusData lands a frame or two later.
        private static bool _pendingInfoRead;
        private static DateTime _infoReadDeadlineUtc;
        private static DateTime _nextInfoAttemptUtc;

        public static void Configure(bool debugLogging) => _debug = debugLogging;

       
        /// Attempt to install patches. Returns false if GameControlManager isn't
        /// resolvable yet (caller retries from OnUpdate).
   
        public static bool TryPatch(HarmonyLib.Harmony harmony)
        {
            Type gcm = ResolveType(GcmTypeNames);
            if (gcm == null) return false;
            _gcmType = gcm;

            _infoProviderType = ResolveType(InfoProviderTypeNames);
            if (_infoProviderType == null)
                MelonLogger.Warning("[GameHooks] Game_InfoProvider type not found; song metadata will be unavailable.");

            int count = 0;

            count += PatchIfPresent(harmony, gcm, "Awake",
                postfix: nameof(Awake_Postfix));
            count += PatchIfPresent(harmony, gcm, "OnLevelFinishedLoading",
                postfix: nameof(OnLevelFinishedLoading_Postfix));
            count += PatchIfPresent(harmony, gcm, "ReturnToMenu",
                prefix: nameof(ReturnToMenu_Prefix));

            MelonLogger.Msg($"[GameHooks] Installed {count}/3 patches on {gcm.FullName}.");
            return count > 0;
        }

        private static int PatchIfPresent(HarmonyLib.Harmony harmony, Type type, string methodName,
            string prefix = null, string postfix = null)
        {
            try
            {
                var method = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                {
                    MelonLogger.Warning($"[GameHooks] {type.Name}.{methodName} not found — skipping.");
                    return 0;
                }

                harmony.Patch(method,
                    prefix: prefix == null ? null
                        : new HarmonyMethod(typeof(GameHooks).GetMethod(prefix, BindingFlags.Public | BindingFlags.Static)),
                    postfix: postfix == null ? null
                        : new HarmonyMethod(typeof(GameHooks).GetMethod(postfix, BindingFlags.Public | BindingFlags.Static)));

                if (_debug) MelonLogger.Msg($"[GameHooks] Patched {type.Name}.{methodName}");
                return 1;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameHooks] Patch of {type.Name}.{methodName} failed: {ex.Message}");
                return 0;
            }
        }

        // ------------------------------------------------------------------
        // Harmony callbacks (main thread)
        // ------------------------------------------------------------------

        public static void Awake_Postfix()
        {
            try
            {
                _inSong = true;
                _songStartFired = false;
                _pendingInfoRead = false;
                if (_debug) MelonLogger.Msg("[GameHooks] GameControlManager.Awake — song scene loading.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameHooks] Awake_Postfix: {ex.Message}");
            }
        }

        public static void OnLevelFinishedLoading_Postfix()
        {
            try
            {
                _inSong = true;
                if (_songStartFired) return;

                if (TryFireSongStart()) return;

                // Metadata not populated yet — retry for up to 3 seconds from OnUpdate.
                _pendingInfoRead = true;
                _infoReadDeadlineUtc = DateTime.UtcNow.AddSeconds(3);
                _nextInfoAttemptUtc = DateTime.UtcNow.AddMilliseconds(250);
                if (_debug) MelonLogger.Msg("[GameHooks] Metadata not ready at OnLevelFinishedLoading; retrying briefly.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameHooks] OnLevelFinishedLoading_Postfix: {ex.Message}");
            }
        }

        public static void ReturnToMenu_Prefix()
        {
            try
            {
                _pendingInfoRead = false;
                if (!_inSong) return;
                _inSong = false;
                _songStartFired = false;
                if (_debug) MelonLogger.Msg("[GameHooks] ReturnToMenu — song ended.");
                SongEnded?.Invoke();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameHooks] ReturnToMenu_Prefix: {ex.Message}");
            }
        }

        /// Called every frame from Main.OnUpdate for the deferred metadata retry.
        public static void Update()
        {
            if (!_pendingInfoRead) return;

            var now = DateTime.UtcNow;
            if (now < _nextInfoAttemptUtc) return;
            _nextInfoAttemptUtc = now.AddMilliseconds(250);

            if (TryFireSongStart())
            {
                _pendingInfoRead = false;
                return;
            }

            if (now >= _infoReadDeadlineUtc)
            {
                // Give up on metadata — fire with placeholders so the presence still updates.
                _pendingInfoRead = false;
                if (!_songStartFired)
                {
                    _songStartFired = true;
                    MelonLogger.Warning("[GameHooks] Song metadata never populated; using placeholder.");
                    SongStarted?.Invoke(new SongInfo { Name = "Unknown Song" });
                }
            }
        }

        // ------------------------------------------------------------------
        // Metadata reading
        // ------------------------------------------------------------------

        /// Returns true if metadata was available and SongStarted fired.
        private static bool TryFireSongStart()
        {
            var info = ReadSongInfo();
            if (info == null || string.IsNullOrWhiteSpace(info.Name)) return false;

            _songStartFired = true;
            if (_debug) MelonLogger.Msg($"[GameHooks] SongStarted: {info.Name} / {info.Author} / {info.Difficulty}");
            SongStarted?.Invoke(info);
            return true;
        }

        private static SongInfo ReadSongInfo()
        {
            try
            {
                if (_infoProviderType == null) return null;

                object instance = GetSingleton(_infoProviderType);
                if (instance == null) return null;

                if (_debug && !_membersDumped)
                {
                    _membersDumped = true;
                    DumpMembers(_infoProviderType);
                }

                return new SongInfo
                {
                    Name = ReadStringMember(instance, SongNameCandidates),
                    Author = ReadStringMember(instance, SongAuthorCandidates),
                    Difficulty = ReadStringMember(instance, DifficultyCandidates)
                };
            }
            catch (Exception ex)
            {
                if (_debug) MelonLogger.Warning($"[GameHooks] ReadSongInfo: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Live stats (score / combo / duration / play time)
        // ------------------------------------------------------------------

        // Verified in SynthRidersWebsocketMod: Game_ScoreManager.s_instance with
        // Score / CurrentCombo (polling from OnUpdate is the proven pattern; the
        // LevelUp-era dump also showed 'currentScore' casing, so both are probed).
        private static readonly string[] ScoreManagerTypeNames =
            { "Il2Cpp.Game_ScoreManager", "Game_ScoreManager", "Il2CppSynth.Game_ScoreManager" };
        private static readonly string[] ScoreCandidates =
            { "Score", "currentScore", "CurrentScore", "_score" };
        private static readonly string[] ComboCandidates =
            { "CurrentCombo", "currentCombo", "_currentCombo", "Combo", "_combo" };

        // Duration: Game_InfoProvider directly, or via its TrackData ('AudioLenght'
        // is the game's own typo — probe it first).
        private static readonly string[] TrackDataCandidates =
            { "TrackData", "_trackData", "trackData", "CurrentTrackData" };
        private static readonly string[] DurationCandidates =
            { "AudioLenght", "AudioLength", "_audioLenght", "Length", "Duration", "_length", "duration" };

        // Play position within the song (member name unverified — candidates + debug
        // dump; used only to resync the countdown, so a miss degrades gracefully).
        private static readonly string[] PlayTimeCandidates =
            { "PlayTime", "CurrentPlayTime", "playTime", "_playTime", "PlayTimeMS", "SongTime", "CurrentTrackTime" };

        private static Type _scoreManagerType;
        private static bool _scoreTypeProbed;
        private static bool _scoreMembersDumped;

        /// Read live score and combo. False if the score manager isn't up.
        public static bool TryReadScore(out long score, out int combo)
        {
            score = 0; combo = 0;
            try
            {
                if (!_scoreTypeProbed)
                {
                    _scoreTypeProbed = true;
                    _scoreManagerType = ResolveType(ScoreManagerTypeNames);
                    if (_scoreManagerType == null)
                        MelonLogger.Warning("[GameHooks] Game_ScoreManager type not found; live score unavailable.");
                }
                if (_scoreManagerType == null) return false;

                object mgr = GetSingleton(_scoreManagerType);
                if (mgr == null) return false;

                double? s = ReadNumericMember(mgr, ScoreCandidates);
                if (s == null)
                {
                    if (_debug && !_scoreMembersDumped)
                    {
                        _scoreMembersDumped = true;
                        DumpMembers(_scoreManagerType);
                    }
                    return false;
                }

                score = (long)s.Value;
                combo = (int)(ReadNumericMember(mgr, ComboCandidates) ?? 0);
                return true;
            }
            catch { return false; }
        }

        /// Song duration in seconds, with a seconds/ms unit heuristic.
        public static bool TryReadDurationSeconds(out float seconds)
        {
            seconds = 0f;
            try
            {
                if (_infoProviderType == null) return false;
                object info = GetSingleton(_infoProviderType);
                if (info == null) return false;

                double? raw = ReadNumericMember(info, DurationCandidates);
                if (raw == null)
                {
                    // Try via the TrackData object.
                    object trackData = ReadObjectMember(info, TrackDataCandidates);
                    if (trackData != null)
                        raw = ReadNumericMember(trackData, DurationCandidates);
                }
                if (raw == null || raw.Value <= 0) return false;

                double v = raw.Value;
                // Heuristic: nothing sane is a >4h song in seconds, so bigger = ms.
                seconds = v > 3600 * 4 ? (float)(v / 1000.0) : (float)v;

                if (_debug) MelonLogger.Msg($"[GameHooks] Song duration: raw={v}, interpreted={seconds:F1}s");
                return seconds > 5f && seconds < 3600f * 4f;
            }
            catch { return false; }
        }

        /// Current play position in seconds. durationSeconds (when known) disambiguates
        /// seconds vs milliseconds: a raw value far beyond the song length must be ms.

        public static bool TryReadPlayTimeSeconds(float durationSeconds, out float seconds)
        {
            seconds = 0f;
            try
            {
                object src = _gcmType != null ? GetSingleton(_gcmType) : null;
                double? raw = src != null ? ReadNumericMember(src, PlayTimeCandidates) : null;

                if (raw == null && _scoreManagerType != null)
                {
                    object mgr = GetSingleton(_scoreManagerType);
                    if (mgr != null) raw = ReadNumericMember(mgr, PlayTimeCandidates);
                }
                if (raw == null || raw.Value < 0) return false;

                double v = raw.Value;
                if (durationSeconds > 0 && v > durationSeconds * 2) v /= 1000.0; // must be ms
                else if (v > 3600 * 4) v /= 1000.0;

                seconds = (float)v;
                return true;
            }
            catch { return false; }
        }

        /// Read the first numeric-convertible member from a candidate list.
        private static double? ReadNumericMember(object instance, string[] candidates)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();

            foreach (string name in candidates)
            {
                try
                {
                    object v = null;
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.CanRead) v = prop.GetValue(instance);
                    if (v == null)
                    {
                        var field = type.GetField(name, flags);
                        if (field != null) v = field.GetValue(instance);
                    }
                    if (v is int || v is long || v is float || v is double || v is short || v is uint)
                        return Convert.ToDouble(v);
                }
                catch { /* try next candidate */ }
            }
            return null;
        }

        /// Read the first non-null object member from a candidate list.
        private static object ReadObjectMember(object instance, string[] candidates)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();

            foreach (string name in candidates)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.CanRead)
                    {
                        object v = prop.GetValue(instance);
                        if (v != null) return v;
                    }
                    var field = type.GetField(name, flags);
                    if (field != null)
                    {
                        object v = field.GetValue(instance);
                        if (v != null) return v;
                    }
                }
                catch { /* try next candidate */ }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Reflection helpers
        // ------------------------------------------------------------------

        private static Type ResolveType(string[] candidates)
        {
            // Deliberately NOT AccessTools.TypeByName: its fallback enumerates every
            // type in every loaded assembly (Assembly.GetTypes), and some interop-
            // generated Unity module wrappers contain unloadable types, spamming
            // ReflectionTypeLoadException warnings on each miss. Assembly.GetType(name)
            // is an exact lookup that never enumerates and never throws for them.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (string name in candidates)
            {
                var direct = Type.GetType(name);
                if (direct != null) return Found(direct);

                foreach (var asm in assemblies)
                {
                    try
                    {
                        var t = asm.GetType(name);
                        if (t != null) return Found(t);
                    }
                    catch { /* dynamic/broken assembly — skip */ }
                }
            }

            // Last resort: short-name scan, tolerating partially-loadable assemblies
            // by using the Types array off the loader exception instead of failing.
            string shortName = candidates[0].Substring(candidates[0].LastIndexOf('.') + 1);
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                    if (t != null && t.Name == shortName)
                        return Found(t);
            }

            return null;

            Type Found(Type t)
            {
                if (_debug) MelonLogger.Msg($"[GameHooks] Resolved type: {t.FullName} ({t.Assembly.GetName().Name})");
                return t;
            }
        }

        /// Robust IL2CPP singleton lookup — probes common names as property AND field.
        private static object GetSingleton(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (string name in SingletonCandidates)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null)
                    {
                        object v = prop.GetValue(null);
                        if (v != null) return v;
                    }

                    var field = type.GetField(name, flags);
                    if (field != null)
                    {
                        object v = field.GetValue(null);
                        if (v != null) return v;
                    }
                }
                catch { /* try next candidate */ }
            }
            return null;
        }

    
        /// Read the first non-empty string-convertible member from a candidate list.
        /// IL2CPP interop exposes native fields as properties, so probe properties first,
        /// then the plain fields.

        private static string ReadStringMember(object instance, string[] candidates)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();

            foreach (string name in candidates)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.CanRead)
                    {
                        string s = prop.GetValue(instance)?.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }

                    var field = type.GetField(name, flags);
                    if (field != null)
                    {
                        string s = field.GetValue(instance)?.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
                catch { /* try next candidate */ }
            }
            return "";
        }

        private static void DumpMembers(Type type)
        {
            try
            {
                MelonLogger.Msg($"[GameHooks] --- {type.FullName} member dump ---");
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var p in type.GetProperties(flags))
                    MelonLogger.Msg($"[GameHooks]   prop  {p.PropertyType.Name} {p.Name}");
                foreach (var f in type.GetFields(flags))
                    MelonLogger.Msg($"[GameHooks]   field {f.FieldType.Name} {f.Name}");
                MelonLogger.Msg("[GameHooks] --- end dump ---");
            }
            catch { }
        }
    }
}
