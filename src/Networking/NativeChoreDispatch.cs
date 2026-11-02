using System;
using System.Reflection;
using System.Threading;
using MonoMod.RuntimeDetour;
using SHCDESE.API;

namespace APIShared.Networking
{
    // One shared scope around SE's native-only blob dispatcher, before typed
    // deserialization/R3 observers. Live native state alone is insufficient after
    // an earlier subscriber reenters the dispatcher or changes Chore globals.
    // The hook and delegates remain rooted for the process; old channels retain
    // their complete dispatch order, arguments and exception behavior.
    internal static class NativeChoreDispatch
    {
        private static Hook hook;
        private static Action<byte[]> original;
        private static int ready;
        private static readonly object registrationGate = new object();
        private static ulong[] packetDemand = Array.Empty<ulong>();
        private static Exception failure;
        [ThreadStatic] private static Frame active;

        internal readonly struct Frame
        {
            internal readonly bool Valid;
            internal readonly short PacketId;
            internal readonly NativeChoreExecution Native;
            internal Frame(short packetId, NativeChoreExecution native) { Valid = true; PacketId = packetId; Native = native; }
        }

        internal static bool IsReady => Volatile.Read(ref ready) != 0;
        internal static void AddDemand(short packetId)
        {
            lock (registrationGate)
            {
                var prior = packetDemand;
                if (Interested(prior, packetId)) return;
                var next = new ulong[1024]; // Complete unsigned 16-bit packet-ID domain.
                if (prior.Length != 0) Array.Copy(prior, next, prior.Length);
                int key = unchecked((ushort)packetId);
                next[key >> 6] |= 1UL << (key & 63);
                Volatile.Write(ref packetDemand, next);
            }
        }

        internal static void Initialize()
        {
            if (IsReady) return;
            if (Volatile.Read(ref hook) != null)
                throw new InvalidOperationException("Native Chore dispatcher publication already failed or is in progress.", failure);
            var method = typeof(GameNetworkAPI).GetMethod("DispatchReceivedScriptExtenderPayload",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]) }, null);
            if (method == null || method.ReturnType != typeof(void))
                throw new MissingMethodException("Native Chore dispatcher contract changed.");
            NativeChoreDispatchContract.Validate(method);
            Hook candidate = null;
            bool publishing = false;
            try
            {
                candidate = new Hook(method, (Action<byte[]>)OnDispatch,
                    new HookConfig { ManualApply = true, ID = "APIShared.Networking.NativeChoreDispatch" });
                original = candidate.GenerateTrampoline<Action<byte[]>>();
                // Root before Apply: a failed publication may still have exposed
                // executable code. Only logical dormancy is safe afterwards.
                Volatile.Write(ref hook, candidate); publishing = true;
                candidate.Apply();
                Volatile.Write(ref ready, 1);
            }
            catch (Exception error)
            {
                if (!publishing) { candidate?.Dispose(); original = null; }
                Volatile.Write(ref failure, error);
                throw;
            }
        }

        private static void OnDispatch(byte[] blob)
        {
            var demand = Volatile.Read(ref packetDemand);
            if (!IsReady || demand.Length == 0) { original(blob); return; }
            Frame captured = default;
            short packetId = blob != null && blob.Length >= 2 ? unchecked((short)(blob[0] | blob[1] << 8)) : (short)0;
            if (blob == null || blob.Length < 2 || !Interested(demand, packetId))
            {
                // Existing packet types avoid native reads. An unrelated nested
                // dispatcher still cannot inherit an outer authenticated frame.
                if (!active.Valid) original(blob);
                else DispatchCaptured(default, blob, original);
                return;
            }
            try
            {
                if (NativeChoreContext.TryCaptureRaw(out var native) && NativeChoreContext.MatchesPayload(native, blob))
                    captured = new Frame(packetId, native);
            }
            catch (Exception error)
            {
                // Failed optional attribution cannot suppress or repeat the
                // original dispatcher. Deactivate only authenticated delivery.
                Volatile.Write(ref failure, error);
                if (Interlocked.Exchange(ref ready, 0) != 0) ReportDeactivation(error);
            }
            DispatchCaptured(captured, blob, original);
        }

        // Reported only by the ready->disabled transition; later deliveries take the early original path.
        private static void ReportDeactivation(Exception error)
        {
            try
            {
                NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger,
                    "AUTHENTICATED_CHORE_DISPATCH: native issuer capture failed; authenticated delivery is disabled for this process, " +
                    "existing channels are unchanged: " + error);
            }
            catch { /* Diagnostics never interrupt the original dispatcher. */ }
        }

        internal static Frame Current => active;
        internal static void DispatchCaptured(Frame captured, byte[] blob, Action<byte[]> dispatch)
        {
            Frame previous = active;
            // Invalid nested calls must not inherit an authenticated outer frame.
            active = captured;
            try { dispatch(blob); }
            finally { active = previous; }
        }

        internal static bool TryCapture(short expectedPacketId, out NativeChoreExecution native)
        {
            native = default;
            Frame captured = Current;
            if (!IsReady || !captured.Valid || captured.PacketId != expectedPacketId) return false;
            if (!Matches(captured, expectedPacketId, NativeChoreContext.TryCaptureRaw(out var current), current)) return false;
            native = captured.Native; return true;
        }

        internal static bool Matches(Frame captured, short expectedPacketId, bool hasCurrent, NativeChoreExecution current) =>
            captured.Valid && captured.PacketId == expectedPacketId && hasCurrent && captured.Native.Matches(current);

        private static bool Interested(ulong[] demand, short packetId)
        {
            int key = unchecked((ushort)packetId);
            return demand.Length != 0 && (demand[key >> 6] & (1UL << (key & 63))) != 0;
        }
    }
}
