using System;
using System.Threading;

namespace APIShared.Audio
{
    // The marker carries context, never a volume decision or live native identity.
    internal static class SoundContextPolicy
    {
        internal const int RecordWords = 21;
        private static int ready;
        internal static bool Active => Volatile.Read(ref ready) != 0;
        // Readiness is one input of the native demand bytes; republish them on every change.
        internal static void SetReady(bool value) { Volatile.Write(ref ready, value ? 1 : 0); SoundNativeDemand.Refresh(); }
        internal static bool MatchesSource(SoundSource source, int type, int state, short unitX, short unitY, short x, short y, int sound, int flag)
            => source != null && source.UnitType == type && source.Flag == flag && source.Situation(sound) != SoundSituation.Unknown &&
                unitX == x && unitY == y && (source.ReturnRva != 0x1700D2 || state == 1);
        internal static bool TryMark(Span<short> record, SoundSource source)
        {
            if (!Active || source == null || record.Length != RecordWords || record[0] != 16 ||
                record[11] != -1 || record[12] != -1 || record[13] != source.Flag ||
                source.Situation(record[3]) == SoundSituation.Unknown) return false;
            record[11] = SoundSources.MarkerFor(source, record[3]); return true;
        }
        internal static SoundContext Read(short[] buffer, int index, int sound, float volume, float pan)
        {
            if (buffer == null || index < 0 || index >= buffer.Length / RecordWords)
                return new SoundContext(sound, volume, pan, SoundPlaybackKind.Unknown, SoundSituation.Unknown, null, null);
            int offset = index * RecordWords;
            if (buffer[offset] != 16 || buffer[offset + 3] != sound)
                return new SoundContext(sound, volume, pan, SoundPlaybackKind.Unknown, SoundSituation.Unknown, null, null);
            bool spatial = buffer[offset + 11] < 0 || buffer[offset + 12] < 0;
            SoundSource source = null;
            bool known = Active && spatial && buffer[offset + 12] == -1 && SoundSources.TryDecode(buffer[offset + 11], sound, buffer[offset + 13], out source);
            return new SoundContext(sound, volume, pan,
                !spatial ? SoundPlaybackKind.Explicit : buffer[offset + 13] == 0 ? SoundPlaybackKind.Spatial : SoundPlaybackKind.SpatialUnattenuated,
                known ? source.Situation(sound) : SoundSituation.Unknown,
                spatial ? buffer[offset + 1] : (int?)null, spatial ? buffer[offset + 2] : (int?)null,
                known ? source.Evidence(sound) : SituationEvidence.Unknown, known ? source.UnitType : (int?)null, known ? source.Key : null);
        }
    }
}
