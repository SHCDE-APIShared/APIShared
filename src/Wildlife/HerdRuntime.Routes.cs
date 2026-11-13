using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Events;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Route events: one function detour on Vanilla's herd route builder 0x127E60(grid, herd, reach, mode),
    // installed on the first handler registration (with thread suspension outside the startup window, like
    // the market detours) and kept for the process lifetime. The builder zeroes the route index (raw +0x5DC)
    // and count (+0x5DE) on every call before it appends waypoints (0x1283D6; its only early exit, 0x128931,
    // lies behind that). Its callers either check count > 0 or walk to route[index] without looking at the
    // count (0x1249F2, 0x124FBE, 0x125829, 0x125B6D, ...), so a route cut to zero gets the leader's tile there.
    internal static unsafe partial class HerdRuntime
    {
        internal const string RouteSiteGroup = "Wildlife.HerdRoutes";
        internal const int RouteRva = 0x127E60, RouteDisplaced = 10;
        // mov [rsp+0x20], rbx; mov [rsp+0x18], r8d; push rbp, rsi, rdi, r12..r15; sub rsp, 0x70.
        internal static readonly byte[] RouteEntryBytes = WildlifeInlineSite.Hex("48895C2420 4489442418 55 56 57 4154 4155 4156 4157 4883EC70");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void RouteCall(IntPtr grid, int herd, int reach, int mode);

        private static readonly NativeDetourBackend RouteBackend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState RoutePublication = new NativeHookPublicationState();
        private static DetourHandle<RouteCall> routeHook;
        private static HookTransaction routeTransaction;
        private static Owned<HerdRouteHandler>[] routeHandlers = Array.Empty<Owned<HerdRouteHandler>>();
        private static string routeFailure = "";

        internal static NativeSites.NativeSiteSpan[] RouteSpans() => new[]
        {
            new NativeSites.NativeSiteSpan(RouteRva, RouteDisplaced, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(RouteRva + RouteDisplaced, RouteEntryBytes.Length - RouteDisplaced, NativeSites.NativeSiteMode.Observe)
        };

        internal static bool TryRegisterRouteHandler(string owner, string id, HerdRouteHandler handler, int order, out string reason)
        {
            if (!ValidateRegistration(owner, id, handler, out reason)) return false;
            lock (Gate)
            {
                if (OrderedRegistrations.Contains(routeHandlers, Owned<HerdRouteHandler>.Key, owner, id)) { reason = "This owner already registered that ID."; return false; }
                if (!TryEnsureRouteHook(out reason)) return false;
                Volatile.Write(ref routeHandlers, OrderedRegistrations.WithAdded(
                    routeHandlers, new Owned<HerdRouteHandler>(owner, id, order, handler), Owned<HerdRouteHandler>.Key));
            }
            NativeApiLog.Info(log, "Herd route handler registered: owner=" + owner + ", id=" + id + ", order=" + order + ".");
            return true;
        }

        // Callers hold Gate. A failed installation is not retried (fail closed).
        private static bool TryEnsureRouteHook(out string reason)
        {
            if (RoutePublication.IsReady) { reason = ""; return true; }
            if (routeFailure.Length != 0 || RoutePublication.HasStarted) { reason = routeFailure.Length != 0 ? routeFailure : "The herd route hook is being installed."; return false; }
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            IntPtr target = (IntPtr)(long)(image + RouteRva);
            var handle = new DetourHandle<RouteCall>();
            bool claimed = false;
            HookTransaction pending = null;
            try
            {
                if (!loadContext.Memory.Slice(RouteRva, RouteEntryBytes.Length).SequenceEqual(new ReadOnlySpan<byte>(RouteEntryBytes)))
                    throw new InvalidOperationException("The herd route builder differs from the audit.");
                NativeSites.NativeIndirectEntry.VerifyBytes(target, RouteEntryBytes);
                ApiSharedNativeSites.Claim(sites, RouteSiteGroup, RouteSpans(), why => new InvalidOperationException(why));
                claimed = true;
                pending = new HookTransaction(loadContext.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(RouteBackend) });
                pending.AddDetour(handle, HookTarget.FromAddress(image + RouteRva), (RouteCall)OnRoute);
                CommitResult result = pending.Commit();
                if (!result.IsCompleteSuccess || !handle.Success) throw new InvalidOperationException("The herd route hook did not prepare.");
                NativeSites.NativeIndirectEntry.Validate(handle.Hook, target, RouteEntryBytes, RouteDisplaced, false);
                Volatile.Write(ref routeHook, handle);
                routeTransaction = pending;
                NativeHookPublication.Enable(pending, result, RoutePublication, typeof(HerdRuntime),
                    () => NativeSites.NativeIndirectEntry.Validate(handle.Hook, target, RouteEntryBytes, RouteDisplaced, true));
                NativeApiLog.Info(log, "Herd route hook published: 0x" + RouteRva.ToString("X") + ", Indirect/" + RouteDisplaced + "; process lifetime.");
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                routeFailure = "Herd route events are unavailable: " + error.Message;
                RoutePublication.RollbackUnpublished(pending, () =>
                {
                    Volatile.Write(ref routeHook, null);
                    routeTransaction = null;
                    if (claimed) ApiSharedNativeSites.Release(sites, RouteSiteGroup);
                });
                NativeApiLog.Error(log, routeFailure);
                reason = routeFailure;
                return false;
            }
        }

        // Simulation thread, inside a herd update. Vanilla's builder runs exactly once.
        private static void OnRoute(IntPtr grid, int herd, int reach, int mode)
        {
            Volatile.Read(ref routeHook).Original(grid, herd, reach, mode);
            var handlers = Volatile.Read(ref routeHandlers);
            if (handlers.Length == 0 || !RoutePublication.IsReady || !TryRead(herd, out HerdInfo info)) return;
            var record = Record(herd);
            int leaderX = -1, leaderY = -1;
            if (info.LeaderUnitId > 0 && UnitAccess.TryGetById(info.LeaderUnitId, out GameUnit* leader, out _) && leader != null)
            { leaderX = leader->r_CurrentTilePositionX; leaderY = leader->r_CurrentTilePositionY; }
            var access = new RouteAccess(record);
            var route = new HerdRoute(info, leaderX, leaderY, access);
            try
            {
                foreach (var item in handlers)
                {
                    try { item.Callback(route); }
                    catch (Exception error) { ReportFailure(item, "route handler", error); }
                }
            }
            finally { access.Close(leaderX, leaderY); }
        }

        // The live route of one herd during one dispatch. Not thread-safe: the dispatch runs on one thread.
        internal sealed class RouteAccess
        {
            private readonly GameTribe* record;
            private bool open = true, changed;

            internal RouteAccess(GameTribe* record) { this.record = record; }

            private short* Waypoints => (short*)&record->r_PatrolPoint1TileX;

            internal int Count
            {
                get
                {
                    Check();
                    int count = (short)record->r_CurrentPatrolPoints;
                    return count < 0 ? 0 : Math.Min(count, HerdRoute.MaximumWaypoints);
                }
            }

            internal HerdWaypoint Get(int index)
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return new HerdWaypoint(Waypoints[index * 2], Waypoints[index * 2 + 1]);
            }

            internal void Truncate(int count)
            {
                int current = Count;
                if (count < 0 || count > current) throw new ArgumentOutOfRangeException(nameof(count));
                if (count == current) return;
                record->r_CurrentPatrolPoints = (ushort)count;
                changed = true;
            }

            internal void ReplaceWithSingle(int tileX, int tileY)
            {
                Check();
                if (!IsTileInside(tileX, tileY)) throw new ArgumentOutOfRangeException(nameof(tileX), "The waypoint must be a tile inside the map.");
                Waypoints[0] = (short)tileX;
                Waypoints[1] = (short)tileY;
                record->r_CurrentPatrolPoints = 1;
                changed = true;
            }

            // Vanilla reads the first waypoint in places without looking at the count; a route that a handler
            // emptied must not leave a stale one there.
            internal void Close(int leaderX, int leaderY)
            {
                if (!open) return;
                open = false;
                if (changed && record->r_CurrentPatrolPoints == 0 && leaderX >= 0 && leaderY >= 0)
                {
                    Waypoints[0] = (short)leaderX;
                    Waypoints[1] = (short)leaderY;
                }
            }

            private void Check()
            {
                if (!open) throw new InvalidOperationException("A herd route is only valid while its handler runs.");
            }
        }
    }
}
