using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.Detours;

namespace APIShared.Networking
{
    // An additive, fail-closed adapter to SE's existing raw Chore submission. The
    // public Ex2 method may fall back to Steam and serialize twice, so it cannot
    // implement the authenticated channel's stronger transport contract.
    internal static class NativeChoreTransport
    {
        private static object engineGate;
        private static FieldInfo pendingPayload;
        private static FieldInfo engineMonitor;
        private static BulkChoreDetours.c_game_queue_chore_delegate nativeQueue;
        private static Func<byte[], bool> submit;
        private static readonly Func<byte[], bool> queueInOriginalIdentity = TryQueueCore;
        internal static bool IsReady => Volatile.Read(ref submit) != null;

        internal static void Initialize(long moduleBase)
        {
            if (IsReady) return;
            MethodInfo method = typeof(GameNetworkAPI).GetMethod("SendScriptExtenderChorePayload",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]) }, null);
            FieldInfo field = typeof(GameNetworkAPI).GetField("ChorePendingSendPayload", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo monitor = typeof(EngineInterface).GetField("threadLock", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null || method.ReturnType != typeof(bool) || field?.FieldType != typeof(byte[]) || monitor?.FieldType != typeof(object))
                throw new InvalidOperationException("Authenticated Chore submission: installed SE/engine adapter contract changed.");
            ValidateSubmissionMethod(method);
            object gate = monitor.GetValue(null);
            var queue = BulkChoreDetours.c_game_queue_chore;
            if (gate == null || queue == null || Marshal.GetFunctionPointerForDelegate(queue).ToInt64() != checked(moduleBase + 0x23990))
                throw new InvalidOperationException("Authenticated Chore submission: native queue binding differs from the audited image.");
            var call = (Func<byte[], bool>)Delegate.CreateDelegate(typeof(Func<byte[], bool>), method);
            engineGate = gate; pendingPayload = field; engineMonitor = monitor; nativeQueue = queue;
            Volatile.Write(ref submit, call);
        }

        // Validate the loaded implementation, not its assembly version or a publicized
        // compile reference. No unknown helper, indirect transport or hook is accepted.
        internal static void ValidateSubmissionMethod(MethodInfo method)
        {
            using (var definition = new DynamicMethodDefinition(method))
                ValidateSubmissionBody(definition.Definition);
        }

        internal static void ValidateSubmissionBody(MethodDefinition method)
        {
            int queueCalls = 0, enter = 0, exit = 0;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.FlowControl == FlowControl.Call && instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    throw new InvalidOperationException("Indirect Chore transport changed.");
                if (instruction.OpCode == OpCodes.Stsfld && !IsField(instruction.Operand,
                    "SHCDESE.API.GameNetworkAPI", "ChorePendingSendPayload", "System.Byte[]"))
                    throw new InvalidOperationException("Unexpected global mutation in raw Chore submission.");
                string operation = instruction.OpCode.Name;
                if (instruction.OpCode == OpCodes.Stfld || operation.StartsWith("stind", StringComparison.Ordinal) ||
                    operation.StartsWith("stelem", StringComparison.Ordinal) || instruction.OpCode == OpCodes.Stobj ||
                    instruction.OpCode == OpCodes.Cpblk || instruction.OpCode == OpCodes.Initblk || instruction.OpCode == OpCodes.Initobj)
                    throw new InvalidOperationException("Unexpected memory mutation in raw Chore submission.");
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Calli) continue;
                if (!(instruction.Operand is MethodReference called)) throw new InvalidOperationException("Indirect Chore transport changed.");
                string type = called.DeclaringType.FullName, name = called.Name;
                if (type == typeof(BulkChoreDetours.c_game_queue_chore_delegate).FullName.Replace('+', '/') && name == "Invoke")
                {
                    ValidateQueueCall(instruction);
                    queueCalls++; continue;
                }
                if (type == "System.Threading.Monitor" && name == "Enter") { enter++; continue; }
                if (type == "System.Threading.Monitor" && name == "Exit") { exit++; continue; }
                if (type == "SHCDESE.GameGlobals.GameGlobalsManager" && name == "get_Instance" ||
                    type == "SHCDESE.Logging.LogHelper" && name == "Warning" ||
                    type == "System.String" && (name == "Format" || name == "Concat") ||
                    type == "System.IntPtr" && name == "op_Explicit") continue;
                throw new InvalidOperationException("Unaudited helper in raw Chore submission: " + called.FullName);
            }
            if (queueCalls != 1 || enter != 1 || exit != 1)
                throw new InvalidOperationException("Raw Chore submission must retain one native queue call and its engine monitor.");
            ValidateMonitorFlow(method);
        }

        private static void ValidateMonitorFlow(MethodDefinition method)
        {
            Instruction enter = null, exit = null, queue = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (!(instruction.Operand is MethodReference called)) continue;
                if (called.DeclaringType.FullName == "System.Threading.Monitor")
                {
                    if (called.Name == "Enter" && called.Parameters.Count == 2 &&
                        called.Parameters[0].ParameterType.FullName == "System.Object" &&
                        called.Parameters[1].ParameterType.FullName == "System.Boolean&") enter = instruction;
                    if (called.Name == "Exit" && called.Parameters.Count == 1 &&
                        called.Parameters[0].ParameterType.FullName == "System.Object") exit = instruction;
                }
                else if (called.DeclaringType.FullName == typeof(BulkChoreDetours.c_game_queue_chore_delegate).FullName.Replace('+', '/') &&
                    called.Name == "Invoke") queue = instruction;
            }
            Instruction flagAddress = Previous(enter), monitorLoad = Previous(flagAddress);
            int flag = Local(flagAddress, true), monitor = Local(monitorLoad, false);
            if (enter == null || exit == null || queue == null || monitor < 0 || flag < 0 || flag == monitor ||
                monitor >= method.Body.Variables.Count || flag >= method.Body.Variables.Count ||
                method.Body.Variables[monitor].VariableType.FullName != "System.Object" ||
                method.Body.Variables[flag].VariableType.FullName != "System.Boolean" ||
                Local(Previous(exit), false) != monitor || method.Body.ExceptionHandlers.Count != 1)
                throw new InvalidOperationException("Raw Chore monitor argument flow changed.");
            int objectStores = 0, flagStores = 0;
            foreach (var instruction in method.Body.Instructions)
            {
                int written = StoredLocal(instruction);
                if (written == monitor)
                {
                    objectStores++;
                    var source = Previous(instruction);
                    if (source?.OpCode != OpCodes.Ldsfld || !IsField(source.Operand, "EngineInterface", "threadLock", "System.Object"))
                        throw new InvalidOperationException("Raw Chore lock must use the actual engine monitor.");
                }
                if (written == flag)
                {
                    flagStores++;
                    if (Previous(instruction)?.OpCode != OpCodes.Ldc_I4_0)
                        throw new InvalidOperationException("Raw Chore lock flag initialization changed.");
                }
                // Only Monitor.Enter may receive either local by reference. A
                // helper must not replace the locked object or its taken flag.
                int addressed = Local(instruction, true);
                if (addressed == monitor || addressed == flag && !ReferenceEquals(instruction, flagAddress))
                    throw new InvalidOperationException("Raw Chore monitor locals escape their scope.");
            }
            var handler = method.Body.ExceptionHandlers[0];
            var instructions = method.Body.Instructions;
            int enterIndex = instructions.IndexOf(enter), queueIndex = instructions.IndexOf(queue), exitIndex = instructions.IndexOf(exit);
            if (objectStores != 1 || flagStores != 1 || handler.HandlerType != ExceptionHandlerType.Finally ||
                handler.TryStart == null || handler.TryEnd == null || handler.HandlerStart == null || handler.HandlerEnd == null ||
                instructions.IndexOf(handler.TryStart) > enterIndex || queueIndex <= enterIndex ||
                queueIndex >= instructions.IndexOf(handler.TryEnd) || exitIndex < instructions.IndexOf(handler.HandlerStart) ||
                exitIndex >= instructions.IndexOf(handler.HandlerEnd))
                throw new InvalidOperationException("Raw Chore queue and monitor release must share their protected finally scope.");
        }

        private static Instruction Previous(Instruction instruction)
        {
            var prior = instruction?.Previous;
            while (prior != null && prior.OpCode == OpCodes.Nop) prior = prior.Previous;
            return prior;
        }
        private static int Local(Instruction instruction, bool address)
        {
            if (instruction == null) return -1;
            if (address) return instruction.OpCode == OpCodes.Ldloca || instruction.OpCode == OpCodes.Ldloca_S
                ? (instruction.Operand as VariableDefinition)?.Index ?? -1 : -1;
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_0: return 0;
                case Code.Ldloc_1: return 1;
                case Code.Ldloc_2: return 2;
                case Code.Ldloc_3: return 3;
                case Code.Ldloc: case Code.Ldloc_S: return (instruction.Operand as VariableDefinition)?.Index ?? -1;
                default: return -1;
            }
        }
        private static int StoredLocal(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Stloc_0: return 0;
                case Code.Stloc_1: return 1;
                case Code.Stloc_2: return 2;
                case Code.Stloc_3: return 3;
                case Code.Stloc: case Code.Stloc_S: return (instruction.Operand as VariableDefinition)?.Index ?? -1;
                default: return -1;
            }
        }

        private static bool IsField(object operand, string type, string name, string fieldType) =>
            operand is FieldReference field && field.DeclaringType.FullName == type && field.Name == name && field.FieldType.FullName == fieldType;

        private static void ValidateQueueCall(Instruction call)
        {
            var opcode = call.Previous;
            var pointer = opcode?.Previous;
            var manager = pointer?.Previous;
            var singleton = manager?.Previous;
            var queue = singleton?.Previous;
            var staged = queue?.Previous;
            // This is an argument-flow check, not merely a function-name match:
            // the only queued opcode is 106 and the manager comes from SE's resolver.
            bool is106 = opcode != null && (opcode.OpCode == OpCodes.Ldc_I4 || opcode.OpCode == OpCodes.Ldc_I4_S) &&
                Convert.ToInt32(opcode.Operand) == 106;
            if (!is106 || pointer?.OpCode != OpCodes.Call || !(pointer.Operand is MethodReference conversion) ||
                conversion.DeclaringType.FullName != "System.IntPtr" || conversion.Name != "op_Explicit" ||
                manager?.OpCode != OpCodes.Ldfld || !IsField(manager.Operand, "SHCDESE.GameGlobals.GameGlobalsManager", "ChoreManagerVA", "System.UInt64") ||
                singleton?.OpCode != OpCodes.Call || !(singleton.Operand is MethodReference get) ||
                get.DeclaringType.FullName != "SHCDESE.GameGlobals.GameGlobalsManager" || get.Name != "get_Instance" ||
                queue?.OpCode != OpCodes.Ldsfld || !IsField(queue.Operand, "SHCDESE.Detours.BulkChoreDetours", "c_game_queue_chore",
                    typeof(BulkChoreDetours.c_game_queue_chore_delegate).FullName.Replace('+', '/')) ||
                staged?.OpCode != OpCodes.Stsfld || !IsField(staged.Operand, "SHCDESE.API.GameNetworkAPI", "ChorePendingSendPayload", "System.Byte[]") ||
                staged.Previous?.OpCode != OpCodes.Ldarg_0)
                throw new InvalidOperationException("Raw Chore queue opcode/manager/staging argument flow changed.");
        }

        internal static bool TryQueue(byte[] blob)
            => EliminatedPlayerViewIdentity.RunSubmission(blob, queueInOriginalIdentity);

        private static bool TryQueueCore(byte[] blob)
        {
            var call = Volatile.Read(ref submit);
            if (call == null || !NativeChoreContext.IsReady || !Internal.UnityMainThreadDispatch.IsMainThread ||
                blob == null || blob.Length < 2 || blob.Length > ChoreTransport.MaximumPayloadBytes) return false;
            lock (engineGate)
            {
                // Both SE bindings are mutable fields. A later replacement must
                // fail this send rather than silently changing transport or locking
                // a different engine object than the one validated at startup.
                if (!ReferenceEquals(engineMonitor.GetValue(null), engineGate) ||
                    !ReferenceEquals(BulkChoreDetours.c_game_queue_chore, nativeQueue) ||
                    !NativeChoreContext.TryGetManager(out long manager) ||
                    !NativeChoreContext.HasFreeQueueSlot(manager)) return false;
                // SE queues under the current native view. Never send as a foreign
                // owner while a view lease is active; real identity is kept separate.
                int actual = PlayerPerspectiveAPI.GetControlledPlayerId();
                if (actual < 1 || actual > 8 || PlayerPerspectiveAPI.GetRawNativeViewPlayerId() != actual ||
                    pendingPayload.GetValue(null) != null) return false;
                try { return call(blob); }
                finally
                {
                    // SE clears the staging field only after the void queue call. An
                    // exception must not leave the next submission using stale bytes.
                    pendingPayload.SetValue(null, null);
                }
            }
        }
    }
}
