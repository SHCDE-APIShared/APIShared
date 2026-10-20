using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using SHCDESE.API;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace APIShared
{
    // Single owner of AIVLoader.UploadDefaultAIV. Vanilla's Coop branch of FRONT_Multiplayer.StartSkirmishGame calls
    // it once per AI member after InitAIVLoading and before loadMultiplayerMap, which bypasses the custom-AIC and
    // custom-AIV handling of its skirmish branch. Plans are collected when the mission lifecycle sees the native
    // multiplayer initialization of a Coop attempt and are dropped when that attempt finishes.
    internal sealed class CoopAiStartImportService
    {
        private delegate void UploadDefault(int lordType, int playerId, bool everySkirmishSet, bool everyHistoricalSet);

        internal interface IBackend
        {
            void UploadDefault(int lordType, int playerId, bool everySkirmishSet, bool everyHistoricalSet);
            void ImportCandidate(int bankIndex, int candidateIndex, short[] data);
            void SetLordConfiguration(EngineInterface.AILordConfigTransferData configuration, int playerId);
        }

        private sealed class Registration
        {
            internal string Owner, Id;
            internal Func<CoopAiStartRequest, CoopAiStartContribution> Provider;
        }

        private sealed class Attempt
        {
            internal CoopAiStartRequest Request;
            internal readonly Dictionary<int, CoopAiSlotPlan> Plans = new Dictionary<int, CoopAiSlotPlan>();
            internal readonly Dictionary<int, string> PlanOwners = new Dictionary<int, string>();
            internal readonly HashSet<int> Executed = new HashSet<int>();
            internal string Fault;
        }

        private readonly ManualLogSource log;
        private readonly int unityThread;
        private readonly object sync = new object();
        private Registration[] registrations = new Registration[0];
        private IBackend backend;
        private Attempt attempt;
        private long nextAttemptId;
        private Hook uploadHook;
        private UploadDefault uploadOriginal;
        private static CoopAiStartImportService published;

        internal CoopAiStartImportService(ManualLogSource log, IBackend backend = null)
        {
            this.log = log;
            this.backend = backend;
            unityThread = Thread.CurrentThread.ManagedThreadId;
        }

        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Method(typeof(global::AIVLoader), nameof(global::AIVLoader.UploadDefaultAIV), typeof(void),
                typeof(int), typeof(int), typeof(bool), typeof(bool))
            .Method(typeof(global::EngineInterface), nameof(global::EngineInterface.ImportAIV), typeof(void),
                typeof(int), typeof(int), typeof(short[]), typeof(int))
            .Method(typeof(global::EngineInterface), nameof(global::EngineInterface.setCustomLordConfig), typeof(void),
                typeof(global::EngineInterface.AILordConfigTransferData).MakeByRefType(), typeof(int));

        // Installs the permanent upload hook. Only a not yet published candidate is rolled back on failure.
        internal static bool TryCreate(ManualLogSource log, out CoopAiStartImportService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.CoopAiStartImport, log, out diagnostic))
                return false;
            var candidate = new CoopAiStartImportService(log);
            Hook hook = null;
            try
            {
                // A compiled method reference binds exactly the validated overload.
                MethodInfo target = ((UploadDefault)AIVLoader.UploadDefaultAIV).Method;
                hook = new Hook(target, (UploadDefault)candidate.OnUpload,
                    new HookConfig { ManualApply = true, ID = "APIShared.CoopAiStartImport.UploadDefaultAIV" });
                candidate.uploadOriginal = hook.GenerateTrampoline<UploadDefault>();
                candidate.backend = new VanillaBackend(candidate);
                hook.Apply();
                candidate.uploadHook = hook;
                published = service = candidate;
                diagnostic = Status(NativeCapabilityState.Available, "Coop AI start import installed.");
                return true;
            }
            catch (Exception ex)
            {
                try { hook?.Undo(); } catch { }
                try { hook?.Dispose(); } catch { }
                diagnostic = Status(NativeCapabilityState.Faulted, ex.Message);
                candidate.Report("installation failed: " + ex);
                return false;
            }
        }

        // Called by the mission lifecycle inside a start attempt, before the native multiplayer initialization.
        internal static void NotifyCoopInitialization(int coopTrail, int coopMission) =>
            published?.BeginAttempt(coopTrail, coopMission);

        // Called by the mission lifecycle when the outermost start attempt returns or throws.
        internal static void NotifyAttemptFinished() => published?.EndAttempt();

        internal ICoopAiStartImportCapability Bind(string owner) => new Binding(this, owner);

        internal void BeginAttempt(int coopTrail, int coopMission)
        {
            var current = new Attempt { Request = new CoopAiStartRequest(++nextAttemptId, coopTrail, coopMission) };
            attempt = current;
            Registration[] snapshot;
            lock (sync) snapshot = registrations;
            var offered = new SortedDictionary<int, List<KeyValuePair<string, CoopAiSlotPlan>>>();
            foreach (Registration registration in snapshot)
            {
                string owner = registration.Owner + "/" + registration.Id;
                CoopAiStartContribution contribution;
                try { contribution = registration.Provider(current.Request); }
                catch (Exception ex)
                {
                    Fail(current, owner + " failed: " + ex.Message);
                    continue;
                }
                if (contribution == null) continue;
                foreach (CoopAiSlotPlan plan in contribution.Plans)
                {
                    if (!offered.TryGetValue(plan.PlayerId, out var list))
                        offered.Add(plan.PlayerId, list = new List<KeyValuePair<string, CoopAiSlotPlan>>());
                    list.Add(new KeyValuePair<string, CoopAiSlotPlan>(owner, plan));
                }
            }
            foreach (KeyValuePair<int, List<KeyValuePair<string, CoopAiSlotPlan>>> slot in offered)
                Choose(current, slot.Key, slot.Value);
            if (current.Plans.Count > 0 || current.Fault != null)
                Debug("attempt " + current.Request.AttemptId + " prepared: trail=" + coopTrail + ", mission=" +
                    coopMission + ", plans=" + current.Plans.Count + (current.Fault == null ? "." : ", fault=" + current.Fault));
        }

        // Complete plans must agree exactly; incomplete ones must be prefixes of the chosen plan.
        private void Choose(Attempt current, int playerId, List<KeyValuePair<string, CoopAiSlotPlan>> offers)
        {
            KeyValuePair<string, CoopAiSlotPlan> chosen = offers[0];
            foreach (KeyValuePair<string, CoopAiSlotPlan> offer in offers)
                if (offer.Value.CandidateListComplete) { chosen = offer; break; }
            foreach (KeyValuePair<string, CoopAiSlotPlan> offer in offers)
            {
                bool agrees = offer.Value.CandidateListComplete || !chosen.Value.CandidateListComplete
                    ? offer.Value.HasSameImport(chosen.Value)
                    : offer.Value.IsPrefixOf(chosen.Value);
                if (!agrees)
                {
                    Fail(current, "player " + playerId + " has contradicting plans from " + chosen.Key + " and " + offer.Key + ".");
                    return;
                }
            }
            if (!chosen.Value.CandidateListComplete)
                Debug("attempt " + current.Request.AttemptId + ": player " + playerId + " imports only " +
                    chosen.Value.CandidateCount + " known castle candidate(s) from " + chosen.Key + ".");
            current.Plans.Add(playerId, chosen.Value);
            current.PlanOwners.Add(playerId, chosen.Key);
        }

        internal void EndAttempt()
        {
            Attempt finished = attempt;
            attempt = null;
            if (finished == null || finished.Fault != null) return;
            foreach (int playerId in finished.Plans.Keys)
                if (!finished.Executed.Contains(playerId))
                    Report("attempt " + finished.Request.AttemptId + " ended without importing the plan for player " +
                        playerId + "; another upload owner may have intercepted it.");
        }

        internal void OnUpload(int lordType, int playerId, bool everySkirmishSet, bool everyHistoricalSet)
        {
            Attempt current = attempt;
            // Fast path: no Coop attempt with plans or faults keeps Vanilla unchanged.
            if (current == null || current.Plans.Count == 0 && current.Fault == null ||
                Thread.CurrentThread.ManagedThreadId != unityThread)
            {
                backend.UploadDefault(lordType, playerId, everySkirmishSet, everyHistoricalSet);
                return;
            }
            if (current.Fault != null)
                throw new InvalidOperationException("Coop AI start aborted: " + current.Fault);
            if (!current.Plans.TryGetValue(playerId, out CoopAiSlotPlan plan))
            {
                backend.UploadDefault(lordType, playerId, everySkirmishSet, everyHistoricalSet);
                return;
            }
            if (plan.LordType != lordType)
                throw new InvalidOperationException("Coop AI start aborted: player " + playerId + " changed Lord type from " +
                    plan.LordType + " to " + lordType + ".");
            if (!current.Executed.Add(playerId))
                throw new InvalidOperationException("Coop AI start aborted: player " + playerId + " was uploaded twice.");
            Execute(plan, everySkirmishSet, everyHistoricalSet);
            Debug("attempt " + current.Request.AttemptId + " imported player " + playerId + ": source=" +
                plan.CastleSource + ", candidates=" + plan.CandidateCount + ", aic=" + plan.HasLordConfiguration +
                ", owner=" + current.PlanOwners[playerId] + ".");
        }

        private void Execute(CoopAiSlotPlan plan, bool everySkirmishSet, bool everyHistoricalSet)
        {
            switch (plan.CastleSource)
            {
                case CoopAiCastleSource.Standard:
                    backend.UploadDefault(plan.LordType, plan.PlayerId, everySkirmishSet, everyHistoricalSet);
                    break;
                case CoopAiCastleSource.Community:
                    backend.UploadDefault(plan.LordType, plan.PlayerId, true, false);
                    break;
                case CoopAiCastleSource.Historical:
                    backend.UploadDefault(plan.LordType, plan.PlayerId, false, true);
                    break;
                default:
                    for (int index = 0; index < plan.CandidateCount; index++)
                        backend.ImportCandidate(plan.PlayerId - 1, index, plan.CandidateView(index));
                    break;
            }
            if (plan.HasLordConfiguration)
                backend.SetLordConfiguration(plan.LordConfiguration, plan.PlayerId);
        }

        private bool Register(string owner, string id, Func<CoopAiStartRequest, CoopAiStartContribution> provider,
            out NativeCapabilityDiagnostic diagnostic)
        {
            if (Thread.CurrentThread.ManagedThreadId != unityThread || provider == null || string.IsNullOrWhiteSpace(id))
            {
                diagnostic = Status(NativeCapabilityState.ValidationFailed,
                    "Registration requires the Unity thread, a provider and a non-empty owner-local ID.");
                return false;
            }
            lock (sync)
            {
                foreach (Registration existing in registrations)
                    if (string.Equals(existing.Owner, owner, StringComparison.Ordinal) &&
                        string.Equals(existing.Id, id, StringComparison.Ordinal))
                    {
                        diagnostic = Status(NativeCapabilityState.ValidationFailed, "The registration ID is already used.");
                        return false;
                    }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[registrations.Length] = new Registration { Owner = owner, Id = id, Provider = provider };
                registrations = next;
            }
            diagnostic = Status(NativeCapabilityState.Available, "Coop AI start provider registered.");
            return true;
        }

        private void Fail(Attempt current, string reason)
        {
            if (current.Fault == null) current.Fault = reason;
            Report("attempt " + current.Request.AttemptId + ": " + reason);
        }

        private void Debug(string message)
        {
            try { NativeApiLog.Debug(log, "Coop AI start import " + message); } catch { }
        }

        private void Report(string message)
        {
            try { NativeApiLog.Error(log, "Coop AI start import " + message); } catch { }
        }

        private static NativeCapabilityDiagnostic Status(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.CoopAiStartImport, state, string.Empty, reason);

        private sealed class Binding : ICoopAiStartImportCapability
        {
            private readonly CoopAiStartImportService service;
            private readonly string owner;
            internal Binding(CoopAiStartImportService service, string owner) { this.service = service; this.owner = owner; }

            public bool TryRegisterProvider(string registrationId,
                Func<CoopAiStartRequest, CoopAiStartContribution> provider, out NativeCapabilityDiagnostic diagnostic) =>
                service.Register(owner, registrationId, provider, out diagnostic);
        }

        // Vanilla equivalents. ImportAIV uses the same bounds and call as the Script Extender's
        // GameAIVManagerAPI.ImportAIV, without constructing that manager. The custom flag 1 matches
        // Vanilla's skirmish branch for selected AIVs.
        private sealed class VanillaBackend : IBackend
        {
            private readonly CoopAiStartImportService owner;
            internal VanillaBackend(CoopAiStartImportService owner) { this.owner = owner; }

            public void UploadDefault(int lordType, int playerId, bool everySkirmishSet, bool everyHistoricalSet) =>
                owner.uploadOriginal(lordType, playerId, everySkirmishSet, everyHistoricalSet);

            public void ImportCandidate(int bankIndex, int candidateIndex, short[] data)
            {
                if ((uint)bankIndex >= GameAIVManagerAPI.MAX_AIV_BANKS ||
                    (uint)candidateIndex >= GameAIVManagerAPI.MAX_AIV_VARIANTS_PER_BANK ||
                    data == null || data.Length == 0)
                    throw new InvalidOperationException("Coop AI start aborted: invalid AIV import bank " + bankIndex +
                        ", candidate " + candidateIndex + ".");
                EngineInterface.ImportAIV(bankIndex, candidateIndex, data, 1);
            }

            public void SetLordConfiguration(EngineInterface.AILordConfigTransferData configuration, int playerId) =>
                EngineInterface.setCustomLordConfig(ref configuration, playerId);
        }
    }
}
