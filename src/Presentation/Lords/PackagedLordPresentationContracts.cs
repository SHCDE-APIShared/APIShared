using APIShared.Lords;
using APIShared.Internal;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.Presentation
{
    /// <summary>One immutable, authenticated packaged Lord presentation. No gameplay or Lua registration is performed.</summary>
    public sealed class PackagedLordPresentationEntry
    {
        // Script Extender asset-provider path convention: _SLA_/<escaped provider GUID>/<relative path>.
        private const string ScopedAssetPrefix = "_SLA_/";
        internal readonly LordMetadataDocument Metadata;
        internal readonly bool HasMetadata;
        internal readonly Dictionary<AILordMessageType, Clip[]> Clips = new Dictionary<AILordMessageType, Clip[]>();
        internal sealed class Clip { internal string Audio, Video; internal LordMetadataDocument Text; }
        /// <summary>Creates a slot-bound entry. The consumer validates source identity and optional private asset provider.</summary>
        /// <remarks>Null metadata retains the authored name and uses the game's legacy title fallback at runtime.
        /// An explicit document, including {}, retains its own title semantics. Media policy is unchanged.</remarks>
        public PackagedLordPresentationEntry(int playerId, string internalName, string authoredName,
            string metadataJson, string assetProviderGuid = null, bool allowGlobalMedia = false)
        {
            if (playerId < 1 || playerId > 8 || string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(authoredName))
                throw new ArgumentException("A packaged Lord needs a valid player slot and identity.");
            PlayerId = playerId; InternalName = internalName; AuthoredName = authoredName; AssetProviderGuid = assetProviderGuid;
            AllowGlobalMedia = allowGlobalMedia;
            HasMetadata = metadataJson != null;
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
        /// <summary>Permits SE's global Lord media lookup when no AssetProviderGuid is set; the consumer verifies that source.</summary>
        public bool AllowGlobalMedia { get; }
        /// <summary>Complete original lordmeta.json, including future fields.</summary>
        public string MetadataJson => Metadata.OriginalJson;
        /// <summary>Resolves a full name using the game's nickname subtype.</summary>
        public string ResolveName(string locale, int nickname) => LordMetadataDocument.Combine(
            Metadata.ResolveName(locale, AuthoredName), Metadata.ResolveTitle(locale, nickname));
        internal string Asset(string path) => string.IsNullOrWhiteSpace(path) || AssetProviderGuid == null ? path :
            ScopedAssetPrefix + Uri.EscapeDataString(AssetProviderGuid) + "/" + path.Replace('\\', '/');
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
}
