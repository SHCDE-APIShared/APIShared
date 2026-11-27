# Mission observations

This guide distinguishes lifecycle notifications, managed readiness and protected
native-world observations. Consumers retain their own activation policy, native
access prerequisites and multiplayer authority.

## Choose an observation

| Need | API | Meaning |
|---|---|---|
| Initialization, completed start or logical end | `ModApiClient.TryGetMissionLifecycle` | Immutable notifications on the publisher thread; managed-ready start replay for late observers |
| First fresh PlayState after managed start | `ModApiClient.TryGetMissionPlayStateReadiness` | A session receipt, with Unity delivery; see [PlayState readiness](THIRD_PARTY_GUIDE.md#current-mission-playstate-readiness) |
| Coherent, known-mode metadata for a loaded and managed-ready native world | `ModApiClient.TryGetNativeWorldReads` | Explicit point-in-time read while the caller holds the actual engine monitor |
| Native map tick | `Simulation.SimulationClock` | Its own expected-mission and native-time contract; see [simulation ticks](THIRD_PARTY_GUIDE.md#native-simulation-ticks) |

Lifecycle registrations last for the process. Register on the Unity thread and
keep the registration's owner-local ID stable. Notifications are queued during
reentrant publication and isolate observer exceptions; no observer sort order is
guaranteed. Initialization phases do not replay. A late observer receives the
current managed-ready start, with `IsReplay` set. See the
[declarations](../src/Missions/MissionLifecycleContracts.cs) for phase meanings,
failure and end semantics.

A notification's `Context` describes that event. An earlier observer can replace
the mission before a later observer receives the original event. Neither the
queued event, the capability's managed `Current`, a cached `GameplayModeGate`,
nor PlayState readiness proves that old native objects remain current.

## Acquire protected native-world reads

Acquire after API publication, for example from `ModApiClient.WhenReady`, and
inspect the returned diagnostic. The managed mission lifecycle owner must be
available, and the installed game's engine monitor binding must validate.
Acquisition adds no hooks, subscriptions or polling. Retain the client for the
process; it is not disposable.

```csharp
NativeWorldReadClient reads;
if (!api.TryGetNativeWorldReads(out reads, out var diagnostic))
{
    Log(diagnostic.Reason);
    return;
}

NativeWorldReadSnapshot observed;
string reason;
lock (reads.SyncRoot)
{
    if (!reads.TryReadCurrent(out observed, out reason))
        return;
}
// These are historical facts from that read, suitable for diagnostics.
Log($"World {observed.WorldId}, mission {observed.Mission.SessionId}, mode {observed.Mode.Kind}");
```

Every read requires the calling thread to hold the exact `SyncRoot`. Reads do not
acquire a lock, dispatch, call consumers or look up units/native game state. They
compare the existing owner's volatile native-load receipt with its exact
managed-ready context and recheck both publications before returning.

Reads are unavailable before managed readiness, even after a `NativeLoaded`
notification. Missing, loading, failed, ended or changing publications return
false with a default result and a reason. Unknown modes and conflicting
customization origins also fail. Success returns an empty reason. Callers can
retry at their next existing observation boundary; this API adds no retry loop.

Known multiplayer is observable. A solo skirmish may have low-level networking
enabled while `IsRealMultiplayer` is false. Apply your own policy to the freshly
returned mode instead of treating a raw networking flag as a multiplayer policy.

## Refresh a retained observation

Use the overload taking a previous successful snapshot when a later read must
refer to the same native world:

```csharp
NativeWorldReadSnapshot fresh;
lock (reads.SyncRoot)
{
    if (!reads.TryReadCurrent(observed, out fresh, out reason))
        return;
}
// Use fresh.Mode for this observation; observed.Mode remains its old value.
```

The overload compares an opaque world identity, then returns fresh current
mission and mode metadata. Metadata can change while the world remains the same.
A same-session native reload creates a different identity and rejects the old
snapshot. Default snapshots fail. `WorldId` and `Mission.SessionId` are diagnostic
process-local numbers; they are not persistence, wire or cross-process tokens,
and numeric equality cannot replace the opaque check. Historical snapshots remain
immutable and may be retained as values.

## Limits and native ownership

A successful read is point-in-time evidence. It grants no mutation authority,
command transport, native pointer lifetime lease or permission to act on a
retained snapshot's mode. Existing lifecycle invalidation can occur outside the
engine monitor immediately after a read. Holding `SyncRoot` does not make every
load, reset, unload or foreign writer wait. Follow each native/shared operation's
own lifetime, thread and authority contract.

The service adds no synchronous before-reset callback, retirement lease or owner
for global unit-stat tables. It does not restore consumer-written defaults or
coordinate overlapping native table changes. Those need separately qualified
ownership and native boundaries; queued lifecycle callbacks cannot supply them.

## Implementation and verification

The existing mission lifecycle remains the sole native-world publisher and hook
owner. Successful native loads create a private world identity; existing
load/unload and mission retirement paths invalidate it. The reader adds no work
to those publishers and changes no existing notification timing. Acquisition
resolves the actual engine monitor once, and prepared readers use only immutable
metadata and reference checks. Successful reads allocate no managed objects.

The focused tests execute the production reader and lifecycle state for nested
and failed loads, reloads, queued replacement/replay, incoherent metadata and
publication races. A compiled installed-assembly check qualifies the retained
monitor's process-initialization assignment; an allocation fixture measures warmed
successful and rejected reads. These fixtures do not run a game load or qualify
arbitrary native memory access. See the [compiled example](../examples/ThirdPartyMod/MissionExample.cs)
and [public contracts](../src/Missions/NativeWorldReadContracts.cs).
