namespace APIShared
{
    /// <summary>Pure bounds checks for zero-based indices, independent of game IDs and storage lifetime.</summary>
    public static class IndexBounds
    {
        /// <summary>Tests whether a zero-based index is inside a collection of the supplied length.</summary>
        /// <param name="index">Zero-based index; zero can be valid.</param>
        /// <param name="length">Exclusive upper bound. Zero and negative lengths admit no index.</param>
        /// <returns>True exactly when index is nonnegative and smaller than length.</returns>
        /// <remarks>This performs no game access, allocation, ID conversion or lifetime validation.
        /// Specialized storage rules and identity/life-state checks remain the caller's responsibility.</remarks>
        public static bool IsValidIndex(int index, int length) => index >= 0 && index < length;
    }
}
