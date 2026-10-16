using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.NativeBrokers
{
    // Registrations of one broker entry. Writes are rare and serialize on one lock; dispatch reads one
    // immutable snapshot without locks or allocations. Order: ascending order, then ordinal owner GUID,
    // then ordinal registration ID, so the result does not depend on registration timing.
    // ensureReady runs under the lock before the first registration is added and installs the entry
    // (null = ready, otherwise the refusal reason). demandChanged republishes the native demand byte: it
    // runs under the lock after the snapshot that contains a new registration is published, and after
    // the snapshot without the last active registration is published, so no active registration meets
    // a false zero.
    internal sealed class NativeBrokerRegistry<THandler> where THandler : class
    {
        internal sealed class Entry
        {
            internal long Failures;
            internal readonly NativeBrokerRegistration Handle;
            internal readonly THandler Pre, Post;
            internal Entry(NativeBrokerRegistration handle, THandler pre, THandler post) { Handle = handle; Pre = pre; Post = post; }
        }

        internal sealed class Snapshot
        {
            internal static readonly Snapshot Empty = new Snapshot(Array.Empty<Entry>(), Array.Empty<Entry>());
            internal readonly Entry[] Pre, Post;
            internal Snapshot(Entry[] pre, Entry[] post) { Pre = pre; Post = post; }
        }

        private readonly object sync = new object();
        private readonly string phaseName;
        private readonly Func<string> ensureReady;
        private readonly Action<bool> demandChanged;
        private Entry[] entries = Array.Empty<Entry>();
        private Snapshot snapshot = Snapshot.Empty;
        private long failures;

        internal NativeBrokerRegistry(string phaseName, Func<string> ensureReady, Action<bool> demandChanged)
        {
            this.phaseName = phaseName;
            this.ensureReady = ensureReady ?? throw new ArgumentNullException(nameof(ensureReady));
            this.demandChanged = demandChanged ?? throw new ArgumentNullException(nameof(demandChanged));
        }

        internal Snapshot Current => Volatile.Read(ref snapshot);
        internal long Failures => Interlocked.Read(ref failures);
        internal int Count { get { lock (sync) return entries.Length; } }

        internal bool TryAdd(string ownerGuid, string registrationId, int order, THandler pre, THandler post,
            out NativeBrokerRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || (pre == null && post == null))
            { reason = "Owner GUID, registration ID and at least one callback are required."; return false; }
            lock (sync)
            {
                foreach (Entry existing in entries)
                    if (existing.Handle.OwnerGuid == ownerGuid && existing.Handle.RegistrationId == registrationId)
                    { reason = "This owner already registered that ID."; return false; }
                string refusal;
                try { refusal = ensureReady(); }
                catch (Exception ex) { refusal = "Broker installation failed: " + ex.Message; }
                if (refusal != null) { reason = refusal; return false; }
                var handle = new NativeBrokerRegistration(ownerGuid, registrationId, order, Remove);
                var next = new Entry[entries.Length + 1];
                Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(handle, pre, post);
                Array.Sort(next, Compare);
                Publish(next);
                demandChanged(true);
                registration = handle;
            }
            reason = string.Empty;
            return true;
        }

        // Called once per handle by NativeBrokerRegistration.Dispose, after it cleared ActiveValue: a
        // dispatch still holding the previous snapshot skips the entry from then on.
        private void Remove(NativeBrokerRegistration handle)
        {
            lock (sync)
            {
                int index = Array.FindIndex(entries, entry => ReferenceEquals(entry.Handle, handle));
                if (index < 0) return;
                var next = new Entry[entries.Length - 1];
                Array.Copy(entries, next, index);
                Array.Copy(entries, index + 1, next, index, next.Length - index);
                Publish(next);
                if (next.Length == 0) demandChanged(false);
            }
        }

        private void Publish(Entry[] next)
        {
            int pre = 0, post = 0;
            foreach (Entry entry in next) { if (entry.Pre != null) pre++; if (entry.Post != null) post++; }
            var preEntries = new Entry[pre];
            var postEntries = new Entry[post];
            pre = post = 0;
            foreach (Entry entry in next)
            {
                if (entry.Pre != null) preEntries[pre++] = entry;
                if (entry.Post != null) postEntries[post++] = entry;
            }
            entries = next;
            Volatile.Write(ref snapshot, new Snapshot(preEntries, postEntries));
        }

        private static int Compare(Entry left, Entry right)
        {
            int comparison = left.Handle.Order.CompareTo(right.Handle.Order);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Handle.OwnerGuid, right.Handle.OwnerGuid);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Handle.RegistrationId, right.Handle.RegistrationId);
        }

        // Callback failures are counted; the first, second, fourth, ... failure of each registration is
        // reported. Reporting must never compromise the native caller or another consumer.
        internal void Report(Entry entry, string phase, Exception error)
        {
            Interlocked.Increment(ref failures);
            long count = Interlocked.Increment(ref entry.Failures);
            if ((count & (count - 1)) != 0) return;
            try { InterceptionDiagnostics.Report(entry.Handle.OwnerGuid + "/" + entry.Handle.RegistrationId + " " + phaseName + " " + phase, error); }
            catch { }
        }
    }

    // Persistent managed path of 0xE2610 while demand exists: the native guard enters Dispatch directly
    // (it is the rooted thunk). Pre callbacks run in order until one overrides; the original runs exactly
    // once unless overridden; Post callbacks then run in order. Calls made from inside a callback through
    // the live entry (or by native code the callback calls) dispatch again, as with a private detour.
    internal sealed class PclReachabilityDispatcher
    {
        internal readonly NativeBrokerRegistry<PclReachabilityHandler> Registry;
        // The trampoline; written once before the first nonzero demand.
        internal PclReachabilityNative Original;
        internal PclReachabilityDispatcher(Func<string> ensureReady, Action<bool> demandChanged)
        { Registry = new NativeBrokerRegistry<PclReachabilityHandler>("PclReachability", ensureReady, demandChanged); }

        // Without .locals init: the call struct is fully assigned by its constructor.
        [System.Runtime.CompilerServices.SkipLocalsInit]
        internal int Dispatch(IntPtr manager, int player, int firstPcl, int secondPcl, int mode)
        {
            NativeBrokerRegistry<PclReachabilityHandler>.Snapshot snapshot = Registry.Current;
            var call = new PclReachabilityCall(manager, player, firstPcl, secondPcl, mode);
            NativeBrokerRegistry<PclReachabilityHandler>.Entry[] pre = snapshot.Pre;
            for (int index = 0; index < pre.Length; index++)
            {
                NativeBrokerRegistry<PclReachabilityHandler>.Entry entry = pre[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                try { entry.Pre(ref call); }
                catch (Exception ex) { call.ClearOverride(); Registry.Report(entry, "Pre", ex); }
                if (call.IsOverridden) break;
            }
            // The original lives outside callback error handling: it is the native function itself.
            if (!call.IsOverridden) call.CompleteOriginal(Original(manager, player, firstPcl, secondPcl, mode));
            call.EnterPost();
            NativeBrokerRegistry<PclReachabilityHandler>.Entry[] post = snapshot.Post;
            for (int index = 0; index < post.Length; index++)
            {
                NativeBrokerRegistry<PclReachabilityHandler>.Entry entry = post[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                int before = call.Result;
                try { entry.Post(ref call); }
                catch (Exception ex) { call.RestoreResult(before); Registry.Report(entry, "Post", ex); }
            }
            return call.Result;
        }
    }

    // Persistent managed path of 0xD90D0 while demand exists: the original runs first, then every Post.
    internal sealed class TileRefreshDispatcher
    {
        internal readonly NativeBrokerRegistry<TileRefreshHandler> Registry;
        // The trampoline; written once before the first nonzero demand.
        internal TileRefreshNative Original;
        internal TileRefreshDispatcher(Func<string> ensureReady, Action<bool> demandChanged)
        { Registry = new NativeBrokerRegistry<TileRefreshHandler>("TileRefresh", ensureReady, demandChanged); }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        internal void Dispatch(IntPtr manager, int tileY, int tileId)
        {
            Original(manager, tileY, tileId);
            NativeBrokerRegistry<TileRefreshHandler>.Entry[] post = Registry.Current.Post;
            if (post.Length == 0) return;
            var call = new TileRefreshCall(manager, tileY, tileId);
            for (int index = 0; index < post.Length; index++)
            {
                NativeBrokerRegistry<TileRefreshHandler>.Entry entry = post[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                try { entry.Post(call); }
                catch (Exception ex) { Registry.Report(entry, "Post", ex); }
            }
        }
    }
}
