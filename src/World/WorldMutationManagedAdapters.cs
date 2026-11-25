using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.LowLevel;

namespace APIShared.WorldMutations
{
    internal static partial class WorldMutationRuntime
    {
        private static readonly List<Patching.SafeIlHook> hooks = new List<Patching.SafeIlHook>();
        private static bool initialized;
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            log = logger;
            if (initialized) return;
            initialized = true;
            if (hash != ApiSharedRuntime.SupportedHash)
                throw new InvalidOperationException("World-mutation source contracts require the audited native hash.");
            if (ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var missions, out var diagnostic))
            {
                if (!missions.TryRegisterObserver("world-mutations-world", _ => { Registry.Invalidate(); RecheckNative(); },
                    _ => { System.Threading.Volatile.Write(ref suspended, 0); Registry.Invalidate(); },
                    phase => { if (phase.IsBeforeInitialization) Registry.Invalidate(); }, out diagnostic))
                    throw new InvalidOperationException("World-mutation lifecycle registration failed: " + diagnostic.Reason);
            }
            else throw new InvalidOperationException("World-mutation lifecycle is unavailable: " + diagnostic.Reason);
            if (Multiplayer.MultiplayerResyncEvents.IsAvailable &&
                !Multiplayer.MultiplayerResyncEvents.TryRegister(APISharedPlugin.PluginGuid, "world-mutations-world",
                    () => { System.Threading.Volatile.Write(ref suspended, 1); Registry.Invalidate(); },
                    () => { Registry.Invalidate(); System.Threading.Volatile.Write(ref suspended, 0); }, out string reason))
                throw new InvalidOperationException(reason);
            System.Threading.Volatile.Write(ref worldLifecycleReady,1);
            if (ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetAivBuildStep(out _, out _))
                Available(WorldMutationSource.AivBuildStep);
            var assembly = typeof(GameBuildingManagerAPI).Assembly;
            TryInstall(assembly, "BulkBuildingDetours", "c_game_player_build_structure_hook_impl",
                "c_game_player_build_structure_delegate", WorldMutationSource.BuildStructure, true);
            TryInstall(assembly, "BulkBuildingDetours", "c_game_build_wall_hook_impl",
                "c_game_build_wall_delegate", WorldMutationSource.HumanWall, false);
            TryInstall(assembly, "BulkAIDetours", "c_game_ai_build_wall_hook_impl",
                "c_game_ai_build_wall_delegate", WorldMutationSource.AiWall, false);
            TryInstall(assembly, "BulkBuildingDetours", "c_game_building_bulldoze_hook_impl",
                "c_game_building_bulldoze_delegate", WorldMutationSource.BuildingRemoval, true);
            TryInstall(assembly, "BulkBuildingDetours", "c_game_bulldoze_wall_hook_impl",
                "c_game_bulldoze_wall_delegate", WorldMutationSource.WallRemoval, false);
            string[] editor = { "set_tiletype", "set_tiletype_to_none", "set_height_to_preset", "raise_tileheight",
                "set_height_to_number", "even_height", "delete" };
            WorldMutationSource[] editorSources = { WorldMutationSource.EditorTileType, WorldMutationSource.EditorTileTypeClear,
                WorldMutationSource.EditorHeightPreset, WorldMutationSource.EditorHeightRaise, WorldMutationSource.EditorHeightNumber,
                WorldMutationSource.EditorHeightEven, WorldMutationSource.EditorDelete };
            for (int index = 0; index < editor.Length; index++)
                TryInstall(assembly, "BulkMapEditorDetours", "c_game_editor_brush_" + editor[index] + "_hook_impl",
                    "c_game_editor_brush_" + editor[index] + "_delegate", editorSources[index], false);
            InitializeNative(context, hash);
            NativeApiLog.Info(log, "WORLD_MUTATIONS_READY: available=" + Registry.AvailableSources + "; original-owned adapters; no observer means no mutation capture.");
        }
        private static void Available(WorldMutationSource source) => Registry.SetAvailable(Registry.AvailableSources | source);
        private static void TryInstall(Assembly assembly, string className, string methodName,
            string delegateName, WorldMutationSource source, bool existingBuildOwner)
        {
            try
            {
                Type type = assembly.GetType("SHCDESE.Detours." + className, true);
                Type nativeDelegate = type.GetNestedType(delegateName, BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null || nativeDelegate == null) throw new MissingMethodException(className + "." + methodName);
                ValidateDelegate(nativeDelegate, source);
                var invoke = nativeDelegate.GetMethod("Invoke");
                if (method.ReturnType != invoke.ReturnType || !method.GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual(invoke.GetParameters().Select(p => p.ParameterType)))
                    throw new InvalidOperationException("World-mutation outer wrapper ABI changed.");
                if (existingBuildOwner && (source == WorldMutationSource.BuildStructure
                    ? !Buildings.HumanConstructionRuntime.HasBuildOriginalBoundary : !Buildings.HumanConstructionRuntime.HasRemovalOriginalBoundary))
                    throw new InvalidOperationException("The existing building-original owner did not initialize.");
                var targets = new List<MethodInfo> { method };
                if (!existingBuildOwner)
                    foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        targets.AddRange(nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .Where(candidate => candidate.Name.Contains("<" + methodName + ">")));
                foreach (var target in targets)
                {
                    bool outer = target == method;
                    var site = new OriginalSite(source, nativeDelegate);
                    if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "WorldMutation." + className + "." + target.Name,
                        target, il => { if (!existingBuildOwner) site.Transform(il); if (outer) ApplyOuterScope(il, source); },
                        log, out var hook, out string reason)) throw new InvalidOperationException(reason);
                    hooks.Add(hook);
                }
                Available(source);
            }
            catch (Exception error)
            {
                // Already published manipulations stay permanently rooted and source-dormant.
                try { NativeApiLog.Error(log, "WORLD_MUTATION_SOURCE_UNAVAILABLE: " + source + ": " + error); } catch { }
            }
        }
        internal static void ValidateDelegate(Type type, WorldMutationSource source)
        {
            Type pointer = typeof(IntPtr), integer = typeof(int), mapper = typeof(SHCDESE.Interop.eMappers);
            Type[] expected;
            switch (source)
            {
                case WorldMutationSource.BuildStructure: expected = new[] { pointer, integer, integer, integer, mapper, integer, integer, typeof(byte) }; break;
                case WorldMutationSource.HumanWall: expected = new[] { pointer, integer, integer, integer, integer, integer, mapper, integer }; break;
                case WorldMutationSource.AiWall: expected = new[] { pointer, integer, integer, integer, mapper }; break;
                case WorldMutationSource.BuildingRemoval: expected = new[] { typeof(RedBird.Core.Memory.NativePointer<SHCDESE.Interop.GameBuildingManager>), integer }; break;
                case WorldMutationSource.WallRemoval: expected = new[] { pointer, integer, integer }; break;
                case WorldMutationSource.EditorTileType: expected = new[] { pointer, integer, integer, integer, integer, integer, integer, typeof(byte) }; break;
                case WorldMutationSource.EditorHeightPreset:
                case WorldMutationSource.EditorHeightRaise: expected = new[] { pointer, integer, integer, integer, integer }; break;
                case WorldMutationSource.EditorHeightNumber: expected = new[] { pointer, integer, integer, integer, typeof(short) }; break;
                case WorldMutationSource.EditorTileTypeClear:
                case WorldMutationSource.EditorHeightEven:
                case WorldMutationSource.EditorDelete: expected = new[] { pointer, integer, integer, integer }; break;
                default: throw new InvalidOperationException("Undeclared world-mutation delegate source.");
            }
            var invoke = type?.GetMethod("Invoke");
            if (invoke == null || invoke.ReturnType != (source == WorldMutationSource.AiWall ? typeof(void) : typeof(long)) ||
                !invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(expected))
                throw new InvalidOperationException("World-mutation effective native delegate ABI changed: " + source);
        }
        private sealed class OriginalSite
        {
            private readonly WorldMutationSource source;
            private readonly Type type;
            private Delegate prior, replacement;
            internal OriginalSite(WorldMutationSource source, Type type) { this.source = source; this.type = type; }
            internal void Transform(ILContext il)
            {
                var getters = il.Body.Instructions.Where(instruction => instruction.Operand is MethodReference method &&
                    method.Name == "get_Original" && method.DeclaringType.FullName.Contains("DetourHandle")).ToArray();
                if (getters.Length != 1) throw new InvalidOperationException("Expected one effective native Original getter: " + il.Method.FullName);
                var cursor = new ILCursor(il); cursor.Goto(getters[0], MoveType.After);
                cursor.EmitDelegate<Func<Delegate, Delegate>>(Wrap); cursor.Emit(OpCodes.Castclass, type);
            }
            private Delegate Wrap(Delegate original)
            {
                if (ReferenceEquals(original, prior)) return replacement;
                lock (this)
                {
                    if (ReferenceEquals(original, prior)) return replacement;
                    replacement = WrapOriginal(original, type, source); prior = original;
                    return replacement;
                }
            }
        }
        internal static void ApplyOuterScope(ILContext il, WorldMutationSource source)
        {
            if (il.Body.Instructions.Count == 0 || il.Body.Instructions.Any(i => i.OpCode == OpCodes.Tail || i.OpCode == OpCodes.Jmp) ||
                il.Method.ReturnType.MetadataType != MetadataType.Int64 && il.Method.ReturnType.MetadataType != MetadataType.Void)
                throw new InvalidOperationException("World-mutation outer wrapper return contract changed.");
            bool returnsValue = il.Method.ReturnType.MetadataType != MetadataType.Void;
            var returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (returns.Length == 0) throw new InvalidOperationException("No outer wrapper return.");
            foreach (var instruction in il.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = LongBranch(instruction.OpCode);
            var token = new VariableDefinition(il.Import(typeof(long)));
            var returned = new VariableDefinition(il.Import(typeof(bool)));
            VariableDefinition result = returnsValue ? new VariableDefinition(il.Method.ReturnType) : null;
            il.Body.Variables.Add(token); il.Body.Variables.Add(returned);
            if (returnsValue) il.Body.Variables.Add(result);
            il.Body.InitLocals = true;
            var first = il.Body.Instructions[0]; var cursor = new ILCursor(il);
            cursor.Goto(first, MoveType.Before);
            cursor.EmitDelegate<Func<long>>(() => Enter(source)); cursor.Emit(OpCodes.Stloc, token);
            var after = returnsValue ? Instruction.Create(OpCodes.Ldloc, result) : Instruction.Create(OpCodes.Nop);
            foreach (var ret in returns)
            {
                ret.OpCode = returnsValue ? OpCodes.Stloc : OpCodes.Nop;
                ret.Operand = returnsValue ? (object)result : null;
                cursor.Goto(ret, MoveType.After); cursor.Emit(OpCodes.Ldc_I4_1); cursor.Emit(OpCodes.Stloc, returned);
                cursor.Emit(OpCodes.Leave, after);
            }
            cursor.Goto(il.Body.Instructions.Last(), MoveType.After);
            cursor.Emit(OpCodes.Nop); var finallyStart = cursor.Prev;
            cursor.Emit(OpCodes.Ldloc, token); cursor.Emit(OpCodes.Ldloc, returned);
            cursor.EmitDelegate<Action<long, bool>>(Leave); cursor.Emit(OpCodes.Endfinally);
            il.Body.Instructions.Add(after); il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
        }
        private static OpCode LongBranch(OpCode code)
        {
            switch (code.Code)
            {
                case Code.Br_S: return OpCodes.Br; case Code.Brfalse_S: return OpCodes.Brfalse; case Code.Brtrue_S: return OpCodes.Brtrue;
                case Code.Beq_S: return OpCodes.Beq; case Code.Bge_S: return OpCodes.Bge; case Code.Bge_Un_S: return OpCodes.Bge_Un;
                case Code.Bgt_S: return OpCodes.Bgt; case Code.Bgt_Un_S: return OpCodes.Bgt_Un; case Code.Ble_S: return OpCodes.Ble;
                case Code.Ble_Un_S: return OpCodes.Ble_Un; case Code.Blt_S: return OpCodes.Blt; case Code.Blt_Un_S: return OpCodes.Blt_Un;
                case Code.Bne_Un_S: return OpCodes.Bne_Un; case Code.Leave_S: return OpCodes.Leave;
                default: throw new InvalidOperationException("Unknown short branch in outer operation scope.");
            }
        }
    }
}
