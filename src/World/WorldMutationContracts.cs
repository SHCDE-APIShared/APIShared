using System;

namespace APIShared.WorldMutations
{
    /// <summary>Audited operation boundaries. These are causes, not proof that geometry changed.</summary>
    [Flags]
    public enum WorldMutationSource
    {
        /// <summary>No operation.</summary>
        None = 0,
        /// <summary>Entire native AIV build step, including its earlier cleanup.</summary>
        AivBuildStep = 1,
        /// <summary>Final effective common building-construction original, for any caller.</summary>
        BuildStructure = 2,
        /// <summary>Final effective human wall-line original.</summary>
        HumanWall = 4,
        /// <summary>Final effective AI wall-tile original.</summary>
        AiWall = 8,
        /// <summary>Editor type/flag brush.</summary>
        EditorTileType = 16,
        /// <summary>Editor type-clear brush.</summary>
        EditorTileTypeClear = 32,
        /// <summary>Editor height-preset brush.</summary>
        EditorHeightPreset = 64,
        /// <summary>Editor height-raise brush.</summary>
        EditorHeightRaise = 128,
        /// <summary>Editor explicit-height brush.</summary>
        EditorHeightNumber = 256,
        /// <summary>Editor height-evening brush.</summary>
        EditorHeightEven = 512,
        /// <summary>Editor deletion brush, which may intentionally delete units.</summary>
        EditorDelete = 1024,
        /// <summary>Native moat-work dig invocation, including nonterminal progress.</summary>
        MoatDig = 2048,
        /// <summary>Native moat-work fill invocation, including nonterminal progress.</summary>
        MoatFill = 4096,
        /// <summary>Terminal building cleanup, distinct from marking a building for removal.</summary>
        BuildingRemoval = 8192,
        /// <summary>Effective wall/moat-removal original; final tile geometry still requires verification.</summary>
        WallRemoval = 16384,
        /// <summary>Every declared source. Check AvailableSources for initialized adapters.</summary>
        All = 32767
    }

    /// <summary>Immutable effective inputs of one actually attempted final original invocation.</summary>
    /// <remarks>IDs are native game IDs, not array indexes. Session and invocation numbers are process-local,
    /// not multiplayer protocol IDs. Packed tile IDs and X/Y are different domains. Read native state only
    /// synchronously in the qualified publisher context; no pointer or view survives a deferred action.</remarks>
    public sealed class WorldMutationContext
    {
        private readonly long[] arguments;
        internal WorldMutationContext(WorldMutationSource source, long invocation, long parent, long batch,
            long session, long generation, long[] args)
        {
            Source = source; InvocationId = invocation; ParentInvocationId = parent; BatchId = batch;
            SessionId = session; WorldGeneration = generation; arguments = args;
        }
        /// <summary>The effective operation, not its outer batch source.</summary>
        public WorldMutationSource Source { get; }
        /// <summary>Unique process-local invocation identity.</summary>
        public long InvocationId { get; }
        /// <summary>Enclosing observed native invocation, or zero.</summary>
        public long ParentInvocationId { get; }
        /// <summary>Outermost synchronous operation batch, shared by mirror siblings and nested writes.</summary>
        public long BatchId { get; }
        /// <summary>Central lifecycle attempt identity; zero outside an identified attempt.</summary>
        public long SessionId { get; }
        /// <summary>Native-world generation; changes on load, retirement or repair invalidation.</summary>
        public long WorldGeneration { get; }
        /// <summary>Number of source-specific native arguments, including argument zero.</summary>
        public int ArgumentCount => arguments.Length;
        /// <summary>Returns a scalar effective argument. Source-specific positions are documented in WORLD_MUTATIONS.md.
        /// Pointer-shaped argument zero is an invocation-scoped opaque address, never a retained native view.</summary>
        public long GetArgument(int index) => arguments[index];
        /// <summary>Native context address at argument zero; valid only inside the synchronous invocation.</summary>
        public ulong NativeContextAddress => unchecked((ulong)arguments[0]);
        /// <summary>One-based player for AIV/build/wall operations; zero when this source has no player argument.</summary>
        public int PlayerId => Source == WorldMutationSource.AivBuildStep || Source == WorldMutationSource.BuildStructure ||
            Source == WorldMutationSource.HumanWall || Source == WorldMutationSource.AiWall ||
            Source == WorldMutationSource.WallRemoval ? (int)arguments[1] : 0;
        /// <summary>Prepared AIV frame index, or -1 for another source.</summary>
        public int FrameIndex => Source == WorldMutationSource.AivBuildStep ? (int)arguments[2] : -1;
        /// <summary>Unnormalized building/wall anchor X, or -1 for a source expressed as a packed center tile.</summary>
        public int X => Source == WorldMutationSource.BuildStructure || Source == WorldMutationSource.HumanWall ||
            Source == WorldMutationSource.AiWall ? (int)arguments[2] : Source == WorldMutationSource.EditorDelete ? (int)arguments[1] : -1;
        /// <summary>Unnormalized building/wall anchor Y, or -1 for a source expressed as a packed center tile.</summary>
        public int Y => Source == WorldMutationSource.BuildStructure || Source == WorldMutationSource.HumanWall ||
            Source == WorldMutationSource.AiWall ? (int)arguments[3] : Source == WorldMutationSource.EditorDelete ? (int)arguments[2] : -1;
        /// <summary>Human wall-line final X; otherwise X.</summary>
        public int EndX => Source == WorldMutationSource.HumanWall ? (int)arguments[4] : X;
        /// <summary>Human wall-line final Y; otherwise Y.</summary>
        public int EndY => Source == WorldMutationSource.HumanWall ? (int)arguments[5] : Y;
        /// <summary>Effective native mapper for building/wall sources, otherwise -1. Cast through the SDK mapper enum.</summary>
        public int Mapper => Source == WorldMutationSource.BuildStructure || Source == WorldMutationSource.AiWall ?
            (int)arguments[4] : Source == WorldMutationSource.HumanWall ? (int)arguments[6] : -1;
        /// <summary>Effective scale or brush size. This is not a guaranteed complete mutation envelope.</summary>
        public int Size => Source == WorldMutationSource.BuildStructure ? (int)arguments[5] :
            Source == WorldMutationSource.EditorTileType ? (int)arguments[1] : IsEditor ? (int)arguments[3] : 0;
        /// <summary>Effective building direction/selector argument; otherwise zero. Inspect the created compound for actual direction.</summary>
        public int Direction => Source == WorldMutationSource.BuildStructure ? (int)arguments[6] : 0;
        /// <summary>Native moat task slot, or zero for another source.</summary>
        public int TaskSlot => Source == WorldMutationSource.MoatDig || Source == WorldMutationSource.MoatFill ? (int)arguments[1] : 0;
        /// <summary>Building ID for terminal cleanup, or zero for another source.</summary>
        public int ObjectId => Source == WorldMutationSource.BuildingRemoval ? (int)arguments[1] : 0;
        /// <summary>Packed editor center tile ID; -1 for XY deletion and other sources.</summary>
        public int TileId => Source == WorldMutationSource.WallRemoval ? (int)arguments[2] : !IsEditor || Source == WorldMutationSource.EditorDelete ? -1 :
            (int)arguments[Source == WorldMutationSource.EditorTileType ? 2 : 1];
        /// <summary>Whether this is one of the seven editor brush sources.</summary>
        public bool IsEditor => (Source & (WorldMutationSource.EditorTileType | WorldMutationSource.EditorTileTypeClear |
            WorldMutationSource.EditorHeightPreset | WorldMutationSource.EditorHeightRaise | WorldMutationSource.EditorHeightNumber |
            WorldMutationSource.EditorHeightEven | WorldMutationSource.EditorDelete)) != 0;
    }

