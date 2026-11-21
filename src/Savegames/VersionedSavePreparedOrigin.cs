using System;
using System.Security.Cryptography;
using System.Threading;

namespace APIShared.Savegames
{
    // Internal metadata only. This process-lived registration does not enroll a
    // critical save writer, reserve an entry, or authorize native world access.
    internal sealed class VersionedSavePreparedSource
    {
        internal VersionedSavePreparedSource(VersionedSaveDataRegistry registry, VersionedSaveDataHandle handle,
            VersionedSaveScope scope, int currentVersion, int minimumReader)
        { Registry = registry; Handle = handle; Scope = scope; CurrentVersion = currentVersion; MinimumReader = minimumReader; }

        internal VersionedSaveDataRegistry Registry { get; }
        internal VersionedSaveDataHandle Handle { get; }
        internal VersionedSaveScope Scope { get; }
        internal int CurrentVersion { get; }
        internal int MinimumReader { get; }
        internal bool TryGetCurrent(VersionedSaveLoad receiver, out VersionedSavePreparedOrigin origin, out string reason) =>
            Registry.TryGetPreparedOrigin(this, receiver, out origin, out reason);
        internal bool TryValidateCompleted(VersionedSavePreparedOrigin origin, out string reason) =>
            Registry.TryValidatePreparedOrigin(this, origin, out reason);
    }

    // A retained token is never sufficient by itself: consumers must revalidate
    // through its exact source. The registry revokes it at the next load/unload,
    // on a failed callback, and when a nested load closes its delivery window.
    internal sealed class VersionedSavePreparedOrigin
    {
        private readonly byte[] digest;
        internal VersionedSavePreparedOrigin(VersionedSavePreparedSource source, long generation,
            VersionedSaveLoadStatus status, VersionedSaveLoadSource loadSource, byte[] original)
        {
            Source = source; Generation = generation; Status = status; LoadSource = loadSource;
            HasRecord = original != null;
            if (original != null && original.Length > VersionedSaveRecordCodec.MaximumRecordBytes)
            { UnavailableReason = "The original source record exceeds the bounded origin limit."; return; }
            try
            {
                using (var sha = SHA256.Create()) digest = sha.ComputeHash(original ?? Array.Empty<byte>());
                UnavailableReason = string.Empty;
            }
            catch (Exception error)
            { UnavailableReason = "The original source digest is unavailable: " + error.Message; }
        }

        internal VersionedSavePreparedSource Source { get; }
        internal long Generation { get; }
        internal VersionedSaveLoadStatus Status { get; }
        internal VersionedSaveLoadSource LoadSource { get; }
        internal bool HasRecord { get; }
        internal string UnavailableReason { get; }
        internal byte[] CopyOriginalRecordDigest() => digest == null ? null : (byte[])digest.Clone();
        internal bool IsReadable => digest != null &&
            (Status == VersionedSaveLoadStatus.Restored || Status == VersionedSaveLoadStatus.NoRecord);

        // Mutated only by this source's registry while holding its lock.
        internal VersionedSaveLoad Receiver { get; private set; }
        internal int CallbackThread { get; private set; }
        internal bool Active { get; private set; }
        internal bool Completed { get; private set; }
        private bool invalidated;
        internal void Activate(VersionedSaveLoad receiver)
        { if (!invalidated) { Receiver = receiver; CallbackThread = Thread.CurrentThread.ManagedThreadId; Active = true; } }
        internal void Finish(bool success)
        { Active = false; Completed = success && !invalidated; Receiver = null; CallbackThread = 0; }
        internal void Invalidate()
        { invalidated = true; Active = false; Completed = false; Receiver = null; CallbackThread = 0; }
    }

    internal sealed partial class VersionedSaveDataRegistry
    {
        private const int MaximumPreparedSources = 128;
        private int preparedSourceCount;
        [ThreadStatic] private static VersionedSavePreparedOrigin activePreparedOrigin;

