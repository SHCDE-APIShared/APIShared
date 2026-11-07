using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;

namespace APIShared.Economy
{
    // Process-owned terminal-world latch. Native producers only write fixed data;
    // Pure status notification is synchronous; only Unity termination leaves the engine thread.
    internal static unsafe class WorkerProductionWorldSafety
    {
        internal const int FaultCodeOffset = 0, FaultUnitOffset = 4, FaultInputOffset = 8,
            FaultProductivityOffset = 12, GenerationOffset = 16, GuardEnabledOffset = 24, AdmittedOffset = 28;
        private static readonly IntPtr native = Allocate();
        private static ManualLogSource log;
        private static object engineGate;
        private static WorkerProductionRunGate runBoundary;
        private static long session;
        private static string reason;
        private static int notificationQueued;
        private static bool faultReported;
        internal static event Action<string> Faulted;
        internal static IntPtr NativeFaultAddress => native;
        internal static object EngineGate => Volatile.Read(ref engineGate);
        internal static WorkerProductionRunGate RunBoundary => Volatile.Read(ref runBoundary);
        internal static long Generation => Volatile.Read(ref *(long*)((byte*)native + GenerationOffset));
        internal static bool Enabled => Volatile.Read(ref *(int*)((byte*)native + GuardEnabledOffset)) != 0;
        internal static bool IsFaulted => Volatile.Read(ref *(int*)native) != 0;
        internal static bool IsAdmitted => Volatile.Read(ref *(int*)((byte*)native + AdmittedOffset)) != 0;
        internal static bool IsAvailable => EngineGate != null;

        private static IntPtr Allocate()
        {
            IntPtr value = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(value, i, 0);
            return value;
        }

        internal static void Initialize(ManualLogSource logger)
        {
            // These are genuine public game members, checked against the loaded assembly.
            MethodInfo stop = typeof(EditorDirector).GetMethod(nameof(EditorDirector.stopGameSim),
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            if (stop == null || stop.ReturnType != typeof(void))
                throw new MissingMethodException("Public EditorDirector.stopGameSim(Boolean) changed.");
            foreach (string name in new[] { "beingFilled", "gotData", "locked" })
            {
                FieldInfo field = typeof(MemoryBuffers.MemBuffer).GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field == null || field.FieldType != typeof(bool))
                    throw new MissingFieldException("Public MemoryBuffers.MemBuffer Boolean " + name + " changed.");
            }
            object gate = EngineMonitor.Current;
            if (gate == null || !MarkedUnitSelectionAPI.HasRunBoundary)
                throw new InvalidOperationException("Production safety needs the validated shared engine run boundary.");
            var boundary = new WorkerProductionRunGate(gate, () => Generation, () => Enabled,
                () => IsFaulted, () => IsAdmitted, MarkedUnitSelectionAPI.ProductionRunOriginal, ObserveNativeFault);
            log = logger;
            Volatile.Write(ref runBoundary, boundary);
            Volatile.Write(ref engineGate, gate);
        }

        internal static void Admit(long sessionId, bool enableGuard)
        {
            object gate = EngineGate ?? throw new InvalidOperationException("Production world safety is unavailable.");
            lock (gate)
            {
                Interlocked.Increment(ref *(long*)((byte*)native + GenerationOffset));
                session = sessionId;
                reason = null;
                notificationQueued = 0;
                faultReported = false;
                for (int i = 0; i < 16; i += 4) Marshal.WriteInt32(native, i, 0);
                // Loading may run its own native consumers, but a simulation run cannot
                // start until the actual mode has selected its admitted bundle member.
                Volatile.Write(ref *(int*)((byte*)native + AdmittedOffset), 0);
                Volatile.Write(ref *(int*)((byte*)native + GuardEnabledOffset), 1);
            }
        }

        internal static void Fault(string diagnostic)
        {
            object gate = EngineGate;
            if (gate == null) throw new InvalidOperationException("Production world safety is unavailable: " + diagnostic);
            lock (gate)
            {
                if (reason == null) reason = diagnostic ?? "Production world validation failed.";
                Volatile.Write(ref *(int*)((byte*)native + GuardEnabledOffset), 1);
                if (!IsFaulted) Volatile.Write(ref *(int*)native, 100);
                NotifyFault();
                QueueTermination();
            }
        }

