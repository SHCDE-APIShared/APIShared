using APIShared.Internal;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API.Components.Archive;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace APIShared.Workshop
{
    // Only the staging decision is extended. The Extender still owns consent,
    // shutdown, deletion and deployment. Installed files are never changed here.
    internal static class WorkshopCleanupRuntime
    {
        private static ILHook hook;
        private static ManualLogSource logger;
        private static bool reported;

        internal static void Initialize(ManualLogSource log)
        {
            if (hook != null) return;
            logger = log;
            try
            {
                MethodInfo method = ValidateExtender(typeof(MapArchive).Assembly);
                hook = new ILHook(method, Modify);
                logger.LogInfo("Workshop cleanup installed; process-lifetime staging callback, no plugin lifecycle scheduling.");
            }
            catch (Exception ex) { logger.LogWarning("Workshop cleanup unavailable: " + ex.Message); }
        }

        // Validate the loaded contract rather than a release number or whole-assembly hash.
        // Everything Initialize checks before publishing the hook: exact signature,
        // managed body, then a dry transformation and compilation of the loaded method.
        // Throws when any check fails; publishes nothing.
        internal static MethodInfo ValidateExtender(Assembly assembly)
        {
            Type type = assembly.GetType("SHCDESE.API.Components.Archive.MapModManager", true);
            MethodInfo method = type.GetMethod("TryStageModUpdate", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(MapArchive) }, null);
            ValidateStagingMethod(method);
            return method;
        }

        internal static void ValidateStagingMethod(MethodInfo method)
        {
            if (method == null) throw new MissingMethodException("MapModManager.TryStageModUpdate");
            var parameters = method.GetParameters();
            if (method.IsStatic || method.IsPublic || method.ContainsGenericParameters ||
                method.ReturnType != typeof(bool) || parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(MapArchive))
                throw new InvalidOperationException("Incompatible Workshop staging method signature: " + method);
            if (method.GetMethodBody() == null)
                throw new InvalidOperationException("Workshop staging method has no managed body: " + method);
            using (var definition = new DynamicMethodDefinition(method))
            {
                using (var context = new ILContext(definition.Definition))
                {
                    Modify(context);
                    GC.KeepAlive(CompileCopy(definition, method));
                }
            }
        }

        internal static Delegate CompileCopy(DynamicMethodDefinition definition, MethodInfo source)
        {
            // CreateDelegate/PrepareDelegate validate the default DMD backend too;
            // ldftn cannot target DynamicMethod on the desktop CLR test runner.
            Type delegateType = typeof(Func<,,,>).MakeGenericType(source.DeclaringType,
                typeof(string), typeof(MapArchive), typeof(bool));
            Delegate compiled = definition.Generate().CreateDelegate(delegateType);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(compiled);
            return compiled;
        }

        internal static void Modify(ILContext il)
        {
            var returns = il.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToArray();
            if (returns.Length == 0) throw new InvalidOperationException("No staging return sites.");
            foreach (var instruction in returns)
            {
                var cursor = new ILCursor(il);
                cursor.Goto(instruction, MoveType.Before);
                cursor.MoveAfterLabels();
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.Emit(OpCodes.Ldarg_2);
                cursor.EmitDelegate<Func<bool, string, MapArchive, bool>>(AfterStage);
            }
        }

        private static bool AfterStage(bool staged, string mapPath, MapArchive archive)
        {
            if (WorkshopRuntime.IsExternallyManaged) return staged;
            try
            {
                if (!reported) { reported = true; logger.LogDebug("Workshop cleanup callback active after startup cleanup."); }
                string guid = archive.Info?.GUID, version = archive.Info?.Version;
                // The Extender owns every foreign map package. Only our own historical
                // deliveries retain the explicitly supported inventory cleanup.
                if (!string.Equals(guid, APISharedPlugin.PluginGuid, StringComparison.Ordinal)) return staged;
                if (guid == null || version == null) return staged;
                WorkshopInventory.CheckGuid(guid);
                string pluginRoot = Path.Combine(Paths.GameRootPath, "BepInEx", "plugins");
                string installedRoot = Path.Combine(pluginRoot, guid);
                string staging = Path.Combine(Paths.GameRootPath, "_SE", ".staging");
                string stagedRoot = Path.Combine(staging, "BepInEx", "plugins", guid);
                string inventoryPath = Path.Combine(installedRoot, WorkshopInventory.FileName);
                string nextJson = staged ? archive.TryReadTextFile("BepInEx/plugins/" + guid + "/" + WorkshopInventory.FileName, true)
                    : (File.Exists(inventoryPath) ? File.ReadAllText(inventoryPath) : null);
                if (string.IsNullOrWhiteSpace(nextJson)) return staged;
                if (!WorkshopInventory.IsContainedWithoutLinks(pluginRoot, inventoryPath)) throw new InvalidDataException("Linked package inventory.");
                // No migration while the installed version differs from the current subscription.
                // In particular, do not reactivate a user-installed newer release as a downgrade.
                WorkshopInventory.Inventory previous = null;
                string installedInfo = Path.Combine(installedRoot, "info.json");
                if (!staged)
                {
                    if (!File.Exists(installedInfo)) return staged;
                    var info = DependencyFreeJson.Parse(File.ReadAllText(installedInfo)) as Dictionary<string, object>;
                    if (info == null || WorkshopInventory.Text(info, "GUID") != guid || WorkshopInventory.Text(info, "Version") != version) return staged;
                }
                else if (File.Exists(inventoryPath) && File.Exists(installedInfo))
                {
                    var info = DependencyFreeJson.Parse(File.ReadAllText(installedInfo)) as Dictionary<string, object>;
                    previous = WorkshopInventory.Read(File.ReadAllText(inventoryPath), guid, WorkshopInventory.Text(info, "Version"));
                }
                var next = WorkshopInventory.Read(nextJson, guid, version);
                if (staged) WorkshopInventory.VerifyFiles(next, stagedRoot);
                var victims = WorkshopInventory.Plan(previous, next, installedRoot, stagedRoot,
                    message => logger.LogWarning(message)).ToArray();
                if (victims.Length == 0) return staged;
                var protectedPaths = WorkshopOwnership.GetCompetingPaths(mapPath, guid, pluginRoot);
                victims = victims.Where(path => {
                    if (!protectedPaths.Contains(path)) return true;
                    logger.LogWarning("Preserved package file claimed by another Workshop subscription: " + path);
                    return false;
                }).ToArray();
                if (victims.Length == 0) return staged;
                foreach (string victim in victims)
                    if (AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic && string.Equals(a.Location, victim, StringComparison.OrdinalIgnoreCase)))
                        logger.LogInfo("Loaded obsolete package file scheduled for post-exit removal: " + victim);
                WorkshopDeletionManifest.Append(Path.Combine(Paths.GameRootPath, "_SE"), victims);
                logger.LogInfo("Workshop cleanup staged " + victims.Length + " obsolete files for " + guid + "; awaiting Script Extender confirmation.");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError("Workshop cleanup preserved installed files: " + ex.Message);
                return staged;
            }
        }
    }
}
