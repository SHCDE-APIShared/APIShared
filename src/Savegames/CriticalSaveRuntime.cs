using System;
using System.Threading;
using APIShared.Economy;
using APIShared.Internal;

namespace APIShared.Savegames
{
    // The public client owns no hook. Enrollment binds metadata to the existing
    // PlayerViewSaveScope/critical writer, and never calls consumer code here.
    internal static class CriticalSaveRuntime
    {
        internal static bool TryEnroll(string owner, VersionedSaveDataHandle handle, CriticalSaveOptions options,
            out CriticalSaveParticipant participant, out string reason)
        {
            participant = null;
            if (!UnityMainThreadDispatch.IsMainThread || ApiSharedRuntime.ProcessInstance.State != NativeApiState.Ready)
            { reason = "Enroll during initialized Unity startup."; return false; }
            object gate = WorkerProductionWorldSafety.EngineGate;
            if (gate == null || !WorkerProductionCriticalSaveRuntime.IsAvailable)
            { reason = "The qualified shared critical writer is unavailable."; return false; }
            lock (gate)
            {
                if (MissionLifecycleService.NativeWorld != null || MissionLifecycleService.ActiveContext != null)
                { reason = "Enroll before the first world."; return false; }
                var registry = VersionedSaveDataRuntime.CurrentRegistry;
                if (registry == null)
                { reason = "Register the exact versioned record first."; return false; }
                if (!registry.TryPrepareCriticalParticipant(owner, handle, options, out var plan, out reason)) return false;
                if (plan.RequiresStorage && !NativeStorageRuntime.Instance.CanRequestCriticalSaveEnrollment(out reason)) return false;
                if (!registry.TryCommitCriticalParticipant(plan, out participant, out reason)) return false;
                // No fallible operations/callbacks remain after metadata commits.
                // Its permanent storage prerequisite refuses the writer until the
                // shared source actually completes startup, including startup failure.
                if (plan.RequiresStorage) NativeStorageRuntime.Instance.CommitCriticalSaveEnrollmentRequest();
                return true;
            }
        }

        internal static bool TryConfirmCreatedWorld(string owner, CriticalSaveParticipant participant,
            MissionContext context, out string reason)
        {
            if (participant == null || !string.Equals(owner, participant.OwnerGuid, StringComparison.Ordinal) ||
                !ReferenceEquals(participant.Registry, VersionedSaveDataRuntime.CurrentRegistry))
            { reason = "The exact live owner-bound participant is required."; return false; }
            object gate = WorkerProductionWorldSafety.EngineGate;
            if (gate == null || !UnityMainThreadDispatch.IsMainThread || !Monitor.IsEntered(gate))
            { reason = "Confirm a created world on the Unity caller holding the actual engine monitor."; return false; }
            lock (gate)
            {
                var clock = MissionLifecycleService.NativeClock;
                var world = MissionLifecycleService.NativeWorld;
                if (context == null || context.SessionId <= 0 || context.StartKind != MissionStartKind.EditorCreated ||
                    clock == null || clock.Context.StartKind != MissionStartKind.EditorCreated ||
                    clock.Context.Mode.IsRealMultiplayer != false || world == null ||
                    !ReferenceEquals(clock.World, world) || context.SessionId != world.Mission ||
                    context.SessionId != (MissionLifecycleService.ActiveContext?.SessionId ?? 0) ||
                    WorkerProductionWorldSafety.IsFaulted)
                { reason = "Only the exact current explicit single-player/editor-created world may confirm empty source state."; return false; }
                return participant.Registry.TryAuthorizeCriticalCreatedWorld(participant.PreparedSource, world, context.SessionId, out reason);
            }
        }

        // Kept behind the demanded transaction branch. A consumer may synchronously
        // start another load while preparing; pin both native world and source generation.
        internal static void RequireCurrentSaveWorld(object world, long generation, bool validateSources = false)
        {
            var clock = MissionLifecycleService.NativeClock;
            object gate = WorkerProductionWorldSafety.EngineGate;
            if (gate == null || !Monitor.IsEntered(gate) || clock == null ||
                !ReferenceEquals(clock.World, MissionLifecycleService.NativeWorld) ||
                !IsSameSaveWorld(world, generation, clock.World, WorkerProductionWorldSafety.Generation,
                    clock.World.Mission, MissionLifecycleService.ActiveContext?.SessionId ?? 0,
                    clock.Context.Mode.Kind != GameModes.GameModeKind.Unknown && clock.Context.Mode.IsRealMultiplayer == false,
                    WorkerProductionWorldSafety.IsFaulted, NativeStorageRuntime.Instance.HasPendingImports))
                throw new InvalidOperationException("The protected save requires its unchanged known single-player/editor world and completed imports.");
            if (validateSources)
            {
                var registry = VersionedSaveDataRuntime.CurrentRegistry ??
                    throw new InvalidOperationException("The critical source registry is unavailable.");
                registry.RequireCriticalWriterWorld(world, clock.World.Mission);
            }
        }

        // The actual pre-Original and final replacement guards share this pure
        // identity predicate. CPU fixtures can vary replacement/fault evidence
        // without fabricating a published native runtime or reading native memory.
        internal static bool IsSameSaveWorld(object expected, long expectedGeneration, object current, long currentGeneration,
            long worldSession, long activeSession, bool knownSinglePlayer, bool faulted, bool pendingImports) =>
            expected != null && ReferenceEquals(expected, current) && expectedGeneration == currentGeneration &&
            worldSession > 0 && worldSession == activeSession && knownSinglePlayer && !faulted && !pendingImports;
    }
}
