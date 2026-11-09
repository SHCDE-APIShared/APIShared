using System;
using System.Collections.Generic;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary
{
    /// <summary>Semantic army roles. Values are API identities, never native role or group numbers.</summary>
    public enum AiMilitaryRole
    {
        /// <summary>Protected from automatic military AI admission.</summary>
        None,
        /// <summary>Native automatic castle positions and reserve.</summary>
        CastleDefense,
        /// <summary>Mobile defense patrols.</summary>
        DefensePatrol,
        /// <summary>Dig the owner's castle moat.</summary>
        HomeMoat,
        /// <summary>Protect the economy.</summary>
        EconomyProtection,
        /// <summary>Native bodyguard/intervention reserve.</summary>
        Intervention,
        /// <summary>Type-selected harassment force.</summary>
        Harassment,
        /// <summary>Free harassment engineer reserve.</summary>
        HarassmentEngineers,
        /// <summary>Free siege engineer reserve.</summary>
        SiegeEngineers,
        /// <summary>Fill an enemy moat.</summary>
        SiegeMoat,
        /// <summary>Siege assassins.</summary>
        SiegeAssassins,
        /// <summary>Siege diversion force.</summary>
        SiegeDiversion,
        /// <summary>Siege ladder carriers.</summary>
        SiegeLadders,
        /// <summary>Siege tunnelers.</summary>
        SiegeTunnelers,
        /// <summary>Main siege assault force.</summary>
        SiegeStorm,
        /// <summary>Siege covering force.</summary>
        SiegeCover,
        /// <summary>Siege shock force.</summary>
        SiegeShock,
        /// <summary>Siege reserve force.</summary>
        SiegeReserve,
        /// <summary>Siege wall assault force.</summary>
        SiegeWall
    }

    /// <summary>Presentation grouping, independent of native physical groups.</summary>
    public enum AiMilitaryArea
    {
        /// <summary>No military AI.</summary>
        None,
        /// <summary>Castle and economy defense.</summary>
        Defense,
        /// <summary>Harassment forces and their engineer pool.</summary>
        Harassment,
        /// <summary>Siege forces and their engineer pool.</summary>
        Siege
    }

    /// <summary>Only recruitment quotas change; actual military strength always includes the unit.</summary>
    public enum AiRecruitmentMode
    {
        /// <summary>The unit satisfies the native recruitment quota.</summary>
        Regular,
        /// <summary>The unit does not replace regular recruitment.</summary>
        Additional
    }

    /// <summary>A one-based slot and its nonzero native global identity. Never reuse a reference across worlds.</summary>
    public readonly struct AiUnitRef : IEquatable<AiUnitRef>
    {
        /// <summary>Creates an identity reference; operations validate its current world and native record.</summary>
        public AiUnitRef(int unitId, uint globalId) { UnitId = unitId; GlobalId = globalId; }
        /// <summary>One-based native unit ID.</summary>
        public int UnitId { get; }
        /// <summary>Native generation identity, not an owner or type.</summary>
        public uint GlobalId { get; }
        /// <inheritdoc/>
        public bool Equals(AiUnitRef other) => UnitId == other.UnitId && GlobalId == other.GlobalId;
        /// <inheritdoc/>
        public override bool Equals(object value) => value is AiUnitRef other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => unchecked(UnitId * 397 ^ (int)GlobalId);
        /// <inheritdoc/>
        public override string ToString() => UnitId + "/" + GlobalId;
    }

    /// <summary>Observed physical group. Desired assignments never persist this as a target.</summary>
    public readonly struct AiGroupRef
    {
        /// <summary>Creates an observed group identity.</summary>
        public AiGroupRef(int tribeId, uint globalId, int playerId)
        { TribeId = tribeId; GlobalId = globalId; PlayerId = playerId; }
        /// <summary>Native group ID; zero denotes no observed group.</summary>
        public int TribeId { get; }
        /// <summary>Native group identity.</summary>
        public uint GlobalId { get; }
        /// <summary>One-based owner.</summary>
        public int PlayerId { get; }
    }

    /// <summary>A semantic role and an independent recruitment policy.</summary>
    public readonly struct AiMilitaryAssignment
    {
        /// <summary>Creates an assignment. None uses regular accounting, because no AI quota applies.</summary>
        public AiMilitaryAssignment(AiMilitaryRole role, AiRecruitmentMode recruitmentMode)
        { Role = role; RecruitmentMode = role == AiMilitaryRole.None ? AiRecruitmentMode.Regular : recruitmentMode; }
        /// <summary>Desired semantic role.</summary>
        public AiMilitaryRole Role { get; }
        /// <summary>Whether the unit satisfies recruitment quotas.</summary>
        public AiRecruitmentMode RecruitmentMode { get; }
    }

    /// <summary>Availability or completion reason. Detail is diagnostic text, not a stable protocol.</summary>
    public enum AiMilitaryReason
    {
        /// <summary>No failure.</summary>
        None,
        /// <summary>A required backend is unavailable.</summary>
        Unavailable,
        /// <summary>Role and type are outside the supported contract.</summary>
        UnsupportedRoleType,
        /// <summary>No live AI/AIC is initialized for this owner.</summary>
        NoAiContext,
        /// <summary>The configured AIC disagrees with the assignment.</summary>
        AicMismatch,
        /// <summary>Native configured group bounds are invalid.</summary>
        InvalidGroupCount,
        /// <summary>A machine needs its complete crew.</summary>
        IncompleteCrew,
        /// <summary>The unit or group no longer has the expected identity.</summary>
        StaleIdentity,
        /// <summary>No native group can be allocated.</summary>
        NoAvailableGroup,
        /// <summary>No fresh suitable task can be reserved.</summary>
        NoAvailableNativeJob,
        /// <summary>A required route failed.</summary>
        NoReachablePath,
        /// <summary>The current group order cannot be established reliably.</summary>
        CurrentOrderUnknown,
        /// <summary>An existing native/event policy refused the change.</summary>
        NativeVeto,
        /// <summary>The current session forbids local simulation changes.</summary>
        MutationNotAllowed,
        /// <summary>The initialization window ended without a valid unit.</summary>
        InitializationFailed,
        /// <summary>The request belongs to an ended world.</summary>
        WorldEnded,
        /// <summary>The unit has an active or foreign-owned job/protection.</summary>
        ExistingAssignment,
        /// <summary>The native transaction did not pass its postconditions.</summary>
        AssignmentFailed
    }

    /// <summary>Requests and declarations are distinct from completed native admission.</summary>
    public enum AiMilitaryAssignmentStatus
    {
        /// <summary>Accepted, waiting for native unit initialization and a quiescent point.</summary>
        Queued,
        /// <summary>Stored for editor/map-start application.</summary>
        Declared,
        /// <summary>Membership, role and appropriate current order passed postconditions.</summary>
        Applied,
        /// <summary>Not applied; inspect Reason.</summary>
        Rejected
    }

    /// <summary>Immutable availability snapshot. Queries do not allocate native groups or reserve tasks.</summary>
    public sealed class AiMilitaryOption
    {
        internal AiMilitaryOption(AiMilitaryRole role, AiMilitaryArea area, bool available, bool deferred,
            AiMilitaryReason reason, string detail)
        { Role = role; Area = area; IsAvailable = available; IsDeferred = deferred; Reason = reason; Detail = detail ?? string.Empty; }
        /// <summary>Semantic role, suitable as a localization key suffix.</summary>
        public AiMilitaryRole Role { get; }
        /// <summary>Menu area.</summary>
        public AiMilitaryArea Area { get; }
        /// <summary>True when this role can be selected; admission revalidates current state.</summary>
        public bool IsAvailable { get; }
        /// <summary>True when final AIC validation is intentionally deferred until map start.</summary>
        public bool IsDeferred { get; }
        /// <summary>Availability reason.</summary>
        public AiMilitaryReason Reason { get; }
        /// <summary>Technical explanatory text; consumers localize Reason rather than parsing this string.</summary>
        public string Detail { get; }
    }

    /// <summary>Immutable result for one exact unit. Queued is never a completed admission.</summary>
    public sealed class AiMilitaryAssignmentResult
    {
        internal AiMilitaryAssignmentResult(AiUnitRef unit, AiMilitaryAssignment assignment,
            AiMilitaryAssignmentStatus status, AiMilitaryReason reason, string detail, AiGroupRef group = default)
        { Unit = unit; Assignment = assignment; Status = status; Reason = reason; Detail = detail ?? string.Empty; Group = group; }
        /// <summary>Exact affected unit.</summary>
        public AiUnitRef Unit { get; }
        /// <summary>Requested semantic assignment.</summary>
        public AiMilitaryAssignment Assignment { get; }
        /// <summary>Current completion status.</summary>
        public AiMilitaryAssignmentStatus Status { get; }
        /// <summary>Failure or availability reason.</summary>
        public AiMilitaryReason Reason { get; }
        /// <summary>Diagnostic detail, not stable localized text.</summary>
        public string Detail { get; }
        /// <summary>Validated group after successful admission.</summary>
        public AiGroupRef Group { get; }
    }

    /// <summary>Invocation-local capture of primary units created by the enclosed placement call.
    /// Complete exactly once under the engine monitor, after the native call returns. Dispose on the
    /// same thread in finally; it ends capture, never unhooks a publisher. No units are replayed.</summary>
    public interface IAiMilitarySpawnScope : IDisposable
    {
        /// <summary>Protects captured primary units and queues admission, or records editor declarations.
        /// Returns one initial result per unit. Later completion uses the supplied callback.</summary>
        IReadOnlyList<AiMilitaryAssignmentResult> Complete();
    }

    /// <summary>Owner-bound military admission and map declarations. The service and publishers live
    /// for the process. Queries serialize native reads with the engine monitor. Completion callbacks
    /// run on the simulation/quiescent caller, are exception-isolated and must dispatch UI work.
    /// Local mutation is refused in real multiplayer; authored maps apply deterministically on load.</summary>
    public interface IAiMilitaryCapability
    {
        /// <summary>Reads supported roles without allocating native groups or changing AIC cursors.</summary>
        IReadOnlyList<AiMilitaryOption> GetOptions(int playerId, eChimps unitType, bool mapEditor);
        /// <summary>Begins capture on the calling placement thread. A refused scope makes no changes.
        /// The caller must hold the engine monitor until Complete and Dispose have both run.</summary>
        bool TryBeginSpawnScope(int playerId, eChimps primaryType, AiMilitaryAssignment assignment,
            bool mapEditor, bool applyInEditor, Action<AiMilitaryAssignmentResult> completed,
            out IAiMilitarySpawnScope scope, out string reason);
        /// <summary>Queues a free unit or this owner's protected unit. True accepts the request,
        /// not the native assignment. Completion occurs once unless the callback itself throws.</summary>
        bool RequestAssignment(AiUnitRef unit, AiMilitaryAssignment assignment,
            Action<AiMilitaryAssignmentResult> completed, out string reason);
        /// <summary>Stores a declaration for an existing editor unit. Final AIC checks occur on map start.</summary>
        bool TryDeclareMapAssignment(AiUnitRef unit, AiMilitaryAssignment assignment, out string reason);
        /// <summary>Reads this world's tracked assignment for the exact unit identity.</summary>
        bool TryGetState(AiUnitRef unit, out AiMilitaryAssignmentResult state);
        /// <summary>Identity-checked pure recruitment marker query. No native reads, allocation or mutation.</summary>
        bool IsExcludedFromRecruitmentQuota(AiUnitRef unit);
    }
}
