using APIShared.Internal;
using System;

namespace APIShared
{
    internal sealed partial class ApiSharedRuntime
    {
        private const string NativeWorldReadsId = "native-world-reads";
        private NativeWorldReadReader nativeWorldReadReader;
        private NativeCapabilityDiagnostic nativeWorldReadDiagnostic;

        // Cold acquisition only: EngineInterface assigns its private static object monitor in .cctor.
        // Retain that process-lifetime object, not a reflection lookup on every observation. The existing
        // lifecycle service remains the sole publisher and hook owner; no producer timing changes here.
        internal bool TryGetNativeWorldReads(string ownerGuid, out NativeWorldReadClient client,
            out NativeCapabilityDiagnostic diagnostic)
        {
            client = null;
            if (!ValidateOwner(ownerGuid, NativeWorldReadsId, out diagnostic)) return false;
            lock (sync)
            {
                if (state != NativeApiState.Ready)
                { diagnostic = Faulted(NativeWorldReadsId, "Native API publication is unavailable."); return false; }
                if (missionLifecycle == null)
                {
                    diagnostic = new NativeCapabilityDiagnostic(NativeWorldReadsId,
                        missionLifecycleDiagnostic.State == NativeCapabilityState.Available
                            ? NativeCapabilityState.Faulted : missionLifecycleDiagnostic.State,
                        binaryHash, "The mission lifecycle owner is unavailable: " + missionLifecycleDiagnostic.Reason);
                    return false;
                }
                if (nativeWorldReadDiagnostic == null)
                {
                    try
                    {
                        object monitor = EngineMonitor.Current;
                        if (monitor == null)
                            nativeWorldReadDiagnostic = new NativeCapabilityDiagnostic(NativeWorldReadsId,
                                NativeCapabilityState.ValidationFailed, binaryHash,
                                "The game's private static object engine monitor is unavailable.");
                        else
                        {
                            nativeWorldReadReader = missionLifecycle.CreateNativeWorldReadReader(monitor);
                            nativeWorldReadDiagnostic = new NativeCapabilityDiagnostic(NativeWorldReadsId,
                                NativeCapabilityState.Available, binaryHash,
                                "Protected reads use the existing native-world and managed-ready publications.");
                        }
                    }
                    catch (Exception ex)
                    {
                        nativeWorldReadDiagnostic = Faulted(NativeWorldReadsId,
                            "The engine monitor could not be bound: " + ex.Message);
                    }
                }
                diagnostic = nativeWorldReadDiagnostic;
                if (nativeWorldReadReader == null) return false;
                client = new NativeWorldReadClient(nativeWorldReadReader);
                return true;
            }
        }
    }
}
