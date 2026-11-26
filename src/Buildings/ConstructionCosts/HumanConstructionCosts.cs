using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using APIShared.Internal;
using APIShared.Savegames;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>Process-held configuration of one mod's Human-only construction extras and its saved payment history.</summary>
    public sealed class HumanConstructionCostRegistration
    {
        internal readonly HumanConstructionLedger Ledger = new HumanConstructionLedger();
        private readonly Dictionary<ConstructionCostTarget, IReadOnlyDictionary<eGoods, int>> costs = new Dictionary<ConstructionCostTarget, IReadOnlyDictionary<eGoods, int>>();
        internal bool Active, LoadedSinceStart;
        internal bool HasConfiguredCosts => costs.Count != 0;
        internal bool ArchiveWritable = true;
        internal bool HasCosts => costs.Count != 0;
        internal HumanConstructionCostRegistration(string ownerGuid, string registrationId) { OwnerGuid = ownerGuid; RegistrationId = registrationId; }
        /// <summary>The stable plugin identity owning this configuration and save record.</summary>
        public string OwnerGuid { get; }
        /// <summary>The owner-local process-held registration identity.</summary>
        public string RegistrationId { get; }
        /// <summary>Activates future charges. Paid receipts remain refundable when the feature is disabled.</summary>
        public void SetActive(bool active) { lock (HumanConstructionCosts.Gate) Active = active; }
        /// <summary>Snapshots one target's signed extras. Gold accepts -10000..10000; other supported stored goods accept
        /// 0..100. Unknown targets/goods are rejected. Call only under the existing synchronized settings contract.</summary>
        public void SetCosts(ConstructionCostTarget target, IReadOnlyDictionary<eGoods, int> values)
        {
            if (!target.IsValid || !target.SupportsHumanExtraCosts) throw new ArgumentException("Unsupported Human construction target.", nameof(target));
            IReadOnlyDictionary<eGoods, int> snapshot = HumanConstructionGoods.Snapshot(values);
            lock (HumanConstructionCosts.Gate) { if (snapshot.Count == 0) costs.Remove(target); else costs[target] = snapshot; }
        }
        /// <summary>Clears future cost configuration without erasing receipts for already paid objects.</summary>
        public void ClearCosts() { lock (HumanConstructionCosts.Gate) costs.Clear(); }
        /// <summary>Gets the immutable configured extras while active; false returns an empty dictionary.</summary>
        public bool TryGetCosts(ConstructionCostTarget target, out IReadOnlyDictionary<eGoods, int> values)
        {
            lock (HumanConstructionCosts.Gate)
            {
                if (Active && costs.TryGetValue(target, out values)) return true;
                values = HumanConstructionGoods.Empty; return false;
            }
        }
    }

    /// <summary>General Human-only construction-cost configuration. Runtime adapters apply one transaction after
    /// the final native inputs have been validated; arbitrary low-level spawns are not construction commands.</summary>
    public static class HumanConstructionCosts
    {
        internal static readonly object Gate = new object();
        private static HumanConstructionCostRegistration[] registrations = Array.Empty<HumanConstructionCostRegistration>();
        private static IMissionLifecycleCapability missions;
        private static bool runtimeAvailable;
        private static string runtimeReason = "Human construction runtime has not initialized.";
        private static Func<ConstructionObjectIdentity, int?> identityValidator;
        private static Func<ConstructionCostTarget, int, bool> quoteEligibility;
        internal static void SetRuntimeAvailability(bool available, string reason)
        { lock (Gate) { runtimeAvailable = available; runtimeReason = reason ?? "Human construction runtime is unavailable."; } }
        // Installed once by the native adapter. Called only after completed managed mission start,
        // never while archive bytes are being restored into the previous native world.
        internal static void SetIdentityValidator(Func<ConstructionObjectIdentity, int?> validator)
        { lock (Gate) identityValidator = validator ?? throw new ArgumentNullException(nameof(validator)); }
        internal static void SetQuoteEligibility(Func<ConstructionCostTarget, int, bool> eligibility)
        { lock (Gate) quoteEligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility)); }

        /// <summary>Gets the immutable sum of active Human extras only when the initialized runtime confirms
        /// this player and target would currently be charged. Free-first buildings, ineligible modes and AI
        /// return false and an empty map, as does a target without active configuration. Opposing owners'
        /// charges may cancel to an empty map while returning true; positive charges still require upfront funds.
        /// This is a quote, not affordability or construction authorization.
        /// Call synchronously under the game's existing read synchronization; it performs no resource mutation.</summary>
        public static bool TryGetEffectiveCosts(ConstructionCostTarget target, int playerId,
            out IReadOnlyDictionary<eGoods, int> values)
        {
            values = HumanConstructionGoods.Empty;
            if (!target.IsValid || !target.SupportsHumanExtraCosts || playerId <= 0) return false;
            Func<ConstructionCostTarget, int, bool> eligible;
            lock (Gate)
            {
                if (!runtimeAvailable) return false;
                bool demand = false;
                foreach (var registration in registrations)
                {
                    if (!registration.TryGetCosts(target, out _)) continue;
                    if (!registration.ArchiveWritable) return false;
                    demand = true;
                }
                if (!demand) return false;
                eligible = quoteEligibility;
            }
            // Native eligibility reads must not run while holding the registry monitor.
            try { if (eligible == null || !eligible(target, playerId)) return false; }
            catch { return false; }
            lock (Gate)
            {
                if (!runtimeAvailable) return false;
                IReadOnlyDictionary<eGoods, int> first = null;
                Dictionary<eGoods, int> sum = null;
                foreach (var registration in registrations)
                {
                    if (!registration.TryGetCosts(target, out var contribution)) continue;
                    if (!registration.ArchiveWritable) return false;
                    if (first == null) { first = contribution; continue; }
                    if (sum == null)
                    {
                        sum = new Dictionary<eGoods, int>();
                        foreach (var item in first) sum.Add(item.Key, item.Value);
                    }
                    foreach (var item in contribution)
                    {
                        long amount = (sum.TryGetValue(item.Key, out int prior) ? prior : 0L) + item.Value;
                        if (amount < int.MinValue || amount > int.MaxValue) return false;
                        if (amount == 0) sum.Remove(item.Key); else sum[item.Key] = (int)amount;
                    }
                }
                if (first == null) return false;
                if (sum == null) { values = first; return first.Count != 0; }
                if (sum.Count == 0) return true;
                values = new ReadOnlyDictionary<eGoods, int>(sum); return true;
            }
        }

        internal static bool HasDemand
        {
            get { lock (Gate) foreach (var registration in registrations) if (registration.Active && registration.HasCosts) return true; return false; }
        }
        internal static bool HasReceipts
        {
            get { lock (Gate) foreach (var registration in registrations) if (registration.Ledger.Count != 0) return true; return false; }
        }

        /// <summary>Registers once on the initialized Unity thread, in the same order on every peer. The registration
        /// and its simulation-relevant VersionedSaveData record live until process exit. Repeated owner/ID is refused.
        /// NoRecord clears payment history: old saves never invent refunds. This method does not authorize a game mode.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, string saveDataIdentifier,
            out HumanConstructionCostRegistration registration, out string reason)
        {
            registration = null;
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Human construction registration requires the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || ownerGuid.Length > 128 || registrationId.Length > 128)
            { reason = "Nonempty owner GUID and registration ID of at most 128 characters are required."; return false; }
            lock (Gate)
            {
                if (!runtimeAvailable) { reason = runtimeReason; return false; }
                foreach (var existing in registrations) if (existing.OwnerGuid == ownerGuid && existing.RegistrationId == registrationId)
                { reason = "Duplicate Human construction cost registration."; return false; }
                if (!EnsureLifecycle(out reason)) return false;
                var created = new HumanConstructionCostRegistration(ownerGuid, registrationId);
                var save = new VersionedSaveDataRegistration(ownerGuid, saveDataIdentifier, 1, 1, new[] { "payments/" },
                    writer => Capture(created, writer), load => Loaded(created, load)) { SimulationRelevant = true };
                if (!VersionedSaveData.TryRegister(save, out _, out reason)) return false;
                var next = new HumanConstructionCostRegistration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length); next[registrations.Length] = created; registrations = next;
                registration = created; reason = ""; return true;
            }
        }

        internal static bool TryBegin(ConstructionCostTarget target, int playerId, bool isHuman, bool isFree,
            IReadOnlyDictionary<eGoods, int> baseCosts, Func<eGoods, int> available,
            out HumanConstructionTransaction transaction, out eGoods missingGood)
        {
            transaction = null; missingGood = eGoods.STORED_NULL;
            if (!isHuman || isFree || playerId <= 0) return true;
            lock (Gate)
            {
                var parts = new List<HumanConstructionTransaction.Part>();
                var total = new Dictionary<eGoods, int>();
                var positive = new Dictionary<eGoods, int>();
                foreach (var registration in registrations)
                {
                    if (!registration.TryGetCosts(target, out var values)) continue;
                    if (!registration.ArchiveWritable) return false;
                    // Refuse before construction when no durable refund receipt could be retained.
                    if (registration.Ledger.Count >= HumanConstructionLedger.MaximumReceipts) return false;
                    parts.Add(new HumanConstructionTransaction.Part(registration, values));
                    foreach (var item in values)
                    {
                        total[item.Key] = checked((total.TryGetValue(item.Key, out int old) ? old : 0) + item.Value);
                        if (item.Value > 0) positive[item.Key] = checked((positive.TryGetValue(item.Key, out int prior) ? prior : 0) + item.Value);
                    }
                }
                if (parts.Count == 0) return true;
                if (available == null) throw new ArgumentNullException(nameof(available));
                var requiredGoods = new HashSet<eGoods>(positive.Keys);
                if (baseCosts != null) foreach (var item in baseCosts) if (item.Value > 0) requiredGoods.Add(item.Key);
                foreach (var good in requiredGoods)
                {
                    int basic = baseCosts != null && baseCosts.TryGetValue(good, out int value) ? value : 0;
                    // Rebates are paid after success, so cannot finance Vanilla's initial affordability check.
                    long required = (long)Math.Max(0, basic) + (positive.TryGetValue(good, out int extra) ? extra : 0);
                    if (available(good) < required) { missingGood = good; return false; }
                }
                transaction = new HumanConstructionTransaction(playerId, parts.ToArray(), new ReadOnlyDictionary<eGoods, int>(total),
                    new ReadOnlyDictionary<eGoods, int>(positive));
                return true;
            }
        }

        // Reserve extras before Vanilla can consume any base resources or run nested construction callbacks.
        internal static bool Reserve(HumanConstructionTransaction transaction, Func<IReadOnlyDictionary<eGoods, int>, bool> tryBook)
        {
            if (transaction == null) return true;
            lock (Gate)
            {
                if (transaction.Finished || transaction.Reserved || transaction.Epoch != epoch || tryBook == null) return false;
                if (transaction.Positive.Count != 0 && !tryBook(transaction.Positive)) { transaction.Finished = true; return false; }
                transaction.Book = tryBook; transaction.Reserved = true;
                return true;
            }
        }

        // The caller's atomic booking boundary is explicit: true means every signed amount was applied, false means none.
        // Positive amounts subtract goods; negative gold adds it. The final native object must already be corroborated.
        internal static bool Commit(HumanConstructionTransaction transaction, ConstructionObjectIdentity identity,
            Func<IReadOnlyDictionary<eGoods, int>, bool> tryBook)
        {
            if (transaction == null) return true;
            lock (Gate)
            {
                if (transaction.Finished || !identity.IsValid || tryBook == null) return false;
                if (transaction.Epoch != epoch) { transaction.Finished = true; return false; }
                if (!transaction.Reserved) return false;
                foreach (var part in transaction.Parts) if (!part.Registration.Ledger.CanRecord(identity))
                {
                    // The final object already exists. Keep its actual payment rather than refunding
                    // the reservation and silently admitting a free object on a duplicate/capacity error.
                    transaction.Finished = true; transaction.Reserved = false; return false;
                }
                transaction.Finished = true;
                var credits = new Dictionary<eGoods, int>();
                // Preserve credits separately from positive debits of other registrations on the same good.
                foreach (var part in transaction.Parts) foreach (var item in part.Values) if (item.Value < 0)
                    credits[item.Key] = checked((credits.TryGetValue(item.Key, out int prior) ? prior : 0) + item.Value);
                bool credited = credits.Count == 0;
                try
                {
                    if (!credited) credited = tryBook(new ReadOnlyDictionary<eGoods, int>(credits));
                    return credited;
                }
                finally
                {
                    foreach (var part in transaction.Parts)
                    {
                        if (credited) part.Registration.Ledger.Add(identity, transaction.PlayerId, part.Values);
                        else
                        {
                            // Successful construction already consumed the reservation. A failed gold-credit
                            // operation cannot turn that paid object into a free one or invent a rebate receipt.
                            var paid = new Dictionary<eGoods, int>();
                            foreach (var item in part.Values) if (item.Value > 0) paid.Add(item.Key, item.Value);
                            if (paid.Count != 0) part.Registration.Ledger.Add(identity, transaction.PlayerId, paid);
                        }
                    }
                    transaction.Reserved = false;
                }
            }
        }

        internal static void Abort(HumanConstructionTransaction transaction)
        {
            if (transaction == null) return;
            lock (Gate)
            {
                if (transaction.Finished) return;
                transaction.Finished = true;
                ReleaseReservation(transaction);
            }
        }
        private static void ReleaseReservation(HumanConstructionTransaction transaction)
        {
            if (!transaction.Reserved) return;
            transaction.Reserved = false;
            if (transaction.Epoch != epoch || transaction.Positive.Count == 0) return;
            var reverse = new Dictionary<eGoods, int>();
            foreach (var item in transaction.Positive) reverse.Add(item.Key, -item.Value);
            if (!transaction.Book(new ReadOnlyDictionary<eGoods, int>(reverse)))
                throw new InvalidOperationException("Construction reservation could not be restored.");
        }
        internal static void Retire(ConstructionObjectIdentity identity) { lock (Gate) foreach (var registration in registrations) registration.Ledger.Retire(identity); }

        // Signed negative gold must be offset against the original's gold refund, never paid as a second rebate.
        internal static bool TryConsumeRefund(ConstructionObjectIdentity identity, int actor, int currentOwner, bool isHuman,
            Func<eGoods, int, int> quoteAmount, out IReadOnlyDictionary<eGoods, int> refund)
        {
            lock (Gate)
            {
                if (!TryQuoteRefund(identity, actor, currentOwner, isHuman, quoteAmount, out refund)) return false;
                // Consume all registrations together before a resource-booking callback can re-enter.
                foreach (var registration in registrations) registration.Ledger.Retire(identity);
                return true;
            }
        }
        internal static bool TryQuoteRefund(ConstructionObjectIdentity identity, int actor, int currentOwner, bool isHuman,
            Func<eGoods, int, int> quoteAmount, out IReadOnlyDictionary<eGoods, int> refund)
        {
            lock (Gate)
            {
                bool found = false; var total = new Dictionary<eGoods, int>();
                foreach (var registration in registrations)
                    if (registration.Ledger.TryQuote(identity, actor, currentOwner, isHuman, quoteAmount, out var values))
                    {
                        found = true;
                        foreach (var item in values) total[item.Key] = checked((total.TryGetValue(item.Key, out int old) ? old : 0) + item.Value);
                    }
                refund = new ReadOnlyDictionary<eGoods, int>(total); return found;
            }
        }

        private static long epoch;
        internal static long Epoch => epoch;
        private static bool EnsureLifecycle(out string reason)
        {
            reason = ""; if (missions != null) return true;
            if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var diagnostic) ||
                !lifecycle.TryRegisterObserver("human-construction-payments", MissionStart, MissionEnd, MissionInitialization, out diagnostic))
            { reason = "Human construction payment lifecycle is unavailable: " + diagnostic?.Reason; return false; }
            missions = lifecycle; return true;
        }
        private static void MissionStart(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay) return;
            lock (Gate)
            {
                epoch++;
                foreach (var registration in registrations)
                {
                    if (!registration.LoadedSinceStart) { registration.Ledger.Clear(); registration.ArchiveWritable = true; }
                    else if (identityValidator != null) registration.Ledger.Prune(identityValidator);
                    registration.LoadedSinceStart = false;
                }
            }
        }
        private static void MissionEnd(MissionLifecycleNotification notification)
        { lock (Gate) { epoch++; foreach (var registration in registrations) registration.LoadedSinceStart = false; } }
        private static void MissionInitialization(MissionLifecycleNotification notification)
        {
            if (notification.Phase != MissionInitializationPhase.BeforeLoad) return;
            lock (Gate) { epoch++; foreach (var registration in registrations) registration.LoadedSinceStart = false; }
        }
        private static bool Capture(HumanConstructionCostRegistration registration, VersionedSaveWriter writer)
        {
            lock (Gate)
            {
                IReadOnlyList<byte[]> chunks = registration.Ledger.Encode();
                for (int i = 0; i < chunks.Count; i++) writer.Write("payments/" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), chunks[i]);
                return true;
            }
        }
        private static void Loaded(HumanConstructionCostRegistration registration, VersionedSaveLoad load)
        {
            lock (Gate)
            {
                registration.LoadedSinceStart = true; registration.Ledger.Clear();
                registration.ArchiveWritable = load.Status == VersionedSaveLoadStatus.NoRecord ||
                    load.Status == VersionedSaveLoadStatus.Restored || load.Status == VersionedSaveLoadStatus.OutOfScope ||
                    load.Status == VersionedSaveLoadStatus.Corrupt;
                if (load.Status != VersionedSaveLoadStatus.Restored) return;
                var names = new List<string>(load.Entries.Keys); names.Sort(StringComparer.Ordinal);
                var chunks = new List<byte[]>(); foreach (string name in names) if (name.StartsWith("payments/", StringComparison.Ordinal)) chunks.Add(load.Entries[name]);
                try { registration.Ledger.Restore(chunks); }
                catch { registration.ArchiveWritable = false; throw; }
            }
        }
    }

    internal sealed class HumanConstructionTransaction
    {
        internal HumanConstructionTransaction(int playerId, Part[] parts, IReadOnlyDictionary<eGoods, int> total,
            IReadOnlyDictionary<eGoods, int> positive)
        { PlayerId = playerId; Parts = parts; Total = total; Positive = positive; Epoch = HumanConstructionCosts.Epoch; }
        internal readonly int PlayerId;
        internal readonly Part[] Parts;
        internal readonly IReadOnlyDictionary<eGoods, int> Total;
        internal readonly IReadOnlyDictionary<eGoods, int> Positive;
        internal readonly long Epoch;
        internal bool Finished;
        internal bool Reserved;
        internal Func<IReadOnlyDictionary<eGoods, int>, bool> Book;
        internal readonly struct Part
        {
            internal Part(HumanConstructionCostRegistration registration, IReadOnlyDictionary<eGoods, int> values) { Registration = registration; Values = values; }
            internal readonly HumanConstructionCostRegistration Registration;
            internal readonly IReadOnlyDictionary<eGoods, int> Values;
        }
    }
}
