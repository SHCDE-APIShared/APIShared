using APIShared.Internal;
using CrusaderDE;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Linq;
using System.Reflection;

namespace APIShared.Presentation
{
    // Patches the exact producer, before encode and native pre-init, not the later HUD assignment.
    // The same RestartMPInfo object consequently supplies both persistent and immediate presentation.
    internal static class CoopLordRestartPresentation
    {
        private static ILHook hook;
        internal static void Install()
        {
            if (hook != null) return;
            MethodInfo target = typeof(Platform_Multiplayer).GetMethod("StartGame", new[] {
                typeof(EngineInterface.MultiplayerSetupData), typeof(FileHeader), typeof(int), typeof(int) });
            ILHook candidate = null;
            try
            {
                candidate = new ILHook(target, Patch, new ILHookConfig { ManualApply = true, ID = "APIShared.CoopLordRestartPresentation" });
                candidate.Apply(); hook = candidate;
            }
            catch { candidate?.Dispose(); throw; }
        }
        internal static void Patch(ILContext il)
        {
            var calls = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
                .Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(HUD_IngameMenu.RestartMPInfo).FullName.Replace('+', '/') && m.Name == "encode" && m.Parameters.Count == 0).ToArray();
            int explicitThis = il.Method.HasThis ? 0 : 1;
            if (calls.Length != 1 || il.Method.Parameters.Count != 4 + explicitThis || il.Method.Parameters[2 + explicitThis].ParameterType.FullName != "System.Int32")
                throw new InvalidOperationException("Coop restart encoder differs from the audited StartGame contract: encoders=" +
                    calls.Length + ", parameters=" + il.Method.Parameters.Count + ", instance=" + il.Method.HasThis);
            var cursor = new ILCursor(il); cursor.Goto(calls[0]);
            cursor.Emit(OpCodes.Dup); cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Ldarg_3);
            cursor.EmitDelegate<Action<HUD_IngameMenu.RestartMPInfo, Platform_Multiplayer, int>>(Populate);
        }
        internal static void Populate(HUD_IngameMenu.RestartMPInfo restart, Platform_Multiplayer platform, int coop)
        {
            if (coop <= 0 || !PackagedLordPresentation.HasEntries || restart == null || platform?.activeLobby == null) return;
            var lobby = platform.activeLobby;
            foreach (var member in lobby.members)
            {
                if (member == null || !member.SkirmishMember || member.SkirmishHumanMember) continue;
                int id = lobby.getThisPlayerFromSteamID(member.id.m_SteamID);
                if (id < 2 || id > 8) continue;
                string data = id == 2 ? lobby.AIVDataPlayer2 : id == 3 ? lobby.AIVDataPlayer3 : id == 4 ? lobby.AIVDataPlayer4 :
                    id == 5 ? lobby.AIVDataPlayer5 : id == 6 ? lobby.AIVDataPlayer6 : id == 7 ? lobby.AIVDataPlayer7 : lobby.AIVDataPlayer8;
                if (string.IsNullOrWhiteSpace(data)) continue;
                var info = new FRONT_Multiplayer.MPAIVInfo(); info.decode(data);
                string identity = PackagedLordPresentation.RestartIdentity(id);
                if (identity == null) continue;
                // The authenticated owner may use a verified peer-local media alias. The lobby
                // carries the host's alias; the restart must resolve this peer's immutable entry.
                ApplySelection(restart, id, member.GetLordType(), info, identity);
            }
        }
        internal static void ApplySelection(HUD_IngameMenu.RestartMPInfo restart, int id, int type, FRONT_Multiplayer.MPAIVInfo info, string identity = null)
        {
            if (id < 1 || id > 8 || restart?.LordNames?.Length != 8 ||
                info == null || info.lordType != type || info.builtInLord || string.IsNullOrWhiteSpace(info.lordName)) return;
            restart.LordNames[id - 1] = identity ?? info.lordName;
            restart.SetImage(info.imageData, id - 1);
        }
    }
}
