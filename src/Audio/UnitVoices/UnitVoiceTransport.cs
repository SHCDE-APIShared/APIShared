using CrusaderDE;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace APIShared.Audio
{
    // Binds the shared, last-writer speech filename to the exact immutable PlayState that
    // contains it. Tokens are transport-only and are restored before the state is published.
    internal static class UnitVoiceTransport
    {
        private const string Prefix = "fx\\speech\\__apiv_";
        private static readonly ConditionalWeakTable<EngineInterface.PlayState, UnitVoiceBinding> bindings =
            new ConditionalWeakTable<EngineInterface.PlayState, UnitVoiceBinding>();
        private static UnitVoicePending pending;
        private static long sequence;

        internal static string CreateToken(UnitVoiceCue cue, out long emissionId)
        {
            int ordinal = UnitVoiceCatalog.Ordinal(cue);
            if (ordinal < 0 || ordinal > ushort.MaxValue) throw new InvalidOperationException("Unit voice cue cannot be transported.");
            emissionId = Interlocked.Increment(ref sequence);
            return Prefix + ordinal.ToString("X4", CultureInfo.InvariantCulture) + "_" +
                unchecked((ulong)emissionId).ToString("X16", CultureInfo.InvariantCulture) + ".wav";
        }

        internal static void Publish(string token, string originalPath, UnitVoiceSource source, long emissionId)
        {
            // The native engine monitor serializes emission and CopyPlayStateStruct. Keep only
            // the final accepted unit voice: Vanilla also transports only the final filename.
            Volatile.Write(ref pending, new UnitVoicePending(token, originalPath, source, emissionId));
        }

        internal static bool TryDecodeToken(string text, out UnitVoiceCue cue, out long emissionId)
        {
            cue = null; emissionId = 0;
            if (text == null || text.Length != Prefix.Length + 4 + 1 + 16 + 4 ||
                !text.StartsWith(Prefix, StringComparison.Ordinal) ||
                text[Prefix.Length + 4] != '_' || !text.EndsWith(".wav", StringComparison.Ordinal)) return false;
            if (!int.TryParse(text.Substring(Prefix.Length, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int ordinal) ||
                !ulong.TryParse(text.Substring(Prefix.Length + 5, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong identifier) ||
                !UnitVoiceCatalog.TryGetOrdinal(ordinal, out cue)) return false;
            emissionId = unchecked((long)identifier);
            return true;
        }

        // Called by the validated CopyPlayStateStruct IL hook while EngineInterface.threadLock
        // is still held. No record is consumed here: several record-18 calls may share a state.
        internal static void BindPlayState(EngineInterface.PlayState state)
        {
            if (state == null || !TryDecodeToken(state.speechFileName, out UnitVoiceCue cue, out long id)) return;
            var value = Volatile.Read(ref pending);
            bool matched = value != null && value.EmissionId == id && string.Equals(value.Token, state.speechFileName, StringComparison.Ordinal);
            // Decode the catalog ordinal even without a snapshot. This guarantees restoration
            // after disable/end transitions or isolated capture failure, never a leaked token.
            state.speechFileName = matched ? value.OriginalPath : "fx\\speech\\" + cue.FileName;
            if (matched)
            {
                bindings.Remove(state);
                bindings.Add(state, new UnitVoiceBinding(value.Source, value.OriginalPath, value.EmissionId));
            }
        }

        // Recovery repairs transport only. A caller must not infer source provenance from it.
        internal static bool TryRecoverOriginalPath(string path, out string originalPath)
        {
            originalPath = path;
            if (!TryDecodeToken(path, out UnitVoiceCue cue, out long id)) return false;
            var value = Volatile.Read(ref pending);
            originalPath = value != null && value.EmissionId == id && string.Equals(value.Token, path, StringComparison.Ordinal)
                ? value.OriginalPath : "fx\\speech\\" + cue.FileName;
            return true;
        }

        internal static bool TryGetBinding(EngineInterface.PlayState state, out UnitVoiceBinding binding)
        { binding = null; return state != null && bindings.TryGetValue(state, out binding); }

        internal static bool TryGetSource(EngineInterface.PlayState state, out UnitVoiceSource source)
        {
            source = default(UnitVoiceSource);
            if (!TryGetBinding(state, out UnitVoiceBinding value)) return false;
            source = value.Source; return true;
        }

        internal static void ResetForTest()
        { Volatile.Write(ref pending, null); }

        private sealed class UnitVoicePending
        {
            internal readonly string Token, OriginalPath;
            internal readonly UnitVoiceSource Source;
            internal readonly long EmissionId;
            internal UnitVoicePending(string token, string path, UnitVoiceSource source, long id)
            { Token = token; OriginalPath = path; Source = source; EmissionId = id; }
        }
    }

    internal sealed class UnitVoiceBinding
    {
        internal readonly UnitVoiceSource Source;
        internal readonly string OriginalPath;
        internal readonly long EmissionId;
        internal UnitVoiceBinding(UnitVoiceSource source, string path, long id)
        { Source = source; OriginalPath = path; EmissionId = id; }
    }
}
