using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;

namespace APIShared.WorldMutations
{
    internal static partial class WorldMutationRuntime
    {
        private static ManualLogSource log;
        private static int suspended;
        private static int worldLifecycleReady;
        internal static bool IsWorldLifecycleReady=>Volatile.Read(ref worldLifecycleReady)!=0;
        private static readonly Func<WorldMutationWorld> ReadWorld = CurrentWorld;
        internal static readonly WorldMutationRegistry Registry = new WorldMutationRegistry(ReadWorld, Report);
        private static WorldMutationWorld CurrentWorld()
        {
            if (Volatile.Read(ref suspended) != 0) return default;
            var clock = MissionLifecycleService.NativeClock;
            return clock == null ? default : new WorldMutationWorld(clock.Context.SessionId, clock.World);
        }
        private static void Report(Exception error)
        {
            // Formatting, NativeApiLog and downstream sinks are all inside the isolation boundary.
            try { NativeApiLog.Error(log, "WORLD_MUTATION_CALLBACK_FAILED: " + error); } catch { }
        }
        internal static long Enter(WorldMutationSource source)
        { try { return Registry.EnterBatch(source); } catch (Exception error) { Report(error); return 0; } }
        internal static void Leave(long token, bool returned)
        { try { Registry.LeaveBatch(token, returned); } catch (Exception error) { Report(error); } }
        private static WorldMutationRegistry.Invocation Begin(WorldMutationSource source, long[] args)
        { try { return Registry.Begin(source, args); } catch (Exception error) { Report(error); return null; } }
        private static void Complete(WorldMutationRegistry.Invocation invocation, bool invoked, bool returned, long result, Exception error)
        { try { Registry.Complete(invocation, invoked, returned, result, error); } catch (Exception callbackError) { Report(callbackError); } }
        internal static bool HasDemand(WorldMutationSource source) => Registry.HasDemand(source);

        // Called by the existing cost owner's OriginalSite before constructing its own cost wrapper.
        // The resulting observation is INSIDE cost handling and therefore sees actual native arguments.
        internal static Delegate WrapOriginal(Delegate original, Type delegateType, WorldMutationSource source)
        {
            var invoke = delegateType.GetMethod("Invoke");
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var demand = Expression.Call(typeof(WorldMutationRuntime), nameof(HasDemand), null, Expression.Constant(source));
            var boxed = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
            var dispatch = Expression.Call(typeof(WorldMutationRuntime), nameof(Dispatch), null,
                Expression.Constant(source), Expression.Constant(original, typeof(Delegate)), boxed);
            Expression slow = invoke.ReturnType == typeof(void) ? (Expression)Expression.Block(dispatch, Expression.Empty()) : Expression.Convert(dispatch, invoke.ReturnType);
            var body = Expression.Condition(demand, slow, Expression.Invoke(Expression.Constant(original, delegateType), parameters));
            return Expression.Lambda(delegateType, body, parameters).Compile();
        }
        private static unsafe long Scalar(object value)
        {
            if (value is IntPtr pointer) return pointer.ToInt64();
            if (value is UIntPtr unsigned) return unchecked((long)unsigned.ToUInt64());
            if (value is ulong wide) return unchecked((long)wide);
            if (value is RedBird.Core.Memory.NativePointer<SHCDESE.Interop.GameBuildingManager> building)
                return unchecked((long)building.Pointer);
            return Convert.ToInt64(value);
        }
        internal static object Dispatch(WorldMutationSource source, Delegate original, object[] args)
        {
            long fallback = 0;
            // A final delegate can be called directly by another mod; retain a safe single-original batch.
            // Ordinary SE entry paths have the encompassing wrapper scope, including all mirror siblings.
            fallback = Enter(source);
            WorldMutationRegistry.Invocation state = null;
            bool invoked = false, returned = false;
            object result = null; Exception failure = null;
            try
            {
                try
                {
                    var effective = new long[args.Length];
                    for (int index = 0; index < args.Length; index++) effective[index] = Scalar(args[index]);
                    state = Begin(source, effective);
                }
                catch (Exception preparationError) { Report(preparationError); }
                invoked = true;
                try { result = original.DynamicInvoke(args); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                returned = true;
                return result;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                long value = 0;
                try { if (returned && result != null) value = Scalar(result); }
                catch (Exception resultError) { Report(resultError); }
                Complete(state, invoked, returned, value, failure);
                Leave(fallback, returned);
            }
        }
    }
}
