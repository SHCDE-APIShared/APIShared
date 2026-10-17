using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;

namespace APIShared.Compatibility
{
    // One synchronous attempt only, before APIShared publishes any services. No event,
    // Unity component, shutdown callback or later retry owns this correction.
    internal static class FixesWheatStartup
    {
        private static int attempted;
        private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int capacity);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Initialize(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue("fixes", out PluginInfo fixes))
                {
                    Report(log, "NOT-NEEDED: Fixes absent; no correction installed");
                    return;
                }
                Require(fixes.Instance != null, "Fixes plugin did not finish synchronous initialization");
                Assembly fixesAssembly = fixes.Instance.GetType().Assembly;
                VerifyBinaries(fixesAssembly);
                RequireStartup();

                FieldInfo lockField = typeof(EngineInterface).GetField("threadLock", StaticFields);
                Require(lockField != null && lockField.IsPrivate && lockField.FieldType == typeof(object), "Engine lock contract changed");
                object engineLock = lockField.GetValue(null);
                Require(engineLock != null && !Monitor.IsEntered(engineLock), "Missing engine lock or reentrant engine call");
                bool taken = false;
                try
                {
                    Monitor.TryEnter(engineLock, 0, ref taken);
                    Require(taken, "Engine busy; no retry permitted");
                    RequireStartup();
                    CorrectUnderLock(fixesAssembly, log);
                }
                finally { if (taken) Monitor.Exit(engineLock); }
            }
            catch (Exception ex)
            {
                SafeLog(log, LogLevel.Error, "REFUSED/UNCONFIRMED: " + ex.Message +
                    "; no confirmed protection. No retry or rollback; other APIShared services continue.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RequireStartup()
        {
            Require(IntPtr.Size == 8, "Only audited Windows x64 startup is supported");
            Require(ReadBoolean(typeof(Chainloader), "_initialized") && !ReadBoolean(typeof(Chainloader), "_loaded"),
                "Chainloader is not in its initial synchronous load");
            Require(Chainloader.PluginInfos.TryGetValue(APISharedPlugin.PluginGuid, out PluginInfo own) &&
                ReferenceEquals(own.Instance, null), "APIShared is not inside its initial AddComponent/Awake");

            int chain = -1, entry = -1, awake = -1;
            MethodInfo chainloaderStart = typeof(Chainloader).GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            Require(chainloaderStart != null, "Chainloader.Start contract unavailable");
            MethodBase activeChainloaderBody = StartupMethodIdentity.ActiveBody(chainloaderStart);
            StackFrame[] frames = new StackTrace(false).GetFrames();
            Require(frames != null, "Startup stack unavailable");
            for (int i = 0; i < frames.Length; i++)
            {
                MethodBase method = frames[i].GetMethod();
                if (method == null) continue;
                if (method.DeclaringType == typeof(APISharedPlugin) && method.Name == "Awake") awake = i;
                if (StartupMethodIdentity.Matches(method, chainloaderStart, activeChainloaderBody))
                {
                    Require(chain == -1, "Reentrant Chainloader.Start");
                    chain = i;
                }
                if (method.DeclaringType == typeof(UnityEngine.Application) && method.Name == ".cctor") entry = i;
            }
            // Direct field reads, never singleton getters. Editor entrypoints are not all locked;
            // therefore the lock alone cannot authorize a correction after game initialization.
            FieldInfo director = typeof(Director).GetField("instance", StaticFields);
            FieldInfo map = typeof(GameMap).GetField("instance", StaticFields);
            bool initialized = ReadBoolean(typeof(Chainloader), "_initialized");
            bool loaded = ReadBoolean(typeof(Chainloader), "_loaded");
            bool ownAbsent = ReferenceEquals(own.Instance, null);
            bool directorAbsent = director != null && ReferenceEquals(director.GetValue(null), null);
            bool mapAbsent = map != null && ReferenceEquals(map.GetValue(null), null);
            Require(director != null && map != null && FixesWheatContract.IsStartup(
                initialized, loaded, ownAbsent, awake, chain, entry, directorAbsent, mapAbsent),
                "Not the audited pre-game Application..cctor -> Chainloader.Start -> Awake context" +
                "; initialized=" + initialized + "; loaded=" + loaded + "; ownAbsent=" + ownAbsent +
                "; frames Awake/Chainloader/Application=" + awake + "/" + chain + "/" + entry +
                "; directorAbsent=" + directorAbsent + "; mapAbsent=" + mapAbsent + "; stack=" + new StackTrace(false));
        }

        private static void CorrectUnderLock(Assembly fixesAssembly, ManualLogSource log)
        {
            IntPtr module = GetModuleHandle("CrusaderDE.dll");
            Require(module != IntPtr.Zero, "CrusaderDE not loaded during synchronous startup");
            var path = new StringBuilder(32768);
            uint count = GetModuleFileName(module, path, path.Capacity);
            Require(count > 0 && count < path.Capacity, "Native module path unavailable");
            VerifyFile(path.ToString(), FixesWheatContract.NativeHash);
            ulong image = (ulong)module.ToInt64();
            Type owner = fixesAssembly.GetType("Fixes.Detours.FixesAIDetours", true);
            var handle = ReadField<HookHandle<X64InlineHook>>(owner, "c_game_ai_sell_item_handler_hook");
            var flags = ReadField<NativeFlagArray>(owner, "wheatSaleCategoryFlagArray");
            Require(handle != null && handle.Failure == null, "Fixes hook handle missing or failed");
            ulong site = image + FixesWheatContract.SiteRva;
            if (handle.Hook == null)
            {
                Require(flags == null, "Flag array exists without its hook");
                FixesWheatContract.RequireEqual(FixesWheatMemory.Read(site, FixesWheatContract.DisplacedLength),
                    FixesWheatContract.Original, "absent Fixes hook entry");
                Report(log, "NOT-NEEDED: wheat hook absent/disabled at initialization; no correction installed");
                return;
            }
            X64InlineHook hook = handle.Hook;
            Require(hook.IsInstalled && handle.IsInstalled && handle.ResolvedAddress == site &&
                hook.TargetAddress == site && hook.DisplacedByteCount == FixesWheatContract.DisplacedLength,
                "Hook installation/target/displacement contract changed");
            Require(flags != null && flags.Length == 9 && flags.Address != 0 && hook.StubAddress != IntPtr.Zero,
                "Wheat flag array/stub contract changed");
            ulong stub = (ulong)hook.StubAddress.ToInt64();
            ulong flagAddress = flags.Address;
            FixesWheatMemory.Read(flagAddress, flags.Length, false);
            byte[] original = (byte[])typeof(X64InlineHook).GetField("_originalBytesBackup",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hook);
            FixesWheatContract.RequireEqual(original, FixesWheatContract.Original, "backend displaced instructions");
            ValidateEntry(image, site, stub);
            bool changed = FixesWheatContract.Correct(
                () => FixesWheatMemory.Read(stub, FixesWheatContract.StubLength),
                (offset, value) =>
                {
                    RequireStartup();
                    ValidateEntry(image, site, stub);
                    FixesWheatMemory.WriteOperand(stub + (uint)offset, value);
                }, flagAddress, site + FixesWheatContract.DisplacedLength);
            ValidateEntry(image, site, stub);
            Report(log, (changed ? "CORRECTED" : "ALREADY-CORRECT") + ": verified full 75-byte stub=0x" + stub.ToString("X") +
                ", siteRva=0x3F0C9, player displacement=0x78; flags/settings unchanged; startup-only, no later retry");
        }

        private static void ValidateEntry(ulong image, ulong site, ulong stub)
        {
            FixesWheatContract.RequireEqual(FixesWheatMemory.Read(image + FixesWheatContract.SellerRva, FixesWheatContract.Prolog.Length),
                FixesWheatContract.Prolog, "seller prolog/player stack derivation");
            FixesWheatContract.RequireEqual(FixesWheatMemory.Read(site, FixesWheatContract.DisplacedLength),
                FixesWheatContract.ExpectedEntry(stub), "installed Fixes entry and padding");
        }

        private static T ReadField<T>(Type owner, string name) where T : class
        {
            FieldInfo field = owner.GetField(name, StaticFields);
            Require(field != null && field.FieldType == typeof(T), "Fixes field contract changed: " + name);
            return (T)field.GetValue(null);
        }

        private static bool ReadBoolean(Type owner, string name)
        {
            FieldInfo field = owner.GetField(name, StaticFields);
            Require(field != null && field.FieldType == typeof(bool), "Startup field contract changed: " + name);
            return (bool)field.GetValue(null);
        }

        // Pure identity preflight, also exercised against installed files by integration tests.
        internal static void VerifyBinaries(Assembly fixesAssembly)
        {
            // Fixes and SE versions are not safety boundaries. Validate their loaded
            // provenance here and their full actual native hook contract below.
            Internal.InstalledAssemblyIdentity.VerifyModule(fixesAssembly, fixesAssembly.Location);
            VerifyAssembly(typeof(Chainloader).Assembly, "8255B28902886085C578B9E427D3073C97002DB85176D2090CDEDA90EF14CE70");
            VerifyAssembly(typeof(EngineInterface).Assembly, "BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789");
            VerifyAssembly(typeof(UnityEngine.Application).Assembly, "E468E19789D608B41E2C7FE4F367C7082FECE7D1883F568E75870F6C5384CC86");
            VerifyAssembly(typeof(X64InlineHook).Assembly, "E085B83B2B87B5D2142C980090D8D9EC616A90A443BD125C17B41E0BB47A44EB");
            VerifyAssembly(typeof(HookHandle<X64InlineHook>).Assembly, "339CECE116F66F59ECC7DA5E9A5C0B2DFE24F4E7A5E7EFC005864FA77C027A1E");
            VerifyAssembly(typeof(NativeFlagArray).Assembly, "EE4B036C486077F7D8898AE054F124EDDE73E6AE1A059E8991C361E2B720D29F");
            VerifyAssembly(typeof(Iced.Intel.Assembler).Assembly, "CC915D1C2E7BE60933C5B129842CA3D08D1FE3CDA6C6D6ADE8824DCDDE16191F");
            Assembly extender = typeof(SHCDESE.API.LowLevel.CrusaderLibrary).Assembly;
            Internal.InstalledAssemblyIdentity.VerifyModule(extender, extender.Location);
            // These backend pins protect the private map/handle semantics used by
            // StartupMethodIdentity, independently of the optional mod's version.
            VerifyAssembly(typeof(MonoMod.RuntimeDetour.ILHook).Assembly,
                "40E49BB314391CD7BDDC2644F8553EEBA92C194B940836B103DF16955C464E0C");
            VerifyAssembly(typeof(MonoMod.Utils.DynamicMethodDefinition).Assembly,
                "9D1495F147AC93C4F81F84538C1A326E8F8A6AEFC78D6289D798F3CE1162C5E9");
            VerifyFile(Path.Combine(Paths.BepInExRootPath, "core", "BepInEx.Preloader.dll"),
                "55D3895351A9D16B63B6F35F1C01B44AC650979E853D0BD3A442B92A082AF64F");
        }

        private static void VerifyAssembly(Assembly assembly, params string[] hashes)
        {
            string path = assembly == typeof(EngineInterface).Assembly
                ? Internal.InstalledAssemblyIdentity.GameAssemblyPath(Paths.GameRootPath)
                : assembly == typeof(UnityEngine.Application).Assembly
                ? Internal.InstalledAssemblyIdentity.UnityCoreAssemblyPath(Paths.GameRootPath)
                : assembly.Location;
            Internal.InstalledAssemblyIdentity.Verify(assembly, path, hashes);
        }

        private static void VerifyFile(string path, string expected)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                Require(string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expected,
                    StringComparison.OrdinalIgnoreCase), "Unsupported binary: " + Path.GetFileName(path));
        }

        private static void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException(reason);
        }
        private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ";
        private static void Report(ManualLogSource log, string message) => SafeLog(log, LogLevel.Info, message);
        private static void SafeLog(ManualLogSource log, LogLevel level, string message)
        {
            // A foreign logging listener must not abort the remaining APIShared initialization.
            try { log.Log(level, Stamp() + "Fixes wheat " + message); }
            catch { }
        }
    }
}
