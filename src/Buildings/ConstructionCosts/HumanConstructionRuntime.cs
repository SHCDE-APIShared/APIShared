using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Networking;

namespace APIShared.Buildings
{
    // The SE publishers are deliberately not used as transaction commits: BuildStructure ignores
    // SkipOriginalFunction, and Refund Post fires even when Original was skipped. Wrap only the
    // final Original delegate after all Pre edits. Dormant calls retain their typed direct path.
    internal static unsafe partial class HumanConstructionRuntime
    {
        private enum Operation { Build, SpawnBuilding, SpawnUnit, SpawnDecoration, Refund, MarkBuildingForRemoval, AddResource, ReleaseBuildingSlot, RetireUnit }
        private static readonly List<Patching.SafeIlHook> hooks = new List<Patching.SafeIlHook>();
        private static ManualLogSource log;
        private static bool ready;
        internal static bool HasBuildOriginalBoundary { get; private set; }
        internal static bool HasRemovalOriginalBoundary { get; private set; }
        [ThreadStatic] private static BuildFrame building;
        [ThreadStatic] private static RefundFrame refund;
        [ThreadStatic] private static DemolitionFrame demolition;
        [ThreadStatic] private static long demolitionSerial;
        private static readonly Dictionary<ConstructionObjectIdentity, RefundFrame> pending = new Dictionary<ConstructionObjectIdentity, RefundFrame>();
        private sealed class BuildFrame
        {
            internal int Player, TileX, TileY, Scale, ExpectedSpawnDepth;
            internal eMappers Mapper;
            internal ConstructionCostTarget Target;
            internal HumanConstructionTransaction Transaction;
            internal ConstructionObjectIdentity Identity;
        }
        private sealed class RefundFrame
        {
            internal ConstructionObjectIdentity Identity; internal int Player, Percentage; internal ConstructionCostTarget Target;
            internal NativeChoreExecution Chore; internal long Scope; internal int WithheldGold;
        }
        private sealed class DemolitionFrame { internal int Player; internal long Serial; }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            log = logger;
            if (ready) return;
            if (hash != ApiSharedRuntime.SupportedHash) throw new InvalidOperationException("Human construction requires the audited native image.");
            var assembly = typeof(GameBuildingManagerAPI).Assembly;
            try
            {
                InitializeRefundInterceptor(assembly);
                Install(assembly, "BulkBuildingDetours", "c_game_player_build_structure_hook_impl", "c_game_player_build_structure_delegate", Operation.Build);
                HasBuildOriginalBoundary = true;
                Install(assembly, "BulkBuildingDetours", "c_game_building_spawn_hook_impl", "c_game_building_spawn_delegate", Operation.SpawnBuilding);
                Install(assembly, "BulkUnitDetours", "c_game_unit_spawn_ex_hook_impl", "c_game_unit_spawn_ex_delegate", Operation.SpawnUnit);
                Install(assembly, "BulkProjectileDetours", "c_game_projectile_spawn_hook_impl", "c_game_projectile_spawn_delegate", Operation.SpawnDecoration);
                Install(assembly, "BulkBuildingDetours", "c_game_building_refund_hook_impl", "c_game_building_refund_delegate", Operation.Refund);
                Install(assembly, BuildingRemovalNativeContracts.ExtenderAdapter.OwnerTypeName,
                    BuildingRemovalNativeContracts.ExtenderAdapter.MarkBuildingForRemovalCallbackName,
                    BuildingRemovalNativeContracts.ExtenderAdapter.MarkBuildingForRemovalDelegateName, Operation.MarkBuildingForRemoval);
                Install(assembly, BuildingRemovalNativeContracts.ExtenderAdapter.OwnerTypeName,
                    BuildingRemovalNativeContracts.ExtenderAdapter.ReleaseBuildingSlotCallbackName,
                    BuildingRemovalNativeContracts.ExtenderAdapter.ReleaseBuildingSlotDelegateName, Operation.ReleaseBuildingSlot);
                HasRemovalOriginalBoundary = true;
                Install(assembly, "BulkUnitDetours", "c_game_unit_delete_hook_impl", "c_game_unit_delete_hook_delegate", Operation.RetireUnit);
                Install(assembly, "BulkPlayerDetours", "c_game_player_add_resources_hook_impl", "c_game_player_add_resources_delegate", Operation.AddResource);
                InitializeDecorationRemoval(context, hash, logger);
                InitializeDisband(context, hash, logger);
                InitializeGoldRefundCalls(context, hash, logger);
                InitializePlacementPreview();
                HumanConstructionCosts.SetIdentityValidator(CurrentOwner);
                HumanConstructionCosts.SetQuoteEligibility((target, player) => PaidSession && Human(player) && !IsFreeConstruction(target, player));
                ready = true;
                HumanConstructionCosts.SetRuntimeAvailability(true, string.Empty);
                NativeApiLog.Info(log, "HUMAN_CONSTRUCTION: final Original boundaries installed; process lifetime.");
            }
            catch (Exception ex)
            {
                // Published SafeIlHooks stay rooted but dormant, never removed during startup teardown.
                ready = false;
                HumanConstructionCosts.SetRuntimeAvailability(false, ex.Message);
                throw;
            }
        }
        private static void Install(Assembly assembly, string className, string methodName, string delegateName, Operation operation)
        {
            Type type = assembly.GetType("SHCDESE.Detours." + className, true);
            Type delegateType = type.GetNestedType(delegateName, BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null || delegateType == null) throw new MissingMethodException(className + "." + methodName);
            var methods = new List<MethodInfo> { method };
            if (operation == Operation.Build)
            {
                // MirrorTool uses a compiler-generated closure with its own Original invocation.
                foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    methods.AddRange(nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(m => m.Name.Contains("<" + methodName + ">")));
            }
            foreach (var target in methods)
            {
                var site = new OriginalSite(operation, delegateType);
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "Construction." + className + "." + target.Name,
                    target, il => site.Transform(il), log, out var hook, out string reason))
                    throw new InvalidOperationException(reason);
                hooks.Add(hook);
            }
        }
        private sealed class OriginalSite
        {
            private readonly Operation operation; private readonly Type type;
            private Delegate prior, replacement;
            internal OriginalSite(Operation operation, Type type) { this.operation = operation; this.type = type; }
            internal void Transform(ILContext il)
            {
                var sites = il.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_Original" &&
                    m.DeclaringType.FullName.Contains("DetourHandle")).ToArray();
                if (sites.Length != 1) throw new InvalidOperationException("Expected one final Original getter in " + il.Method.FullName);
                var cursor = new ILCursor(il); cursor.Goto(sites[0], MoveType.After);
                cursor.EmitDelegate<Func<Delegate, Delegate>>(Wrap);
                cursor.Emit(OpCodes.Castclass, type);
                if (operation == Operation.SpawnUnit)
                    ApplyMethodScope(il, c => { c.Emit(OpCodes.Ldarg,il.Method.Parameters[6]); c.EmitDelegate<Func<eChimps,int>>(EnterUnitScope); }, previous => LeaveMethodScope(operation, previous));
                else if (operation == Operation.SpawnDecoration)
                    ApplyMethodScope(il, c => { c.Emit(OpCodes.Ldarg,il.Method.Parameters[10]); c.EmitDelegate<Func<ProjectileType,int>>(EnterDecorationScope); }, previous => LeaveMethodScope(operation, previous));
                else if (operation == Operation.SpawnBuilding || operation == Operation.AddResource)
                    ApplyMethodScope(il, () => EnterMethodScope(operation), previous => LeaveMethodScope(operation, previous));
            }
            private Delegate Wrap(Delegate original)
            {
                if (ReferenceEquals(original, prior)) return replacement;
                lock (this)
                {
                    if (ReferenceEquals(original, prior)) return replacement;
                    Delegate effectiveOriginal = operation == Operation.Build
                        ? WorldMutations.WorldMutationRuntime.WrapOriginal(original, type, WorldMutations.WorldMutationSource.BuildStructure) :
                        operation == Operation.ReleaseBuildingSlot
                        ? WorldMutations.WorldMutationRuntime.WrapOriginal(original, type, WorldMutations.WorldMutationSource.BuildingRemoval) : original;
                    var invoke = type.GetMethod("Invoke");
                    var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                    Expression interested = Expression.Call(typeof(HumanConstructionRuntime), nameof(Interested), null, Expression.Constant(operation));
                    if (operation == Operation.SpawnDecoration)
                        interested = Expression.AndAlso(Expression.Equal(parameters[10], Expression.Constant(ProjectileType.Brazier)), interested);
                    if (operation == Operation.SpawnUnit)
                        interested = Expression.AndAlso(Expression.OrElse(
                            Expression.Equal(parameters[6], Expression.Constant(eChimps.CHIMP_TYPE_MANGONEL)),
                            Expression.Equal(parameters[6], Expression.Constant(eChimps.CHIMP_TYPE_BALLISTA))), interested);
                    var boxed = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
                    var dispatch = Expression.Call(typeof(HumanConstructionRuntime), nameof(Dispatch), null,
                        Expression.Constant(operation), Expression.Constant(effectiveOriginal, typeof(Delegate)), boxed);
                    Expression slow = invoke.ReturnType == typeof(void) ? (Expression)Expression.Block(dispatch, Expression.Empty()) : Expression.Convert(dispatch, invoke.ReturnType);
                    var body = Expression.Condition(interested, slow, Expression.Invoke(Expression.Constant(effectiveOriginal, type), parameters));
                    replacement = Expression.Lambda(type, body, parameters).Compile(); prior = original;
                    return replacement;
                }
            }
        }
        private static bool Interested(Operation operation)
        {
            if (!ready) return false;
            if (operation == Operation.AddResource) return refund != null;
            if (operation == Operation.SpawnBuilding) return building != null;
            if (operation == Operation.Refund || operation == Operation.MarkBuildingForRemoval || operation == Operation.ReleaseBuildingSlot || operation == Operation.RetireUnit) return HumanConstructionCosts.HasReceipts;
            return HumanConstructionCosts.HasDemand;
        }
        private static object Invoke(Delegate original, object[] args)
        {
            try { return original.DynamicInvoke(args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        private static object Zero(Delegate original) => original.Method.ReturnType == typeof(void) ? null : (object)0L;
        private static bool Human(int player) => player > 0 && player <= GamePlayerManagerAPI.MAX_PLAYERS && !GamePlayerManagerAPI.Instance.IsAIPlayer(player);
        private static bool PaidSession => MissionLifecycleService.ActiveContext != null &&
            (!MissionLifecycleService.ActiveContext.Mode.IsMapEditor ||
             GameData.Instance.lastGameState?.game_type == (int)Enums.eGameTypeModes.GAMETYPE_SIEGE_THAT_BUILDER);
        private static bool Chore(byte opcode, int player, out NativeChoreExecution execution) =>
            NativeChoreContext.TryCaptureRaw(opcode, out execution) && execution.Sender == player;
        private static bool Demolishing(int player, out NativeChoreExecution execution, out long scope)
        {
            scope = demolition?.Player == player ? demolition.Serial : 0;
            execution = default; return scope != 0 || Chore(29, player, out execution);
        }
        private static Dictionary<eGoods,int> Base(ConstructionCostTarget target)
        {
            var values = new Dictionary<eGoods,int>();
            if (target.SupportsVanillaCosts)
            {
                if (!ConstructionCosts.TryGetCurrent(target, out var cost, out string reason)) throw new InvalidOperationException(reason);
                foreach (var good in HumanConstructionGoods.All) { int amount=cost.GetAmount(good); if(amount!=0) values.Add(good,amount); }
            }
            return values;
        }
        private static bool Begin(ConstructionCostTarget target, int player, bool free, out HumanConstructionTransaction transaction,
            HumanConstructionTransaction existingReservation = null)
        {
            transaction = null;
            if (!PaidSession || !Human(player) || free || IsFreeConstruction(target, player)) return true;
            return HumanConstructionCosts.TryBegin(target, player, true, false, Base(target),
                good => GamePlayerManagerAPI.Instance.GetGoodAmount(player,good), out transaction, out _, existingReservation);
        }
        // Runs under the native simulation lock. Preflight all positive debits before the first write;
        // SE's subtract operation is void, and its native storage operations must remain in this order.
        private static bool Book(int player, IReadOnlyDictionary<eGoods,int> amounts)
        {
            var api = GamePlayerManagerAPI.Instance;
            var before = new Dictionary<eGoods,int>();
            foreach(var entry in amounts)
            {
                int available=api.GetGoodAmount(player,entry.Key);
                if(entry.Value>0 && available<entry.Value) return false;
                before[entry.Key]=available;
            }
            try
            {
                foreach(var entry in amounts)
                    if(entry.Value>0) api.RemoveGood(player,entry.Key,entry.Value);
                foreach(var entry in amounts)
                    if(entry.Value<0 && !api.TryAddGood(player,entry.Key,-entry.Value))
                    { RestoreBooking(player,amounts,before); return false; }
                // A later SE participant may cancel a subtraction/addition. Record only actual payments.
                foreach(var entry in amounts)
                    if((long)api.GetGoodAmount(player,entry.Key)!=(long)before[entry.Key]-entry.Value)
                    { RestoreBooking(player,amounts,before); return false; }
                return true;
            }
            catch(Exception error)
            {
                try { RestoreBooking(player,amounts,before); }
                catch(Exception restoreError) { ReportFailure("resource booking rollback",restoreError); }
                ReportFailure("resource booking",error);
                return false;
            }
        }
        private static void RestoreBooking(int player,IReadOnlyDictionary<eGoods,int> amounts,Dictionary<eGoods,int> before)
        {
            var api=GamePlayerManagerAPI.Instance;
            foreach(var entry in amounts)
            {
                int current=api.GetGoodAmount(player,entry.Key);
                long delta=(long)before[entry.Key]-current;
                // Undo only this operation's observed contribution, never mint resources to compensate
                // for a foreign callback's independent expense beyond the requested booking amount.
                if(entry.Value>0 && delta>0)
                {
                    int restore=(int)Math.Min(delta,entry.Value);
                    if(!api.TryAddGood(player,entry.Key,restore))
                        throw new InvalidOperationException("Unable to restore a construction resource reservation.");
                }
                else if(entry.Value<0 && delta<0)
                    api.RemoveGood(player,entry.Key,(int)Math.Min(-delta,-(long)entry.Value));
            }
        }
        private static object Dispatch(Operation operation, Delegate original, object[] args)
        {
            switch(operation)
            {
                case Operation.Build: return Build(original,args);
                case Operation.SpawnBuilding: return SpawnBuilding(original,args);
                case Operation.SpawnUnit: return SpawnUnit(original,args);
                case Operation.SpawnDecoration: return SpawnDecoration(original,args);
                case Operation.Refund: return Refund(original,args);
                case Operation.MarkBuildingForRemoval: return MarkBuildingForRemoval(original,args);
                case Operation.AddResource: return AddResource(original,args);
                case Operation.ReleaseBuildingSlot: return RetireObject(original,args,false);
                case Operation.RetireUnit: return RetireObject(original,args,true);
                default: return Invoke(original,args);
            }
        }
        private static bool Prepare(ConstructionCostTarget target, int player, bool free, out HumanConstructionTransaction transaction)
        {
            transaction = null;
            try
            {
                if (!Begin(target, player, free, out transaction)) return false;
                return HumanConstructionCosts.Reserve(transaction, values => Book(player, values));
            }
            catch (Exception error)
            {
                Cancel(transaction);
                ReportFailure("construction preparation", error);
                return false;
            }
        }
        private static void Cancel(HumanConstructionTransaction transaction)
        {
            try { HumanConstructionCosts.Abort(transaction); }
            catch (Exception error) { ReportFailure("construction reservation refund", error); }
        }
        private static void Complete(HumanConstructionTransaction transaction, ConstructionObjectIdentity identity)
        {
            try
            {
                if (!HumanConstructionCosts.Commit(transaction, identity, values => Book(transaction.PlayerId, values)))
                    ReportFailure("construction payment record", new InvalidOperationException("The confirmed construction payment could not be recorded."));
            }
            catch (Exception error) { ReportFailure("construction payment record", error); }
        }
        private static void ReportFailure(string operation, Exception error)
        {
            // Diagnostics must never replace an already completed native operation's return value.
            try { NativeApiLog.Error(log, "HUMAN_CONSTRUCTION " + operation + ": " + error); } catch { }
        }
        private static object Build(Delegate original, object[] args)
        {
            int player=(int)args[1]; var target=ConstructionCostTarget.FromMapper((eMappers)args[4]);
            if (!target.SupportsHumanExtraCosts || !PaidSession || !Human(player)) return Invoke(original,args);
            if (!Prepare(target,player,Convert.ToByte(args[7])!=0,out var transaction)) return Zero(original);
            if(transaction==null) return Invoke(original,args);
            var previous = building;
            var frame = new BuildFrame
            {
                Player = player, Mapper = (eMappers)args[4], TileX = (int)args[2], TileY = (int)args[3], Scale = (int)args[5],
                Target = target, Transaction = transaction, ExpectedSpawnDepth = buildingSpawnDepth + 1
            };
            building = frame;
            object result;
            try { result=Invoke(original,args); }
            catch { Cancel(transaction); throw; }
            finally { building=previous; }
            if(frame.Identity.IsValid) Complete(transaction,frame.Identity);
            else Cancel(transaction);
            return result;
        }
        private static object SpawnBuilding(Delegate original,object[] args)
        {
            object result=Invoke(original,args);
            try
            {
                long rawId=Convert.ToInt64(result);
                var frame=building;
                if(rawId>0 && rawId<=int.MaxValue && IsPrimaryBuildingSpawn(frame) && frame.Player==(int)args[1] &&
                    ConstructionCostTarget.FromStructure((eStructs)args[5]).Equals(frame.Target) &&
                    BuildingAccess.TryGetById((int)rawId,out GameBuilding* b,out _) && b->r_PlayerIdOwner==frame.Player &&
                    (b->r_AliveState==AliveState.IsAlive || b->r_AliveState==AliveState.NeedsInit))
                    frame.Identity=new ConstructionObjectIdentity(ConstructionObjectKind.Building,(int)rawId,b->r_GlobalId);
            }
            catch (Exception error) { ReportFailure("building identity capture", error); }
            return result;
        }
        private static object SpawnUnit(Delegate original,object[] args)
        {
            int player=(int)args[1];
            if(unitSpawnDepth!=1 || !PaidSession || !Chore(42,player,out _)) return Invoke(original,args);
            var type=(eChimps)args[6]; var target=ConstructionCostTarget.FromStructure(type==eChimps.CHIMP_TYPE_MANGONEL?eStructs.STRUCT_MANGONEL:eStructs.STRUCT_BALLISTA);
            if(!Prepare(target,player,false,out var transaction)) return Zero(original);
            object result;
            try { result=Invoke(original,args); }
            catch { Cancel(transaction); throw; }
            if(transaction==null) return result;
            ConstructionObjectIdentity identity=default;
            try
            {
                long rawId=Convert.ToInt64(result);
                if(rawId>0 && rawId<=int.MaxValue && UnitAccess.TryGetById((int)rawId,out GameUnit* u,out _) &&
                    u->r_UnitChimp==type && u->r_ControllableForPlayerId==player &&
                    (u->r_AliveState==AliveState.NeedsInit || u->r_AliveState==AliveState.IsAlive))
                    identity=new ConstructionObjectIdentity(ConstructionObjectKind.Unit,(int)rawId,u->r_GlobalId);
            }
            catch (Exception error) { ReportFailure("unit identity capture", error); }
            if(identity.IsValid) Complete(transaction,identity); else Cancel(transaction);
            return result;
        }
        private static object SpawnDecoration(Delegate original,object[] args)
        {
            int player=Convert.ToInt32(args[2]);
            if(decorationSpawnDepth!=1 || !PaidSession || !Chore(69,player,out _)) return Invoke(original,args);
            var target=ConstructionCostTarget.FromStructure(eStructs.STRUCT_BRAZIER);
            if(!Prepare(target,player,false,out var transaction)) return Zero(original);
            object result;
            try { result=Invoke(original,args); }
            catch { Cancel(transaction); throw; }
            if(transaction==null) return result;
            ConstructionObjectIdentity identity=default;
            try
            {
                long rawSlot=Convert.ToInt64(result);
                var span=GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
                if(rawSlot>=25 && rawSlot<span.Length)
                {
                    ref var projectile=ref span[(int)rawSlot];
                    if(projectile.r_ProjectileType==ProjectileType.Brazier && (ushort)projectile.r_PlayerSourceId==player &&
                        (projectile.r_AliveState==AliveState.NeedsInit || projectile.r_AliveState==AliveState.IsAlive))
                        identity=new ConstructionObjectIdentity(ConstructionObjectKind.Decoration,(int)rawSlot,projectile.r_GlobalId);
                }
            }
            catch (Exception error) { ReportFailure("decoration identity capture", error); }
            if(identity.IsValid) Complete(transaction,identity); else Cancel(transaction);
            return result;
        }
        private static int QuoteMaterialRefund(eGoods good,int paid,int percentage)
        {
            var api=GameBuildingManagerAPI.Instance; float multiplier;
            switch(good)
            {
                case eGoods.STORED_WOOD_PLANKS: multiplier=api.WoodRefundMultiplier;break;
                case eGoods.STORED_STONE_BLOCKS: multiplier=api.StoneRefundMultiplier;break;
                case eGoods.STORED_IRON_INGOTS: multiplier=api.IronRefundMultiplier;break;
                case eGoods.STORED_PITCH_RAW: multiplier=api.PitchRefundMultiplier;break;
                case eGoods.STORED_GOLD: multiplier=api.GoldRefundMultiplier;break;
                default: multiplier=0.5f;break;
            }
            return HumanConstructionRefundMath.Materials(paid,multiplier,percentage);
        }
        private static object Refund(Delegate original,object[] args)
        {
            // An unassigned nested refund must not borrow its caller's receipt/token. A valid
            // building or Disband path below establishes its own frame after authentication.
            var previous=refund; refund=null;
            try { return RefundCurrent(original,args); }
            finally { refund=previous; }
        }
        private static object RefundCurrent(Delegate original,object[] args)
        {
            if(TryDisbandRefund(original,args,out object disbandResult)) return disbandResult;
            int id=(int)args[1],player=(int)args[2];
            if(id<=0 || !Human(player) || !Demolishing(player,out var chore,out long scope) ||
                !BuildingAccess.TryGetById(id,out GameBuilding* b,out _) || b->r_PlayerIdOwner!=player)
                return InvokeRefundOriginal(original,args);
            var target=ConstructionCostTarget.FromStructure(b->r_BuildingType);
            var frame=new RefundFrame{Identity=new ConstructionObjectIdentity(ConstructionObjectKind.Building,id,b->r_GlobalId),
                Player=player,Target=target,Percentage=ConstructionRefundPolicies.Evaluate(target,player,(int)args[3]),Chore=chore,Scope=scope};
            args[3]=frame.Percentage;
            var previous=refund; refund=frame;
            try { object result=InvokeRefundOriginal(original,args); pending[frame.Identity]=frame; return result; }
            finally { refund=previous; }
        }
        private static object AddResource(Delegate original,object[] args)
        {
            var frame=refund;
            if(MatchesGoldRefundCall() &&
                Convert.ToInt32(args[1])==frame.Player && Convert.ToInt32(args[2])==(int)eGoods.STORED_GOLD &&
                HumanConstructionCosts.TryQuoteRefund(frame.Identity,frame.Player,frame.Player,true,(g,paid)=>QuoteFrameRefund(frame,g,paid),out var amounts) &&
                amounts.TryGetValue(eGoods.STORED_GOLD,out int gold) && gold<0)
            {
                int amount=Convert.ToInt32(args[3]); int withheld=Math.Min(Math.Max(0,amount),Math.Max(0,-gold-frame.WithheldGold));
                args[3]=amount-withheld; frame.WithheldGold+=withheld;
            }
            return Invoke(original,args);
        }
        private static object MarkBuildingForRemoval(Delegate original,object[] args)
        {
            int id=(int)args[1]; ConstructionObjectIdentity identity=default; int owner=0;
            if(BuildingAccess.TryGetById(id,out GameBuilding* b,out _)){identity=new ConstructionObjectIdentity(ConstructionObjectKind.Building,id,b->r_GlobalId);owner=b->r_PlayerIdOwner;}
            object result=Invoke(original,args);
            if(!identity.IsValid || !BuildingAccess.TryGetById(id,out b,out _) || b->r_GlobalId!=identity.GlobalId || b->r_AliveState!=AliveState.MarkedForDeletion) return result;
            if(pending.TryGetValue(identity,out var frame))
            {
                pending.Remove(identity);
                bool scoped=Demolishing(frame.Player,out var chore,out long scope) && frame.Scope==scope && (scope!=0 || frame.Chore.Matches(chore));
                if(scoped) PayRefund(identity,frame.Player,owner,frame.Percentage);
                else HumanConstructionCosts.Retire(identity);
            }
            else HumanConstructionCosts.Retire(identity);
            return result;
        }
        private static object RetireObject(Delegate original,object[] args,bool unit)
        {
            ConstructionObjectIdentity identity=default;
            try
            {
                long raw=Convert.ToInt64(args[1]);
                if(raw>0 && raw<=int.MaxValue)
                {
                    if(unit && UnitAccess.TryGetById((int)raw,out GameUnit* u,out _) &&
                        (u->r_UnitChimp==eChimps.CHIMP_TYPE_MANGONEL || u->r_UnitChimp==eChimps.CHIMP_TYPE_BALLISTA))
                        identity=new ConstructionObjectIdentity(ConstructionObjectKind.Unit,(int)raw,u->r_GlobalId);
                    if(!unit && BuildingAccess.TryGetById((int)raw,out GameBuilding* b,out _))
                        identity=new ConstructionObjectIdentity(ConstructionObjectKind.Building,(int)raw,b->r_GlobalId);
                }
            }
            catch(Exception error){ReportFailure("terminal identity capture",error);}
            object result=Invoke(original,args);
            try {if(identity.IsValid && !CurrentOwner(identity).HasValue) { HumanConstructionCosts.Retire(identity); pending.Remove(identity); }}
            catch(Exception error){ReportFailure("terminal payment retirement",error);}
            return result;
        }
        private static void PayRefund(ConstructionObjectIdentity identity,int actor,int owner,int percentage)
        {
            if(!HumanConstructionCosts.TryConsumeRefund(identity,actor,owner,Human(actor),(g,paid)=>QuoteMaterialRefund(g,paid,percentage),out var amounts)) return;
            foreach(var entry in amounts) if(entry.Value>0) GamePlayerManagerAPI.Instance.TryAddGood(actor,entry.Key,entry.Value);
        }
        /// <summary>Wraps an already authenticated, tick-synchronous custom self-demolition operation. This does
        /// not authorize the actor or execute any native command; consumers retain permission/identity checks.</summary>
        public static void RunSelfDemolition(int actor, Action operation)
        {
            if(operation==null) throw new ArgumentNullException(nameof(operation));
            var previous=demolition; demolition=new DemolitionFrame{Player=actor,Serial=++demolitionSerial};
            try { operation(); } finally { demolition=previous; }
        }
    }
}
