using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>Vanilla's quarry-pile search and perimeter coordinates. All coordinates are top-left game tiles.</summary>
    public static unsafe class QuarryPilePlacement
    {
        private const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        private const int FinderRva = 0xBD7A0;
        private const int OffsetHelperRva = 0xC0270;
        private const int BuildingManagerRva = 0x64CCBB0;
        private const int TileManagerRva = 0x405EDB0;
        private const int SearchOffsetsRva = BuildingManagerRva + 0x31B7D0;
        private const int PlacementScratchRva = 0x60AD458;
        private const int PlacementScratchLength = 0x150;
        private const int PlacementStateRva = 0x60AD4AC;
        private const int QuarrySize = 6;
        private const int PileSize = 2;
        private static readonly byte[] OffsetHelperEntry =
            { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x8D, 0x42, 0xFF, 0x41, 0x8B, 0xD8, 0x44, 0x8B, 0xDA, 0x4C, 0x8B,
              0xD1, 0x83, 0xF8, 0x0C };
        private static readonly (int Rva, byte[] Bytes)[] FinderSpans =
        {
            (FinderRva, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x48, 0x89, 0x6C, 0x24, 0x20, 0x89, 0x54, 0x24, 0x10, 0x56 }),
            (0xBD817, new byte[] { 0xE8, 0x54, 0x2A, 0x00, 0x00 }),
            (0xBD851, new byte[] { 0xE8, 0x0A, 0xA6, 0xFB, 0xFF, 0x83, 0x3D, 0x4F, 0xFC, 0xFE, 0x05, 0x00, 0x75, 0x4A })
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FindPile(IntPtr manager, int playerId, int x, int y, int size, int pileSize,
            int outwardOffset, short mapper, int pathComponent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CalculateOffset(IntPtr manager, int size, int pileSize, int perimeterIndex, int outwardOffset);

        private static InitialSearch initialSearch;
        private static RelocationSearch relocationSearch;
        private static int initialized;

        /// <summary>Whether the startup contract for native initial-pile searches was validated.</summary>
        public static bool InitialSearchAvailable => Volatile.Read(ref initialSearch) != null;

        /// <summary>Whether the startup contract for native relocation coordinates was validated.</summary>
        public static bool RelocationAvailable => Volatile.Read(ref relocationSearch) != null;

        /// <summary>
        /// Finds the first 2×2 initial pile candidate using Vanilla's native search order, tile validation and
        /// the path component currently stored for the top-left quarry tile. It does not refresh path components.
        /// The caller must hold the game engine monitor. The optional predicate runs synchronously inside
        /// validation on the caller's thread and should only read captured data. Nested searches and active
        /// Extender placement overrides are rejected.
        /// Placement scratch is restored before return or propagation of a predicate exception.
        /// </summary>
        public static bool TryFindInitialCandidate(int playerId, int quarryX, int quarryY, Func<int, int, bool> permits,
            out int pileX, out int pileY)
        {
            var current = Volatile.Read(ref initialSearch);
            pileX = pileY = 0;
            return current != null && current.TryFind(playerId, quarryX, quarryY, permits, out pileX, out pileY);
        }

        /// <summary>
        /// Scopes the existing placement validator to one initial quarry pile at the supplied tile. The caller
        /// must hold the game engine monitor, revalidate the requested candidate first, and synchronously invoke
        /// one real quarry construction for that player inside the callback with placement bypass disabled.
        /// An already active Extender placement override rejects the call. The selected pile still undergoes
        /// normal validation. A true result confirms that the callback returned true and the validator observed
        /// the selected candidate. The callback owns validation of the actual building result and any mutation
        /// it made. Callback exceptions propagate once the scope is cleared. Nested scopes are rejected.
        /// </summary>
        public static bool TryWithInitialCandidateFilter(int playerId, int pileX, int pileY, Func<bool> placeQuarry)
        {
            var current = Volatile.Read(ref initialSearch);
            return current != null && current.TryPlace(playerId, pileX, pileY, placeQuarry);
        }

        /// <summary>
        /// Gets an indexed coordinate in the 24-position clockwise perimeter used for relocation,
        /// with outward attempts from 1 through 9. The caller must hold the game engine monitor.
        /// Returned coordinates may be off the map. The caller owns map and placement validation.
        /// </summary>
        public static bool TryGetRelocationCandidate(int quarryX, int quarryY, int perimeterIndex, int placementTry,
            out int pileX, out int pileY)
        {
            var current = Volatile.Read(ref relocationSearch);
            pileX = pileY = 0;
            return current != null && current.TryGet(quarryX, quarryY, perimeterIndex, placementTry, out pileX, out pileY);
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string nativeHash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
                return;
            if (!string.Equals(nativeHash, NativeHash, StringComparison.OrdinalIgnoreCase) || context.ModuleHandle == IntPtr.Zero)
            {
                NativeApiLog.Error(log, "Quarry pile placement unavailable: unsupported native image.");
                return;
            }
            var engine = EngineMonitor.Current;
            if (engine == null)
            {
                NativeApiLog.Error(log, "Quarry pile placement unavailable: game engine monitor not found.");
                return;
            }
            try
            {
                RequireSpan(context, OffsetHelperRva, OffsetHelperEntry);
                var search = new RelocationSearch(context.ModuleHandle, engine, log);
                ClaimCallSite("Buildings.QuarryPileOffsets", OffsetHelperRva, OffsetHelperEntry.Length);
                Volatile.Write(ref relocationSearch, search);
                NativeApiLog.Info(log, "Quarry relocation candidate helper resolved by reference RVA 0xC0270.");
            }
            catch (Exception ex)
            {
                NativeApiLog.Error(log, "Quarry relocation candidates unavailable: " + ex.Message);
                return;
            }
            try
            {
                foreach (var span in FinderSpans)
                    RequireSpan(context, span.Rva, span.Bytes);
                var search = new InitialSearch(context.ModuleHandle, engine, log);
                const string site = "Buildings.InitialQuarryPileFinder";
                ClaimCallSite(site, FinderRva, FinderSpans[0].Bytes.Length);
                try { BuildingR3EventHooks.OnPlacementValidation.Observable.Subscribe(search.ObservePlacement); }
                catch
                {
                    NativeSiteRegistry.Unregister(APISharedPlugin.PluginGuid, site);
                    throw;
                }
                Volatile.Write(ref initialSearch, search);
                NativeApiLog.Info(log, "Initial quarry pile finder resolved by reference RVA 0xBD7A0.");
            }
            catch (Exception ex)
            {
                NativeApiLog.Error(log, "Initial quarry pile search unavailable: " + ex.Message);
            }
        }

        private static void ClaimCallSite(string name, int rva, int length)
        {
            var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, name, rva, length, NativeSiteMode.CallOnly);
            if (!claim.Succeeded)
                throw new InvalidOperationException(claim.Reason);
        }

        private static void RequireSpan(CrusaderLibraryLoadContext context, int rva, byte[] bytes)
        {
            if (rva > context.Memory.Length - bytes.Length || !context.Memory.Slice(rva, bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException("Load-image bytes differ at RVA 0x" + rva.ToString("X") + ".");
            if (!MatchesLiveSpan(context.ModuleHandle, rva, bytes))
                throw new InvalidOperationException("Foreign patch at RVA 0x" + rva.ToString("X") + ".");
        }

        private static bool MatchesLiveSpan(IntPtr image, int rva, byte[] bytes) =>
            new ReadOnlySpan<byte>((void*)IntPtr.Add(image, rva), bytes.Length).SequenceEqual(bytes);

        private static bool IsMapSquare(GameTileManagerAPI tiles, int x, int y, int size)
        {
            if ((uint)x >= GameTileManagerAPI.MAX_WIDTH || (uint)y >= GameTileManagerAPI.MAX_HEIGHT)
                return false;
            for (int dy = 0; dy < size; dy++)
            for (int dx = 0; dx < size; dx++)
                if (!tiles.IsTileInsideMapBounds(x + dx, y + dy))
                    return false;
            return true;
        }

        private sealed class RelocationSearch
        {
            private readonly IntPtr image;
            private readonly object engine;
            private readonly ManualLogSource log;
            private readonly CalculateOffset calculate;
            private readonly IntPtr manager;

            internal RelocationSearch(IntPtr image, object engine, ManualLogSource log)
            {
                this.image = image;
                this.engine = engine;
                this.log = log;
                manager = IntPtr.Add(image, BuildingManagerRva);
                calculate = Marshal.GetDelegateForFunctionPointer<CalculateOffset>(IntPtr.Add(image, OffsetHelperRva));
            }

            internal bool TryGet(int quarryX, int quarryY, int perimeterIndex, int placementTry, out int x, out int y)
            {
                x = y = 0;
                if ((uint)quarryX >= GameTileManagerAPI.MAX_WIDTH || (uint)quarryY >= GameTileManagerAPI.MAX_HEIGHT ||
                    (uint)perimeterIndex >= 4 * QuarrySize || (uint)(placementTry - 1) >= 9 ||
                    InitialSearch.IsActive || !Monitor.IsEntered(engine) ||
                    GameGlobalsManager.Instance.GameBuildingManagerVA != unchecked((ulong)manager.ToInt64()))
                    return false;
                if (!MatchesLiveSpan(image, OffsetHelperRva, OffsetHelperEntry))
                {
                    Volatile.Write(ref relocationSearch, null);
                    NativeApiLog.Error(log, "Quarry relocation candidates disabled: foreign patch at RVA 0xC0270.");
                    return false;
                }
                long previous = Marshal.ReadInt64(image, SearchOffsetsRva);
                try
                {
                    calculate(manager, QuarrySize, PileSize, perimeterIndex, placementTry);
                    x = quarryX + Marshal.ReadInt32(image, SearchOffsetsRva);
                    y = quarryY + Marshal.ReadInt32(image, SearchOffsetsRva + sizeof(int));
                    return true;
                }
                finally { Marshal.WriteInt64(image, SearchOffsetsRva, previous); }
            }
        }

        private sealed class InitialSearch
        {
            private sealed class Scope
            {
                internal int Player;
                internal Func<int, int, bool> Permits;
                internal int AllowedX = -1, AllowedY = -1;
                internal ExceptionDispatchInfo Error;
            }

            [ThreadStatic] private static Scope active;
            internal static bool IsActive => active != null;
            private readonly IntPtr image, buildingManager, tileManager;
            private readonly object engine;
            private readonly ManualLogSource log;
            private readonly FindPile find;
            private readonly byte[] scratch = new byte[PlacementScratchLength];

            internal InitialSearch(IntPtr image, object engine, ManualLogSource log)
            {
                this.image = image;
                this.engine = engine;
                this.log = log;
                buildingManager = IntPtr.Add(image, BuildingManagerRva);
                tileManager = IntPtr.Add(image, TileManagerRva);
                find = Marshal.GetDelegateForFunctionPointer<FindPile>(IntPtr.Add(image, FinderRva));
            }

            private bool IsReady(int playerId)
            {
                if (active != null || (uint)(playerId - 1) >= 8 || !Monitor.IsEntered(engine) ||
                    GameGlobalsManager.Instance.GameBuildingManagerVA != unchecked((ulong)buildingManager.ToInt64()) ||
                    GameGlobalsManager.Instance.GameTileManagerVA != unchecked((ulong)tileManager.ToInt64()) ||
                    GameTileManagerAPI.Instance.TileManager.UsePlacementBlockedOverride)
                    return false;
                if (!MatchesLiveSpan(image, OffsetHelperRva, OffsetHelperEntry))
                    return Disable(OffsetHelperRva);
                foreach (var span in FinderSpans)
                    if (!MatchesLiveSpan(image, span.Rva, span.Bytes))
                        return Disable(span.Rva);
                return true;
            }

            private bool Disable(int rva)
            {
                Volatile.Write(ref initialSearch, null);
                NativeApiLog.Error(log, "Initial quarry pile search disabled: foreign patch at RVA 0x" + rva.ToString("X") + ".");
                return false;
            }

            internal bool TryFind(int playerId, int quarryX, int quarryY, Func<int, int, bool> permits,
                out int pileX, out int pileY)
            {
                pileX = pileY = 0;
                if (!IsReady(playerId))
                    return false;
                var tiles = GameTileManagerAPI.Instance;
                if (!IsMapSquare(tiles, quarryX, quarryY, QuarrySize))
                    return false;
                int id = tiles.GetTileId(quarryX, quarryY);
                var components = tiles.GetPathConnectionLayer();
                if ((uint)id >= (uint)components.Length)
                    return false;

                // The constructor sign-extends the packed component value before passing it to the finder.
                int component = unchecked((short)components[id]);
                var scope = new Scope { Player = playerId, Permits = permits };
                long oldOffsets = Marshal.ReadInt64(image, SearchOffsetsRva);
                var placement = IntPtr.Add(image, PlacementScratchRva);
                Marshal.Copy(placement, scratch, 0, scratch.Length);
                active = scope;
                try
                {
                    // The constructor expands the search square to 8, 10, then 12; its last eight rounds
                    // expand the offset of the same 12-tile perimeter. The native finder orders each round.
                    for (int round = 1; round <= 11; round++)
                    {
                        int radius = Math.Min(round, 3);
                        scope.AllowedX = scope.AllowedY = -1;
                        Marshal.WriteInt32(image, PlacementStateRva, 1);
                        find(buildingManager, playerId, quarryX - radius, quarryY - radius,
                            QuarrySize + radius * 2, PileSize, Math.Max(0, round - 3), (short)eMappers.MAPPER_QUARRYPILE, component);
                        scope.Error?.Throw();
                        if (Marshal.ReadInt32(image, PlacementStateRva) != 0)
                            continue;
                        int x = quarryX - radius + Marshal.ReadInt32(image, SearchOffsetsRva);
                        int y = quarryY - radius + Marshal.ReadInt32(image, SearchOffsetsRva + sizeof(int));
                        if (x != scope.AllowedX || y != scope.AllowedY)
                            return false;
                        pileX = x;
                        pileY = y;
                        return true;
                    }
                    return false;
                }
                finally
                {
                    active = null;
                    Marshal.Copy(scratch, 0, placement, scratch.Length);
                    Marshal.WriteInt64(image, SearchOffsetsRva, oldOffsets);
                }
            }

            internal bool TryPlace(int playerId, int pileX, int pileY, Func<bool> placeQuarry)
            {
                if (placeQuarry == null || !IsReady(playerId) ||
                    !IsMapSquare(GameTileManagerAPI.Instance, pileX, pileY, PileSize))
                    return false;
                var scope = new Scope { Player = playerId, Permits = (x, y) => x == pileX && y == pileY };
                active = scope;
                try { return placeQuarry() && scope.AllowedX == pileX && scope.AllowedY == pileY; }
                finally { active = null; }
            }

            internal void ObservePlacement(BuildingPlacementValidationEventArgs args)
            {
                var scope = active;
                if (scope == null || args.Phase != EventHookPhase.Post || args.PlayerId != scope.Player ||
                    args.Mappers != eMappers.MAPPER_QUARRYPILE || args.Unknown1 != PileSize || args.Unknown2 != 0 ||
                    Marshal.ReadInt32(image, PlacementStateRva) != 0)
                    return;
                try
                {
                    if (scope.Error == null && IsMapSquare(GameTileManagerAPI.Instance, args.TileX, args.TileY, PileSize) &&
                        (scope.Permits == null || scope.Permits(args.TileX, args.TileY)))
                    {
                        scope.AllowedX = args.TileX;
                        scope.AllowedY = args.TileY;
                        return;
                    }
                }
                catch (Exception ex) { scope.Error = ExceptionDispatchInfo.Capture(ex); }
                Marshal.WriteInt32(image, PlacementStateRva, 1);
            }
        }
    }
}
