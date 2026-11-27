using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Presentation
{
    internal readonly struct NotificationEmission
    {
        internal readonly ulong Ticket;
        internal readonly int Header, End, Section, TextIndex, Epoch;
        internal readonly string Subtitle;
        internal readonly bool SubtitleKnown, HasAudio, HasVideo;

        internal NotificationEmission(ulong ticket, int header, int end, int section, int textIndex,
            string subtitle, bool subtitleKnown, bool hasAudio, bool hasVideo, int epoch = 0)
        {
            Ticket = ticket;
            Epoch = epoch;
            Header = header;
            End = end;
            Section = section;
            TextIndex = textIndex;
            Subtitle = subtitle;
            SubtitleKnown = subtitleKnown;
            HasAudio = hasAudio;
            HasVideo = hasVideo;
        }
    }

    internal readonly struct NotificationPresentationRecord
    {
        internal readonly ulong Ticket;
        internal readonly int HeaderIndex, AudioIndex, VideoIndex, Section, TextIndex, Epoch;
        internal readonly string Subtitle;
        internal readonly bool SubtitleKnown, HasVideo;

        internal NotificationPresentationRecord(NotificationEmission emission, int audioIndex, int videoIndex)
        {
            Ticket = emission.Ticket;
            Epoch = emission.Epoch;
            HasVideo = emission.HasVideo;
            HeaderIndex = emission.Header;
            AudioIndex = audioIndex;
            VideoIndex = videoIndex;
            Section = emission.Section;
            TextIndex = emission.TextIndex;
            Subtitle = emission.Subtitle;
            SubtitleKnown = emission.SubtitleKnown;
        }
    }

    // The staging area is shared between coordinated engine callers. Only the sealed batch
    // is visible from the publication hook; current/next staging are distinct even during Drain.
    internal static class NotificationTransport
    {
        internal const int MaxRecordCount = 700000;
        private const int Words = LordMessageGroups.Words;
        private const int MaxEmissions = 32;
        private static readonly ConditionalWeakTable<EngineInterface.PlayState, Batch> bindings =
            new ConditionalWeakTable<EngineInterface.PlayState, Batch>();
        private static readonly List<NotificationEmission> staging = new List<NotificationEmission>(4);
        private static readonly List<NotificationEmission> live = new List<NotificationEmission>(4);
        private static int stagingEpoch, liveEpoch, sealedCount;
        private static IntPtr destination;
        private static MemoryBuffers.MemBuffer liveBuffer;
        private static bool sealedBatch;

        internal static bool Observe(int epoch, IntPtr actualDestination, bool isStaging, NotificationEmission emission)
        {
            if (epoch != NotificationNativeRuntime.Epoch) return false;
            if (isStaging)
            {
                if (stagingEpoch != epoch)
                {
                    staging.Clear();
                    stagingEpoch = epoch;
                }
                return Append(staging, emission);
            }
            return !sealedBatch && liveBuffer != null && liveEpoch == epoch && actualDestination == destination &&
                Append(live, emission);
        }

        private static bool Append(List<NotificationEmission> records, NotificationEmission emission)
        {
            if (records.Count >= MaxEmissions || (records.Count > 0 && records[records.Count - 1].End > emission.Header))
                return false;
            records.Add(emission);
            return true;
        }

        private static unsafe void BeginTick(MemoryBuffers.MemBuffer buffer)
        {
            if (!NotificationNativeRuntime.IsReady || !NotificationNativeRuntime.OwnsEngine) return;
            try
            {
                bool interrupted = liveBuffer != null && !sealedBatch;
                ClearLive();
                if (interrupted || !NotificationNativeRuntime.IsStagingDestination())
                {
                    NotificationNativeRuntime.Invalidate();
                    return;
                }
                if (buffer == null || buffer.memory == null || buffer.memory.Length / Words > MaxRecordCount)
                    return;
                int epoch = NotificationNativeRuntime.Epoch;
                if (staging.Count > 0)
                {
                    int count = NotificationNativeRuntime.ReadStagingCount();
                    if (stagingEpoch == epoch && count >= staging[staging.Count - 1].End && count <= buffer.memory.Length / Words)
                        live.AddRange(staging);
                    staging.Clear();
                }
                fixed (short* storage = buffer.memory) destination = new IntPtr(storage);
                if (destination == IntPtr.Zero) return;
                liveEpoch = epoch;
                liveBuffer = buffer;
            }
            catch (Exception error)
            {
                ClearLive();
                NotificationNativeRuntime.TransportFailed(error);
            }
        }

        private static void SealTick(int count)
        {
            if (!NotificationNativeRuntime.IsReady || !NotificationNativeRuntime.OwnsEngine) return;
            try
            {
                NotificationNativeRuntime.ReconcileActivePresentation();
                if (liveBuffer == null) return;
                if (liveEpoch != NotificationNativeRuntime.Epoch || !NotificationNativeRuntime.IsStagingDestination() ||
                    NotificationNativeRuntime.ReadStagingCount() != 0 || count < 0 || count > liveBuffer.memory.Length / Words ||
                    (live.Count > 0 && live[live.Count - 1].End > count))
                {
                    ClearLive();
                    NotificationNativeRuntime.Invalidate();
                    return;
                }
                sealedBatch = true;
                sealedCount = count;
                destination = IntPtr.Zero;
            }
            catch (Exception error)
            {
                ClearLive();
                NotificationNativeRuntime.TransportFailed(error);
            }
        }

        internal static void BeforeBufferPublication(MemoryBuffers.MemBuffer buffer)
        {
            if (liveBuffer == null || !NotificationNativeRuntime.IsReady || !NotificationNativeRuntime.OwnsEngine ||
                !ReferenceEquals(buffer, liveBuffer))
                return;
            try
            {
                if (!sealedBatch || live.Count == 0 || liveEpoch != NotificationNativeRuntime.Epoch ||
                    buffer.gameState == null || buffer.numTiles != sealedCount) return;
                var result = new NotificationPresentationRecord[live.Count];
                for (int i = 0; i < live.Count; i++)
                    if (!TryValidateRecord(buffer.memory, sealedCount, live[i], out result[i]))
                    {
                        NotificationNativeRuntime.Invalidate();
                        return;
                    }

                // PlayState stores a single path per media type, which can belong to a
                // subsequent non-notification record. Inspect only the tail after the
                // earliest observed media record in this validated frame.
                LimitMediaOwnership(buffer.memory, sealedCount, live, result);
                bindings.Remove(buffer.gameState);
                bindings.Add(buffer.gameState, new Batch(buffer.memory, liveEpoch, sealedCount, result));
            }
            catch (Exception error) { NotificationNativeRuntime.TransportFailed(error); }
            finally { ClearLive(); }
        }

        internal static void LimitMediaOwnership(short[] words, int count, List<NotificationEmission> emissions,
            NotificationPresentationRecord[] records)
        {
            int audioStart = count, videoStart = count;
            foreach (var record in records)
            {
                if (record.AudioIndex >= 0) audioStart = Math.Min(audioStart, record.AudioIndex);
                if (record.VideoIndex >= 0) videoStart = Math.Min(videoStart, record.VideoIndex);
            }

            int audio = -1, video = -1;
            for (int index = count - 1; index >= Math.Min(audioStart, videoStart); index--)
            {
                int start = index * Words;
                // PlayState publishes one filename for all managed speech channels.
                if (index >= audioStart && words[start] == 18)
                {
                    audio = index;
                    audioStart = count;
                }
                if (index >= videoStart && words[start] == 21)
                {
                    if (words[start + 3] >= 0) video = index;
                    videoStart = count;
                }
            }
            for (int i = 0; i < records.Length; i++)
            {
                var record = records[i];
                int ownedAudio = record.AudioIndex == audio ? audio : -1;
                int ownedVideo = record.VideoIndex == video ? video : -1;
                if (ownedAudio != record.AudioIndex || ownedVideo != record.VideoIndex)
                    records[i] = new NotificationPresentationRecord(emissions[i], ownedAudio, ownedVideo);
            }
        }

        internal static bool TryValidateRecord(short[] words, int count, NotificationEmission emission, out NotificationPresentationRecord record)
        {
            record = default;
            if (words == null || count < 0 || count > words.Length / Words || emission.Header < 0 ||
                emission.End <= emission.Header || emission.End > count) return false;
            int head = emission.Header * Words;
            if (words[head] != 20 || words[head + 11] != emission.Section || words[head + 12] != emission.TextIndex ||
                words[head + 13] != (emission.HasAudio ? 1 : 0) || words[head + 14] != (emission.HasVideo ? 1 : 0))
                return false;
            int audio = -1, video = -1;
            for (int index = emission.Header + 1; index < emission.End; index++)
            {
                int start = index * Words;
                switch (words[start])
                {
                    case 18 when words[start + 12] == 1:
                        if (audio >= 0) return false;
                        audio = index;
                        break;
                    case 21 when words[start + 3] >= 0:
                        if (video >= 0) return false;
                        video = index;
                        break;
                    case 20:
                        return false;
                }
            }
            if ((audio >= 0) != emission.HasAudio || (video >= 0) != emission.HasVideo) return false;
            record = new NotificationPresentationRecord(emission, audio, video);
            return true;
        }

        private static void ClearLive()
        {
            live.Clear();
            liveBuffer = null;
            destination = IntPtr.Zero;
            sealedBatch = false;
            sealedCount = 0;
        }

        private static bool TryBatch(EngineInterface.PlayState state, out Batch batch)
        {
            batch = null;
            return state != null && NotificationNativeRuntime.IsReady && bindings.TryGetValue(state, out batch) &&
                batch.Epoch == NotificationNativeRuntime.Epoch;
        }

        internal static bool HasRecords(EngineInterface.PlayState state) => TryBatch(state, out _);

        internal static bool MatchesBuffer(EngineInterface.PlayState state, short[] records, int count) =>
            TryBatch(state, out var batch) && ReferenceEquals(batch.Words, records) && batch.Count == count;

        internal static bool TryGet(EngineInterface.PlayState state, int index, out NotificationPresentationRecord record)
        {
            record = default;
            if (index < 0 || !TryBatch(state, out var batch)) return false;
            foreach (var item in batch.Records)
            {
                if (item.HeaderIndex != index && item.AudioIndex != index && item.VideoIndex != index) continue;
                record = item;
                return true;
            }
            return false;
        }

        internal static void InstallTickEdit(ILContext il)
        {
            Instruction tick = null;
            VariableDefinition buffer = null;
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                if (!(instruction.Operand is MethodReference method)) continue;
                if (method.DeclaringType.FullName == typeof(EngineInterface).FullName && method.Name == "DLL_RunTick")
                {
                    if (tick != null || method.HasThis || method.ReturnType.MetadataType != MetadataType.Int32 ||
                        method.Parameters.Count != 38 || method.Parameters[0].ParameterType.FullName != "System.Int16*")
                        throw InvalidTick("native call");
                    tick = instruction;
                }
                if (method.DeclaringType.FullName == typeof(MemoryBuffers).FullName && method.Name == "getFreeBuffer")
                {
                    if (buffer != null || !instruction.Next.MatchStloc(out int index))
                        throw InvalidTick("buffer reservation");
                    buffer = il.Body.Variables[index];
                }
            }
            if (tick == null || buffer == null || buffer.VariableType.FullName != "MemoryBuffers/MemBuffer" ||
                !tick.Next.MatchStloc(out _) || IsLabelTarget(il, tick) || IsLabelTarget(il, tick.Next))
                throw InvalidTick("buffer or invocation boundaries");

            var cursor = new ILCursor(il);
            cursor.Goto(tick, MoveType.Before);
            cursor.Emit(OpCodes.Ldloc, buffer);
            cursor.Emit(OpCodes.Call, typeof(NotificationTransport).GetMethod(nameof(BeginTick), BindingFlags.Static | BindingFlags.NonPublic));
            cursor.Goto(tick, MoveType.After);
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Call, typeof(NotificationTransport).GetMethod(nameof(SealTick), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool IsLabelTarget(ILContext il, Instruction target)
        {
            foreach (var instruction in il.Body.Instructions)
            {
                if (instruction.Operand == target || (instruction.Operand is ILLabel label && label.Target == target)) return true;
                if (instruction.Operand is ILLabel[] labels)
                    foreach (var item in labels) if (item.Target == target) return true;
                if (instruction.Operand is Instruction[] targets)
                    foreach (var item in targets) if (item == target) return true;
            }
            foreach (var handler in il.Body.ExceptionHandlers)
                if (handler.TryStart == target || handler.TryEnd == target || handler.HandlerStart == target ||
                    handler.HandlerEnd == target || handler.FilterStart == target) return true;
            return false;
        }

        private static InvalidOperationException InvalidTick(string detail) =>
            new InvalidOperationException("Notification tick IL contract changed: " + detail + ".");

        private sealed class Batch
        {
            internal readonly short[] Words;
            internal readonly int Epoch, Count;
            internal readonly NotificationPresentationRecord[] Records;
            internal Batch(short[] words, int epoch, int count, NotificationPresentationRecord[] records)
            { Words = words; Epoch = epoch; Count = count; Records = records; }
        }
    }
}
