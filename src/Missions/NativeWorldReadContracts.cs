using APIShared.GameModes;
using System;

namespace APIShared
{
    /// <summary>Point-in-time, managed-ready evidence for one loaded native world. Retaining this value
    /// does not keep native memory alive or grant permission to mutate it. Re-read under the client's
    /// <see cref="NativeWorldReadClient.SyncRoot"/> before using fresh mission or mode evidence.</summary>
    public readonly struct NativeWorldReadSnapshot
    {
        internal readonly MissionLifecycleState.NativeWorldState World;

        internal NativeWorldReadSnapshot(MissionLifecycleState.NativeWorldState world, MissionContext mission)
        {
            World = world;
            Mission = mission;
        }

        /// <summary>Process-local native-load identity; zero for a default value. Not a save, network or
        /// cross-process identifier. A reload may change this while Mission.SessionId stays the same.</summary>
        public long WorldId => World?.Sequence ?? 0;
        /// <summary>Immutable managed-ready context captured by this read; null for a default value.
        /// Metadata may subsequently change without replacing the native world.</summary>
        public MissionContext Mission { get; }
        /// <summary>Mode evidence from this read, including known multiplayer. This value is not mutation
        /// authority or a transport policy; an old snapshot's mode must not authorize a later operation.</summary>
        public GameModeSnapshot Mode => Mission?.Mode ?? default;
    }

    /// <summary>Protected observations of the existing mission owner's native-world publication. No hooks,
    /// polling, dispatch or native memory access are added. This client is process-lived and not disposable.</summary>
    public sealed class NativeWorldReadClient
    {
        private readonly NativeWorldReadReader reader;
        internal NativeWorldReadClient(NativeWorldReadReader reader)
        {
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>The validated game engine monitor. Callers must already hold this exact object on the
        /// calling thread for every read. Acquiring it does not grant mutation authority or make native
        /// load/reset paths wait; invalidation can occur immediately after a successful read.</summary>
        public object SyncRoot => reader.SyncRoot;

        /// <summary>Any thread holding SyncRoot: returns a coherent managed-ready native world with known,
        /// nonconflicting mode evidence. Returns false/default and a reason for an unheld monitor, loading,
        /// failed/ended world, unknown/conflicting mode or publication change during the read. No locking,
        /// callbacks or dispatch occurs, and no read is available before managed readiness. Success has an
        /// empty reason. A returned receipt is point-in-time evidence, not a native pointer lifetime lease.</summary>
        public bool TryReadCurrent(out NativeWorldReadSnapshot current, out string reason) =>
            reader.TryRead(default, false, out current, out reason);

        /// <summary>As above, additionally requires the same opaque native world as a previously successful
        /// read. Default, replaced and reloaded receipts fail. Success returns fresh mission/mode evidence,
        /// which may differ from expectedWorld even within the same world. Do not authorize work using the
        /// retained value's Mode. Numeric WorldId or SessionId equality alone cannot satisfy this check.</summary>
        public bool TryReadCurrent(NativeWorldReadSnapshot expectedWorld,
            out NativeWorldReadSnapshot current, out string reason) =>
            reader.TryRead(expectedWorld, true, out current, out reason);
    }
}
