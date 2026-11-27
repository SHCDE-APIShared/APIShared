using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using APIShared.Internal;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace APIShared.Patching
{
    /// <summary>An IL hook whose manipulator can never break other patches of the same method.</summary>
    /// <remarks>
    /// MonoMod rebuilds a method from its original IL whenever any IL hook or Harmony patch of that
    /// method changes, and runs every applied manipulator again without exception handling. One
    /// throwing manipulator therefore drops every IL edit and Harmony patch of the method, and it keeps
    /// failing every later change because it stays applied. This wrapper runs the manipulator on each
    /// rebuild, catches every exception and restores the method body to the state it received, so the
    /// other patches stay intact. The failed edit is reported once per failure as a mod error.
    /// <para>Install from any thread before the target first runs, or accept that the edit takes effect
    /// on the next call. The hook stays installed for the process lifetime and is never undone after a
    /// successful first application; control the feature through its own logical state.</para>
    /// <para>A manipulator should find all sites before its first edit. The rollback still restores
    /// a partially edited body. It cannot catch IL that is accepted here but rejected later by MonoMod's
    /// code generation or the JIT; <see cref="TryInstall"/> therefore generates and compiles a copy first.
    /// Each rebuild sees the edits of hooks ordered before this one, so later rebuilds can fail even
    /// when the first one succeeded. Check <see cref="IsApplied"/> or confirm the first patched call.</para>
    /// </remarks>
    public sealed class SafeIlHook
    {
        private readonly ILContext.Manipulator manipulator;
        private readonly ManualLogSource log;
        private ILHook hook;
        private string diagnostic = "Pending";
        private int applied;
        private long runs, failures;

        private SafeIlHook(string ownerGuid, string id, MethodBase target, ILContext.Manipulator manipulator, ManualLogSource log)
        { OwnerGuid = ownerGuid; Id = id; Target = target; this.manipulator = manipulator; this.log = log; }

        /// <summary>The owning mod GUID given at installation.</summary>
        public string OwnerGuid { get; }
        /// <summary>The hook ID, passed to MonoMod unchanged; prefix it with the mod name so it is unique.</summary>
        public string Id { get; }
        /// <summary>The patched method.</summary>
        public MethodBase Target { get; }
        /// <summary>True when the most recent rebuild of the method kept this hook's edit.</summary>
        /// <remarks>A later hook's own failure can still prevent the rebuilt body from being published.</remarks>
        public bool IsApplied => Volatile.Read(ref applied) != 0;
        /// <summary>"Applied", or why the most recent rebuild left the method unchanged.</summary>
        public string Diagnostic => Volatile.Read(ref diagnostic);
        /// <summary>How often MonoMod ran this manipulator, including the validation on a copy.</summary>
        public long Runs => Interlocked.Read(ref runs);
        /// <summary>How many runs were rolled back.</summary>
        public long Failures => Interlocked.Read(ref failures);
        /// <summary>Raised after each run of the manipulator with the run's outcome.</summary>
        /// <remarks>Runs on the thread that changed the patches of the method, inside MonoMod's lock.
        /// Keep handlers short; their exceptions are caught and reported.</remarks>
        public event Action<SafeIlHook> Rebuilt;

        /// <summary>Validates the manipulator on a copy of the method, then applies it permanently.</summary>
        /// <param name="ownerGuid">The installing mod's stable GUID.</param>
        /// <param name="id">A stable, globally unique hook ID, e.g. "MyMod.Feature".</param>
        /// <param name="target">A method with an IL body.</param>
        /// <param name="manipulator">The edit. It may throw; nothing escapes this hook.</param>
        /// <param name="log">Receives failure reports; may be null.</param>
        /// <param name="hook">The installed hook, or null.</param>
        /// <param name="reason">Empty on success, otherwise why nothing was installed.</param>
        /// <remarks>Nothing stays installed when this returns false. A failure of the first application
        /// caused by other hooks of the method also removes this unpublished candidate again.</remarks>
        public static bool TryInstall(string ownerGuid, string id, MethodBase target, ILContext.Manipulator manipulator,
            ManualLogSource log, out SafeIlHook hook, out string reason)
        {
            hook = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || target == null || manipulator == null)
            { reason = "Owner, ID, target and manipulator are required."; return false; }
            var candidate = new SafeIlHook(ownerGuid, id, target, manipulator, log);
            if (!candidate.TryValidate(out reason)) return false;
            ILHook native = null;
            try
            {
                native = new ILHook(target, candidate.Run, new ILHookConfig { ManualApply = true, ID = id });
                candidate.hook = native;
                Volatile.Write(ref candidate.applied, 0); // Only the real application may report success.
                native.Apply();
                // The method was rebuilt without this edit (other hooks changed the IL it expects). The edit
                // was rolled back and reported, so nothing of it runs; keeping the hook would let a later
                // rebuild activate an edit whose owner already treats the feature as unavailable.
                if (!candidate.IsApplied) throw new InvalidOperationException(candidate.Diagnostic);
            }
            catch (Exception error)
            {
                // Either case leaves this candidate unpublished: a failed rebuild publishes no body, and a
                // rolled-back edit is not part of the published one. Leaving a failed hook applied would make
                // every later change of the method fail again; remove only this hook.
                try { native?.Dispose(); } catch (Exception cleanup) { candidate.Report("could not remove the unpublished hook: " + cleanup.Message); }
                candidate.hook = null;
                Volatile.Write(ref candidate.applied, 0);
                reason = "Applying failed: " + error.Message;
                Volatile.Write(ref candidate.diagnostic, reason);
                return false;
            }
            hook = candidate; reason = string.Empty; return true;
        }

        /// <summary>Wraps a manipulator for an IL hook that the caller creates and applies itself.</summary>
        /// <remarks>For installers that need their own validation or compiler. Pass <see cref="Manipulator"/>
        /// to the caller's <c>ILHook</c>, validate with the unwrapped manipulator (the wrapped one never throws),
        /// and create that hook with <c>ManualApply = true</c>. Nothing is installed by this call.</remarks>
        public static SafeIlHook Wrap(string ownerGuid, string id, MethodBase target, ILContext.Manipulator manipulator, ManualLogSource log)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || target == null || manipulator == null)
                throw new ArgumentException("Owner, ID, target and manipulator are required.");
            return new SafeIlHook(ownerGuid, id, target, manipulator, log);
        }

        /// <summary>The non-throwing manipulator that MonoMod runs for this hook.</summary>
        public ILContext.Manipulator Manipulator => Run;

        /// <summary>Runs <paramref name="edit"/> on <paramref name="il"/> and restores the body if it throws.</summary>
        /// <remarks>For an independent part of a larger manipulator, so that its failure keeps the other
        /// parts. Returns false with the exception when the body was restored.</remarks>
        public static bool TryEdit(ILContext il, ILContext.Manipulator edit, out Exception error)
        {
            if (il == null) throw new ArgumentNullException(nameof(il));
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            var snapshot = BodySnapshot.Capture(il);
            try { edit(il); error = null; return true; }
            catch (Exception failure)
            {
                error = failure;
                try { snapshot.Restore(il); }
                catch (Exception restore) { error = new AggregateException("The IL body could not be restored.", failure, restore); throw error; }
                return false;
            }
        }

        private bool TryValidate(out string reason)
        {
            try
            {
                if (Target.GetMethodBody() == null) { reason = "The target has no IL body."; return false; }
                using (var definition = new DynamicMethodDefinition(Target))
                {
                    using (var il = new ILContext(definition.Definition))
                    {
                        il.ReferenceBag = RuntimeILReferenceBag.Instance;
                        il.Invoke(Run);
                    }
                    if (!IsApplied) { reason = Diagnostic; return false; }
                    // Generate alone can defer verification; force the real JIT path before publishing.
                    MethodInfo generated = definition.Generate();
                    if (DetourHelper.GetNativeStart(generated) == IntPtr.Zero) throw new InvalidProgramException("The validated copy has no compiled entry.");
                }
                reason = string.Empty; return true;
            }
            catch (Exception error)
            {
                reason = "Validation failed: " + error.Message;
                Volatile.Write(ref applied, 0);
                Volatile.Write(ref diagnostic, reason);
                Report(reason);
                return false;
            }
        }

        private void Run(ILContext il)
        {
            Interlocked.Increment(ref runs);
            bool edited;
            Exception error;
            // A failed restore cannot be repaired here; throwing would still break every other patch.
            try { edited = TryEdit(il, manipulator, out error); }
            catch (Exception restore) { edited = false; error = restore; }
            if (edited)
            {
                Volatile.Write(ref applied, 1);
                Volatile.Write(ref diagnostic, "Applied");
            }
            else
            {
                Interlocked.Increment(ref failures);
                Volatile.Write(ref applied, 0);
                string reason = "Edit rolled back: " + error.Message;
                Volatile.Write(ref diagnostic, reason);
                Report(reason + Environment.NewLine + error);
            }
            var handlers = Rebuilt;
            if (handlers == null) return;
            foreach (Action<SafeIlHook> handler in handlers.GetInvocationList())
            {
                try { handler(this); }
                catch (Exception failure) { Report("Rebuilt handler failed: " + failure.Message); }
            }
        }

        private void Report(string message)
        {
            try { RuntimeHealth.Failure(log ?? RuntimeHealth.ProcessLogger, "Patching/" + OwnerGuid + "/" + Id, true, "IL hook " + Id + " (" + OwnerGuid + ") on " +
                Target.DeclaringType?.FullName + "." + Target.Name + ": " + message); }
            catch { }
        }

        // Everything a manipulator can change through ILContext. Instruction objects are reused, so
        // labels and operands that refer to them stay valid after the restore.
        private sealed class BodySnapshot
        {
            private Instruction[] instructions;
            private OpCode[] opcodes;
            private object[] operands;
            private ILLabel[][] labelArrays;
            private ILLabel[][] labelArrayItems;
            private int[] offsets;
            private VariableDefinition[] variables;
            private TypeReference[] variableTypes;
            private ExceptionHandler[] handlers;
            private Instruction[][] handlerBounds;
            private TypeReference[] catchTypes;
            private ExceptionHandlerType[] handlerTypes;
            private ILLabel[] labels;
            private Instruction[] labelTargets;
            private bool initLocals;
            private int maxStack;

            internal static BodySnapshot Capture(ILContext il)
            {
                var body = il.Body;
                var result = new BodySnapshot { initLocals = body.InitLocals, maxStack = body.MaxStackSize };
                int count = body.Instructions.Count;
                result.instructions = new Instruction[count];
                result.opcodes = new OpCode[count];
                result.operands = new object[count];
                result.labelArrays = new ILLabel[count][];
                result.labelArrayItems = new ILLabel[count][];
                result.offsets = new int[count];
                for (int i = 0; i < count; i++)
                {
                    var instruction = body.Instructions[i];
                    result.instructions[i] = instruction;
                    result.opcodes[i] = instruction.OpCode;
                    result.operands[i] = instruction.Operand;
                    result.offsets[i] = instruction.Offset;
                    if (instruction.Operand is ILLabel[] array)
                    { result.labelArrays[i] = array; result.labelArrayItems[i] = (ILLabel[])array.Clone(); }
                }
                result.variables = new VariableDefinition[body.Variables.Count];
                result.variableTypes = new TypeReference[body.Variables.Count];
                for (int i = 0; i < body.Variables.Count; i++)
                { result.variables[i] = body.Variables[i]; result.variableTypes[i] = body.Variables[i].VariableType; }
                result.handlers = new ExceptionHandler[body.ExceptionHandlers.Count];
                result.handlerBounds = new Instruction[body.ExceptionHandlers.Count][];
                result.catchTypes = new TypeReference[body.ExceptionHandlers.Count];
                result.handlerTypes = new ExceptionHandlerType[body.ExceptionHandlers.Count];
                for (int i = 0; i < body.ExceptionHandlers.Count; i++)
                {
                    var handler = body.ExceptionHandlers[i];
                    result.handlers[i] = handler;
                    result.handlerBounds[i] = new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart };
                    result.catchTypes[i] = handler.CatchType;
                    result.handlerTypes[i] = handler.HandlerType;
                }
                result.labels = new ILLabel[il.Labels.Count];
                result.labelTargets = new Instruction[il.Labels.Count];
                for (int i = 0; i < il.Labels.Count; i++) { result.labels[i] = il.Labels[i]; result.labelTargets[i] = il.Labels[i].Target; }
                return result;
            }

            internal void Restore(ILContext il)
            {
                var body = il.Body;
                body.Instructions.Clear();
                for (int i = 0; i < instructions.Length; i++)
                {
                    var instruction = instructions[i];
                    instruction.OpCode = opcodes[i];
                    instruction.Operand = operands[i];
                    instruction.Offset = offsets[i];
                    if (labelArrays[i] != null) Array.Copy(labelArrayItems[i], labelArrays[i], labelArrays[i].Length);
                    body.Instructions.Add(instruction);
                }
                // Cecil's Clear keeps the outer links of the old first and last instruction.
                if (instructions.Length > 0) { instructions[0].Previous = null; instructions[instructions.Length - 1].Next = null; }
                body.Variables.Clear();
                for (int i = 0; i < variables.Length; i++) { variables[i].VariableType = variableTypes[i]; body.Variables.Add(variables[i]); }
                body.ExceptionHandlers.Clear();
                for (int i = 0; i < handlers.Length; i++)
                {
                    var handler = handlers[i];
                    handler.TryStart = handlerBounds[i][0]; handler.TryEnd = handlerBounds[i][1];
                    handler.HandlerStart = handlerBounds[i][2]; handler.HandlerEnd = handlerBounds[i][3];
                    handler.FilterStart = handlerBounds[i][4]; handler.CatchType = catchTypes[i]; handler.HandlerType = handlerTypes[i];
                    body.ExceptionHandlers.Add(handler);
                }
                for (int i = 0; i < labels.Length; i++) labels[i].Target = labelTargets[i];
                body.InitLocals = initLocals;
                body.MaxStackSize = maxStack;
            }
        }
    }
}
