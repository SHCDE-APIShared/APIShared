using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;

namespace APIShared.ScriptExtenderFixes
{
    internal static partial class SaveContextRuntime
    {
        [ThreadStatic] private static NormalFrame normal;
        // 0=pending, 1=available, 2=failed. A coupled rebuild failure is one-way.
        private static int normalManagedState;
        internal static bool NormalManagedReady => Volatile.Read(ref normalManagedState) == 1;
        private static Patching.SafeIlHook normalProducerHook;

        // Value frames keep unqueried saves free of added snapshots and closures.
        // Present invalid frames still mask callers and the optional SE provider.
        internal struct NormalFrame
        {
            internal bool Enabled, Usable, FactoryStarted, HasPinned, IsSaveFile, IsMapEditorSave;
            internal string Path;
        }

        internal struct NormalScope
        {
            internal NormalFrame Previous;
            internal SaveProducerKind PreviousProducer;
        }

        internal struct SaveFactoryArguments
        {
            internal bool IsSaveFile, IsMapEditorSave;
            internal string FilePath;
        }

        private static NormalScope EnterNormalProducer()
        {
            var previous = new NormalScope { Previous = normal, PreviousProducer = producer };
            normal = new NormalFrame { Enabled = Volatile.Read(ref normalManagedState) == 1 };
            producer = SaveProducerKind.Normal;
            return previous;
        }

        // Called after the existing successful SetCurrentFileContext. Capture its
        // actual decoded local; never decode another pointer or inspect globals.
        private static void PublishNormalDecoded(string path, bool isSaveFile, bool isMapEditorSave)
        {
            if (producer != SaveProducerKind.Normal || !normal.Enabled || string.IsNullOrEmpty(path)) return;
            normal.Path = path;
            normal.IsSaveFile = isSaveFile;
            normal.IsMapEditorSave = isMapEditorSave;
            normal.Usable = true;
        }

        private static void ExitNormalProducer(NormalScope previous)
        { normal = previous.Previous; producer = previous.PreviousProducer; }

        internal static bool TryGetNormalSaveContext(out SaveContext context, out bool active)
        {
            context = null;
            active = producer == SaveProducerKind.Normal;
            if (!active || !normal.Enabled || !normal.Usable || normal.FactoryStarted ||
                synchronizedSaveDepth != 0 || Volatile.Read(ref normalManagedState) != 1) return false;
            context = new SaveContext(normal.IsSaveFile, normal.IsMapEditorSave, normal.Path);
            normal.HasPinned = true;
            return true;
        }

        private static SaveFactoryArguments SelectNormalFactoryContext(bool isSaveFile, bool isMapEditorSave, string path)
        {
            if (producer == SaveProducerKind.Normal)
            {
                // Close before the real constructor, even without an early read.
                // An explicit query pins only the ensuing first selection. Later
                // handler-reentrant factories keep their own legacy arguments.
                bool first = !normal.FactoryStarted;
                normal.FactoryStarted = true;
                if (first && normal.Enabled && normal.HasPinned && synchronizedSaveDepth == 0 && Volatile.Read(ref normalManagedState) == 1)
                    return new SaveFactoryArguments { IsSaveFile = normal.IsSaveFile,
                        IsMapEditorSave = normal.IsMapEditorSave, FilePath = normal.Path };
            }
            return new SaveFactoryArguments { IsSaveFile = isSaveFile,
                IsMapEditorSave = isMapEditorSave, FilePath = path };
        }

