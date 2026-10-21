using System;
using System.Collections.Generic;

namespace APIShared
{
    /// <summary>Castle (AIV) source for one Coop Trail AI slot.</summary>
    public enum CoopAiCastleSource
    {
        /// <summary>Vanilla's standard castles of the slot's Lord type.</summary>
        Standard = 0,
        /// <summary>Vanilla's community castle set of the slot's Lord type.</summary>
        Community = 1,
        /// <summary>Vanilla's historical castle of the slot's Lord type.</summary>
        Historical = 2,
        /// <summary>Caller-supplied castle candidates, imported in the given order.</summary>
        Explicit = 3,
    }

    /// <summary>
    /// Immutable import plan for one AI slot of one Coop Trail start attempt.
    /// </summary>
    /// <remarks>
    /// Player IDs are 1-based (2..8); native AIV banks are the zero-based <c>PlayerId - 1</c>. <see cref="LordType"/>
    /// is the Lord type Vanilla passes to its default-castle upload for this slot; a mismatch at start aborts the
    /// attempt. Candidates are copied on construction and on read. An optional Lord configuration (custom AIC) is
    /// copied by value and handed to the game exactly once, after the castles of this slot.
    /// <para>An explicit plan whose candidate list is known to be the complete current selection is complete.
    /// A provider that only knows a prefix, such as the single candidate Vanilla's lobby data transmits, marks the
    /// plan incomplete. See <see cref="ICoopAiStartImportCapability"/> for how plans of several owners combine.</para>
    /// </remarks>
    public sealed class CoopAiSlotPlan
    {
        /// <summary>Maximum number of explicit castle candidates per slot.</summary>
        public const int MaximumCandidates = 50;

        private readonly short[][] candidates;
        private readonly EngineInterface.AILordConfigTransferData lordConfiguration;

        /// <summary>Creates a validated plan. Explicit sources need 1..<see cref="MaximumCandidates"/> non-empty
        /// candidates; the other sources must not carry candidates and are always complete. Invalid input throws
        /// ArgumentException.</summary>
        public CoopAiSlotPlan(int playerId, int lordType, CoopAiCastleSource castleSource,
            IReadOnlyList<short[]> candidates, EngineInterface.AILordConfigTransferData? lordConfiguration,
            bool candidateListComplete = true)
        {
            if (playerId < 2 || playerId > 8)
                throw new ArgumentOutOfRangeException(nameof(playerId), "Coop AI player IDs are 2 through 8.");
            if (lordType < 0)
                throw new ArgumentOutOfRangeException(nameof(lordType));
            if (castleSource < CoopAiCastleSource.Standard || castleSource > CoopAiCastleSource.Explicit)
                throw new ArgumentOutOfRangeException(nameof(castleSource));
            int count = candidates?.Count ?? 0;
            if (castleSource == CoopAiCastleSource.Explicit ? count < 1 || count > MaximumCandidates : count != 0)
                throw new ArgumentException("Only explicit castle sources carry 1 to " + MaximumCandidates +
                    " candidates.", nameof(candidates));
            this.candidates = new short[count][];
            for (int index = 0; index < count; index++)
            {
                short[] data = candidates[index];
                if (data == null || data.Length == 0)
                    throw new ArgumentException("Castle candidate " + index + " is empty.", nameof(candidates));
                this.candidates[index] = (short[])data.Clone();
            }
            PlayerId = playerId;
            LordType = lordType;
            CastleSource = castleSource;
            HasLordConfiguration = lordConfiguration.HasValue;
            this.lordConfiguration = lordConfiguration.GetValueOrDefault();
            CandidateListComplete = castleSource != CoopAiCastleSource.Explicit || candidateListComplete;
        }

        /// <summary>1-based player ID, 2..8.</summary>
        public int PlayerId { get; }
        /// <summary>Lord type expected at Vanilla's default-castle upload for this slot.</summary>
        public int LordType { get; }
        /// <summary>Castle source.</summary>
        public CoopAiCastleSource CastleSource { get; }
        /// <summary>Number of explicit castle candidates; 0 for Vanilla sources.</summary>
        public int CandidateCount => candidates.Length;
        /// <summary>True when a custom Lord configuration (AIC) is transferred after the castles.</summary>
        public bool HasLordConfiguration { get; }
        /// <summary>False only for an explicit plan that knows a prefix of the selection's candidates.</summary>
        public bool CandidateListComplete { get; }
        /// <summary>Copy of the custom Lord configuration; default when <see cref="HasLordConfiguration"/> is false.</summary>
        public EngineInterface.AILordConfigTransferData LordConfiguration => lordConfiguration;

        /// <summary>Returns a copy of one explicit castle candidate.</summary>
        public short[] CopyCandidate(int index) => (short[])candidates[index].Clone();

        internal short[] CandidateView(int index) => candidates[index];

        // Two owners describing exactly the same import do not conflict; any difference does.
        internal bool HasSameImport(CoopAiSlotPlan other) =>
            other != null && other.candidates.Length == candidates.Length && other.IsPrefixOf(this);

