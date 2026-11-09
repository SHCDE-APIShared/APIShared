using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using CrusaderDE;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace APIShared.Economy
{
    // Managed continuations are conditional ONLY on a newly failed critical save on
    // this synchronous caller. Native Vanilla save failures retain their old behavior.
    internal static class WorkerProductionSaveUiRuntime
    {
        private static readonly List<ILHook> hooks = new List<ILHook>();
        private static ManualLogSource log;
        internal static bool IsAvailable { get; private set; }

        internal static void Initialize(ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            var edits = new List<Tuple<MethodInfo, ILContext.Manipulator>>();
            var editor = typeof(EditorDirector).GetMethod("SaveSaveGameOrMap", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (editor == null || editor.ReturnType != typeof(void)) throw new MissingMethodException("EditorDirector.SaveSaveGameOrMap");
            edits.Add(Tuple.Create(editor, (ILContext.Manipulator)TransformEditorSave));
            var requester = typeof(HUD_LoadSaveRequester).GetMethod("RunOKAction", BindingFlags.NonPublic | BindingFlags.Instance);
            if (requester == null || requester.ReturnType != typeof(void)) throw new MissingMethodException("HUD_LoadSaveRequester.RunOKAction");
            var workshop = new List<MethodInfo>();
            FindWorkshopMethods(typeof(HUD_WorkshopUploader), workshop);
            if (workshop.Count != 2) throw new InvalidOperationException("Both workshop save continuations must be identified.");
            foreach (var method in workshop) edits.Add(Tuple.Create(method, (ILContext.Manipulator)TransformWorkshopSave));
            // Verify/load-generate the complete set before the first permanent publication.
            foreach (var edit in edits) Probe(edit.Item1, edit.Item2);
            Probe(requester, TransformRequester);
            var candidates = new List<ILHook>();
            bool publishing = false;
            try
            {
                foreach (var edit in edits) candidates.Add(new ILHook(edit.Item1, edit.Item2,
                    new ILHookConfig { ManualApply = true, ID = "APIShared.WorkerProduction.SaveOutcome." + edit.Item1.Name }));
                hooks.AddRange(candidates); publishing = true;
                foreach (var candidate in candidates) candidate.Apply();
                // The existing requester broker owns this method and its process lifetime.
                Savegames.SaveLoadDialogEvents.InstallCriticalSaveGuard(TransformRequester);
                IsAvailable = true;
            }
            catch
            {
                if (!publishing) foreach (var candidate in candidates) candidate.Dispose();
                throw;
            }
        }
        private static void FindWorkshopMethods(Type type, List<MethodInfo> found)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                using (var definition = new DynamicMethodDefinition(method))
                    if (SaveCalls(definition.Definition, "EditorDirector", "SaveSaveGameOrMap").Length != 0) found.Add(method);
            }
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) FindWorkshopMethods(nested, found);
        }
        private static void Probe(MethodInfo method, ILContext.Manipulator edit)
        {
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            { context.ReferenceBag = RuntimeILReferenceBag.Instance; context.Invoke(edit); GenerateAndCompile(definition); }
        }

        // Generate alone can defer verification until an ILHook applies its detour.
        // Force the installed MonoMod runtime's actual JIT path before publishing any
        // member of this cohort in Unity Mono. Desktop tests JIT an isolated boundary
        // body; the real game bodies contain Unity ECalls unavailable to desktop CLR.
        internal static MethodInfo GenerateAndCompile(DynamicMethodDefinition definition)
        {
            MethodInfo generated = definition.Generate();
            if (DetourHelper.GetNativeStart(generated) == IntPtr.Zero)
                throw new InvalidProgramException("The generated production save continuation has no compiled entry.");
            return generated;
        }

        internal static void TransformEditorSave(ILContext il)
        {
            var calls = SaveCalls(il.Method, "EngineInterface", "SaveSaveGame");
            var names = SaveCalls(il.Method, "GameData", "set_currentFileName");
            var dirty = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f &&
                f.DeclaringType.FullName == "EditorDirector" && f.Name == "mapChanged").ToArray();
            if (calls.Length != 1 || calls[0].Next?.OpCode != OpCodes.Pop ||
                names.Length != 1 || dirty.Length != 1 || dirty[0].Previous?.OpCode != OpCodes.Ldc_I4_0 ||
                il.Body.Instructions.IndexOf(calls[0]) >= il.Body.Instructions.IndexOf(names[0]) ||
                il.Body.Instructions.IndexOf(names[0]) >= il.Body.Instructions.IndexOf(dirty[0]))
                throw new InvalidOperationException("Editor save outcome/dirty/filename IL contract differs.");
            var token = CaptureToken(il);
            // Keep the original pop; only our explicit critical failure skips success state.
            InjectFailureReturn(il, calls[0].Next, token, null);
        }
        internal static void TransformRequester(ILContext il)
        {
            var calls = il.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference m && m.Name == "Invoke" && m.DeclaringType is GenericInstanceType generic &&
                generic.ElementType.FullName == "System.Action`2" && generic.GenericArguments[0].FullName == "System.String" &&
                generic.GenericArguments[1].FullName == "FileHeader").ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Requester confirmation callback IL differs.");
            var token = CaptureToken(il);
            InjectFailureReturn(il, calls[0], token, ReopenRequester);
        }
        internal static void TransformWorkshopSave(ILContext il) => TransformWorkshopSave(il,ReleaseWorkshop);
        // The transform owns control flow; its UI-only reaction is an external boundary
        // which executable desktop fixtures can substitute without initializing Noesis.
        internal static void TransformWorkshopSave(ILContext il,Action failed)
        {
            var calls = SaveCalls(il.Method, "EditorDirector", "SaveSaveGameOrMap");
            if (il.Method.ReturnType.FullName != "System.Void" || calls.Length != 1)
                throw new InvalidOperationException("Workshop save continuation IL differs.");
            var token = CaptureToken(il);
            InjectFailureReturn(il, calls[0], token, failed);
        }
        private static Instruction[] SaveCalls(MethodDefinition method, string owner, string name) =>
            method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference m && m.DeclaringType.FullName == owner && m.Name == name).ToArray();
        private static VariableDefinition CaptureToken(ILContext il)
        {
            var token = new VariableDefinition(il.Method.Module.ImportReference(typeof(long))); il.Body.Variables.Add(token); il.Body.InitLocals = true;
            var cursor = new ILCursor(il); cursor.EmitDelegate<Func<long>>(() => WorkerProductionCriticalSaveRuntime.OutcomeToken);
            cursor.Emit(OpCodes.Stloc, token); return token;
        }
        private static void InjectFailureReturn(ILContext il, Instruction after, VariableDefinition token, Action failed)
        {
            Instruction continuation = after.Next ?? throw new InvalidOperationException("Missing save continuation.");
            NormalizeBranches(il.Method);
            var stacks = ValidateStacks(il.Method);
            if (!stacks.TryGetValue(continuation, out int retained))
                throw new InvalidOperationException("Unreachable save continuation.");
            var cursor = new ILCursor(il); cursor.Goto(after, MoveType.After);
            cursor.Emit(OpCodes.Ldloc, token); cursor.EmitDelegate<Func<long, bool>>(WorkerProductionCriticalSaveRuntime.FailedSince);
            cursor.Emit(OpCodes.Brfalse, continuation);
            if (failed != null) cursor.EmitDelegate(failed);
            // A void call need not empty its caller's stack: the concrete workshop
            // publication lambda keeps GetUserWorkshopPath's duplicated string for
            // its later .data path. Only the aborted path discards those live values.
            for (int index = 0; index < retained; index++) cursor.Emit(OpCodes.Pop);
            cursor.Emit(OpCodes.Ret);
            ValidateStacks(il.Method);
        }

        private static Dictionary<Instruction,int> ValidateStacks(MethodDefinition method)
        {
            // These audited continuations have no exception regions. A future EH body
            // needs a deliberate leave/finally contract, never a ret out of its region.
            if (method.Body.HasExceptionHandlers) throw new InvalidOperationException("Save continuation exception regions are unsupported.");
            var instructions = new HashSet<Instruction>(method.Body.Instructions);
            if (instructions.Count == 0) throw new InvalidOperationException("Empty save continuation.");
            var depths = new Dictionary<Instruction,int>(); var pending = new Queue<Instruction>();
            depths.Add(method.Body.Instructions[0],0); pending.Enqueue(method.Body.Instructions[0]);
            while (pending.Count != 0)
            {
                Instruction instruction = pending.Dequeue(); int depth = depths[instruction], pop, push;
                Code code = instruction.OpCode.Code;
                if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
                {
                    var called = instruction.Operand as MethodReference ?? throw new InvalidOperationException("Unresolved save call.");
                    pop = called.Parameters.Count + (called.HasThis && code != Code.Newobj ? 1 : 0);
                    push = code == Code.Newobj || called.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
                }
                else if (code == Code.Ret)
                {
                    pop = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1; push = 0;
                    if (depth != pop) throw new InvalidProgramException("Production save return has a nonempty or missing result stack.");
                }
                else
                { pop = StackCount(instruction.OpCode.StackBehaviourPop); push = StackCount(instruction.OpCode.StackBehaviourPush); }
                if (depth < pop) throw new InvalidProgramException("Production save continuation stack underflow.");
                int nextDepth = checked(depth - pop + push);
                var targets = new List<Instruction>();
                FlowControl flow = instruction.OpCode.FlowControl;
                if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)
                {
                    if (instruction.Operand is Instruction target) targets.Add(target);
                    else if (instruction.Operand is ILLabel label) targets.Add(label.Target);
                    else if (instruction.Operand is Instruction[] branches) targets.AddRange(branches);
                    else if (instruction.Operand is ILLabel[] labels) targets.AddRange(labels.Select(value => value?.Target));
                    else throw new InvalidProgramException("Unsupported production save branch operand.");
                }
                if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw)
                {
                    if (instruction.Next == null) throw new InvalidProgramException("Production save continuation falls out of its body.");
                    targets.Add(instruction.Next);
                }
                foreach (Instruction target in targets)
                {
                    if (target == null || !instructions.Contains(target)) throw new InvalidProgramException("Production save branch leaves its body.");
                    if (depths.TryGetValue(target,out int previous))
                    { if (previous != nextDepth) throw new InvalidProgramException("Production save branch merges different stack depths."); }
                    else { depths.Add(target,nextDepth); pending.Enqueue(target); }
                }
            }
            return depths;
        }
        private static int StackCount(StackBehaviour value)
        {
            if (value == StackBehaviour.Pop0 || value == StackBehaviour.Push0) return 0;
            if (value == StackBehaviour.Varpop || value == StackBehaviour.Varpush || value.ToString() == "PopAll")
                throw new InvalidProgramException("Unsupported variable-stack production save instruction.");
            return value.ToString().Split('_').Length;
        }
        private static void NormalizeBranches(MethodDefinition method)
        {
            // Insertion can extend a preexisting short branch beyond its signed-byte
            // distance. Preserve its target and always emit the equivalent long form.
            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.OpCode.Code)
                {
                    case Code.Br_S: instruction.OpCode=OpCodes.Br; break;
                    case Code.Brfalse_S: instruction.OpCode=OpCodes.Brfalse; break;
                    case Code.Brtrue_S: instruction.OpCode=OpCodes.Brtrue; break;
                    case Code.Beq_S: instruction.OpCode=OpCodes.Beq; break;
                    case Code.Bge_S: instruction.OpCode=OpCodes.Bge; break;
                    case Code.Bgt_S: instruction.OpCode=OpCodes.Bgt; break;
                    case Code.Ble_S: instruction.OpCode=OpCodes.Ble; break;
                    case Code.Blt_S: instruction.OpCode=OpCodes.Blt; break;
                    case Code.Bne_Un_S: instruction.OpCode=OpCodes.Bne_Un; break;
                    case Code.Bge_Un_S: instruction.OpCode=OpCodes.Bge_Un; break;
                    case Code.Bgt_Un_S: instruction.OpCode=OpCodes.Bgt_Un; break;
                    case Code.Ble_Un_S: instruction.OpCode=OpCodes.Ble_Un; break;
                    case Code.Blt_Un_S: instruction.OpCode=OpCodes.Blt_Un; break;
                    case Code.Leave_S: instruction.OpCode=OpCodes.Leave; break;
                }
            }
        }

        private static void ReopenRequester()
        {
            if (!MainViewModel.viewModelLoaded) return;
            MainViewModel.Instance.Show_HUD_LoadSaveRequester = true;
        }
        private static void ReleaseWorkshop()
        {
            HUD_WorkshopUploader.CanClose = true;
            if (!MainViewModel.viewModelLoaded) return;
            var uploader = MainViewModel.Instance.HUDWorkshopUploader;
            if (uploader?.FindName("UploadPanel") is Noesis.Grid panel) panel.Visibility = Noesis.Visibility.Hidden;
        }
        internal static void ReportFailure(string reason)
        {
            // Never execute UI work from native/Chore callbacks or while the engine lock is held.
            if (!Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                if (MainViewModel.viewModelLoaded)
                    HUD_ConfirmationPopup.ShowConfirmationOKMessage(reason, null, "Production save failed", false);
            }))
                NativeApiLog.Error(log, "Production save failure UI could not be queued: " + reason);
        }
    }

    internal static class WorkerProductionManagedContracts
    {
        internal static void ValidateReceive(MethodInfo method)
        {
            using (var definition = new DynamicMethodDefinition(method))
            {
                int native = 0, enter = 0, exit = 0;
                foreach (var instruction in definition.Definition.Body.Instructions)
                {
                    if (!(instruction.Operand is MethodReference called)) continue;
                    if (called.DeclaringType.FullName == "EngineInterface" && called.Name == "DLL_ReceiveChore") native++;
                    else if (called.DeclaringType.FullName == "System.Threading.Monitor" && called.Name == "Enter") enter++;
                    else if (called.DeclaringType.FullName == "System.Threading.Monitor" && called.Name == "Exit") exit++;
                    else if (instruction.OpCode.FlowControl == FlowControl.Call)
                        throw new InvalidOperationException("Unexpected call in EngineInterface.ReceiveChore: " + called.FullName);
                }
                if (native != 1 || enter != 1 || exit != 1) throw new InvalidOperationException("Engine receive monitor/native-call IL contract differs.");
            }
        }
    }
}
