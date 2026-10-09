using System;

namespace APIShared
{
    // Pure layout policy shared with the game-free tests. Coordinates are HUD-local units.
    internal static class UnitHudActionButtonLayout
    {
        internal const int Size = 35;
        internal const int Gap = 4;
        internal const int Pitch = Size + Gap;
        internal static int Capacity(float anchorLeft, float leftBoundary) =>
            float.IsNaN(anchorLeft) || float.IsInfinity(anchorLeft) ||
            float.IsNaN(leftBoundary) || float.IsInfinity(leftBoundary) ? 0 :
            Math.Max(0, (int)Math.Floor((anchorLeft - leftBoundary) / Pitch));
        internal static int PageCount(int count, int capacity) =>
            count <= 0 || capacity <= 0 ? 0 : 1 + (count - 1) / capacity;
        internal static int ClampPage(int page, int count, int capacity) =>
            Math.Max(0, Math.Min(page, Math.Max(0, PageCount(count, capacity) - 1)));
        internal static int NextPage(int page, int count, int capacity) =>
            PageCount(count, capacity) <= 1 ? 0 : (ClampPage(page, count, capacity) + 1) % PageCount(count, capacity);
        internal static bool OwnContext(bool shown, bool controlsVisible, bool controlsInteractive,
            float controlsOpacity, int playerId, int selectionCount) =>
            shown && controlsVisible && controlsInteractive && controlsOpacity > 0 &&
            playerId >= 1 && playerId <= 8 && selectionCount > 0;
    }
}
