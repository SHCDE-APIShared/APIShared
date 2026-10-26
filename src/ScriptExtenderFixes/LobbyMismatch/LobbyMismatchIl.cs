using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Linq;
using System.Reflection;

namespace APIShared.ScriptExtenderFixes
{
    internal static class LobbyMismatchIl
    {
        internal delegate void Popup(string title, Action accept, bool multiplayer, bool sands,
            Steamworks.CSteamID lobby, string reason);

        internal static void ValidateTarget(MethodInfo target)
        {
            if (target == null) throw new MissingMethodException("GameNetworkAPI.RejectLobby");
            var parameters = target.GetParameters();
            if (!target.IsStatic || !target.IsPrivate || target.ContainsGenericParameters ||
                target.ReturnType != typeof(bool) || target.GetMethodBody() == null || parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(Steamworks.CSteamID) || parameters[1].ParameterType != typeof(string))
                throw new InvalidOperationException("Unsupported lobby rejection signature: " + target);
        }

        internal static void ReplacePopup(ILContext il, Popup show)
        {
            var calls = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "CrusaderDE.HUD_ConfirmationPopup" && m.Name == "ShowOK").ToArray();
            if (calls.Length != 1 || !(calls[0].Operand is MethodReference popup) || popup.HasThis ||
                popup.ReturnType.FullName != "System.Void" || popup.Parameters.Count != 4 ||
                popup.Parameters[0].ParameterType.FullName != "System.String" ||
                popup.Parameters[1].ParameterType.FullName != "System.Action" ||
                popup.Parameters[2].ParameterType.FullName != "System.Boolean" ||
                popup.Parameters[3].ParameterType.FullName != "System.Boolean")
                throw new InvalidOperationException("Unsupported lobby rejection popup call.");
            int index = il.Body.Instructions.IndexOf(calls[0]);
            // Refuse a future SE dialog implementation instead of replacing unrelated messages.
            if (!il.Body.Instructions.Take(index).Reverse().Take(12).Any(i =>
                i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "missing mods!")))
                throw new InvalidOperationException("The audited lobby rejection message is absent.");
            var cursor = new ILCursor(il) { Index = index };
            cursor.Remove();
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate(show);
        }
    }
}
