using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI.MapLoader;

namespace APIShared.ScriptExtenderFixes
{
    // Correct the shared factory, before any mod receives its context. No per-consumer
    // workaround, settings rule change, archive rewrite, or inferred filename is needed.
    internal static class SaveContextRuntime
    {
        [ThreadStatic] private static int synchronizedSaveDepth;
        // Pending producer and active wrapper frames share this existing factory owner's
        // caller-thread stack. A nested unknown wrapper shadows, never borrows, its caller.
        [ThreadStatic] private static TrailFrame trail;
        internal struct TrailFrame
        {
            internal bool Present, AwaitingWrapper, InWrapper, Usable, FactoryStarted, HasPinned;
            internal string SourceMapPath, Destination, PinnedPath;
            internal byte[] RestartInfo;
        }
        private static Patching.SafeIlHook trailProducerHook, trailWrapperHook;
        internal static bool TrailManagedReady => trailProducerHook?.IsApplied == true && trailWrapperHook?.IsApplied == true;
        private static readonly List<Patching.SafeIlHook> hooks = new List<Patching.SafeIlHook>();
        private static int attempted;
        // Gameplay readiness stays fixed for the process, as before: a rollback in a later rebuild is reported by SafeIlHook and must not flip a feature mid-mission.
        internal static bool ManagedReady { get; private set; }

