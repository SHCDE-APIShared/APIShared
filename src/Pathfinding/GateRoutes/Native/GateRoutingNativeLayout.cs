using System;

namespace APIShared.GateRouting.Native
{
    /// <summary>Audited native layout used with <see cref="GateRoutingNativeHooks"/> by a gate-routing provider.</summary>
    /// <remarks>
    /// Every value belongs to the CrusaderDE.dll identified by <see cref="ReferenceSha256"/>. They are properties,
    /// not constants, so a provider reads the values of the installed APIShared at run time: after a game update an
    /// APIShared update alone refreshes them. Use them only after <see cref="GateRoutingNativeHooks.TryRequest"/>
    /// succeeded or the Validate methods passed for the loaded image; they never prove a different DLL. RVAs are
    /// relative to the loaded module base; record offsets are relative to native R9 at the capturer-comparison
    /// callbacks. Thread independent and side-effect free.
    /// </remarks>
    public static class GateRoutingNativeLayout
    {
        /// <summary>SHA-256 of the audited CrusaderDE.dll.</summary>
        public static string ReferenceSha256 => EnemyGatePathfindingNativeDefinition.ReferenceSha256;
        /// <summary>Map width and height in tiles.</summary>
        public static int MapGridWidth => EnemyGatePathfindingNativeDefinition.MapGridWidth;
        /// <summary>Exclusive upper bound of native tile IDs; the size of a per-tile mask.</summary>
        public static int MaximumTileIdExclusive => EnemyGatePathfindingNativeDefinition.MaximumTileIdExclusive;
        /// <summary>Node limit Vanilla passes to the direct cursor tile search.</summary>
        public static int DirectCursorSearchNodeLimit => EnemyGatePathfindingNativeDefinition.DirectCursorSearchNodeLimit;
        /// <summary>Entry of the direct tile search (<see cref="GateRoutingNativeHooks.DirectTileSearchDelegate"/>).</summary>
        public static int DirectTileSearchRva => EnemyGatePathfindingNativeDefinition.DirectTileSearchRva;
        /// <summary>Entry of the PCL reachability query (<see cref="GateRoutingNativeHooks.PclReachabilityDelegate"/>).</summary>
        public static int PclReachabilityRva => EnemyGatePathfindingNativeDefinition.PclReachabilityRva;
        /// <summary>Int32 ID of the active local player.</summary>
        public static int ActivePlayerIdRva => EnemyGatePathfindingNativeDefinition.ActivePlayerIdRva;
        /// <summary>Int32 X tile coordinate under the mouse cursor.</summary>
        public static int CursorMouseTileXRva => EnemyGatePathfindingNativeDefinition.CursorMouseTileXRva;
        /// <summary>Int32 Y tile coordinate under the mouse cursor.</summary>
        public static int CursorMouseTileYRva => EnemyGatePathfindingNativeDefinition.CursorMouseTileYRva;
        /// <summary>Int32 tile ID under the mouse cursor.</summary>
        public static int CursorMouseTileIdRva => EnemyGatePathfindingNativeDefinition.CursorMouseTileIdRva;
        /// <summary>Row table: the Int32 at <c>TileRowStartRva + y * 12</c> is the tile ID of (0, y); add x for (x, y).</summary>
        public static int TileRowStartRva => EnemyGatePathfindingNativeDefinition.TileRowStartRva;
        /// <summary>Native path manager passed as <c>manager</c> to the movement callbacks.</summary>
        public static int NativePathManagerRva => EnemyGatePathfindingNativeDefinition.NativePathManagerRva;
        /// <summary>Byte stride of one native tribe record (the <c>tribeManager</c> callbacks).</summary>
        public static int NativeTribeRecordStride => EnemyGatePathfindingNativeDefinition.NativeTribeRecordStride;
        /// <summary>Offset of the player ID within a native tribe record.</summary>
        public static int NativeTribePlayerIdOffset => EnemyGatePathfindingNativeDefinition.NativeTribePlayerIdOffset;
        /// <summary>Displacement of the UInt16 captured-by-player table read by both capturer comparisons
        /// (base register RAX for the PCL graph, R13 for the builder precheck, plus the building stride in RDX).</summary>
        public static int CapturedByPlayerTableDisplacement => EnemyGatePathfindingNativeDefinition.CapturedByPlayerTableDisplacement;
        /// <summary>Displaced block of the PCL-graph capturer comparison (the <c>pcl</c> callback of InstallComparisons).</summary>
        public static int PclGraphCapturedByFilterRva => EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterRva;
        /// <summary>Exact displaced length of the PCL-graph capturer block.</summary>
        public static int PclGraphCapturedByFilterHookLength => EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterHookLength;
        /// <summary>Unique byte pattern of the PCL-graph capturer block, for an independent signature check.</summary>
        public static string PclGraphCapturedByComparePattern => EnemyGatePathfindingNativeDefinition.PclGraphCapturedByComparePattern;
        /// <summary>Offset of the displaced block within <see cref="PclGraphCapturedByComparePattern"/>.</summary>
        public static int PclGraphCapturedByCompareOffsetInPattern => EnemyGatePathfindingNativeDefinition.PclGraphCapturedByCompareOffsetInPattern;
        /// <summary>Displaced block of the builder-precheck capturer comparison (the <c>builder</c> callback).</summary>
        public static int BuilderPrecheckCapturedByFilterRva => EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterRva;
        /// <summary>Exact displaced length of the builder-precheck capturer block.</summary>
        public static int BuilderPrecheckCapturedByFilterHookLength => EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength;
        /// <summary>Unique byte pattern of the builder-precheck capturer block.</summary>
        public static string BuilderPrecheckCapturedByComparePattern => EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByComparePattern;
        /// <summary>Offset of the displaced block within <see cref="BuilderPrecheckCapturedByComparePattern"/>.</summary>
        public static int BuilderPrecheckCapturedByCompareOffsetInPattern => EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByCompareOffsetInPattern;
        /// <summary>Int32 building ID of the compared record, relative to R9.</summary>
        public static int RecordBuildingIdOffset => EnemyGatePathfindingNativeDefinition.RecordBuildingIdOffset;
        /// <summary>UInt32 global ID of the searching subject, relative to R9.</summary>
        public static int RecordSubjectGlobalIdOffset => EnemyGatePathfindingNativeDefinition.RecordSubjectGlobalIdOffset;
        /// <summary>Owner player ID of the compared record, relative to R9.</summary>
        public static int RecordOwnerPlayerIdOffset => EnemyGatePathfindingNativeDefinition.RecordOwnerPlayerIdOffset;
        /// <summary>First PCL of the compared record, relative to R9.</summary>
        public static int RecordFirstPclOffset => EnemyGatePathfindingNativeDefinition.RecordFirstPclOffset;
        /// <summary>Second PCL of the compared record, relative to R9.</summary>
        public static int RecordSecondPclOffset => EnemyGatePathfindingNativeDefinition.RecordSecondPclOffset;
        /// <summary>Third PCL of the compared record, relative to R9.</summary>
        public static int RecordThirdPclOffset => EnemyGatePathfindingNativeDefinition.RecordThirdPclOffset;

        /// <summary>Checks the capturer-comparison blocks, including both surrounding branches, in the loaded image.</summary>
        /// <param name="memory">The complete loaded CrusaderDE.dll image.</param>
        /// <exception cref="InvalidOperationException">A byte or boundary differs from the audited contract.</exception>
        public static void ValidateComparisonSites(ReadOnlySpan<byte> memory) =>
            EnemyGatePathfindingNativeDefinition.ValidateNativeHookContracts(memory);

        /// <summary>Checks every movement site of <see cref="GateRoutingNativeHooks.InstallMovement"/> in the loaded image.</summary>
        /// <param name="memory">The complete loaded CrusaderDE.dll image.</param>
        /// <param name="sharedHookOwner">True when the caller itself already detoured the shared function entries
        /// (path builder, approaches, consumer, cursor stager); their prologs are then not compared.</param>
        /// <exception cref="InvalidOperationException">A byte or boundary differs from the audited contract.</exception>
        public static void ValidateMovementSites(ReadOnlySpan<byte> memory, bool sharedHookOwner) =>
            EnemyGatePathfindingNativeDefinition.ValidateSamePclNativeFilterContracts(memory, sharedHookOwner);
    }
}