    /// <summary>One original call's outcome; child completion is not permission to mutate the world.</summary>
    public sealed class WorldMutationCompletion
    {
        internal WorldMutationCompletion(WorldMutationContext context) { Context = context; }
        internal void Set(bool invoked, bool returned, long result, Exception error)
        { OriginalInvoked = invoked; OriginalReturned = returned; ReturnValue = result; Failure = error; }
        /// <summary>The exact immutable begin context.</summary>
        public WorldMutationContext Context { get; }
        /// <summary>Whether the actual original was attempted.</summary>
        public bool OriginalInvoked { get; private set; }
        /// <summary>Whether the actual original returned normally; no semantic success is implied.</summary>
        public bool OriginalReturned { get; private set; }
        /// <summary>Unchanged integral native result; zero for void or incomplete calls.</summary>
        public long ReturnValue { get; private set; }
        /// <summary>Original failure when available, never formatted by the broker outside its protected reporter.</summary>
        public Exception Failure { get; private set; }
    }

    /// <summary>Outermost operation completion, after mirror siblings, nested originals and wrapper Post callbacks.</summary>
    public sealed class WorldMutationBatchCompletion
    {
        internal WorldMutationBatchCompletion(long id, long session, long generation, WorldMutationSource boundary)
        { BatchId = id; SessionId = session; WorldGeneration = generation; BoundarySource = boundary; }
        internal void Set(bool returned, bool failed, bool current) { ReturnedNormally = returned; ContainsOriginalFailure = failed; IsCurrentWorld = current; }
        /// <summary>Matches the invocation contexts in this batch.</summary>
        public long BatchId { get; }
        /// <summary>Central attempt identity captured at begin.</summary>
        public long SessionId { get; }
        /// <summary>Native-world generation captured at begin.</summary>
        public long WorldGeneration { get; }
        /// <summary>The outer wrapper or AIV frame that owns this batch.</summary>
        public WorldMutationSource BoundarySource { get; }
        /// <summary>All enclosing wrapper scopes returned normally. This does not mean every placement succeeded.</summary>
        public bool ReturnedNormally { get; private set; }
        /// <summary>At least one observed original failed or was not invoked.</summary>
        public bool ContainsOriginalFailure { get; private set; }
        /// <summary>The same identified native world is still current at publication.</summary>
        public bool IsCurrentWorld { get; private set; }
    }

    /// <summary>Consumer-private per-invocation state, retained only for this synchronous operation.</summary>
    public interface IWorldMutationInvocation
    {
        /// <summary>Called once in reverse observer order. Queue evidence here; do not perform early child rescue.</summary>
        void Complete(WorldMutationCompletion completion);
    }
    /// <summary>Observes effective original inputs and their complete outer batch.</summary>
    public interface IWorldMutationObserver
    {
        /// <summary>Captures required old state or returns null. Runs directly before the effective original.</summary>
        IWorldMutationInvocation TryBegin(WorldMutationContext context);
        /// <summary>Runs after the entire outer operation. Revalidate identity and actual geometry before any qualified mutation.</summary>
        void OnBatchCompleted(WorldMutationBatchCompletion completion);
    }
}