        private static void SelectNormalFactoryArguments(ILContext il, Instruction constructor)
        {
            var values = new VariableDefinition(il.Method.Module.ImportReference(typeof(SaveFactoryArguments)));
            il.Body.Variables.Add(values);
            il.Body.InitLocals = true;
            var cursor = new ILCursor(il);
            cursor.Goto(constructor, MoveType.Before);
            // A prior managed patch may branch directly to newobj with the same
            // three arguments. Its incoming labels must enter selection as well.
            cursor.MoveAfterLabels();
            cursor.Emit(OpCodes.Call, typeof(SaveContextRuntime).GetMethod(nameof(SelectNormalFactoryContext), BindingFlags.Static | BindingFlags.NonPublic));
            cursor.Emit(OpCodes.Stloc, values);
            foreach (string name in new[] { nameof(SaveFactoryArguments.IsSaveFile), nameof(SaveFactoryArguments.IsMapEditorSave), nameof(SaveFactoryArguments.FilePath) })
            {
                cursor.Emit(OpCodes.Ldloca, values);
                cursor.Emit(OpCodes.Ldfld, typeof(SaveFactoryArguments).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
            }
        }

        internal static void CorrectNormalProducer(ILContext il)
        {
            const string owner = "SHCDESE.Detours.BulkMapLoaderDetours";
            string[] arguments = { "System.IntPtr", "System.Int32", "System.Int32", "System.Int32", "System.Int32", "System.Int32", "System.Byte", "System.Byte", "System.Byte" };
            var body = il.Body;
            var calls = body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "SetCurrentFileContext" &&
                m.DeclaringType.FullName == typeof(GameMapArchiveManagerAPI).FullName && m.ReturnType.FullName == "System.Void" &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.String", "System.Boolean" })).ToArray();
            var handles = body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference field &&
                field.DeclaringType.FullName == owner && field.Name == "c_game_dll_savesavegame_hook").ToArray();
            var invokes = body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == owner + "/c_game_dll_savesavegame_delegate" && m.Name == "Invoke" &&
                m.ReturnType.FullName == "System.Int64" && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(arguments)).ToArray();
            var returns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (il.Method.HasThis || il.Method.ReturnType.FullName != "System.Int64" ||
                !il.Method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(arguments) ||
                calls.Length != 1 || handles.Length != 1 || invokes.Length != 1 || returns.Length != 1 ||
                body.ExceptionHandlers.Count != 1 || body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Catch ||
                body.ExceptionHandlers[0].CatchType?.FullName != "System.Exception")
                throw new InvalidOperationException("Normal/editor producer signature, setup or catch contract differs.");
            Instruction setup = calls[0], handle = handles[0], invoke = invokes[0];
            VariableDefinition path = LoadedVariable(body, setup.Previous?.Previous);
            VariableDefinition save = LoadedVariable(body, setup.Previous);
            Instruction getter = handle.Next;
            var getterMethod = getter?.Operand as MethodReference;
            if (path?.VariableType.FullName != "System.String" || save?.VariableType.FullName != "System.Boolean" ||
                getter == null || getter.OpCode != OpCodes.Callvirt || getterMethod?.Name != "get_Original" || getterMethod.HasParameters ||
                !(handle.Operand is FieldReference handleField) || getterMethod.DeclaringType.FullName != handleField.FieldType.FullName ||
                !OriginalAccessorReturnsDelegate(getterMethod, owner + "/c_game_dll_savesavegame_delegate") ||
                body.ExceptionHandlers[0].TryStart == null || body.ExceptionHandlers[0].TryEnd == null || body.ExceptionHandlers[0].HandlerEnd == null ||
                body.Instructions.IndexOf(setup) < body.Instructions.IndexOf(body.ExceptionHandlers[0].TryStart) ||
                body.Instructions.IndexOf(setup) >= body.Instructions.IndexOf(body.ExceptionHandlers[0].TryEnd) ||
                body.Instructions.IndexOf(handle) < body.Instructions.IndexOf(body.ExceptionHandlers[0].HandlerEnd) ||
                !ReferenceEquals(invoke.Next, returns[0]))
                throw new InvalidOperationException("Normal/editor decoded setup or Original accessor contract differs.");
            Instruction next = getter.Next;
            for (int index = 0; index < arguments.Length; index++, next = next?.Next)
                if (!LoadsArgument(next, index)) throw new InvalidOperationException("Normal/editor Original argument forwarding differs.");
            if (!ReferenceEquals(next, invoke)) throw new InvalidOperationException("Normal/editor Original invocation differs.");
            ValidateNormalDecodedValues(body, path, save, owner);

