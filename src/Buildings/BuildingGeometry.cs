namespace APIShared.Buildings
{
    /// <summary>Pure geometry helpers for building footprints.</summary>
    public static class BuildingGeometry
    {
        internal const int NativeUnitsPerTile = 8;
        internal const int HalfTileNativeUnits = NativeUnitsPerTile / 2;

        /// <summary>Finds the center of a square footprint in eighths of a tile.</summary>
        /// <remarks>
        /// The begin coordinates are the minimum tile coordinates on each axis. Grid size is
        /// the number of tiles per side. The result uses native world coordinates, where
        /// a tile starts at tile * 8 and its center is tile * 8 + 4.
        /// Returns false with zero outputs for a nonpositive size or a center outside Int32.
        /// The method needs no game state and can be called from any thread.
        /// </remarks>
        public static bool TryGetSquareFootprintCenter(
            int beginX, int beginY, int gridSize, out int centerX, out int centerY)
        {
            centerX = centerY = 0;
            if (gridSize <= 0)
                return false;

            // Tile * 8 is the tile boundary. The editor passes that boundary to 0x17FEF0
            // from 0x118CA0, and the spawn writes tile * 8 + 4 as the unit's world position.
            // Use half the full width so the midpoint uses the same coordinate system.
            long offset = (long)gridSize * HalfTileNativeUnits;
            long x = (long)beginX * NativeUnitsPerTile + offset;
            long y = (long)beginY * NativeUnitsPerTile + offset;
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue)
                return false;

            centerX = (int)x;
            centerY = (int)y;
            return true;
        }
    }
}
