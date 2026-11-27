using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;

namespace APIShared.ScriptExtenderFixes
{
    // Corrects the unit ids of two Script Extender Hunter events (Extender 2.14.3, RedBird 1.5.0,
    // native FBCB9319; static audit of 2026-11-08):
    //  - OnUnitHunterQueryTarget reads HunterUnitId from [rsp+0xF0], assuming a 0x60 shift. RedBird's
    //    X64FastcallSafe pushes 0x30, so the slot is HunterUpdate's saved R15, the dispatcher's constant 2
    //    (0x183C35): every Hunter is reported as unit 2 (Extender issue 123). The correct id is the
    //    query's own EDX, recorded natively at the query entry by Wildlife.HunterNativeSites.
    //  - OnHunterPickUpMeat (0x12FD7C) reads UnitId from R9, which HunterUpdate sets only later
    //    (0x12FDA1); the value is a dispatcher leftover. Inside HunterUpdate the updating Hunter is the
    //    dispatcher's current unit (0x9302C4, loaded into RBX at 0x12FC8C).
    // APIShared subscribes before its consumers and rewrites only these ids; a fixed Extender yields the
    // same values, so the correction is idempotent. Subscriptions are process-lived.
    internal static unsafe class HunterEventIdentityRuntime
    {
        private const int CurrentUnitRva = 0x9302C4;
        private static Action<UnitHunterQueryTargetEventArgs, int> setHunterUnitId;
        private static IDisposable querySubscription, pickupSubscription;
        private static int* currentUnit;
        private static ManualLogSource logger;
        private static int queryCorrections, pickupCorrections, pickupRejected;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (querySubscription != null || pickupSubscription != null) return;
            logger = log;
            if (!string.Equals(hash, Wildlife.HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
            {
                NativeApiLog.Error(log, "SE_HUNTER_EVENT_IDS: unaudited native image; Extender Hunter event ids stay uncorrected.");
                return;
            }
            currentUnit = (int*)(context.ModuleHandle + CurrentUnitRva);

            try
            {
                if (!Wildlife.HunterNativeSites.IsAvailable)
                    throw new InvalidOperationException(Wildlife.HunterNativeSites.Diagnostic);
                setHunterUnitId = CompileHunterSetter();
                querySubscription = UnitR3EventHooks.OnUnitHunterQueryTarget.Observable.Subscribe(OnQuery);
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "SE_HUNTER_EVENT_IDS: Hunter query id stays uncorrected; error=" + error.Message);
            }

            try { pickupSubscription = UnitR3EventHooks.OnHunterPickUpMeat.Observable.Subscribe(OnPickUp); }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "SE_HUNTER_EVENT_IDS: Hunter meat pickup id stays uncorrected; error=" + error.Message);
            }
            NativeApiLog.Info(log, "SE_HUNTER_EVENT_IDS: query=" + (querySubscription != null) + ", pickup=" + (pickupSubscription != null) +
                "; corrected before consumers from the query's own unit argument and the dispatcher's current unit.");
        }

        // The auto-property has no setter. Validated on a probe instance before use.
        private static Action<UnitHunterQueryTargetEventArgs, int> CompileHunterSetter()
        {
            FieldInfo field = typeof(UnitHunterQueryTargetEventArgs).GetField("<HunterUnitId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(int))
                throw new MissingFieldException(typeof(UnitHunterQueryTargetEventArgs).FullName, "HunterUnitId backing field");
            var method = new DynamicMethod("APIShared_SetHunterUnitId", null,
                new[] { typeof(UnitHunterQueryTargetEventArgs), typeof(int) }, typeof(HunterEventIdentityRuntime).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            var setter = (Action<UnitHunterQueryTargetEventArgs, int>)method.CreateDelegate(typeof(Action<UnitHunterQueryTargetEventArgs, int>));
            var probe = new UnitHunterQueryTargetEventArgs(EventHookPhase.Pre, 1, 2);
            setter(probe, 7);
            if (probe.HunterUnitId != 7 || probe.QueryUnitId != 1)
                throw new InvalidOperationException("The HunterUnitId backing field could not be written.");
            return setter;
        }

        private static void OnQuery(UnitHunterQueryTargetEventArgs args)
        {
            if (args.Phase != EventHookPhase.Pre || !Wildlife.HunterNativeSites.TryGetCurrentQuery(out int hunter, out _) ||
                args.HunterUnitId == hunter)
                return;
            int reported = args.HunterUnitId;
            setHunterUnitId(args, hunter);
            if (Interlocked.Increment(ref queryCorrections) == 1)
                NativeApiLog.Info(logger, "SE_HUNTER_EVENT_IDS: first Hunter query id corrected: reported=" + reported + ", actual=" + hunter + ".");
        }

        private static void OnPickUp(UnitHunterPickUpMeatEventArgs args)
        {
            if (args.Phase != EventHookPhase.Pre) return;
            int hunter = *currentUnit;
            if (!UnitAccess.TryGetById(hunter, out GameUnit* unit, out _) || unit->r_UnitChimp != eChimps.CHIMP_TYPE_HUNTER)
            {
                // Outside HunterUpdate the current unit is no Hunter; leave the Extender's value untouched.
                if (Interlocked.Increment(ref pickupRejected) == 1)
                    NativeApiLog.Error(logger, "SE_HUNTER_EVENT_IDS: meat pickup outside a Hunter update; currentUnit=" + hunter + ", reported=" + args.UnitId + ".");
                return;
            }
            if (args.UnitId == hunter) return;
            int reported = args.UnitId;
            args.UnitId = hunter;
            if (Interlocked.Increment(ref pickupCorrections) == 1)
                NativeApiLog.Info(logger, "SE_HUNTER_EVENT_IDS: first meat pickup id corrected: reported=" + reported + ", actual=" + hunter + ".");
        }
    }
}
