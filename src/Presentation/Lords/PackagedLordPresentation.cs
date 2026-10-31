using APIShared.Content.Lords;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.API.Components.AI;
using SHCDESE.API.Components.Network;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace APIShared.Presentation
{
    /// <summary>One immutable, authenticated packaged Lord presentation. No gameplay or Lua registration is performed.</summary>
    public sealed class PackagedLordPresentationEntry
    {
        internal readonly LordMetadataDocument Metadata;
        internal readonly Dictionary<AILordMessageType, Clip[]> Clips = new Dictionary<AILordMessageType, Clip[]>();
        internal sealed class Clip { internal string Audio, Video; internal LordMetadataDocument Text; }
        /// <summary>Creates a slot-bound entry. The consumer validates source identity and optional private asset provider.</summary>
        public PackagedLordPresentationEntry(int playerId, string internalName, string authoredName,
            string metadataJson, string assetProviderGuid = null, bool allowGlobalMedia = false)
        {
            if (playerId < 1 || playerId > 8 || string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(authoredName))
                throw new ArgumentException("A packaged Lord needs a valid player slot and identity.");
            PlayerId = playerId; InternalName = internalName; AuthoredName = authoredName; AssetProviderGuid = assetProviderGuid;
            AllowGlobalMedia = allowGlobalMedia;
            Metadata = LordMetadataDocument.Parse(metadataJson ?? "{}");
            foreach (AILordMessageType category in Enum.GetValues(typeof(AILordMessageType)))
            {
                var rows = DependencyFreeJson.Parse(Metadata.GetMessageClipsJson(category.ToString())) as List<object>;
                if (rows == null) continue;
                Clips[category] = rows.Select(row => {
                    var data = row as Dictionary<string, object> ?? throw new ArgumentException("Invalid Lord message clip.");
                    object audio, video, text;
                    data.TryGetValue("AudioPath", out audio); data.TryGetValue("VideoPath", out video); data.TryGetValue("LocalizedText", out text);
                    return new Clip { Audio = audio as string ?? "", Video = video as string ?? "",
                        Text = LordMetadataDocument.Parse(DependencyFreeJson.Serialize(new Dictionary<string, object> { ["text"] = text })) };
                }).ToArray();
            }
            if ((!Clips.TryGetValue(AILordMessageType.IncomingMessage, out var incoming) || incoming.Length == 0) && !string.IsNullOrWhiteSpace(Metadata.GetPath("IncomingMessage")))
                Clips[AILordMessageType.IncomingMessage] = new[] { new Clip { Audio = Metadata.GetPath("IncomingMessage"), Video = "", Text = LordMetadataDocument.Parse("{}") } };
        }
        /// <summary>One-based player slot.</summary>
        public int PlayerId { get; }
        /// <summary>Current game identity; separate from the authored display name.</summary>
        public string InternalName { get; }
        /// <summary>Nonlocalized fallback without a Workshop prefix.</summary>
        public string AuthoredName { get; }
        /// <summary>Optional private media provider. Null alone supplies metadata without claiming media availability.</summary>
        public string AssetProviderGuid { get; }
        /// <summary>Explicitly permits existing global/game media lookup for a verified legacy global-mode source.</summary>
        public bool AllowGlobalMedia { get; }
        /// <summary>Complete original lordmeta.json, including future fields.</summary>
        public string MetadataJson => Metadata.OriginalJson;
        /// <summary>Resolves a full name using the game's nickname subtype.</summary>
        public string ResolveName(string locale, int nickname) => LordMetadataDocument.Combine(
            Metadata.ResolveName(locale, AuthoredName), Metadata.ResolveTitle(locale, nickname));
        internal string Asset(string path) => string.IsNullOrWhiteSpace(path) || AssetProviderGuid == null ? path :
            "_SLA_/" + Uri.EscapeDataString(AssetProviderGuid) + "/" + path.Replace('\\', '/');
    }

    /// <summary>Permanent owner registration; publish immutable snapshots on Unity and clear them at session end.</summary>
    public sealed class PackagedLordPresentationRegistration
    {
        internal PackagedLordPresentationEntry[] Entries = new PackagedLordPresentationEntry[0];
        /// <summary>Replaces this owner's selection. Empty/null deactivates it without uninstalling hooks.</summary>
        public void SetEntries(IReadOnlyList<PackagedLordPresentationEntry> entries)
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Lord presentation publication requires Unity.");
            var copy = entries?.ToArray() ?? new PackagedLordPresentationEntry[0];
            if (copy.Any(x => x == null) || copy.Select(x => x.PlayerId).Distinct().Count() != copy.Length)
                throw new ArgumentException("Ambiguous packaged Lord slots.");
            Entries = copy;
            PackagedLordPresentation.Refresh();
        }
        internal PackagedLordPresentationRegistration() { }
    }

    /// <summary>Shared, scoped compatibility adapter for complete packaged Lord metadata.</summary>
    /// <remarks>Register/publish on Unity. Lookups use immutable snapshots on the caller thread; no file access or
    /// provider callbacks in lookups. Unclaimed identities retain SE/Vanilla. Multiple different claims fail closed.
    /// Portraits use the Unity asset resolver and are cached on demand. Consumers own session lifetime,
    /// host authorization and media-provider validation. Registrations and hooks
    /// remain rooted until process exit; this service never changes SE's global Lord catalog or Lua availability.</remarks>
    public static class PackagedLordPresentation
    {
        private delegate string ComputerName(int lord, int nickname);
        private delegate bool Supported(GameAIManagerAPI self, string name);
        private delegate bool Text(GameAIManagerAPI self, string name, out string text);
        private delegate bool Title(GameAIManagerAPI self, string name, out string text, int nickname);
        private delegate bool Details(GameAIManagerAPI self, string name, out LordDetails details);
        private delegate bool Face(GameAIManagerAPI self, string name, out ImageSource face);
        private delegate bool Media(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio);
        private delegate string CustomText(CustomisationFileManager self, string name, int id);
        private delegate string PlayerName(Platform_Multiplayer self, int player, bool active, bool exclude);
        private static readonly ManagedInterceptionHook<ComputerName> names = new ManagedInterceptionHook<ComputerName>();
        private static readonly ManagedInterceptionHook<Supported> supported = new ManagedInterceptionHook<Supported>();
        private static readonly ManagedInterceptionHook<Text> display = new ManagedInterceptionHook<Text>(), join = new ManagedInterceptionHook<Text>(), leave = new ManagedInterceptionHook<Text>(), speech = new ManagedInterceptionHook<Text>(), lua = new ManagedInterceptionHook<Text>();
        private static readonly ManagedInterceptionHook<Title> titles = new ManagedInterceptionHook<Title>();
        private static readonly ManagedInterceptionHook<Details> details = new ManagedInterceptionHook<Details>();
        private static readonly ManagedInterceptionHook<Face> faces = new ManagedInterceptionHook<Face>();
        private static readonly ManagedInterceptionHook<Media> media = new ManagedInterceptionHook<Media>();
        private static readonly ManagedInterceptionHook<CustomText> customText = new ManagedInterceptionHook<CustomText>();
        private static readonly ManagedInterceptionHook<PlayerName> playerNames = new ManagedInterceptionHook<PlayerName>();
        private static readonly Dictionary<string, PackagedLordPresentationRegistration> owners = new Dictionary<string, PackagedLordPresentationRegistration>();
        private static PackagedLordPresentationEntry[] entries = new PackagedLordPresentationEntry[0];
        private static readonly object speechGate = new object();
        private static readonly Dictionary<string, string> spoken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private sealed class PortraitEntry { internal Texture2D Texture; internal ImageSource Image; }
        private static readonly Dictionary<string, PortraitEntry> portraits = new Dictionary<string, PortraitEntry>();
        private static readonly MethodInfo textureLoader = typeof(GameAssetManagerAPI).GetMethod("TryLoadTexture",
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(Texture2D).MakeByRefType() }, null);
        private static bool installed;

        /// <summary>Registers one process-lifetime owner. Failures return a reason and no registration.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            out PackagedLordPresentationRegistration registration, out string reason)
        {
            registration = null;
            try
            {
                if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Register on Unity.");
                if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId)) throw new ArgumentException("Owner and ID are required.");
                string key = ownerGuid + "\n" + registrationId;
                if (owners.ContainsKey(key)) throw new ArgumentException("Duplicate Lord presentation registration.");
                Install();
                registration = new PackagedLordPresentationRegistration(); owners.Add(key, registration); reason = null; return true;
            }
            catch (Exception error) { reason = error.Message; return false; }
        }
        internal static void Refresh()
        {
            Volatile.Write(ref entries, owners.Values.SelectMany(x => x.Entries).ToArray());
            lock (speechGate) spoken.Clear();
        }
        private static PackagedLordPresentationEntry Find(string name)
        {
            PackagedLordPresentationEntry found = null;
            foreach (var entry in Volatile.Read(ref entries))
                if (string.Equals(entry.InternalName, name, StringComparison.OrdinalIgnoreCase))
                { if (found != null && !Equivalent(found, entry)) return null; found = entry; }
            return found;
        }
        private static PackagedLordPresentationEntry Slot(int slot)
        {
            PackagedLordPresentationEntry found = null;
            foreach (var entry in Volatile.Read(ref entries)) if (entry.PlayerId == slot)
            { if (found != null && !Equivalent(found, entry)) return null; found = entry; }
            return found;
        }
        private static bool Equivalent(PackagedLordPresentationEntry left, PackagedLordPresentationEntry right) =>
            string.Equals(left.InternalName, right.InternalName, StringComparison.OrdinalIgnoreCase) &&
            left.MetadataJson == right.MetadataJson && left.AuthoredName == right.AuthoredName &&
            left.AssetProviderGuid == right.AssetProviderGuid && left.AllowGlobalMedia == right.AllowGlobalMedia;
        internal static bool Claims(int player, string name)
        {
            var entry = Slot(player);
            return entry != null && string.Equals(entry.InternalName, name, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool HasEntries => Volatile.Read(ref entries).Length != 0;
        internal static string RestartIdentity(int player) => Slot(player)?.InternalName;
        private static string Locale => GameLocale.Current();
        private static void Install()
        {
            if (installed) return;
            if (textureLoader == null || textureLoader.ReturnType != typeof(bool)) throw new MissingMethodException("GameAssetManagerAPI.TryLoadTexture");
            CoopLordRestartPresentation.Install();
            var ai = typeof(GameAIManagerAPI); var textParams = new[] { typeof(string), typeof(string).MakeByRefType() };
            names.Install(typeof(OnScreenText), "getComputerName", typeof(string), new[] { typeof(int), typeof(int) }, Name, "packaged-lord-name");
            supported.Install(ai, "IsSupportedCustomLord", typeof(bool), new[] { typeof(string) }, IsSupported, "packaged-lord-supported");
            display.Install(ai, "TryGetDisplayName", typeof(bool), textParams, Display, "packaged-lord-display");
            titles.Install(ai, "TryGetLordTitle", typeof(bool), new[] { typeof(string), typeof(string).MakeByRefType(), typeof(int) }, TitleText, "packaged-lord-title");
            details.Install(ai, "TryGetLordDetails", typeof(bool), new[] { typeof(string), typeof(LordDetails).MakeByRefType() }, DetailText, "packaged-lord-details");
            faces.Install(ai, "TryGetFace", typeof(bool), new[] { typeof(string), typeof(ImageSource).MakeByRefType() }, Portrait, "packaged-lord-face");
            join.Install(ai, "TryGetJoinAudio", typeof(bool), textParams, Join, "packaged-lord-join");
            leave.Install(ai, "TryGetLeaveAudio", typeof(bool), textParams, Leave, "packaged-lord-leave");
            speech.Install(ai, "TryGetCurrentSpeechText", typeof(bool), textParams, Speech, "packaged-lord-subtitle");
            lua.Install(ai, "TryGetLuaInitPath", typeof(bool), textParams, LuaPath, "packaged-lord-lua-availability");
            media.Install(ai, "TryGetVideoAndAudio", typeof(bool), new[] { typeof(string), typeof(AILordMessageType), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() }, Message, "packaged-lord-media");
            customText.Install(typeof(CustomisationFileManager), "GetCustomLordText", typeof(string), new[] { typeof(string), typeof(int) }, Custom, "packaged-lord-custom-text");
            playerNames.Install(typeof(Platform_Multiplayer), "getPlayerName", typeof(string), new[] { typeof(int), typeof(bool), typeof(bool) }, Player, "packaged-lord-player-name");
            installed = true;
        }
        private static string Name(int lord, int nickname)
        {
            // Same audited conversion as SE, without constructing its native-backed singleton.
            int slot = lord - (int)Enums.AILords.SK_X1 + 1;
            var entry = slot >= 1 && slot <= 8 ? Slot(slot) : null;
            return entry == null ? names.Original(lord, nickname) : entry.ResolveName(Locale, nickname);
        }
        private static string Player(Platform_Multiplayer self, int player, bool active, bool exclude)
        {
            var entry = !exclude ? Slot(player) : null;
            if (entry == null) return playerNames.Original(self, player, active, exclude);
            Platform_Multiplayer.MPGameMember member = null;
            if (self.gameMembers != null) foreach (var candidate in self.gameMembers)
                if (candidate.playerID == player) { member = candidate; break; }
            if (member == null || !member.skirmishAI || active && member.kicked) return playerNames.Original(self, player, active, exclude);
            var state = GameData.Instance?.lastGameState;
            return state?.computer_names != null && player < state.computer_names.Length ? entry.ResolveName(Locale, state.computer_names[player]) : playerNames.Original(self, player, active, exclude);
        }
        private static bool IsSupported(GameAIManagerAPI self, string name) => Find(name) != null || supported.Original(self, name);
        private static bool Display(GameAIManagerAPI self, string name, out string text)
        { var entry = Find(name); if (entry == null) return display.Original(self, name, out text); text = entry.Metadata.ResolveName(Locale, entry.AuthoredName); return true; }
        private static bool TitleText(GameAIManagerAPI self, string name, out string text, int nickname)
        { var entry = Find(name); if (entry == null) return titles.Original(self, name, out text, nickname); text = entry.Metadata.ResolveTitle(Locale, nickname); return true; }
        private static string Custom(CustomisationFileManager self, string name, int id)
        {
            var entry = id >= 34 && id <= 41 ? Find(name) : null;
            if (entry == null) return customText.Original(self, name, id);
            string text = entry.Metadata.ResolveTitle(Locale, id - 34);
            int comma = text.IndexOf(','); return comma < 0 ? text : text.Remove(comma, 1);
        }
        private static bool DetailText(GameAIManagerAPI self, string name, out LordDetails result)
        {
            var entry = Find(name); if (entry == null) return details.Original(self, name, out result);
            result = new LordDetails { Description = entry.Metadata.ResolveText("LocalizedDescription", Locale),
                DifficultyRating = entry.Metadata.ResolveText("LocalizedDifficultyRating", Locale), FavouriteTroops = entry.Metadata.ResolveText("LocalizedFavouriteTroops", Locale),
                Castles = entry.Metadata.ResolveText("LocalizedCastles", Locale), PlayStyle = entry.Metadata.ResolveText("LocalizedPlayStyle", Locale), FavouriteSaying = entry.Metadata.ResolveText("LocalizedFavouriteSaying", Locale) }; return true;
        }
        private static bool Portrait(GameAIManagerAPI self, string name, out ImageSource result)
        {
            var entry = Find(name); if (entry == null) return faces.Original(self, name, out result);
            string path = entry.Asset(entry.Metadata.GetPath("FacePath")); result = null;
            if (entry.AssetProviderGuid == null && !entry.AllowGlobalMedia) return false;
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!UnityMainThreadDispatch.IsMainThread) return false;
            string key = Locale + "\n" + path;
            if (portraits.TryGetValue(key, out var cached)) { result = cached.Image; return result != null; }
            var args = new object[] { path, null };
            var portrait = new PortraitEntry();
            if ((bool)textureLoader.Invoke(GameAssetManagerAPI.Instance, args) && args[1] is Texture2D texture)
            { portrait.Texture = texture; result = portrait.Image = new TextureSource(texture); }
            portraits[key] = portrait; return result != null;
        }
        private static bool Join(GameAIManagerAPI self, string name, out string text) => Audio(join.Original, self, name, "JoinAudioPath", out text);
        private static bool Leave(GameAIManagerAPI self, string name, out string text) => Audio(leave.Original, self, name, "LeaveAudioPath", out text);
        private static bool Audio(Text next, GameAIManagerAPI self, string name, string field, out string text)
        { var entry = Find(name); if (entry == null) return next(self, name, out text); text = entry.AssetProviderGuid == null && !entry.AllowGlobalMedia ? "" : entry.Asset(entry.Metadata.GetPath(field)); return !string.IsNullOrWhiteSpace(text); }
        private static bool LuaPath(GameAIManagerAPI self, string name, out string path)
        {
            if (Find(name) == null || self.LordDataDict.ContainsKey(name.ToLower())) return lua.Original(self, name, out path);
            path = null; return false; // Metadata-only support must not claim a gameplay script exists.
        }
        private static bool Speech(GameAIManagerAPI self, string name, out string text)
        { if (Find(name) == null) return speech.Original(self, name, out text); lock (speechGate) return spoken.TryGetValue(name, out text); }
        private static bool Message(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
        {
            var entry = Find(name); if (entry == null) return media.Original(self, name, type, out video, out audio);
            video = audio = "";
            PackagedLordPresentationEntry.Clip[] clips;
            if (!entry.Clips.TryGetValue(type, out clips) || clips.Length == 0) return false;
            // Same SE RNG and selection on every peer, independent of local media availability.
            var selected = clips[DeterministicRandom.Next(0, clips.Length)];
            string text = selected.Text.ResolveText("text", Locale);
            lock (speechGate) { if (string.IsNullOrEmpty(text)) spoken.Remove(name); else spoken[name] = text; }
            if (entry.AssetProviderGuid == null && !entry.AllowGlobalMedia) return false;
            video = entry.Asset(selected.Video); audio = entry.Asset(selected.Audio);
            return true;
        }
    }
}