        // Struct scope: an unused registry adds no callback context allocation.
        // Every registry-dispatched consumer callback masks an outer Prepared
        // delivery, so a nested capture/migration/Loaded callback cannot borrow it.
        private readonly struct PreparedDispatchScope : IDisposable
        {
            private readonly VersionedSavePreparedOrigin previous;
            internal PreparedDispatchScope(VersionedSavePreparedOrigin current)
            { previous = activePreparedOrigin; activePreparedOrigin = current; }
            public void Dispose() { activePreparedOrigin = previous; }
        }

        // Internal startup opt-in only; later NativeStorage enrollment must also
        // require SaveGamesAndMaps, qualified critical saving and reserved-marker
        // ownership. This method deliberately grants none of those authorities.
        internal bool TryTrackPreparedSource(string owner, VersionedSaveDataHandle handle,
            out VersionedSavePreparedSource source, out string reason)
        {
            source = null;
            lock (sync)
            {
                if (generation != 0 || windowOpen)
                { reason = "Prepared origins must be tracked before the first archive load."; return false; }
                Slot selected = null;
                foreach (Slot slot in slots)
                    if (ReferenceEquals(slot.Handle, handle)) { selected = slot; break; }
                if (selected == null || !string.Equals(owner, selected.Owner, StringComparison.Ordinal))
                { reason = "The exact owner-matching registered handle is required."; return false; }
                if (selected.Prepared == null)
                { reason = "The source has no Prepared callback."; return false; }
                if (selected.PreparedSource != null)
                { reason = "This source already tracks Prepared origins."; return false; }
                if (preparedSourceCount >= MaximumPreparedSources)
                { reason = "At most 128 sources may track Prepared origins."; return false; }
                source = new VersionedSavePreparedSource(this, handle, selected.Scope, selected.Current, selected.MinimumReader);
                selected.PreparedSource = source; preparedSourceCount++;
                reason = string.Empty; return true;
            }
        }

        private Slot FindPreparedSource(VersionedSavePreparedSource source)
        {
            if (source == null || !ReferenceEquals(source.Registry, this)) return null;
            foreach (Slot slot in slots)
                if (ReferenceEquals(slot.PreparedSource, source) && ReferenceEquals(slot.Handle, source.Handle)) return slot;
            return null;
        }

        internal bool TryGetPreparedOrigin(VersionedSavePreparedSource source, VersionedSaveLoad receiver,
            out VersionedSavePreparedOrigin origin, out string reason)
        {
            origin = null;
            lock (sync)
            {
                Slot slot = FindPreparedSource(source);
                VersionedSavePreparedOrigin candidate = receiver?.PreparedOrigin;
                if (slot == null || candidate == null || !ReferenceEquals(candidate.Source, source) ||
                    !ReferenceEquals(slot.PreparedOrigin, candidate) || !ReferenceEquals(candidate.Receiver, receiver) ||
                    !ReferenceEquals(activePreparedOrigin, candidate) || !candidate.Active ||
                    candidate.CallbackThread != Thread.CurrentThread.ManagedThreadId || !windowOpen || candidate.Generation != generation)
                { reason = "Only the exact receiver's active matching Prepared delivery may stage data."; return false; }
                if (!candidate.IsReadable)
                { reason = string.IsNullOrEmpty(candidate.UnavailableReason) ? "The source outcome cannot authorize preparation." : candidate.UnavailableReason; return false; }
                origin = candidate; reason = string.Empty; return true;
            }
        }

        internal bool TryValidatePreparedOrigin(VersionedSavePreparedSource source, VersionedSavePreparedOrigin origin, out string reason)
        {
            lock (sync)
            {
                Slot slot = FindPreparedSource(source);
                if (slot == null || origin == null || !ReferenceEquals(origin.Source, source) ||
                    !ReferenceEquals(slot.PreparedOrigin, origin) || origin.Generation != generation || !origin.Completed || !origin.IsReadable)
                { reason = "The exact source has no successfully completed current Prepared origin."; return false; }
                reason = string.Empty; return true;
            }
        }
    }
}