        // True when this plan agrees with the other in everything and its candidates are the other's first ones.
        internal bool IsPrefixOf(CoopAiSlotPlan other)
        {
            if (other == null || other.PlayerId != PlayerId || other.LordType != LordType ||
                other.CastleSource != CastleSource || other.HasLordConfiguration != HasLordConfiguration ||
                candidates.Length > other.candidates.Length)
                return false;
            if (HasLordConfiguration && !lordConfiguration.Equals(other.lordConfiguration))
                return false;
            for (int index = 0; index < candidates.Length; index++)
            {
                short[] left = candidates[index], right = other.candidates[index];
                if (left.Length != right.Length) return false;
                for (int item = 0; item < left.Length; item++)
                    if (left[item] != right[item]) return false;
            }
            return true;
        }
    }

    /// <summary>Immutable description of the Coop Trail start attempt a provider is asked about.</summary>
    public sealed class CoopAiStartRequest
    {
        internal CoopAiStartRequest(long attemptId, int coopTrail, int coopMission)
        {
            AttemptId = attemptId;
            CoopTrail = coopTrail;
            CoopMission = coopMission;
        }

        /// <summary>Process-local, increasing attempt identifier.</summary>
        public long AttemptId { get; }
        /// <summary>Vanilla Coop Trail number passed to the native multiplayer initialization (1..4).</summary>
        public int CoopTrail { get; }
        /// <summary>Vanilla Coop mission identifier passed to the native multiplayer initialization.</summary>
        public int CoopMission { get; }
    }

    /// <summary>One provider's answer for a start attempt.</summary>
    public sealed class CoopAiStartContribution
    {
        private static readonly CoopAiSlotPlan[] NoPlans = new CoopAiSlotPlan[0];

        /// <summary>Creates a contribution. A provider may plan each player at most once.</summary>
        public CoopAiStartContribution(IReadOnlyList<CoopAiSlotPlan> plans)
        {
            var copied = new CoopAiSlotPlan[plans?.Count ?? 0];
            var players = new HashSet<int>();
            for (int index = 0; index < copied.Length; index++)
            {
                copied[index] = plans[index] ?? throw new ArgumentException("Plans must not be null.", nameof(plans));
                if (!players.Add(copied[index].PlayerId))
                    throw new ArgumentException("Player " + copied[index].PlayerId + " is planned twice.", nameof(plans));
            }
            Plans = copied.Length == 0 ? NoPlans : copied;
        }

        /// <summary>Slot plans of this provider.</summary>
        public IReadOnlyList<CoopAiSlotPlan> Plans { get; }
    }

    /// <summary>
    /// Shared owner of Vanilla's Coop Trail default-castle upload. Providers describe AI castles and custom Lord
    /// configurations for a start attempt; APIShared imports them at the audited native start point.
    /// </summary>
    /// <remarks>
    /// <para>Timing: providers run once per Coop Trail start attempt on the Unity thread, inside the native
    /// multiplayer initialization that precedes the AI upload loop, in registration order and outside registry
    /// locks. They must only read already prepared in-memory state (no UI, file or network work) and confirm
    /// selection, session and host authority themselves; APIShared never substitutes a local file for a missing
    /// confirmation. Returning null contributes nothing.</para>
    /// <para>Execution: for a planned slot Vanilla's upload is replaced exactly once: Vanilla castle sources use
    /// Vanilla's own upload with the requested set, explicit candidates are imported into the zero-based bank
    /// <c>PlayerId - 1</c> in order, then the optional Lord configuration is set. No additional AIV reset is
    /// performed. Unplanned slots and starts without plans keep Vanilla behavior unchanged.</para>
    /// <para>Several owners: complete plans for the same player must be identical and are imported once; an
    /// incomplete plan must be a prefix of the complete plan (same Lord type, source and AIC, equal leading
    /// candidates), which then supplies the candidates. Without a complete plan, identical incomplete plans import
    /// their known candidates (logged at debug level). This is a data rule, not a provider priority.</para>
    /// <para>Failure: a provider exception, contradicting plans for the same player, a Lord type mismatch or a
    /// repeated upload for a slot aborts the attempt with <see cref="InvalidOperationException"/> at the first AI
    /// upload; nothing falls back to Vanilla castles. Consumers should validate earlier, at their own start
    /// buttons, to avoid reaching this abort.</para>
    /// <para>Lifetime: registrations are process-wide and never removed; control participation through the
    /// provider result. Plans exist only for the current attempt and are discarded when it finishes.</para>
    /// </remarks>
    public interface ICoopAiStartImportCapability
    {
        /// <summary>Registers a provider under an owner-local stable ID. Requires the Unity thread.
        /// A duplicate ID or a null provider is rejected.</summary>
        bool TryRegisterProvider(string registrationId, Func<CoopAiStartRequest, CoopAiStartContribution> provider,
            out NativeCapabilityDiagnostic diagnostic);
    }
}
