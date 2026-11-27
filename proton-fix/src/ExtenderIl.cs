using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.ProtonWorkshopFix
{
    internal static class ExtenderIl
    {
        internal static bool Calls(Instruction instruction, string type, string name)
        {
            var reference = instruction?.Operand as MethodReference;
            return reference != null && (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                reference.DeclaringType.FullName == type && reference.Name == name;
        }

        internal static void RejectBoundary(ILContext il, Instruction site)
        {
            if (site == null || il.Body.Instructions.Any(i => ReferenceEquals(i.Operand, site) ||
                ((i.Operand as Instruction[]) ?? new Instruction[0]).Contains(site)) || il.Body.ExceptionHandlers.Any(h =>
                h.TryStart == site || h.TryEnd == site || h.HandlerStart == site || h.HandlerEnd == site || h.FilterStart == site))
                throw new InvalidOperationException("Incoming IL branch or exception boundary at patch insertion.");
        }

        internal static void ValidateCounter(ILContext il, int limit, string managerTypeName = ExtenderContract.ManagerName)
        {
            var code = il.Body.Instructions;
            string type = managerTypeName;
            var reads = code.Where(i => Calls(i, type, "ReadDeployAttempts")).ToArray();
            var writes = code.Where(i => Calls(i, type, "WriteDeployAttempts")).ToArray();
            var launches = code.Where(i => Calls(i, type, "LaunchUpdaterAndExit")).ToArray();
            var confirmations = code.Where(i => Calls(i, "SHCDESE.Interop.MinWinAPI", "MessageBoxA")).ToArray();
            if (reads.Length != 1 || writes.Length != 1 || launches.Length != 1 || confirmations.Length != 1)
                throw new InvalidOperationException("Ambiguous deployment read/write/confirmation/launch sites.");
            Instruction read = reads[0], write = writes[0], consent = confirmations[0], launch = launches[0];
            if (read.Previous?.OpCode != OpCodes.Ldarg_0 || !IsStoreLocal(read.Next) ||
                !IsLoadLocal(read.Next.Next) || LocalIndex(read.Next) != LocalIndex(read.Next.Next) ||
                Integer(read.Next.Next.Next) != limit ||
                (read.Next.Next.Next.Next?.OpCode != OpCodes.Blt && read.Next.Next.Next.Next?.OpCode != OpCodes.Blt_S))
                throw new InvalidOperationException("Attempt-limit comparison changed.");
            var compare = read.Next.Next.Next.Next;
            int readIndex = code.IndexOf(read), consentIndex = code.IndexOf(consent), writeIndex = code.IndexOf(write), launchIndex = code.IndexOf(launch);
            if (!(compare.Operand is Instruction allowed) || code.IndexOf(allowed) <= readIndex || code.IndexOf(allowed) > consentIndex ||
                consentIndex <= readIndex || writeIndex <= consentIndex || launchIndex <= writeIndex ||
                Integer(consent.Next) != 6 || (consent.Next.Next?.OpCode != OpCodes.Beq && consent.Next.Next?.OpCode != OpCodes.Beq_S))
                throw new InvalidOperationException("Deployment consent order changed.");
            Instruction success = consent.Next.Next.Operand as Instruction;
            if (write.Previous?.OpCode != OpCodes.Add || Integer(write.Previous.Previous) != 1 ||
                !IsLoadLocal(write.Previous.Previous.Previous) ||
                LocalIndex(write.Previous.Previous.Previous) != LocalIndex(read.Next) ||
                write.Previous.Previous.Previous.Previous?.OpCode != OpCodes.Ldarg_0 ||
                success != write.Previous.Previous.Previous.Previous ||
                write.Next?.OpCode != OpCodes.Ldarg_0 || write.Next.Next != launch || launch.Next?.OpCode != OpCodes.Ret)
                throw new InvalidOperationException("Confirmation no longer directly owns the attempt and launcher.");
            RejectBoundary(il, read.Next); RejectBoundary(il, write);
            // The comparison must have a terminating rejection path before the consent block.
            if (!code.Skip(code.IndexOf(compare) + 1).Take(code.IndexOf(allowed) - code.IndexOf(compare) - 1).Any(i => i.OpCode == OpCodes.Ret))
                throw new InvalidOperationException("Attempt-limit rejection no longer terminates.");
        }

        internal static void ModifyCounter(ILContext il, int limit, string managerTypeName = ExtenderContract.ManagerName)
        {
            ValidateCounter(il, limit, managerTypeName); // Inspect everything before the first mutation.
            string type = managerTypeName;
            var read = il.Body.Instructions.Single(i => Calls(i, type, "ReadDeployAttempts"));
            var write = il.Body.Instructions.Single(i => Calls(i, type, "WriteDeployAttempts"));
            var cursor = new ILCursor(il);
            cursor.Goto(write, MoveType.Before);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, Callback(typeof(RetryGate), "BeforeWrite"));
            cursor.Goto(read, MoveType.After);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Call, Callback(typeof(RetryGate), "AfterRead"));
        }

        internal static void ModifyUnix(ILContext il, ExtenderContract contract)
        {
            // DMD represents instance methods as static copies with an explicit owner argument.
            int contextIndex = il.Method.HasThis ? 0 : 1;
            if (il.Method.ReturnType.FullName != "System.Boolean" || il.Method.Parameters.Count != contextIndex + 1 ||
                (!il.Method.HasThis && il.Method.Parameters[0].ParameterType.FullName != contract.ManagerTypeName) ||
                !il.Method.Parameters[contextIndex].ParameterType.IsByReference || il.Body.Instructions.Count == 0)
                throw new InvalidOperationException("Linux launcher body/signature changed.");
            var original = il.Body.Instructions[0];
            var cursor = new ILCursor(il);
            cursor.Goto(original, MoveType.Before);
            // Existing incoming labels/handlers stay on the original instruction, never on this guarded entry.
            cursor.Emit(OpCodes.Call, Callback(typeof(PatcherRuntime), "IsActive"));
            cursor.Emit(OpCodes.Brfalse, original);
            cursor.Emit(OpCodes.Ldarg_0);
            foreach (var getter in contract.Getters) { cursor.Emit(OpCodes.Ldarg_1); cursor.Emit(OpCodes.Call, getter); }
            cursor.Emit(OpCodes.Call, Callback(typeof(ExtenderDeployment), "Launch"));
            cursor.Emit(OpCodes.Ret);
        }

        private static MethodInfo Callback(Type type, string name) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static bool IsStoreLocal(Instruction i) => i != null && (i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Stloc_S ||
            i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Stloc_1 || i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Stloc_3);
        private static bool IsLoadLocal(Instruction i) => i != null && (i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloc_S ||
            i.OpCode == OpCodes.Ldloc_0 || i.OpCode == OpCodes.Ldloc_1 || i.OpCode == OpCodes.Ldloc_2 || i.OpCode == OpCodes.Ldloc_3);
        private static int LocalIndex(Instruction i)
        {
            if (i.Operand is VariableDefinition variable) return variable.Index;
            if (i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Ldloc_0) return 0;
            if (i.OpCode == OpCodes.Stloc_1 || i.OpCode == OpCodes.Ldloc_1) return 1;
            if (i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Ldloc_2) return 2;
            if (i.OpCode == OpCodes.Stloc_3 || i.OpCode == OpCodes.Ldloc_3) return 3;
            return -1;
        }
        private static int? Integer(Instruction i)
        {
            if (i == null) return null;
            if (i.OpCode == OpCodes.Ldc_I4) return (int)i.Operand;
            if (i.OpCode == OpCodes.Ldc_I4_S) return Convert.ToInt32(i.Operand);
            if (i.OpCode == OpCodes.Ldc_I4_M1) return -1;
            if (i.OpCode == OpCodes.Ldc_I4_0) return 0;
            if (i.OpCode == OpCodes.Ldc_I4_1) return 1;
            if (i.OpCode == OpCodes.Ldc_I4_2) return 2;
            if (i.OpCode == OpCodes.Ldc_I4_3) return 3;
            if (i.OpCode == OpCodes.Ldc_I4_4) return 4;
            if (i.OpCode == OpCodes.Ldc_I4_5) return 5;
            if (i.OpCode == OpCodes.Ldc_I4_6) return 6;
            if (i.OpCode == OpCodes.Ldc_I4_7) return 7;
            if (i.OpCode == OpCodes.Ldc_I4_8) return 8;
            return null;
        }
    }
}
