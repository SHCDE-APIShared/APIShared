using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Economy
{
    // Startup-only data-flow analysis. Public SE member identities describe the
    // capability; generated types, local numbers, opcodes and site counts do not.
    // Unknown/ambiguous flows are rejected before any instruction is edited.
    internal static class FearPopularityIl
    {
        [Flags]
        private enum Kind { Other=1, Zero=2, Handle=4, Coefficient=8, Fear=16, Product=32, FearAddress=64, ResultAddress=128, LocalAddress=256, ArgumentAddress=512 }
        private sealed class Value
        {
            internal readonly Kind Kind;
            internal readonly HashSet<object> Origins;
            internal Value(Kind kind, params object[] origins) { Kind=kind; Origins=new HashSet<object>(origins); }
            internal Value(Kind kind, IEnumerable<object> origins) { Kind=kind; Origins=new HashSet<object>(origins); }
            internal static readonly Value Other = new Value(Kind.Other), Zero = new Value(Kind.Zero);
            internal static Value Merge(Value a, Value b)
            {
                if (a.Kind == b.Kind && a.Origins.SetEquals(b.Origins)) return a;
                return new Value(a.Kind | b.Kind, a.Origins.Concat(b.Origins));
            }
        }
        private sealed class Frame
        {
            internal readonly List<Value> Stack;
            internal readonly Value[] Locals, Arguments;
            internal Frame(MethodDefinition method)
            {
                Stack=new List<Value>(); Locals=Enumerable.Repeat(Value.Other,method.Body.Variables.Count).ToArray();
                Arguments=(method.HasThis?new[]{new Value(Kind.Other,method)}:new Value[0])
                    .Concat(method.Parameters.Select(p=>new Value(Kind.Other,p))).ToArray();
            }
            private Frame(Frame source) { Stack=new List<Value>(source.Stack); Locals=(Value[])source.Locals.Clone(); Arguments=(Value[])source.Arguments.Clone(); }
            internal Frame Copy() => new Frame(this);
            internal Value Pop()
            {
                if (Stack.Count == 0) throw Unsupported("Invalid evaluation stack.");
                var value=Stack[Stack.Count-1]; Stack.RemoveAt(Stack.Count-1); return value;
            }
            internal bool Merge(Frame incoming)
            {
                if (Stack.Count != incoming.Stack.Count) throw Unsupported("Ambiguous evaluation stack at control-flow join.");
                bool changed=false;
                for (int i=0;i<Arguments.Length;i++) { var next=Value.Merge(Arguments[i],incoming.Arguments[i]); changed |= !ReferenceEquals(next,Arguments[i]); Arguments[i]=next; }
                for (int i=0;i<Locals.Length;i++) { var next=Value.Merge(Locals[i],incoming.Locals[i]); changed |= !ReferenceEquals(next,Locals[i]); Locals[i]=next; }
                for (int i=0;i<Stack.Count;i++) { var next=Value.Merge(Stack[i],incoming.Stack[i]); changed |= !ReferenceEquals(next,Stack[i]); Stack[i]=next; }
                return changed;
            }
        }
        private sealed class Site
        {
            internal readonly Instruction Instruction;
            internal readonly bool Swapped;
            internal readonly HashSet<object> Resource;
            internal Site(Instruction instruction, bool swapped, Value fear) { Instruction=instruction; Swapped=swapped; Resource=fear.Origins; }
        }
        private sealed class Contract
        {
            internal readonly Dictionary<Instruction,Site> Sites = new Dictionary<Instruction,Site>();
            internal readonly HashSet<object> UsedGetters = new HashSet<object>();
            internal readonly HashSet<object> StoredProducts = new HashSet<object>();
        }
        private static InvalidOperationException Unsupported(string reason) => new InvalidOperationException("Unsupported fear popularity data flow: " + reason);
        internal static bool IsCoefficientGetter(MethodReference method) => method.Name == "get_GoodBadThingPopModifierMultiplier" &&
            method.DeclaringType.FullName == "SHCDESE.GameGlobals.GameGlobalsManager";
        internal static bool IsContributionField(FieldReference field) => field.Name == "r_GoodBadThingsPopularityModifier" &&
            field.DeclaringType.FullName == "SHCDESE.Interop.GamePlayerResources";
        private static bool IsFearField(FieldReference field) => field.Name == "r_GoodBadThingBoost" &&
            field.DeclaringType.FullName == "SHCDESE.Interop.GamePlayerResources";
        private static bool IsManagedInt(TypeReference type) => type is GenericInstanceType generic &&
            generic.ElementType.FullName == "RedBird.Core.Memory.Managed.ManagedValue`1" &&
            generic.GenericArguments.Count == 1 && generic.GenericArguments[0].MetadataType == MetadataType.Int32;
        private static void RejectCoefficient(Value value)
        {
            if ((value.Kind & (Kind.Handle | Kind.Coefficient)) != 0) throw Unsupported("Coefficient escapes the validated multiplication.");
        }
        private static void RejectReferenceEscape(Value value, bool allowVariables = false)
        {
            Kind addresses=Kind.FearAddress | Kind.ResultAddress | Kind.LocalAddress | Kind.ArgumentAddress;
            if ((value.Kind & addresses) != 0 && !(allowVariables && (value.Kind == Kind.LocalAddress || value.Kind == Kind.ArgumentAddress)))
                throw Unsupported("Reference escapes its validated load/store or ref/out assignment.");
        }
        private static void AssignAddress(MethodDefinition method, Frame frame, Value pointer, Value value)
        {
            foreach (var origin in pointer.Origins)
            {
                if (pointer.Kind == Kind.LocalAddress && origin is VariableDefinition local) frame.Locals[local.Index]=value;
                else if (pointer.Kind == Kind.ArgumentAddress && origin is ParameterDefinition argument) frame.Arguments[argument.Index+(method.HasThis?1:0)]=value;
            }
        }
        private static int Local(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_0: case Code.Stloc_0: return 0;
                case Code.Ldloc_1: case Code.Stloc_1: return 1;
                case Code.Ldloc_2: case Code.Stloc_2: return 2;
                case Code.Ldloc_3: case Code.Stloc_3: return 3;
                default: return ((VariableDefinition)instruction.Operand).Index;
            }
        }
        private static int StackCount(StackBehaviour behaviour)
        {
            if (behaviour == StackBehaviour.Pop0 || behaviour == StackBehaviour.Push0) return 0;
            if (behaviour == StackBehaviour.Varpop || behaviour == StackBehaviour.Varpush || behaviour.ToString() == "PopAll")
                throw Unsupported("Unsupported variable-stack instruction.");
            return behaviour.ToString().Split('_').Length;
        }
        private static void Store(Value pointer, Value value, Contract contract)
        {
            if ((value.Kind & ~(Kind.Product | Kind.Zero)) != 0) throw Unsupported("Native fear contribution no longer receives the direct product or zero.");
            if (contract == null) return;
            foreach (var origin in value.Origins)
            {
                if (!(origin is Instruction instruction) || !contract.Sites.TryGetValue(instruction,out var site))
                    throw Unsupported("Contribution source is unresolved.");
                if (pointer.Origins.Count == 0 || !pointer.Origins.SetEquals(site.Resource))
                    throw Unsupported("Fear input and contribution target do not identify the same resource pointer.");
                contract.StoredProducts.Add(instruction);
            }
        }
        private static void Step(MethodDefinition method, Instruction instruction, Frame frame, Contract contract)
        {
            var op=instruction.OpCode.Code;
            if (op == Code.Nop || op == Code.Break) return;
            if (instruction.OpCode.Name.StartsWith("ldloc") && op != Code.Ldloca && op != Code.Ldloca_S)
            { frame.Stack.Add(frame.Locals[Local(instruction)]); return; }
            if (instruction.OpCode.Name.StartsWith("stloc")) { frame.Locals[Local(instruction)]=frame.Pop(); return; }
            if (op == Code.Ldloca || op == Code.Ldloca_S)
            {
                if (contract != null && (frame.Locals[Local(instruction)].Kind & (Kind.Coefficient | Kind.Handle | Kind.Fear | Kind.Product)) != 0)
                    throw Unsupported("Tracked local is exposed by reference.");
                frame.Stack.Add(new Value(Kind.LocalAddress,instruction.Operand)); return;
            }
            if (instruction.OpCode.Name.StartsWith("ldarg") && op != Code.Ldarga && op != Code.Ldarga_S)
            {
                int index=op == Code.Ldarg_0 ? 0 : op == Code.Ldarg_1 ? 1 : op == Code.Ldarg_2 ? 2 : op == Code.Ldarg_3 ? 3 :
                    ((ParameterDefinition)instruction.Operand).Index + (method.HasThis ? 1 : 0);
                frame.Stack.Add(frame.Arguments[index]); return;
            }
            if (op == Code.Starg || op == Code.Starg_S)
            { frame.Arguments[((ParameterDefinition)instruction.Operand).Index+(method.HasThis?1:0)]=frame.Pop(); return; }
            if (op == Code.Ldarga || op == Code.Ldarga_S)
            { frame.Stack.Add(new Value(Kind.ArgumentAddress,instruction.Operand)); return; }
            if (op == Code.Dup) { var value=frame.Pop(); frame.Stack.Add(value); frame.Stack.Add(value); return; }
            if (op == Code.Ldc_I4_0 || ((op == Code.Ldc_I4 || op == Code.Ldc_I4_S) && Convert.ToInt32(instruction.Operand) == 0))
            { frame.Stack.Add(Value.Zero); return; }
            if (op == Code.Conv_I4 || op == Code.Conv_U4)
            {
                var value=frame.Pop();
                if (contract != null) { RejectCoefficient(value); RejectReferenceEscape(value); }
                frame.Stack.Add(value); return;
            }
            if (op == Code.Ldfld || op == Code.Ldflda)
            {
                var pointer=frame.Pop(); var field=(FieldReference)instruction.Operand;
                if (contract != null) RejectCoefficient(pointer);
                if (IsFearField(field))
                {
                    if (field.FieldType.MetadataType != MetadataType.Int32) throw Unsupported("Fear level must be a signed Int32.");
                    frame.Stack.Add(new Value(op == Code.Ldfld ? Kind.Fear : Kind.FearAddress,pointer.Origins));
                }
                else if (IsContributionField(field) && op == Code.Ldflda)
                {
                    if (field.FieldType.MetadataType != MetadataType.UInt32 && field.FieldType.MetadataType != MetadataType.Int32)
                        throw Unsupported("Contribution storage must retain its 32-bit representation.");
                    frame.Stack.Add(new Value(Kind.ResultAddress,pointer.Origins));
                }
                else frame.Stack.Add(new Value(Kind.Other,instruction));
                return;
            }
            if (op == Code.Ldind_I4)
            {
                var pointer=frame.Pop(); if (contract != null) RejectCoefficient(pointer);
                frame.Stack.Add(pointer.Kind == Kind.FearAddress ? new Value(Kind.Fear,pointer.Origins) : Value.Other); return;
            }
            if (op == Code.Stfld)
            {
                var value=frame.Pop(); var pointer=frame.Pop(); var field=(FieldReference)instruction.Operand;
                if (contract != null) { RejectCoefficient(value); RejectCoefficient(pointer); RejectReferenceEscape(value); }
                if (IsContributionField(field))
                {
                    if (field.FieldType.MetadataType != MetadataType.UInt32 && field.FieldType.MetadataType != MetadataType.Int32)
                        throw Unsupported("Contribution storage must retain its 32-bit representation.");
                    if (contract != null) Store(pointer,value,contract);
                }
                return;
            }
            if (op == Code.Stind_I4 || op == Code.Stind_I || op == Code.Stind_Ref || op == Code.Stobj)
            {
                var value=frame.Pop(); var pointer=frame.Pop();
                if (contract != null)
                {
                    RejectCoefficient(value); RejectCoefficient(pointer); RejectReferenceEscape(value);
                    if (pointer.Kind == Kind.ResultAddress)
                    {
                        if (op != Code.Stind_I4) throw Unsupported("Contribution address write changed width.");
                        Store(pointer,value,contract);
                    }
                    else RejectReferenceEscape(pointer,true);
                }
                AssignAddress(method,frame,pointer,value); return;
            }
            if (op == Code.Ldind_I || op == Code.Ldind_Ref || op == Code.Ldobj)
            {
                var pointer=frame.Pop();
                if (contract != null) { RejectCoefficient(pointer); RejectReferenceEscape(pointer,true); }
                object origin=pointer.Origins.Count == 1 ? pointer.Origins.First() : null;
                var local=origin as VariableDefinition;
                var argument=origin as ParameterDefinition;
                frame.Stack.Add(pointer.Kind == Kind.LocalAddress && local != null ? frame.Locals[local.Index] :
                    pointer.Kind == Kind.ArgumentAddress && argument != null ? frame.Arguments[argument.Index+(method.HasThis?1:0)] : new Value(Kind.Other,instruction));
                return;
            }
            if (op == Code.Mul)
            {
                var right=frame.Pop(); var left=frame.Pop();
                bool normal=left.Kind == Kind.Coefficient && right.Kind == Kind.Fear;
                bool swapped=right.Kind == Kind.Coefficient && left.Kind == Kind.Fear;
                if (normal || swapped)
                {
                    if (contract != null)
                    {
                        var coefficient=normal?left:right; var fear=normal?right:left;
                        contract.Sites[instruction]=new Site(instruction,swapped,fear);
                        contract.UsedGetters.UnionWith(coefficient.Origins);
                    }
                    frame.Stack.Add(new Value(Kind.Product,instruction));
                }
                else { if (contract != null) { RejectCoefficient(left); RejectCoefficient(right); } frame.Stack.Add(Value.Other); }
                return;
            }
            if (op == Code.Call || op == Code.Callvirt || op == Code.Newobj)
            {
                var call=(MethodReference)instruction.Operand;
                var arguments=new List<Value>();
                for (int i=0;i<call.Parameters.Count;i++) arguments.Add(frame.Pop());
                Value receiver=call.HasThis && op != Code.Newobj ? frame.Pop() : Value.Other;
                if (IsCoefficientGetter(call))
                {
                    if (!call.HasThis || call.Parameters.Count != 0 || !IsManagedInt(call.ReturnType)) throw Unsupported("Coefficient getter signature changed.");
                    frame.Stack.Add(new Value(Kind.Handle,instruction)); return;
                }
                if (call.Name == "GetValue" && call.HasThis && call.Parameters.Count == 0 && IsManagedInt(call.DeclaringType) &&
                    (call.ReturnType.MetadataType == MetadataType.Int32 || call.ReturnType is GenericParameter typeParameter && typeParameter.Type == GenericParameterType.Type && typeParameter.Position == 0) && receiver.Kind == Kind.Handle)
                { frame.Stack.Add(new Value(Kind.Coefficient,receiver.Origins)); return; }
                if (contract != null)
                {
                    RejectCoefficient(receiver); RejectReferenceEscape(receiver,true);
                    foreach (var argument in arguments) { RejectCoefficient(argument); RejectReferenceEscape(argument,true); }
                }
                // out/ref calls establish or replace the local's pointer identity.
                // A later ref mutation must not be mistaken for the resource read earlier.
                foreach (var address in arguments.Concat(new[] {receiver}))
                    if (address.Kind == Kind.LocalAddress)
                        foreach (var origin in address.Origins)
                            if (origin is VariableDefinition local) frame.Locals[local.Index]=new Value(Kind.Other,instruction);
                foreach (var address in arguments.Concat(new[] {receiver}))
                    if (address.Kind == Kind.ArgumentAddress)
                        foreach (var origin in address.Origins)
                            if (origin is ParameterDefinition parameter) frame.Arguments[parameter.Index+(method.HasThis?1:0)]=new Value(Kind.Other,instruction);
                if (op == Code.Newobj || call.ReturnType.MetadataType != MetadataType.Void) frame.Stack.Add(new Value(Kind.Other,instruction));
                return;
            }
            if (op == Code.Ret)
            { if (method.ReturnType.MetadataType != MetadataType.Void && contract != null) RejectCoefficient(frame.Pop()); return; }
            int pops=StackCount(instruction.OpCode.StackBehaviourPop);
            for (int i=0;i<pops;i++) { var value=frame.Pop(); if (contract != null) { RejectCoefficient(value); RejectReferenceEscape(value); } }
            int pushes=StackCount(instruction.OpCode.StackBehaviourPush);
            for (int i=0;i<pushes;i++) frame.Stack.Add(new Value(Kind.Other,instruction));
        }
        private static Dictionary<Instruction,Frame> Flow(MethodDefinition method)
        {
            // Exception edges cannot be inferred from ordinary branches. Reject this
            // currently unsupported flow rather than skipping potential coefficient paths.
            if (method.Body.HasExceptionHandlers) throw Unsupported("Exception-region flow requires a supported analysis.");
            var body = new HashSet<Instruction>(method.Body.Instructions);
            if (body.Count == 0) throw Unsupported("Empty method body.");
            var states=new Dictionary<Instruction,Frame>(); var pending=new Queue<Instruction>();
            var first=method.Body.Instructions[0]; states.Add(first,new Frame(method)); pending.Enqueue(first);
            while (pending.Count != 0)
            {
                var instruction=pending.Dequeue(); var frame=states[instruction].Copy(); Step(method,instruction,frame,null);
                var successors=new List<Instruction>();
                // ILContext.Invoke (and ILHook) exposes labels, while an uninvoked
                // Cecil body exposes Instructions. Resolve without rewriting either.
                if (instruction.OpCode.FlowControl == FlowControl.Branch || instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instruction.Operand is Instruction target) successors.Add(target);
                    else if (instruction.Operand is ILLabel label) successors.Add(label.Target);
                    else if (instruction.Operand is Instruction[] targets) successors.AddRange(targets);
                    else if (instruction.Operand is ILLabel[] labels) successors.AddRange(labels.Select(item => item?.Target));
                    else throw Unsupported("Unsupported branch target representation.");
                    if (successors.Any(next => next == null || !body.Contains(next)))
                        throw Unsupported("Branch target is missing or outside the method body.");
                }
                if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.OpCode.FlowControl != FlowControl.Return &&
                    instruction.OpCode.FlowControl != FlowControl.Throw && instruction.Next != null) successors.Add(instruction.Next);
                foreach (var next in successors)
                {
                    if (!states.TryGetValue(next,out var existing)) { states.Add(next,frame.Copy()); pending.Enqueue(next); }
                    else if (existing.Merge(frame)) pending.Enqueue(next);
                }
            }
            return states;
        }
        private static Contract Analyze(ILContext context)
        {
            var method=context.Method; var frames=Flow(method); var contract=new Contract();
            // Determine products first, then validate stores and all other consumers
            // against the converged CFG states. Dictionary traversal order is irrelevant.
            foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Mul && frames.ContainsKey(i)))
                Step(method,instruction,frames[instruction].Copy(),contract);
            foreach (var instruction in method.Body.Instructions)
            {
                if (!frames.TryGetValue(instruction,out var frame))
                {
                    if (instruction.Operand is MethodReference call && IsCoefficientGetter(call)) throw Unsupported("Unreachable coefficient access.");
                    continue;
                }
                Step(method,instruction,frame.Copy(),contract);
            }
            var getters=method.Body.Instructions.Where(i => i.Operand is MethodReference call && IsCoefficientGetter(call)).Cast<object>().ToArray();
            if (contract.Sites.Count == 0 || !contract.UsedGetters.SetEquals(getters) || !contract.StoredProducts.SetEquals(contract.Sites.Keys.Cast<object>()))
                throw Unsupported("Every coefficient access and product must reach the validated contribution field.");
            return contract;
        }
        internal static List<Instruction> Sites(ILContext context) => Analyze(context).Sites.Keys.ToList();
        internal static void Apply(ILContext context)
        {
            var contract=Analyze(context); // Validate all paths before either/all edits.
            foreach (var site in contract.Sites.Values)
            {
                var replacement=context.Method.Module.ImportReference(typeof(FearPopularity).GetMethod(site.Swapped ? "MultiplySwapped" : "Multiply",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                site.Instruction.OpCode=OpCodes.Call; site.Instruction.Operand=replacement;
            }
        }
    }
}
