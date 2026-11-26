using System;
using System.Linq;
using System.Reflection;
using APIShared.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Maps
{
    public static partial class MapLoadPolicy
    {
        private static SafeIlHook hook;
        [ThreadStatic] private static string activeName;
        [ThreadStatic] private static string activePath;
        [ThreadStatic] private static bool aliasConsumed;

        internal static bool CanAlias => hook?.IsApplied == true;

        private static bool TryInstallNativeAlias(out string diagnostic)
        {
            diagnostic = "The installed game DLL does not support the audited map filename hook.";
            if (string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
            {
                var target = typeof(EngineInterface).GetMethod("loadMap", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(int), typeof(string), typeof(bool), typeof(bool), typeof(int), typeof(int), typeof(bool) }, null);
                if (target?.ReturnType == typeof(EngineInterface.LoadMapReturnData))
                    return SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.MapLoadPolicy", target, Transform, log, out hook, out diagnostic);
                else diagnostic = "The managed map loader signature differs from the audited version.";
            }
            return false;
        }

        internal static EngineInterface.LoadMapReturnData RunWithName(string path, string name,
            Func<EngineInterface.LoadMapReturnData> load)
        {
            if (name == null) return load();
            string previous = activeName;
            string previousPath = activePath;
            bool previousConsumed = aliasConsumed;
            activeName = name;
            activePath = path;
            aliasConsumed = false;
            try
            {
                var result = load();
                if (aliasConsumed) NativeApiLog.Info(log, "MAP_NATIVE_FILENAME_ALIAS: " + path + " -> " + name);
                return result;
            }
            finally { activeName = previous; activePath = previousPath; aliasConsumed = previousConsumed; }
        }

        internal static void Transform(ILContext il)
        {
            var instructions = il.Body.Instructions;
            var basenameCalls = instructions.Where(i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IO.Path" &&
                m.Name == "GetFileName" && m.ReturnType.FullName == "System.String" && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.String").ToArray();
            if (basenameCalls.Length != 1 || instructions.Count(i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference m && m.DeclaringType.FullName == "EngineInterface" &&
                m.Name == "DLL_LoadMapToPlay" && m.Parameters.Count == 11) != 1)
                throw new InvalidOperationException("The map filename and native call IL contract differs.");
            var cursor = new ILCursor(il);
            cursor.Goto(basenameCalls[0], MoveType.After);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate<Func<string, string, string>>((name, path) =>
            {
                if (activeName == null || aliasConsumed || !string.Equals(activePath, path, StringComparison.Ordinal))
                    return name;
                aliasConsumed = true;
                return activeName;
            });
        }
    }
}
