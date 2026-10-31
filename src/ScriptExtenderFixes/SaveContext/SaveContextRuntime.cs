using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static readonly List<ILHook> hooks = new List<ILHook>();
        private static int attempted;
        internal static bool ManagedReady { get; private set; }

        internal static void InitializeManaged(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            var candidates = new List<ILHook>();
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
                stage = "prepare-hooks";
                foreach (var edit in edits)
                    candidates.Add(new ILHook(edit.Item1, edit.Item2, new ILHookConfig {
                        ManualApply = true, ID = "APIShared.ScriptExtenderFixes.SaveContext." + edit.Item1.Name }));
                // Root before Apply: even a failed/partial publication must never be undone.
                hooks.AddRange(candidates);
                stage = "publish-hooks";
                foreach (var hook in candidates) hook.Apply();
                ManagedReady = true;
                NativeApiLog.Info(log, "SE_SAVE_CONTEXT: managed save factory and map/save load flags corrected; process lifetime.");
            }
            catch (Exception error)
            {
                if (stage != "publish-hooks") foreach (var candidate in candidates) candidate.Dispose();
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
            Instruction[] stores = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld &&
                i.Operand is FieldReference field && field.DeclaringType.FullName == typeof(GameMapArchiveManagerAPI).FullName &&
                field.Name == "_currentFileIsSaveFile" && field.FieldType.FullName == "System.Boolean").ToArray();
            if (stores.Length != 1 || stores[0].Previous?.OpCode != (editorCapable ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0) ||
                !il.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "LoadMapArchive" &&
                    m.DeclaringType.FullName == typeof(GameMapArchiveManagerAPI).FullName))
                throw new InvalidOperationException("Archive load classification contract differs: " + il.Method.FullName);
            var cursor = new ILCursor(il);
            cursor.Goto(stores[0], MoveType.Before);
            cursor.Emit(OpCodes.Pop);
            cursor.Emit(OpCodes.Ldarg_0);
            if (editorCapable) cursor.EmitDelegate<Func<LoadSaveGameEventArgs, bool>>(e => !e.LoadingEditorMap);
            else cursor.EmitDelegate<Func<MapLoadEventArgs, bool>>(e => e.bMultiplayerSave != 0);
        }

        internal static SaveContext NormalizeSave(SaveContext context) => synchronizedSaveDepth == 0 ? context :
            // SE's path may still name the source map. The Chore has no managed target path.
            new SaveContext(true, false, null);

        internal static void RunSynchronizedSave(Action original)
        {
            synchronizedSaveDepth++;
            try { original(); }
            finally { synchronizedSaveDepth--; }
        }
    }
}
