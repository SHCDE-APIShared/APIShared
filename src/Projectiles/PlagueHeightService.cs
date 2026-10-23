using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;

namespace APIShared
{
    // Initialized eagerly during LibraryLoaded, before simulation. Static service and publication
    // roots retain every hook/data buffer even when an executable publication fails halfway.
    internal static class PlagueHeightService
    {
        internal const string Id = NativeCapabilityIds.PlagueHeight;
        private static readonly Dictionary<string, Owner> owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        private static PlagueHeightRuntime runtime;
        private static PlagueHeightPolicies policies;
        private static NativeCapabilityDiagnostic status = new NativeCapabilityDiagnostic(Id,
            NativeCapabilityState.Pending, "", "Native initialization has not completed.");
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (status.State != NativeCapabilityState.Pending) return;
            if (hash != ApiSharedRuntime.SupportedHash)
            {
                status = new NativeCapabilityDiagnostic(Id, NativeCapabilityState.UnsupportedBuild, hash,
                    "Plague heights require the audited native image; Vanilla remains active.");
                NativeApiLog.Error(log, status.Reason); return;
            }
            try
            {
                runtime = new PlagueHeightRuntime(log, context);
                policies = new PlagueHeightPolicies(runtime.SetPolicy);
                status = new NativeCapabilityDiagnostic(Id, NativeCapabilityState.Available, hash,
                    "Permanent native plague hooks ready; inactive until an owner enables a category.");
            }
            catch (Exception error)
            {
                status = new NativeCapabilityDiagnostic(Id, NativeCapabilityState.ValidationFailed, hash, error.Message);
                NativeApiLog.Error(log, "Plague height service unavailable; Vanilla remains active: " + error);
            }
        }
        internal static bool TryGet(string ownerGuid, out IPlagueHeightCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null; diagnostic = status;
            if (status.State != NativeCapabilityState.Available) return false;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(Id, NativeCapabilityState.ValidationFailed,
                    status.BinaryHash, "A non-empty owner GUID is required."); return false;
            }
            lock (owners)
            {
                if (!owners.TryGetValue(ownerGuid, out var owner))
                    owners.Add(ownerGuid, owner = new Owner(ownerGuid));
                capability = owner;
            }
            return true;
        }
        private sealed class Owner : IPlagueHeightCapability
        {
            private readonly string guid;
            internal Owner(string guid) { this.guid = guid; }
            public bool TrySetPolicy(bool active, PlagueSurfaceCategory categories, int maximumHeightDifference,
                out NativeCapabilityDiagnostic diagnostic)
            {
                diagnostic = status;
                if (!policies.TrySet(guid, active, categories, maximumHeightDifference))
                {
                    diagnostic = new NativeCapabilityDiagnostic(Id, NativeCapabilityState.ValidationFailed,
                        status.BinaryHash, "Invalid category flags or tolerance outside 0..65534; policy unchanged.");
                    return false;
                }
                return true;
            }
        }
    }
}
