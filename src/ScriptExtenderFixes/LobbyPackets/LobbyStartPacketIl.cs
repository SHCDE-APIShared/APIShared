using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Steamworks;

namespace APIShared.ScriptExtenderFixes
{
    internal static class LobbyStartPacketIl
    {
        internal static void Stage(ILContext il)
        {
            bool original = il.Method.HasThis && il.Method.Parameters.Count == 1 &&
                il.Method.Parameters[0].ParameterType.FullName == typeof(Platform_Multiplayer).FullName;
            bool normalized = !il.Method.HasThis && il.Method.Parameters.Count == 2 &&
                il.Method.Parameters[0].ParameterType.FullName == "SHCDESE.ManagedHooks.ManagedHookManager" &&
                il.Method.Parameters[1].ParameterType.FullName == typeof(Platform_Multiplayer).FullName;
            if ((!original && !normalized) ||
                il.Method.ReturnType.FullName != "System.Void") throw new InvalidOperationException("SE drain signature differs.");
            var calls = il.Body.Instructions.Where(i => i.Operand is MethodReference m &&
                m.DeclaringType.FullName == typeof(Platform_Multiplayer.MPData).FullName.Replace('+', '/') && m.Name == "FromBytes").ToArray();
            var receivers = il.Body.Instructions.Where(i => i.Operand is MethodReference m &&
                m.Name == "ReceiveMessagesOnChannel" && m.DeclaringType.FullName == typeof(SteamNetworkingMessages).FullName).ToArray();
            var identities = il.Body.Instructions.Where(i => i.Operand is MethodReference m &&
                m.Name == "GetSteamID" && m.DeclaringType.FullName == typeof(SteamNetworkingIdentity).FullName).ToArray();
            if (calls.Length != 1 || receivers.Length != 1 || identities.Length != 1 ||
                !il.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "HandleRawPacket") ||
                !il.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Trampoline"))
                throw new InvalidOperationException("SE lobby drain shape differs; handshake preservation unavailable.");
            if (!calls[0].Next.MatchStloc(out int data) || !identities[0].Next.MatchStloc(out int sender) ||
                il.Body.Variables[data].VariableType.FullName != typeof(Platform_Multiplayer.MPData).FullName.Replace('+','/') ||
                il.Body.Variables[sender].VariableType.FullName != typeof(CSteamID).FullName)
                throw new InvalidOperationException("SE lobby decode locals differ.");
            // All calls are the audited destructive drain, custom dispatch, log, and original
            // trampoline. In particular an upstream low-packet forwarder MUST disable this edit.
            const string manager = "SHCDESE.ManagedHooks.ManagedHookManager";
            string nativeDelegate = manager + "/platform_Multiplayer_ReceiveLobbyMessagesDelegate";
            string[] contract = {
                "Steamworks.SteamNetworkingMessages::ReceiveMessagesOnChannel",
                "System.Runtime.InteropServices.Marshal::PtrToStructure",
                "System.Runtime.InteropServices.Marshal::Copy",
                "Steamworks.SteamNetworkingIdentity::GetSteamID",
                "Steamworks.SteamNetworkingMessage_t::Release",
                "Platform_Multiplayer/MPData::FromBytes",
                "SHCDESE.API.GameNetworkAPI::get_Instance",
                "System.Nullable`1<Steamworks.CSteamID>::.ctor",
                "SHCDESE.API.GameNetworkAPI::HandleRawPacket",
                "SHCDESE.API.GameXAMLManagerAPI::get_Instance",
                "SHCDESE.API.GameXAMLManagerAPI::FlushDeferredPerPlayerUpdates",
                "SHCDESE.Logging.LogHelper::Error",
                "SHCDESE.ManagedHooks.ManagedDetour`1<" + nativeDelegate + ">::get_Trampoline",
                nativeDelegate + "::Invoke"
            };
            int[] argumentCounts = { 3, 1, 4, 0, 1, 1, 0, 1, 3, 0, 0, 4, 0, 1 };
            var actualCalls = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call ||
                i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj).Select(i => i.Operand as MethodReference).ToArray();
            if (actualCalls.Length != contract.Length || actualCalls.Where((m, i) => m == null ||
                m.DeclaringType.FullName + "::" + m.Name != contract[i] || m.Parameters.Count != argumentCounts[i]).Any())
                throw new InvalidOperationException("SE drain call ownership/order/signature differs.");
            var receive = receivers[0];
            if (!receive.Previous.Previous.Previous.Previous.Previous.MatchLdcI4(2) ||
                !receive.Previous.Previous.Previous.Previous.MatchLdloc(out int array) ||
                !receive.Previous.Previous.Previous.MatchLdloc(array) || receive.Previous.Previous.OpCode != OpCodes.Ldlen ||
                receive.Previous.OpCode != OpCodes.Conv_I4)
                throw new InvalidOperationException("SE receiver is not the audited channel 2 drain.");
            var handle = il.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "HandleRawPacket");
            var first = calls[0].Next.Next;
            if (il.Body.Instructions.IndexOf(identities[0]) >= il.Body.Instructions.IndexOf(calls[0]) ||
                !first.MatchLdloc(data) || !(first.Next.Operand is FieldReference packetType) || packetType.Name != "packetType" ||
                !first.Next.Next.MatchLdcI4(1000) ||
                (first.Next.Next.Next.OpCode != OpCodes.Blt && first.Next.Next.Next.OpCode != OpCodes.Blt_S) ||
                Target(first.Next.Next.Next.Operand) != handle.Next.Next || handle.Next.OpCode != OpCodes.Pop ||
                !handle.Next.Next.MatchLdloc(out int index) || !handle.Next.Next.Next.MatchLdcI4(1) ||
                handle.Next.Next.Next.Next.OpCode != OpCodes.Add || !handle.Next.Next.Next.Next.Next.MatchStloc(index))
                throw new InvalidOperationException("SE low packets no longer fall directly into the receive-loop increment.");
            var cursor = new ILCursor(il);
            cursor.Goto(calls[0].Next, MoveType.After);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldloc, il.Body.Variables[data]);
            cursor.Emit(OpCodes.Ldloc, il.Body.Variables[sender]);
            cursor.EmitDelegate<Action<Platform_Multiplayer, Platform_Multiplayer.MPData, CSteamID>>(LobbyStartPacketRuntime.Stage);
        }
        private static Instruction Target(object operand) => operand is ILLabel label ? label.Target : operand as Instruction;
        internal static void Replay(ILContext il)
        {
            bool original = il.Method.HasThis && il.Method.Parameters.Count == 1 &&
                il.Method.Parameters[0].ParameterType.FullName == "System.Boolean";
            bool normalized = !il.Method.HasThis && il.Method.Parameters.Count == 2 &&
                il.Method.Parameters[0].ParameterType.FullName == typeof(Platform_Multiplayer).FullName &&
                il.Method.Parameters[1].ParameterType.FullName == "System.Boolean";
            if (!original && !normalized)
                throw new InvalidOperationException("Vanilla receive signature differs.");
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate<Action<Platform_Multiplayer, bool>>(LobbyStartPacketRuntime.Replay);
        }
        internal static void Exit(ILContext il)
        {
            var cursor = new ILCursor(il); cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<Platform_Multiplayer>>(LobbyStartPacketRuntime.Exit);
        }
    }
}