        internal static void RequireGuard()
        {
            object gate = EngineGate ?? throw new InvalidOperationException("Production world safety is unavailable.");
            lock (gate) Volatile.Write(ref *(int*)((byte*)native + GuardEnabledOffset), 1);
        }

        internal static void ConfirmPrepared(bool enableGuard)
        {
            object gate = EngineGate ?? throw new InvalidOperationException("Production world safety is unavailable.");
            lock (gate)
            {
                if(IsFaulted) throw new InvalidOperationException("A faulted production world cannot be readmitted.");
                Volatile.Write(ref *(int*)((byte*)native + AdmittedOffset), 1);
                Volatile.Write(ref *(int*)((byte*)native + GuardEnabledOffset), enableGuard ? 1 : 0);
            }
        }

        internal static void Retire()
        {
            object gate=EngineGate;if(gate==null)return;
            lock(gate)
            {
                Interlocked.Increment(ref *(long*)((byte*)native+GenerationOffset));
                Volatile.Write(ref *(int*)((byte*)native+AdmittedOffset),0);
                Volatile.Write(ref *(int*)((byte*)native+GuardEnabledOffset),1);
            }
        }

        // Called at the existing post-native buffer boundary, before publication/work/readback.
        internal static void ObserveNativeFault()
        {
            if (!IsFaulted) return;
            object gate = EngineGate;
            if (gate == null) return;
            lock (gate)
            {
                if (reason == null)
                    reason = "Production input contract violated: code=" + Marshal.ReadInt32(native) +
                        "; unit=" + Marshal.ReadInt32(native, FaultUnitOffset) +
                        "; amount=" + Marshal.ReadInt32(native, FaultInputOffset) +
                        "; productivity=" + Marshal.ReadInt32(native, FaultProductivityOffset) + ".";
                NotifyFault();
                QueueTermination();
            }
        }

        // Called under the engine monitor, before retirement can invalidate a queued stop.
        // The sole production subscriber publishes immutable status; UI work stays deferred.
        // Latch before callbacks so reentrant fault observations and dispatch retries cannot
        // publish the same fault twice. Admit resets this independently of enqueue success.
        private static void NotifyFault()
        {
            if (faultReported) return;
            faultReported = true;
            var callbacks = Faulted;
            if (callbacks != null)
                foreach (Action<string> callback in callbacks.GetInvocationList())
                    try { callback(reason); } catch (Exception error) { Report(error); }
        }

        private static void QueueTermination()
        {
            if (Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
            long expectedGeneration = Generation;
            long expectedSession = session;
            string message = reason;
            try { log?.LogError($"{DateTime.Now:HH:mm:ss.fff} WORKER_PRODUCTION_WORLD_BLOCKED: {message}"); } catch { }
            // Dispatch must not depend on SimulationWorkPump: subsequent runs are blocked.
            if (!UnityMainThreadDispatch.TryEnqueue(() => Terminate(expectedGeneration, expectedSession, message)))
                Interlocked.Exchange(ref notificationQueued, 0); // Remain blocked; a later observation retries delivery.
        }

        private static void Terminate(long expectedGeneration, long expectedSession, string message)
        {
            if (Generation != expectedGeneration || !IsFaulted || session != expectedSession) return;
            // A stale callback can never stop a replacement mission. stopGameSim invokes
            // Vanilla's local simulation stop and, in MP, its normal LeaveGame/exit flow.
            if (MissionLifecycleService.ActiveContext?.SessionId != expectedSession) return;
            try { EditorDirector.instance?.stopGameSim(false); }
            catch (Exception error) { Report(error); } // The terminal latch remains closed.
        }

        private static void Report(Exception error)
        { try { log?.LogError($"{DateTime.Now:HH:mm:ss.fff} WORKER_PRODUCTION_TERMINATION: {error}"); } catch { } }
    }
}