            // Validate before mutation. Keep the inner catch and Original outside
            // it, adding one outer finally which restores only caller-thread TLS.
            var module = il.Method.Module;
            var previous = new VariableDefinition(module.ImportReference(typeof(NormalScope)));
            var result = new VariableDefinition(module.TypeSystem.Int64);
            body.Variables.Add(previous); body.Variables.Add(result); body.InitLocals = true;
            Instruction first = body.Instructions[0], finish = Instruction.Create(OpCodes.Ldloc, result);
            returns[0].OpCode = OpCodes.Stloc; returns[0].Operand = result;
            body.Instructions.Insert(body.Instructions.IndexOf(returns[0]) + 1, Instruction.Create(OpCodes.Leave, finish));
            Instruction cleanup = Instruction.Create(OpCodes.Ldloc, previous);
            body.Instructions.Add(cleanup);
            body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SaveContextRuntime).GetMethod(nameof(ExitNormalProducer), BindingFlags.Static | BindingFlags.NonPublic))));
            body.Instructions.Add(Instruction.Create(OpCodes.Endfinally));
            body.Instructions.Add(finish); body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = finish });
            var cursor = new ILCursor(il);
            cursor.Goto(first, MoveType.Before);
            cursor.Emit(OpCodes.Call, typeof(SaveContextRuntime).GetMethod(nameof(EnterNormalProducer), BindingFlags.Static | BindingFlags.NonPublic));
            cursor.Emit(OpCodes.Stloc, previous);
            cursor.Goto(setup, MoveType.After);
            cursor.Emit(OpCodes.Ldloc, path); cursor.Emit(OpCodes.Ldloc, save);
            cursor.Emit(OpCodes.Ldarg, il.Method.Parameters[8]); cursor.Emit(OpCodes.Ldc_I4_0); cursor.Emit(OpCodes.Cgt_Un);
            cursor.Emit(OpCodes.Call, typeof(SaveContextRuntime).GetMethod(nameof(PublishNormalDecoded), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static VariableDefinition LoadedVariable(Mono.Cecil.Cil.MethodBody body, Instruction instruction)
        {
            if (instruction == null) return null;
            if (instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S) return instruction.Operand as VariableDefinition;
            int index = instruction.OpCode == OpCodes.Ldloc_0 ? 0 : instruction.OpCode == OpCodes.Ldloc_1 ? 1 :
                instruction.OpCode == OpCodes.Ldloc_2 ? 2 : instruction.OpCode == OpCodes.Ldloc_3 ? 3 : -1;
            return index >= 0 && index < body.Variables.Count ? body.Variables[index] : null;
        }

        private static bool OriginalAccessorReturnsDelegate(MethodReference method, string delegateName)
        {
            // Cecil retains the generic accessor return as !0 on the actual
            // DetourHandle<T> MemberRef; resolve that exact type argument without
            // loading the hook owner or weakening this to a name-only match.
            if (!(method.DeclaringType is GenericInstanceType handle) || handle.GenericArguments.Count != 1 ||
                handle.ElementType.FullName != "RedBird.Abstractions.Hooks.Transaction.DetourHandle`1" ||
                handle.GenericArguments[0].FullName != delegateName) return false;
            return method.ReturnType.FullName == delegateName || method.ReturnType is GenericParameter parameter &&
                parameter.Type == GenericParameterType.Type && parameter.Position == 0;
        }

        private static bool StoresVariable(Mono.Cecil.Cil.MethodBody body, Instruction instruction, VariableDefinition variable)
        {
            if (instruction == null) return false;
            if (instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S) return ReferenceEquals(instruction.Operand, variable);
            return variable.Index == 0 && instruction.OpCode == OpCodes.Stloc_0 || variable.Index == 1 && instruction.OpCode == OpCodes.Stloc_1 ||
                variable.Index == 2 && instruction.OpCode == OpCodes.Stloc_2 || variable.Index == 3 && instruction.OpCode == OpCodes.Stloc_3;
        }

        private static void ValidateNormalDecodedValues(Mono.Cecil.Cil.MethodBody body, VariableDefinition path, VariableDefinition save, string owner)
        {
            var decoders = body.Instructions.Where(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "System.String" && m.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(new[] { "System.Char*", "System.Int32", "System.Int32" })).ToArray();
            var flags = body.Instructions.Where(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f &&
                f.DeclaringType.FullName == owner && f.Name == "_isMapEditorSave" && f.FieldType.FullName == "System.Boolean").ToArray();
            var saveStores = body.Instructions.Where(i => StoresVariable(body, i, save)).ToArray();
            if (decoders.Length != 1 || flags.Length != 1 || saveStores.Length != 1)
                throw new InvalidOperationException("Normal/editor decoder or flag derivation differs.");
            Instruction decoder = decoders[0], size = decoder.Previous, trim = decoder.Next?.Next?.Next;
            if (size?.OpCode != OpCodes.Div || size.Previous?.OpCode != OpCodes.Ldc_I4_2 || !LoadsArgument(size.Previous.Previous, 1) ||
                size.Previous.Previous.Previous?.OpCode != OpCodes.Ldc_I4_0 ||
                !(size.Previous.Previous.Previous.Previous?.Operand is MethodReference convert) ||
                convert.FullName != "System.Void* System.IntPtr::op_Explicit(System.IntPtr)" ||
                !LoadsArgument(size.Previous.Previous.Previous.Previous.Previous, 0) ||
                decoder.Next?.OpCode != OpCodes.Ldc_I4_1 || decoder.Next.Next?.OpCode != OpCodes.Newarr ||
                !(decoder.Next.Next.Operand is TypeReference character) || character.FullName != "System.Char" ||
                trim?.OpCode != OpCodes.Call || !(trim.Operand is MethodReference trimMethod) ||
                trimMethod.FullName != "System.String System.String::TrimEnd(System.Char[])" || !StoresVariable(body, trim.Next, path) ||
                flags[0].Previous?.OpCode != OpCodes.Cgt_Un || flags[0].Previous.Previous?.OpCode != OpCodes.Ldc_I4_0 ||
                !LoadsArgument(flags[0].Previous.Previous.Previous, 8) ||
                saveStores[0].Previous?.OpCode != OpCodes.Ceq || saveStores[0].Previous.Previous?.OpCode != OpCodes.Ldc_I4_0 ||
                !LoadsArgument(saveStores[0].Previous.Previous.Previous, 8))
                throw new InvalidOperationException("Normal/editor UTF16 or save/editor flag semantics differ.");
        }

        private static bool LoadsArgument(Instruction instruction, int index)
        {
            if (instruction == null) return false;
            if (index == 0 && instruction.OpCode == OpCodes.Ldarg_0 || index == 1 && instruction.OpCode == OpCodes.Ldarg_1 ||
                index == 2 && instruction.OpCode == OpCodes.Ldarg_2 || index == 3 && instruction.OpCode == OpCodes.Ldarg_3) return true;
            return (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S) &&
                instruction.Operand is ParameterDefinition parameter && parameter.Index == index;
        }

        private static void NormalCohortRebuilt(Patching.SafeIlHook hook)
        {
            // Called only when a patch is rebuilt. Do not change existing readiness
            // flags or reactivate this optional scope after a coupled rollback.
            if (!hook.IsApplied) Interlocked.Exchange(ref normalManagedState, 2);
        }

        private static void InitializeNormalManaged(ManualLogSource log)
        {
            try
            {
                if (!ManagedReady || saveFactoryHook?.IsApplied != true) return;
                Type owner = typeof(ModSaveDataAPI).Assembly.GetType("SHCDESE.Detours.BulkMapLoaderDetours", true);
                MethodInfo method = owner.GetMethod("c_game_dll_savesavegame_hook_impl", BindingFlags.Public | BindingFlags.Static);
                if (method == null) throw new MissingMethodException(owner.FullName, "c_game_dll_savesavegame_hook_impl");
                // Qualify both bodies before publishing the optional producer.
                // The existing factory cohort is already applied; failure here
                // leaves its previous corrections and Trail extension available.
                foreach (var edit in new[] {
                    Tuple.Create(Require("PrepareMapArchiveAndGetBytes", typeof(bool)), (ILContext.Manipulator)CorrectSaveFactory),
                    Tuple.Create(method, (ILContext.Manipulator)CorrectNormalProducer) })
                    using (var definition = new DynamicMethodDefinition(edit.Item1))
                    using (var context = new ILContext(definition.Definition))
                    { context.ReferenceBag = RuntimeILReferenceBag.Instance; context.Invoke(edit.Item2); definition.Generate(); }
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.ScriptExtenderFixes.SaveContext.NormalProducer",
                    method, CorrectNormalProducer, log, out normalProducerHook, out string failure) || normalProducerHook?.IsApplied != true)
                    throw new InvalidOperationException(failure);
                normalProducerHook.Rebuilt += NormalCohortRebuilt;
                if (saveFactoryHook.IsApplied && normalProducerHook.IsApplied)
                    Interlocked.CompareExchange(ref normalManagedState, 1, 0);
                else Interlocked.Exchange(ref normalManagedState, 2);
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref normalManagedState, 2);
                NativeApiLog.Error(log, "SE_SAVE_CONTEXT: optional normal/editor producer unavailable; " + error);
            }
        }
    }
}
