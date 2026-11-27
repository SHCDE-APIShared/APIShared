using APIShared.Internal;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop.Enums;
using System;
using System.IO;
using System.Text;
using System.Threading;
using AILords = SHCDESE.Interop.AILords;

namespace APIShared.Presentation
{
    /// <summary>A read-only reference to the three authored parts of one Vanilla Lord message.</summary>
    /// <remarks>Paths are the original game's references, not decoded assets. Normal game and Extender
    /// loaders still select language and any installed resource overrides. No message or random draw is produced.</remarks>
    public sealed class VanillaLordMessage
    {
        internal VanillaLordMessage(AILords lord, AILordMessageType type, string audio, string video, int textIndex)
        { Lord = lord; MessageType = type; AudioPath = audio; VideoPath = video; TextIndex = textIndex; }

        /// <summary>The built-in Lord whose authored message is referenced; this is not a player slot.</summary>
        public AILords Lord { get; }
        /// <summary>The message category, excluding the preliminary announcement and nickname entries.</summary>
        public AILordMessageType MessageType { get; }
        /// <summary>The game's original speech reference, including its fx/speech prefix.</summary>
        public string AudioPath { get; }
        /// <summary>The game's original video filename.</summary>
        public string VideoPath { get; }
        /// <summary>The game's localized skirmish speech section.</summary>
        public global::Enums.eTextSections TextSection => global::Enums.eTextSections.TEXT_SKIRMISH_SPEECH;
        /// <summary>The one-based authored message index within <see cref="TextSection"/>.</summary>
        public int TextIndex { get; }
    }

    /// <summary>Original Vanilla Lord message references, resolved once from the native image without playing anything.</summary>
    /// <remarks>Available after APIShared's native initialization. Immutable references are safe to retain for
    /// process lifetime and to query from any thread. The reader never mutates the game, queue or shared RNG.
    /// Failure disables this catalog only; independent message presentation and custom sources remain usable.</remarks>
    public static class VanillaLordMessages
    {
        private static VanillaLordMessage[] messages;
        private static string diagnostic = "Pending native initialization.";
        private static int attempted;

        /// <summary>Whether the native source tables passed their independent validation.</summary>
        public static bool Available => Volatile.Read(ref messages) != null;
        /// <summary>The availability or validation failure reason for the Vanilla reference reader.</summary>
        public static string Diagnostic => Volatile.Read(ref diagnostic);

        /// <summary>Finds an authored Vanilla message without loading assets, displaying a message or consuming RNG.</summary>
        /// <param name="lord">A built-in Extender Lord enum, not a player or custom-AIC slot.</param>
        /// <param name="type">An actual message category; IncomingMessage and nicknames are not full messages.</param>
        /// <param name="message">Immutable original reference on success.</param>
        /// <param name="reason">An unavailable-catalog or invalid-reference reason on failure.</param>
        /// <returns>False without changing game state when the catalog or requested reference is unavailable.</returns>
        public static bool TryGet(AILords lord, AILordMessageType type, out VanillaLordMessage message, out string reason)
        {
            message = null;
            int slot = (int)lord, category = (int)type;
            if (!ValidReference(slot, category)) { reason = "A built-in Lord and a full message category are required."; return false; }
            VanillaLordMessage[] snapshot = Volatile.Read(ref messages);
            if (snapshot == null) { reason = Diagnostic; return false; }
            message = snapshot[category * NativeLordMessageCatalog.Slots + slot];
            reason = string.Empty;
            return true;
        }

        internal static bool ValidReference(int lord, int type) =>
            lord >= (int)AILords.SK_RAT && lord <= (int)AILords.SK_DLC4B &&
            type >= (int)AILordMessageType.Taunt1 && type <= (int)AILordMessageType.WillAttackEnemy;

        internal static void Initialize(CrusaderLibraryLoadContext context, string nativeHash, ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            try
            {
                if (context == null || context.ModuleHandle == IntPtr.Zero) throw new InvalidOperationException("Native image is unavailable.");
                string cache = NativeLordMessageCatalog.CachePath(nativeHash);
                var anchors = NativeLordMessageCatalog.Resolve(context.Memory, nativeHash, cache);
                var loaded = NativeLordMessageCatalog.Read(context.Memory, unchecked((ulong)context.ModuleHandle.ToInt64()), anchors);
                Volatile.Write(ref messages, loaded);
                Volatile.Write(ref diagnostic, "Ready");
                NativeApiLog.Info(log, "LORD_MESSAGE_VANILLA_READY: method=" + anchors.Method +
                    ",baseAnchor=0x" + anchors.Base.ToString("X") + ",genericAnchor=0x" + anchors.Generic.ToString("X") +
                    ",builtInAnchor=0x" + anchors.BuiltIn.ToString("X") + "; immutable original media and localized text references.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref diagnostic, "Unavailable: " + error.Message);
                NativeApiLog.Error(log, "LORD_MESSAGE_VANILLA_UNAVAILABLE: " + error.Message);
            }
        }
    }

