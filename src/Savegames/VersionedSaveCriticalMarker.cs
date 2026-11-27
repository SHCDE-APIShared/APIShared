using System;
using System.Threading;

namespace APIShared.Savegames
{
    internal sealed partial class VersionedSaveDataRegistry
    {
        // A consumer may mutate its copied Prepared payload. Confirmation must
        // instead read the original stored envelope and receive another copy.
        // Legacy readers and migrations cannot synthesize a stored marker.
        // Active staging requires the exact current TLS/thread delivery. Later
        // cold validation additionally waits for EndLoad (including Loaded/Post
        // failures), not merely the completion of the Prepared callback.
        // This is provenance metadata, never native-world publication authority.
        internal bool TryReadCriticalPreparedMarker(VersionedSaveCriticalSource source,
            VersionedSavePreparedOrigin origin, out byte[] marker, out bool present, out string reason)
        {
            marker = null; present = false;
            lock (sync)
            {
                Slot slot = FindPreparedSource(source?.PreparedSource);
                if (slot == null || !ReferenceEquals(slot.CriticalSource, source) || origin == null ||
                    !ReferenceEquals(slot.PreparedOrigin, origin) || origin.Generation != generation || !origin.IsReadable)
                { reason = "The exact readable current enrolled source origin is required."; return false; }
                bool active = windowOpen && origin.Active && ReferenceEquals(activePreparedOrigin, origin) &&
                    origin.CallbackThread == Thread.CurrentThread.ManagedThreadId;
                bool finalized = !windowOpen && origin.Completed;
                if (!active && !finalized)
                { reason = "Read the marker in its exact active Prepared delivery or after the complete load window."; return false; }
                VersionedSaveLoad load = slot.PreparedOutcome?.Load;
                if (load == null || load.Status != origin.Status)
                { reason = "The registry-owned prepared outcome changed."; return false; }
                byte[] original = slot.PreparedOutcome.LoadedBytes;
                if (original == null || !VersionedSaveRecordCodec.HasSignature(original))
                { reason = string.Empty; return true; }
                var decoded = VersionedSaveRecordCodec.Decode(original, source.PreparedSource.CurrentVersion);
                if (decoded.Status != VersionedSaveRecordCodec.DecodeStatus.Valid)
                { reason = "The original enrolled source envelope cannot be read."; return false; }
                if (decoded.Entries.TryGet(source.ReservedEntry, out byte[] saved))
                {
                    if (decoded.RecordVersion < source.MinimumCutoverReader || decoded.MinimumReaderVersion < source.MinimumCutoverReader)
                    { reason = "The stored cutover marker has an older compatibility envelope."; return false; }
                    marker = (byte[])saved.Clone(); present = true;
                }
                reason = string.Empty; return true;
            }
        }
    }
}
