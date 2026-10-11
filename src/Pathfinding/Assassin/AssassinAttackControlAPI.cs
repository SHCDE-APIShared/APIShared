using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;

namespace APIShared
{
    /// <summary>How a vetoed obstacle attack is completed before the original Assassin update.</summary>
    public enum AssassinObstacleCompletionMode
    {
        /// <summary>Clear the obstacle, set idle and reset the animation timer (legacy behavior).</summary>
        IdleOnly,
        /// <summary>Use Vanilla's existing objective-based target selection; idle only if it fails.</summary>
        NativeRetarget
    }

    /// <summary>Optional process-owned Assassin update guard. No consumer owns a native hook.</summary>
    public static unsafe class AssassinAttackControlAPI
    {
        private static IntPtr module;
        private static ScanRegion region;
        private static ManualLogSource log;
        private static bool supported;
        private static HookTransaction transaction;
        private static DetourHandle<AssassinAttackNativeContract.UpdateDelegate> hook;
        private static Func<int, bool> guard;
        private static AssassinObstacleCompletionMode completionMode;
        private static AssassinAttackNativeContract.RetargetDelegate retarget;
        private static string owner;
        private static readonly object Sync = new object();
        private static int errorLogged;
        internal static void Initialize(IntPtr nativeModule, ScanRegion nativeRegion, string hash, ManualLogSource logger)
        {
            module = nativeModule; region = nativeRegion; log = logger;
            supported = string.Equals(hash, AssassinPathAPI.ReferenceHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Registers the sole process-lifetime guard using legacy idle-only completion.</summary>
        /// <remarks>Runs synchronously on the native simulation thread with a one-based unit game ID.
        /// Return true only to end a state-101/107 obstacle attack. Do not issue commands, retain native
        /// pointers, or register another owner. Callback exceptions preserve the original update.</remarks>
        public static void RegisterGuard(string ownerGuid, Func<int, bool> callback) =>
            RegisterGuard(ownerGuid, callback, AssassinObstacleCompletionMode.IdleOnly);

        /// <summary>Registers the sole guard with an explicit obstacle-completion mode.</summary>
        /// <remarks>Same callback contract as the two-argument overload. NativeRetarget selects from
        /// Vanilla's existing objectives after the callback, without a consumer route search. Failure
        /// leaves the unit idle; a successful selection retains Vanilla's written state and context.
        /// Registrations and hooks remain rooted until process exit; consumers toggle their predicate.
        /// The original update always runs once. Repeated selection of a vetoed target can be vetoed again.</remarks>
        public static void RegisterGuard(string ownerGuid, Func<int, bool> callback, AssassinObstacleCompletionMode mode)
        {
            if (string.IsNullOrEmpty(ownerGuid) || callback == null) throw new ArgumentException("Owner and callback required.");
            if (mode != AssassinObstacleCompletionMode.IdleOnly && mode != AssassinObstacleCompletionMode.NativeRetarget)
                throw new ArgumentOutOfRangeException(nameof(mode));
            lock (Sync)
            {
                if (owner != null) throw new InvalidOperationException("Assassin attack guard already belongs to " + owner);
                if (mode == AssassinObstacleCompletionMode.NativeRetarget)
                {
                    if (!supported || module == IntPtr.Zero) throw new InvalidOperationException("Native retarget image unavailable.");
                    AssassinAttackNativeContract.ValidateRetargetEntry(module + 0x122800);
                    retarget = Marshal.GetDelegateForFunctionPointer<AssassinAttackNativeContract.RetargetDelegate>(module + 0x122800);
                }
                EnsureInstalled();
                completionMode = mode;
                owner = ownerGuid;
                Volatile.Write(ref guard, callback); // Publishes mode/delegate together on the simulation thread.
            }
        }

        private static void DispatchAssassinUpdate()
        {
            try
            {
                int unitId = *(int*)((byte*)module + 0x9302C4);
                if (Volatile.Read(ref guard)?.Invoke(unitId) == true &&
                    UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) &&
                    UnitAccess.IsReallyAlive(in *unit) && (unit->r_AIState == 101 || unit->r_AIState == 107))
                {
                    AssassinObstacleCompletion.Complete(unit, unitId, module + 0x7CC6720,
                        completionMode == AssassinObstacleCompletionMode.NativeRetarget, retarget);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref errorLogged, 1) == 0)
                    log?.LogError("Assassin attack guard failed; original update retained: " + ex);
            }
            hook.Original(); // Full original housekeeping and state machine, exactly once.
        }

        private static void EnsureInstalled()
        {
            if (hook != null) return;
            if (!supported || module == IntPtr.Zero || region == null)
                throw new InvalidOperationException("Assassin update requires the audited native image.");
            byte[] expected = { 0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10 };
            IntPtr target = module + 0x16CD70;
            for (int i=0;i<expected.Length;i++)
                if (Marshal.ReadByte(target,i) != expected[i]) throw new InvalidOperationException("Assassin update prologue changed or occupied.");
            var candidate = new DetourHandle<AssassinAttackNativeContract.UpdateDelegate>();
            HookTransaction pending = null;
            try
            {
                pending = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true, Backend = AssassinAttackNativeContract.Backend });
                pending.AddDetour(candidate, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), (AssassinAttackNativeContract.UpdateDelegate)DispatchAssassinUpdate);
                if (!pending.Commit().IsCompleteSuccess || !candidate.Success)
                    throw new InvalidOperationException("Assassin update transaction failed.");
                AssassinAttackNativeContract.Validate(candidate.Hook, target);
                transaction = pending;
                hook = candidate; // Publication: process lifetime; no reachable teardown.
                log?.LogInfo("Assassin attack guard installed; APIShared owner, RVA=0x16CD70, Indirect/10, continuation=0x16CD7A.");
            }
            catch
            {
                if (hook == candidate) throw;
                pending?.Dispose(); // Unpublished failed candidate only.
                throw;
            }
        }
    }
}