        internal static void InitializeManaged(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            string stage = "validate-methods";
            try
            {
                var edits = new[] {
                    Tuple.Create(Require("PrepareMapArchiveAndGetBytes", typeof(bool)), (ILContext.Manipulator)CorrectSaveFactory),
                    Tuple.Create(Require("OnLoadMap", typeof(MapLoadEventArgs)), (ILContext.Manipulator)(il => CorrectLoadFlag(il, false))),
                    Tuple.Create(Require("OnLoadSave", typeof(LoadSaveGameEventArgs)), (ILContext.Manipulator)(il => CorrectLoadFlag(il, true)))
                };
                // Validate and generate all bodies before publishing any of the permanent hooks.
                foreach (var edit in edits)
                    using (var definition = new DynamicMethodDefinition(edit.Item1))
                    using (var context = new ILContext(definition.Definition))
                    {
                        context.ReferenceBag = RuntimeILReferenceBag.Instance;
                        context.Invoke(edit.Item2);
                        definition.Generate();
                    }
                // Published hooks are never undone; a failed installation leaves nothing behind.
                stage = "publish-hooks";
                foreach (var edit in edits)
                {
                    if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.ScriptExtenderFixes.SaveContext." + edit.Item1.Name,
                        edit.Item1, edit.Item2, log, out var hook, out string failure))
                        throw new InvalidOperationException(failure);
                    lock (hooks) hooks.Add(hook);
                }
                ManagedReady = true;
                InitializeTrailManaged(log);
                NativeApiLog.Info(log, "SE_SAVE_CONTEXT: managed save factory and map/save load flags corrected; process lifetime.");
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "SE_SAVE_CONTEXT: correction unavailable; stage=" + stage +
                    ",extender=" + typeof(GameMapArchiveManagerAPI).Assembly.FullName +
                    "; synchronized saves may omit save-only records; error=" + error);
            }
        }

        private static MethodInfo Require(string name, Type argument) =>
            typeof(GameMapArchiveManagerAPI).GetMethod(name, BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.NonPublic, null, new[] { argument }, null) ??
                throw new MissingMethodException(typeof(GameMapArchiveManagerAPI).FullName, name);

        internal static void CorrectSaveFactory(ILContext il)
        {
            Instruction[] constructors = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Newobj &&
                i.Operand is MethodReference method && method.DeclaringType.FullName == typeof(SaveContext).FullName).ToArray();
            if (constructors.Length != 1 || !(constructors[0].Operand is MethodReference ctor) ||
                ctor.Parameters.Count != 3 || ctor.Parameters[0].ParameterType.FullName != "System.Boolean" ||
                ctor.Parameters[1].ParameterType.FullName != "System.Boolean" || ctor.Parameters[2].ParameterType.FullName != "System.String" ||
                !il.Body.Instructions.Any(i => i.Operand is MethodReference m &&
                    m.DeclaringType.FullName == typeof(ModSaveDataAPI).FullName && m.Name == "WriteAllModDataToArchive"))
                throw new InvalidOperationException("SaveContext factory/dispatch contract differs: " + il.Method.FullName);
            var cursor = new ILCursor(il);
            cursor.Goto(constructors[0], MoveType.After);
            cursor.EmitDelegate<Func<SaveContext, SaveContext>>(NormalizeSave);
        }

        internal static void CorrectLoadFlag(ILContext il, bool editorCapable)
        {
            Instruction[] archiveReads = il.Body.Instructions.Where(i => i.Operand is MethodReference m &&
                m.Name == "LoadMapArchive" && m.DeclaringType.FullName == typeof(GameMapArchiveManagerAPI).FullName).ToArray();
            Instruction[] stores = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld &&
                i.Operand is FieldReference field && field.DeclaringType.FullName == typeof(GameMapArchiveManagerAPI).FullName &&
                field.Name == "_currentFileIsSaveFile" && field.FieldType.FullName == "System.Boolean").ToArray();
            if (stores.Length != 1 || stores[0].Previous?.OpCode != (editorCapable ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0) ||
                archiveReads.Length != 1)
                throw new InvalidOperationException("Archive load classification contract differs: " + il.Method.FullName);
            var cursor = new ILCursor(il);
            cursor.Goto(stores[0], MoveType.Before);
            cursor.Emit(OpCodes.Pop);
            cursor.Emit(OpCodes.Ldarg_0);
            if (editorCapable) cursor.EmitDelegate<Func<LoadSaveGameEventArgs, bool>>(e => !e.LoadingEditorMap);
            else cursor.EmitDelegate<Func<MapLoadEventArgs, bool>>(e => e.bMultiplayerSave != 0);
            cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            if (editorCapable) cursor.EmitDelegate<Action<LoadSaveGameEventArgs>>(Savegames.VersionedSaveDataRuntime.BeginArchive);
            else cursor.EmitDelegate<Action<MapLoadEventArgs>>(Savegames.VersionedSaveDataRuntime.BeginArchive);
            cursor.Goto(archiveReads[0], MoveType.After);
            cursor.Emit(OpCodes.Ldarg_0);
            if (editorCapable) cursor.EmitDelegate<Action<LoadSaveGameEventArgs>>(Savegames.VersionedSaveDataRuntime.CompleteArchive);
            else cursor.EmitDelegate<Action<MapLoadEventArgs>>(Savegames.VersionedSaveDataRuntime.CompleteArchive);
        }

        // A snapshot query is not factory entry: it must neither close an early
        // context window nor trigger the separately owned critical factory frame.
        internal static SaveContext NormalizeSnapshot(SaveContext context) =>
            synchronizedSaveDepth == 0 ? context : new SaveContext(true, false, null);

        internal static bool TryGetSynchronizedSaveContext(out SaveContext context)
        {
            context = null;
            if (synchronizedSaveDepth == 0) return false;
            context = NormalizeSnapshot(null);
            return true;
        }

        internal static SaveContext NormalizeSave(SaveContext context)
        {
            // Preserve every existing factory payload, including a normal export
            // reentered by an outer Trail handler. The managed Trail scope knows
            // its expected context; it cannot repair or reclassify another save.
            SaveContext normalized = NormalizeSnapshot(context);
            if (trail.Present)
            {
                bool first = !trail.FactoryStarted;
                trail.FactoryStarted = true;
                if (first && trail.InWrapper && trail.HasPinned &&
                    (normalized == null || normalized.IsSaveFile || normalized.IsMapEditorSave ||
                    !string.Equals(normalized.FilePath, trail.PinnedPath, StringComparison.Ordinal)))
                {
                    var frame = Savegames.VersionedSaveFactoryFrame.Current;
                    if (frame != null) frame.RecordFailure(new InvalidOperationException(
                        "The first Trail factory transport differs from its owned source context."));
                }
            }
            ObserveFactoryContext(normalized);
            return normalized;
        }

        internal static bool TryGetTrailSaveContext(out SaveContext context, out bool active)
        {
            context = null;
            active = trail.Present;
            if (!active || !trail.InWrapper || !trail.Usable || trail.FactoryStarted || synchronizedSaveDepth != 0) return false;
            string logicalPath = trail.PinnedPath;
            if (!trail.HasPinned)
            {
                try
                {
                    // LoadMapFile encodes this original managed mappath as Unicode;
                    // the inspected SE load owner decodes all bytes and trims only
                    // trailing NULs. Keep replacement-surrogate and embedded-NUL
                    // behavior identical, without reading mutable manager globals.
                    string path = Encoding.Unicode.GetString(Encoding.Unicode.GetBytes(trail.SourceMapPath)).TrimEnd('\0');
                    if (string.IsNullOrEmpty(path)) { trail.Usable = false; return false; }
                    logicalPath = path;
                }
                catch (Exception) { trail.Usable = false; return false; }
            }
            context = new SaveContext(false, false, logicalPath);
            // Only an accepted detached snapshot opts this factory into pinning.
            trail.PinnedPath = logicalPath;
            trail.HasPinned = true;
            return true;
        }

        // Only the verified successful editor load branch calls this helper.
        // Do no optional encoding/reflection/allocation before an actual query.
        internal static bool CreateTrailMissionFromSource(string destination, byte[] restartInfo, string sourceMapPath)
        {
            TrailFrame prior = trail;
            trail = new TrailFrame { Present = true, AwaitingWrapper = true,
                SourceMapPath = sourceMapPath, Destination = destination, RestartInfo = restartInfo };
            try { return EngineInterface.CreateTrailMission(destination, restartInfo); }
            finally { trail = prior; }
        }

        private static TrailFrame EnterTrailWrapper(string destination, byte[] restartInfo)
        {
            TrailFrame prior = trail;
            if (prior.Present && prior.AwaitingWrapper &&
                ReferenceEquals(prior.Destination, destination) && ReferenceEquals(prior.RestartInfo, restartInfo))
            {
                trail.AwaitingWrapper = false;
                trail.InWrapper = true;
                trail.Usable = !string.IsNullOrEmpty(prior.SourceMapPath);
            }
            else trail = new TrailFrame { Present = true, InWrapper = true };
            return prior;
        }

        private static void ExitTrailWrapper(TrailFrame prior) => trail = prior;

        private static void InitializeTrailManaged(ManualLogSource log)
        {
            // Optional managed extension: failure never disables the existing
            // normal/editor factory correction or synchronized save policy.
            try
            {
                MethodInfo producer = typeof(EditorDirector).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Single(m => m.Name == "SaveCustomTrailMap" && m.ReturnType == typeof(void) &&
                        m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] {
                            "System.String", "System.String", "System.String", "CrusaderDE.HUD_IngameMenu+RestartSkirmishMapInfo" }));
                MethodInfo wrapper = typeof(EngineInterface).GetMethod("CreateTrailMission", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(byte[]) }, null);
                if (wrapper == null || wrapper.ReturnType != typeof(bool)) throw new MissingMethodException("EngineInterface.CreateTrailMission");
                var edits = new[] { Tuple.Create(wrapper, (ILContext.Manipulator)CorrectTrailWrapper),
                    Tuple.Create(producer, (ILContext.Manipulator)CorrectTrailProducer) };
                foreach (var edit in edits)
                    using (var definition = new DynamicMethodDefinition(edit.Item1))
                    using (var context = new ILContext(definition.Definition))
                    { context.ReferenceBag = RuntimeILReferenceBag.Instance; context.Invoke(edit.Item2); definition.Generate(); }
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.ScriptExtenderFixes.SaveContext.TrailWrapper",
                    wrapper, CorrectTrailWrapper, log, out trailWrapperHook, out string wrapperFailure)) throw new InvalidOperationException(wrapperFailure);
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.ScriptExtenderFixes.SaveContext.TrailProducer",
                    producer, CorrectTrailProducer, log, out trailProducerHook, out string producerFailure)) throw new InvalidOperationException(producerFailure);
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "SE_SAVE_CONTEXT: optional managed Trail producer unavailable; " + error);
            }
        }

        internal static void CorrectTrailProducer(ILContext il)
        {
            var calls = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "EngineInterface" && m.Name == "CreateTrailMission" &&
                m.ReturnType.FullName == "System.Boolean" && m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == "System.String" && m.Parameters[1].ParameterType.FullName == "System.Byte[]").ToArray();
            var loads = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "EngineInterface" && m.Name == "LoadMapFile" &&
                m.ReturnType.FullName == "EngineInterface/LoadMapReturnData" && m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == "System.String" && m.Parameters[1].ParameterType.FullName == "System.Boolean").ToArray();
            if (calls.Length != 1 || loads.Length != 1 || il.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException("Trail producer call/exception contract differs.");
            Instruction call = calls[0], load = loads[0], branch = load.Next?.Next?.Next;
            Instruction failureTarget = branch?.Operand is ILLabel label ? label.Target : branch?.Operand as Instruction;
            if (load.Previous?.OpCode != OpCodes.Ldc_I4_1 || load.Previous.Previous?.OpCode != OpCodes.Ldarg_1 ||
                load.Next?.OpCode != OpCodes.Ldfld || !(load.Next.Operand is FieldReference field) ||
                field.Name != "errorCode" || field.FieldType.FullName != "System.Int32" ||
                field.DeclaringType.FullName != "EngineInterface/LoadMapReturnData" ||
                load.Next.Next?.OpCode != OpCodes.Ldc_I4_1 ||
                (branch?.OpCode != OpCodes.Bne_Un && branch?.OpCode != OpCodes.Bne_Un_S) ||
                (failureTarget == null || !ReferenceEquals(failureTarget, call.Next?.Next)) || call.Next?.OpCode != OpCodes.Pop ||
                call.Previous?.OpCode != OpCodes.Ldloc_0 || call.Previous.Previous?.OpCode != OpCodes.Ldarg_3 ||
                il.Body.Instructions.IndexOf(load) >= il.Body.Instructions.IndexOf(call))
                throw new InvalidOperationException("Trail producer must call once after its actual editor-load success.");
            var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Remove();
            cursor.Emit(OpCodes.Call, typeof(SaveContextRuntime).GetMethod(nameof(CreateTrailMissionFromSource), BindingFlags.Static | BindingFlags.NonPublic));
        }

        internal static void CorrectTrailWrapper(ILContext il)
        {
            var calls = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "EngineInterface" && m.Name == "DLL_CreateTrailMission" &&
                m.ReturnType.FullName == "System.Int32" && m.Parameters.Count == 4 &&
                m.Parameters[0].ParameterType.FullName == "System.Byte*" && m.Parameters[1].ParameterType.FullName == "System.Int32" &&
                m.Parameters[2].ParameterType.FullName == "System.Byte*" && m.Parameters[3].ParameterType.FullName == "System.Int32").ToArray();
            var returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (calls.Length != 1 || returns.Length != 1 || il.Body.ExceptionHandlers.Count != 1 ||
                il.Body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Finally ||
                il.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Threading.Monitor" && m.Name == "Enter") != 1 ||
                il.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Threading.Monitor" && m.Name == "Exit") != 1)
                throw new InvalidOperationException("Trail wrapper native call/monitor/finally contract differs.");
            var body = il.Body;
            var prior = new VariableDefinition(il.Method.Module.ImportReference(typeof(TrailFrame)));
            var result = new VariableDefinition(il.Method.Module.TypeSystem.Boolean);
            body.Variables.Add(prior); body.Variables.Add(result); body.InitLocals = true;
            Instruction first = body.Instructions[0], finish = Instruction.Create(OpCodes.Ldloc, result);
            returns[0].OpCode = OpCodes.Stloc; returns[0].Operand = result;
            body.Instructions.Insert(body.Instructions.IndexOf(returns[0]) + 1, Instruction.Create(OpCodes.Leave, finish));
            Instruction cleanup = Instruction.Create(OpCodes.Ldloc, prior);
            body.Instructions.Add(cleanup);
            body.Instructions.Add(Instruction.Create(OpCodes.Call, il.Method.Module.ImportReference(typeof(SaveContextRuntime).GetMethod(nameof(ExitTrailWrapper), BindingFlags.Static | BindingFlags.NonPublic))));
            body.Instructions.Add(Instruction.Create(OpCodes.Endfinally));
            body.Instructions.Add(finish); body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first,
                TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = finish });
            var cursor = new ILCursor(il); cursor.Goto(first, MoveType.Before);
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, typeof(SaveContextRuntime).GetMethod(nameof(EnterTrailWrapper), BindingFlags.Static | BindingFlags.NonPublic));
            cursor.Emit(OpCodes.Stloc, prior);
        }

        private static void ObserveFactoryContext(SaveContext context)
        {
            var frame = Savegames.VersionedSaveFactoryFrame.Current;
            if (frame == null) return;
            // This is the ensuing handler dispatch's exact transport payload,
            // not fresh normal/export/Trail provenance or native reachability.
            if (context == null)
            {
                frame.RecordFailure(new InvalidOperationException("The save factory supplied no context."));
                return;
            }
            frame.Capture(new Savegames.VersionedSaveContext(context.IsSaveFile, context.IsMapEditorSave, context.FilePath));
        }

        internal static void RunSynchronizedSave(Action original)
        {
            synchronizedSaveDepth++;
            try { original(); }
            finally { synchronizedSaveDepth--; }
        }
    }
}