    // A reader of the load-time PE snapshot, not a native hook. Two independent addressing forms
    // corroborate both table displacements and the 30-pointer category stride before any pointer is read.
    internal static class NativeLordMessageCatalog
    {
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int Slots = (int)AILords.SK_DLC4B + 1;
        internal const int Categories = (int)AILordMessageType.WillAttackEnemy + 1;
        private const int BaseRva = 0x103ADC, GenericRva = 0x103B22, BuiltInRva = 0x103BA3;
        private const string BasePattern = "49 63 C0 48 69 C8 E4 05 00 00 4D 89 7B D0 4C 8D 3D ?? ?? ?? ?? 4A 63 BC 39 ?? ?? ?? ?? 83 FF 1D 7E ??";
        private const string GenericPattern = "48 69 DD F0 00 00 00 44 8B C6 48 8D 15 ?? ?? ?? ?? 48 8D 4C 24 30 4E 8B 8C 3B ?? ?? ?? ?? E8 ?? ?? ?? ?? 4E 8B 8C 3B ?? ?? ?? ??";
        private const string BuiltInPattern = "48 6B CD 1E 6B C7 22 F7 DE 44 8D 45 DE 48 03 CF BA DD 00 00 00 4D 8B 8C CF ?? ?? ?? ?? 44 03 C0 49 8B 84 CF ?? ?? ?? ??";

        internal readonly struct Anchors
        {
            internal Anchors(int moduleBase, int generic, int builtIn, string method)
            { Base = moduleBase; Generic = generic; BuiltIn = builtIn; Method = method; }
            internal int Base { get; }
            internal int Generic { get; }
            internal int BuiltIn { get; }
            internal string Method { get; }
        }

        internal static string CachePath(string hash)
        {
            if (hash == null || hash.Length != 64) return null;
            for (int i = 0; i < hash.Length; i++) if (!Uri.IsHexDigit(hash[i])) return null;
            return Path.Combine(BepInEx.Paths.CachePath, "APIShared", "lord-messages",
                hash.ToUpperInvariant() + "-" + typeof(VanillaLordMessages).Assembly.GetName().Version + ".bin");
        }

        internal static Anchors Resolve(ReadOnlySpan<byte> image, string hash, string cachePath = null)
        {
            bool known = string.Equals(hash, NativeHash, StringComparison.OrdinalIgnoreCase);
            Anchors cached = default;
            bool hasCache = !known && TryReadCache(cachePath, out cached);
            int b = known ? BaseRva : hasCache ? cached.Base : BaseRva;
            int g = known ? GenericRva : hasCache ? cached.Generic : GenericRva;
            int n = known ? BuiltInRva : hasCache ? cached.BuiltIn : BuiltInRva;
            // A cache hit gets the same complete-byte validation as a known reference. A stale
            // cached anchor simply takes the resolver's executable-section uniqueness fallback.
            var baseAnchor = NativePatternResolver.ResolveUnique(image, BasePattern, b, known || hasCache, "Lord media module-base anchor");
            var generic = NativePatternResolver.ResolveUnique(image, GenericPattern, g, known || hasCache, "Lord media generic table anchor");
            var builtIn = NativePatternResolver.ResolveUnique(image, BuiltInPattern, n, known || hasCache, "Lord media built-in table anchor");
            if (!(baseAnchor.Rva < generic.Rva && generic.Rva < builtIn.Rva && builtIn.Rva - baseAnchor.Rva < 0x200))
                throw new InvalidOperationException("Lord media anchors do not belong to one audited wrapper.");
            string method = known && baseAnchor.Method == "reference-rva" && generic.Method == "reference-rva" && builtIn.Method == "reference-rva"
                ? "reference-rva" : hasCache && baseAnchor.Method == "reference-rva" && generic.Method == "reference-rva" && builtIn.Method == "reference-rva"
                ? "validated-cache" : "pattern-fallback";
            var result = new Anchors(baseAnchor.Rva, generic.Rva, builtIn.Rva, method);
            ValidateAddressing(image, result, out _, out _);
            if (!known && method != "validated-cache") TryWriteCache(cachePath, result);
            return result;
        }

