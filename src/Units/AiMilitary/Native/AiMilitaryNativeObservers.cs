using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using APIShared.NativeSites;
using APIShared.Units.AiMilitary.Orders;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Units.AiMilitary.Native
{
    // 87D0: raw sections and conversions are restored before any initial 71670/CDE60 fold.
    // 122B40: native strategic dispatch; its return is not an individual task-success acknowledgement.
    internal static class AiMilitaryNativeObservers
    {
        internal const string SiteName = "Units.AiMilitary.WorldAndOrders";
        internal const int RestoreRva = 0x87D0, StrategicRva = 0x122B40, TaskBuildRva = 0x10DF60;
        internal static readonly byte[] RestoreEntry = { 0x48, 0x89, 0x5C, 0x24, 0x20, 0x48, 0x89, 0x54, 0x24, 0x10 };
        internal static readonly byte[] StrategicEntry = { 0x44, 0x89, 0x44, 0x24, 0x18, 0x48, 0x89, 0x4C, 0x24, 0x08 };
        internal static readonly byte[] TaskBuildEntry = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x44, 0x89, 0x44, 0x24, 0x18 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RestoreCall(IntPtr archive, IntPtr sections);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long StrategicCall(IntPtr manager, int tribe, int action);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TaskBuildCall(IntPtr manager, int owner, int target);
        private static readonly RestoreCall RestoreCallback = OnRestore;
        private static readonly StrategicCall StrategicCallback = OnStrategic;
        private static readonly TaskBuildCall TaskBuildCallback = OnTaskBuild;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static readonly NativeDetourBackend backend = new NativeDetourBackend(new NativeDetourOptions {
            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static DetourHandle<RestoreCall> restore;
        private static DetourHandle<StrategicCall> strategic;
        private static DetourHandle<TaskBuildCall> tasks;
        private static HookTransaction transaction;
        private static IntPtr demand, module;
        private static AiObserverDemandGate gate;
        private static AiObserverDemandGate taskGate;
        private static int lastTaskOwner, lastTaskTarget;
        private static long taskEpoch;
        private static Func<bool> restored;
        private static Action<int, int, long> ordered;
        private static ManualLogSource log;
        internal static bool IsReady => publication.IsReady;
        internal static void EnableObservation() { if (demand != IntPtr.Zero) Marshal.WriteInt32(demand, 1); }
        internal static void BeginWorld() { lastTaskOwner = lastTaskTarget = 0; taskEpoch++; }
        internal static bool TryGetGlobalTaskOwner(out int owner, out int target, out long epoch)
        { owner = lastTaskOwner; target = lastTaskTarget; epoch = taskEpoch; return owner > 0 && target > 0; }
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(RestoreRva, RestoreEntry.Length, NativeSiteMode.Exclusive),
            new NativeSiteSpan(StrategicRva, StrategicEntry.Length, NativeSiteMode.Exclusive),
            new NativeSiteSpan(TaskBuildRva, TaskBuildEntry.Length, NativeSiteMode.Exclusive)
        };
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger,
            Func<bool> onRestored, Action<int, int, long> onOrdered)
        {
            publication.ThrowIfUnavailable(); if (publication.IsReady) return;
            if (!string.Equals(hash, AiMilitaryNativeBindings.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AI world/order observations require the audited native image.");
            if (!context.Memory.Slice(RestoreRva, RestoreEntry.Length).SequenceEqual(RestoreEntry) ||
                !context.Memory.Slice(StrategicRva, StrategicEntry.Length).SequenceEqual(StrategicEntry) ||
                !context.Memory.Slice(TaskBuildRva, TaskBuildEntry.Length).SequenceEqual(TaskBuildEntry))
                throw new InvalidOperationException("AI world/order entry contracts differ.");
            module = context.ModuleHandle; log = logger; restored = onRestored; ordered = onOrdered;
            NativeIndirectEntry.VerifyBytes(module + RestoreRva, RestoreEntry);
            NativeIndirectEntry.VerifyBytes(module + StrategicRva, StrategicEntry);
            NativeIndirectEntry.VerifyBytes(module + TaskBuildRva, TaskBuildEntry);
            var registry = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(registry, SiteName, Spans(), message => new InvalidOperationException(message));
            demand = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(demand, 0);
            HookTransaction candidate = null;
            try
            {
                candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(backend) });
                restore = new DetourHandle<RestoreCall>(); strategic = new DetourHandle<StrategicCall>();
                candidate.AddDetour(restore, HookTarget.FromAddress(unchecked((ulong)(module + RestoreRva).ToInt64())), RestoreCallback);
                // A native intermediary keeps the dormant strategic path entirely native.
                gate = new AiObserverDemandGate(demand, StrategicCallback);
                candidate.AddDetour(strategic, HookTarget.FromAddress(unchecked((ulong)(module + StrategicRva).ToInt64())),
                    StrategicCallback, gate);
                tasks = new DetourHandle<TaskBuildCall>(); taskGate = new AiObserverDemandGate(demand, TaskBuildCallback);
                candidate.AddDetour(tasks, HookTarget.FromAddress(unchecked((ulong)(module + TaskBuildRva).ToInt64())), TaskBuildCallback, taskGate);
                var result = candidate.Commit();
                if (!result.IsCompleteSuccess || !restore.Success || !strategic.Success || !tasks.Success)
                    throw new InvalidOperationException("Raw restore/strategic observer preparation failed.");
                NativeIndirectEntry.Validate(restore.Hook, module + RestoreRva, RestoreEntry, false);
                NativeIndirectEntry.Validate(strategic.Hook, module + StrategicRva, StrategicEntry, false);
                NativeIndirectEntry.Validate(tasks.Hook, module + TaskBuildRva, TaskBuildEntry, false);
                transaction = candidate;
                NativeHookPublication.Enable(candidate, result, publication, typeof(AiMilitaryNativeObservers), () => {
                    NativeIndirectEntry.Validate(restore.Hook, module + RestoreRva, RestoreEntry, true);
                    NativeIndirectEntry.Validate(strategic.Hook, module + StrategicRva, StrategicEntry, true);
                    NativeIndirectEntry.Validate(tasks.Hook, module + TaskBuildRva, TaskBuildEntry, true);
                });
            }
            catch
            {
                publication.RollbackUnpublished(candidate, () => {
                    Marshal.FreeHGlobal(demand); demand = IntPtr.Zero;
                    ApiSharedNativeSites.Release(registry, SiteName);
                });
                throw;
            }
        }
        private static int OnRestore(IntPtr archive, IntPtr sections)
        {
            int result = restore.Original(archive, sections);
            if (result == 1 && IsReady)
                try
                {
                    // All three audited callers branch directly to their ordinary failure return on
                    // zero, before any AI/fold. Never run a partially protected authored world.
                    if (!Savegames.NativeWorldRestoreParticipants.Restore()) return 0;
                    if (restored != null && !restored()) return 0;
                }
                catch (Exception error) { Report("raw-restore", error); return 0; }
            return result;
        }
        private static long OnStrategic(IntPtr manager, int tribe, int action)
        {
            var observers = AiStrategicOrderEvents.Snapshot;
            long invocation = observers.Length == 0 ? 0 : AiStrategicOrderEvents.NextInvocation();
            if (observers.Length != 0) AiStrategicOrderEvents.Publish(observers, true,
                new AiStrategicOrderObservation(invocation, manager, tribe, action, false, 0));
            long result = 0; bool completed = false;
            AiMilitaryOrderBroker.EnterGroupDispatch();
            try
            {
                result = strategic.Original(manager, tribe, action); completed = true;
                if (IsReady) try { ordered?.Invoke(tribe, action, result); } catch (Exception error) { Report("strategic-order", error); }
                return result;
            }
            finally
            {
                AiMilitaryOrderBroker.ExitGroupDispatch();
                if (observers.Length != 0) AiStrategicOrderEvents.Publish(observers, false,
                    new AiStrategicOrderObservation(invocation, manager, tribe, action, completed, result));
            }
        }
        private static void Report(string stage, Exception error)
        {
            try { Internal.RuntimeHealth.Failure(log, "ai-native-observers/" + stage, true, "AI native observation failed: " + error); } catch { }
        }
        private static void OnTaskBuild(IntPtr manager, int owner, int target)
        {
            var observers = AiGlobalTaskEvents.Snapshot;
            long invocation = observers.Length == 0 ? 0 : AiGlobalTaskEvents.NextInvocation();
            if (observers.Length != 0) AiGlobalTaskEvents.Publish(observers, true,
                new AiGlobalTaskObservation(invocation, manager, owner, target, false));
            lastTaskOwner = lastTaskTarget = 0; taskEpoch++;
            bool completed = false;
            try
            {
                tasks.Original(manager, owner, target); completed = true;
                lastTaskOwner = owner; lastTaskTarget = target;
            }
            finally
            {
                if (observers.Length != 0) AiGlobalTaskEvents.Publish(observers, false,
                    new AiGlobalTaskObservation(invocation, manager, owner, target, completed));
            }
        }
    }
}
