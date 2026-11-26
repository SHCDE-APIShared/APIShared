using System;
using System.IO;
using System.Linq;
using System.Reflection;
using APIShared.Patching;
using APIShared.Steam;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.DependencyBootstrap
{
    internal sealed class SteamDependencyRegistration
    {
        private const string Owner = "APIShared.DependencyBootstrap.SteamAppDependencies";
        private const string ManagerName = "SHCDESE.API.Components.ModManager.GameAssetModManager";
        private const string ProviderName = "SHCDESE.API.GameAssetManagerAPI";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static SteamDependencyRegistration installed;
        private SafeIlHook hook;
        private readonly PropertyInfo sourceProperty, infoProperty, guidProperty, manifestProperty, networkProperty;
        private readonly MethodInfo openRead;
        private readonly int startupThread = Environment.CurrentManagedThreadId;
        internal MethodInfo Target { get; }

        private SteamDependencyRegistration(Assembly extender)
        {
            var manager = extender.GetType(ManagerName, true);
            var candidate = manager.GetNestedType("AssetModCandidate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(ManagerName, "AssetModCandidate");
            Target = manager.GetMethod("RegisterAssetModCore", Instance, null, new[] { candidate }, null);
            if (Target == null || Target.ReturnType != typeof(void) || Target.GetMethodBody() == null)
                throw new InvalidOperationException("Extender asset registration signature changed.");

            sourceProperty = candidate.GetProperty("Source", Instance);
            infoProperty = candidate.GetProperty("Info", Instance);
            var sourceType = extender.GetType("SHCDESE.API.Components.Assets.IModResourceSource", true);
            var infoType = extender.GetType("SHCDESE.API.Components.ModManager.ModInfo", true);
            if (sourceProperty?.PropertyType != sourceType || infoProperty?.PropertyType != infoType ||
                !typeof(IDisposable).IsAssignableFrom(sourceType))
                throw new InvalidOperationException("Extender asset candidate contract changed.");
            openRead = sourceType.GetMethod("OpenRead", new[] { typeof(string) });
            guidProperty = infoType.GetProperty("GUID", Instance);
            manifestProperty = infoType.GetProperty("Manifest", Instance);
            networkProperty = infoType.GetProperty("NetworkMode", Instance);
            if (openRead?.ReturnType != typeof(Stream) || guidProperty?.PropertyType != typeof(string) ||
                !HasZeroMember(manifestProperty, "Asset") || !HasZeroMember(networkProperty, "Clientside"))
                throw new InvalidOperationException("Extender asset metadata contract changed.");

            RequireSingleCall(manager.GetMethod("RegisterAll", Type.EmptyTypes), ManagerName, Target.Name);
            RequireSingleCall(manager.GetMethod("RegisterAssetMod", new[] { typeof(string) }), ManagerName, Target.Name);
            RequireSingleCall(Target, ProviderName, "RegisterModProvider");
        }

        private static bool HasZeroMember(PropertyInfo property, string name) =>
            property != null && property.PropertyType.IsEnum && Enum.IsDefined(property.PropertyType, name) &&
            Convert.ToInt64(Enum.Parse(property.PropertyType, name)) == 0;

        private static void RequireSingleCall(MethodInfo method, string type, string name)
        {
            if (method == null) throw new InvalidOperationException("Missing Extender asset registration entry point.");
            using (var copy = new DynamicMethodDefinition(method))
            {
                int calls = copy.Definition.Body.Instructions.Count(i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name);
                if (calls != 1) throw new InvalidOperationException("Extender asset registration call path changed in " + method.Name + ".");
            }
        }

        internal static SteamDependencyRegistration Validate(Assembly extender) =>
            new SteamDependencyRegistration(extender);

        internal static void TryInstall(Assembly extender)
        {
            try
            {
                if (installed != null) return;
                var gate = Validate(extender);
                if (!SafeIlHook.TryInstall(Owner, Owner, gate.Target, AddRegistrationGate, null, out gate.hook, out string reason))
                    throw new InvalidOperationException(reason);
                installed = gate;
            }
            catch (Exception ex)
            {
                ProbeTrace.Error(SteamDependencyManifest.Property + " inactive; the Extender can still load these packages. " + ex.GetBaseException().Message);
            }
        }

        internal static void AddRegistrationGate(ILContext il)
        {
            var first = il.Body.Instructions[0];
            if (il.Body.ExceptionHandlers.Any(h => h.TryStart == first || h.HandlerStart == first || h.FilterStart == first))
                throw new InvalidOperationException("Extender asset entry starts with an exception region.");
            var check = typeof(SteamDependencyRegistration).GetMethod(nameof(BeforeRegister), BindingFlags.Static | BindingFlags.NonPublic);
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Call, check);
            cursor.Emit(OpCodes.Brtrue, first);
            cursor.Emit(OpCodes.Ret);
        }

        private static bool BeforeRegister(object candidate)
        {
            var gate = installed;
            return gate == null || gate.Allow(candidate, gate.QueryOnStartupThread, ProbeTrace.Warning);
        }

        private SteamAppAccess QueryOnStartupThread(uint appId) =>
            Environment.CurrentManagedThreadId == startupThread
                ? SteamAppAccessQuery.Get(appId) : SteamAppAccess.SteamUnavailable;

        internal bool Allow(object candidate, Func<uint, SteamAppAccess> query, Action<string> warning)
        {
            string guid = "<unknown>";
            IDisposable source = null;
            string reason;
            try
            {
                source = (IDisposable)sourceProperty.GetValue(candidate);
                object info = infoProperty.GetValue(candidate);
                guid = (string)guidProperty.GetValue(info);
                string json;
                using (var reader = new StreamReader((Stream)openRead.Invoke(source, new object[] { "info.json" })))
                    json = reader.ReadToEnd();
                reason = SteamDependencyManifest.GetBlockReason(json, () =>
                    Convert.ToInt64(manifestProperty.GetValue(info)) == 0 &&
                    Convert.ToInt64(networkProperty.GetValue(info)) == 0 &&
                    !(ShadowRuntime.Winners?.Any(p => string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase)) ?? false), query);
                if (reason == null) return true;
            }
            catch (Exception ex) { reason = ex.GetBaseException().Message; }

            try { source?.Dispose(); }
            catch (Exception ex) { reason += " Resource cleanup failed: " + ex.GetBaseException().Message; }
            warning(SteamDependencyManifest.Property + " skipped [" + guid + "]: " + reason);
            return false;
        }
    }
}