        internal static VanillaLordMessage[] Read(ReadOnlySpan<byte> image, ulong moduleBase, Anchors anchors)
        {
            NativePeImage pe = NativePeImage.Parse(image);
            ValidateAddressing(image, anchors, out int audioTable, out int videoTable);
            RequireData(pe, audioTable, checked(Slots * Categories * sizeof(ulong)), "Lord audio pointer table");
            RequireData(pe, videoTable, checked(Slots * Categories * sizeof(ulong)), "Lord video pointer table");
            var result = new VanillaLordMessage[Slots * Categories];
            for (int category = (int)AILordMessageType.Taunt1; category <= (int)AILordMessageType.WillAttackEnemy; category++)
                for (int lord = (int)AILords.SK_RAT; lord <= (int)AILords.SK_DLC4B; lord++)
                {
                    int index = category * Slots + lord;
                    string audio = ReadString(image, pe, moduleBase, audioTable + index * sizeof(ulong));
                    string video = ReadString(image, pe, moduleBase, videoTable + index * sizeof(ulong));
                    if (!audio.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !video.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Lord source tables contain an unsupported filename contract.");
                    result[index] = new VanillaLordMessage((AILords)lord, (AILordMessageType)category, "fx\\speech\\" + audio, video,
                        checked((lord - (int)AILords.SK_RAT) * Categories + category));
                }
            return result;
        }

        private static void ValidateAddressing(ReadOnlySpan<byte> image, Anchors anchors, out int audio, out int video)
        {
            // LEA R15,[RIP+disp32] points at image base, so both table displacements are RVAs.
            if (NativePatternResolver.ResolveRelativeTarget(image, anchors.Base + 17, anchors.Base + 21) != 0)
                throw new InvalidOperationException("Lord media R15 anchor no longer denotes the module base.");
            int categoryByteStride = NativePatternResolver.ReadInt32(image, anchors.Generic + 3);
            int categorySlots = image[anchors.BuiltIn + 3];
            int textStride = image[anchors.BuiltIn + 6];
            if (categoryByteStride != Slots * sizeof(ulong) || categorySlots != Slots || textStride != Categories ||
                image[anchors.Base + 31] != (int)AILords.SK_DLC4B ||
                NativePatternResolver.ReadInt32(image, anchors.BuiltIn + 17) != (int)global::Enums.eTextSections.TEXT_SKIRMISH_SPEECH)
                throw new InvalidOperationException("Lord media table or localized-text indexing changed.");
            audio = NativePatternResolver.ReadInt32(image, anchors.Generic + 26);
            video = NativePatternResolver.ReadInt32(image, anchors.Generic + 39);
            if (audio != NativePatternResolver.ReadInt32(image, anchors.BuiltIn + 36) ||
                video != NativePatternResolver.ReadInt32(image, anchors.BuiltIn + 25))
                throw new InvalidOperationException("Independent Lord media table anchors disagree.");
        }

        private static void RequireData(NativePeImage pe, int rva, int length, string what)
        {
            if (pe.RequireMappedRange(rva, length, what).Executable)
                throw new InvalidOperationException(what + " points into executable code.");
        }

        private static string ReadString(ReadOnlySpan<byte> image, NativePeImage pe, ulong moduleBase, int pointerRva)
        {
            ulong pointer = 0;
            for (int i = 0; i < sizeof(ulong); i++) pointer |= (ulong)image[pointerRva + i] << (i * 8);
            if (pointer < moduleBase || pointer - moduleBase >= (ulong)image.Length)
                throw new InvalidOperationException("Lord media string pointer lies outside the native image.");
            int start = checked((int)(pointer - moduleBase));
            NativeSection section = pe.RequireMappedRange(start, 1, "Lord media string");
            if (section.Executable) throw new InvalidOperationException("Lord media string points into executable code.");
            int max = Math.Min(section.End - start, 512), length = 0;
            while (length < max && image[start + length] != 0)
            {
                byte value = image[start + length];
                if (value < 0x20 || value > 0x7E || value == '/' || value == '\\')
                    throw new InvalidOperationException("Lord media table entry is not an original ASCII filename.");
                length++;
            }
            if (length == 0 || length == max) throw new InvalidOperationException("Lord media filename is empty or unterminated.");
            return Encoding.ASCII.GetString(image.Slice(start, length).ToArray());
        }

        private static bool TryReadCache(string path, out Anchors anchors)
        {
            anchors = default;
            try
            {
                if (path == null || !File.Exists(path) || new FileInfo(path).Length != 16) return false;
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadInt32() != 0x314D4C41) return false;
                    anchors = new Anchors(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), "cache");
                    return true;
                }
            }
            catch { return false; }
        }

        private static void TryWriteCache(string path, Anchors anchors)
        {
            try
            {
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new BinaryWriter(File.Create(path)))
                { writer.Write(0x314D4C41); writer.Write(anchors.Base); writer.Write(anchors.Generic); writer.Write(anchors.BuiltIn); }
            }
            catch { /* A cache is an optional startup optimization, never a prerequisite. */ }
        }
    }
}
