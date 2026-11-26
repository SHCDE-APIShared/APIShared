namespace APIShared.Sight
{
    /// <summary>Which Vanilla routine answers a <see cref="LineOfSight"/> query.</summary>
    public enum LineOfSightRoutine
    {
        /// <summary>
        /// Vanilla's sight wrapper (RVA 0xA06F0), the routine every unit sight test of the game calls: it traces
        /// from the viewer to the target and, only when that answers 0, traces back from the target to the viewer
        /// and returns the second answer. A positive result therefore means that at least one direction is clear.
        /// </summary>
        Bidirectional = 0,

        /// <summary>
        /// Vanilla's sight core (RVA 0x9E350) from the viewer to the target only, with Vanilla's obstacle checks
        /// enabled exactly as the wrapper's own first call does. Use it when both directions must be judged
        /// separately; it is never called in only this direction by Vanilla's unit code.
        /// </summary>
        Directional = 1
    }

    /// <summary>Why a <see cref="LineOfSight"/> query gave no result.</summary>
    public enum LineOfSightFailure
    {
        /// <summary>The query ran; its result is valid.</summary>
        None = 0,
        /// <summary>The service is not ready, the native image is not the audited one, or it was disabled; see
        /// <see cref="LineOfSight.UnavailableReason"/>.</summary>
        Unavailable = 1,
        /// <summary>A point lies outside the current map (negative coordinates or a tile outside the map diamond).</summary>
        OutsideMap = 2,
        /// <summary>A height lies outside +/- <see cref="LineOfSight.MaximumHeightMagnitude"/>.</summary>
        HeightOutOfRange = 3,
        /// <summary>The query was started from inside another query on the same thread.</summary>
        Reentrant = 4,
        /// <summary>A unit pointer was null or the unit ID named no existing unit record.</summary>
        InvalidUnit = 5,
        /// <summary>Vanilla wrote outside the documented private context slot; the service disabled itself for the
        /// process and every later query reports <see cref="Unavailable"/>.</summary>
        NativeContractViolation = 6,
        /// <summary>The routine value is not a defined <see cref="LineOfSightRoutine"/>.</summary>
        InvalidRoutine = 7
    }

    /// <summary>A point in Vanilla's world coordinates for a sight query.</summary>
    /// <remarks>
    /// World X and Y are fine units, <see cref="LineOfSight.TileWorldSize"/> per tile; Vanilla stores a unit at
    /// <c>tile * 8 + offset</c> and places tile centres at <c>tile * 8 + 4</c>. Height is in Vanilla's sight height
    /// units: a tile height (the byte of the tile height grid) plus building, unit and eye offsets.
    /// </remarks>
    public readonly struct SightPoint
    {
        /// <summary>Creates a point from world coordinates.</summary>
        /// <param name="x">World X (fine units).</param>
        /// <param name="y">World Y (fine units).</param>
        /// <param name="height">Sight height including every offset the caller wants Vanilla to use.</param>
        public SightPoint(int x, int y, int height)
        {
            X = x;
            Y = y;
            Height = height;
        }

        /// <summary>World X in fine units.</summary>
        public int X { get; }

        /// <summary>World Y in fine units.</summary>
        public int Y { get; }

        /// <summary>Sight height including all offsets.</summary>
        public int Height { get; }

        /// <summary>Tile X that Vanilla's sight core derives from <see cref="X"/> (truncating division by 8).</summary>
        public int TileX => X / LineOfSight.TileWorldSize;

        /// <summary>Tile Y that Vanilla's sight core derives from <see cref="Y"/> (truncating division by 8).</summary>
        public int TileY => Y / LineOfSight.TileWorldSize;
    }
}
