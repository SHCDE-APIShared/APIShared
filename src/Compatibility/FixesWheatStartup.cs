using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
                VerifyProvenance(fixesAssembly);
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
            ulong image = (ulong)module.ToInt64();
            // The unpatched code of the loaded module; live memory is compared against it below.
            var disk = new FixesWheatAnalysis.PeImage(File.ReadAllBytes(path.ToString()));
            Type owner = fixesAssembly.GetType("Fixes.Detours.FixesAIDetours", true);
            var handle = ReadField<HookHandle<X64InlineHook>>(owner, "c_game_ai_sell_item_handler_hook");
            var flags = ReadField<NativeFlagArray>(owner, "wheatSaleCategoryFlagArray");
            Require(handle != null && handle.Failure == null, "Fixes hook handle missing or failed");
            if (handle.Hook == null)
            {
                Require(flags == null, "Flag array exists without its hook");
                Report(log, "NOT-NEEDED: wheat hook absent/disabled at initialization; no correction installed");
                return;
            }
            X64InlineHook hook = handle.Hook;
            ulong site = hook.TargetAddress;
            int displacedLength = hook.DisplacedByteCount;
            Require(hook.IsInstalled && handle.IsInstalled && handle.ResolvedAddress == site &&
                site > image && site - image < disk.SizeOfImage && displacedLength >= 14 && displacedLength <= 64,
                "Hook installation/target/displacement contract changed");
            Require(flags != null && flags.Length >= 9 && flags.Address != 0 && hook.StubAddress != IntPtr.Zero,
                "Wheat flag array/stub contract changed");
            ulong stub = (ulong)hook.StubAddress.ToInt64();
            ulong flagAddress = flags.Address;
            FixesWheatMemory.Read(flagAddress, flags.Length, false);
            byte[] original = (byte[])typeof(X64InlineHook).GetField("_originalBytesBackup",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hook);
            Require(original != null && original.Length == displacedLength, "Backend displaced snapshot unavailable");
            uint siteRva = (uint)(site - image);
            // Disk bytes, backend snapshot and stub copy must agree; the frame is derived from the actual code.
            FixesWheatAnalysis.SellerFrame frame = FixesWheatAnalysis.AnalyzeSeller(disk, siteRva, original);
            ulong continuation = site + (uint)displacedLength;
            Func<byte[], FixesWheatAnalysis.WheatStub> analyze = bytes =>
                FixesWheatAnalysis.AnalyzeStub(bytes, stub, flagAddress, original, continuation, frame.PlayerHome);
            Func<byte[]> readStub = () => FixesWheatMemory.ReadAvailable(stub, 256);
            ValidateEntry(image, disk, frame, site, stub, displacedLength);
            FixesWheatAnalysis.WheatStub before = analyze(readStub());
            bool changed = FixesWheatAnalysis.Correct(readStub,
                (offset, value) =>
                {
                    RequireStartup();
                    ValidateEntry(image, disk, frame, site, stub, displacedLength);
                    FixesWheatMemory.WriteOperand(stub + (uint)offset, value);
                }, analyze);
            ValidateEntry(image, disk, frame, site, stub, displacedLength);
            Report(log, (changed ? "CORRECTED" : "ALREADY-CORRECT") + ": derived from the actual code; stub=0x" +
                stub.ToString("X") + " (" + before.Length + " bytes), siteRva=0x" + siteRva.ToString("X") +
                ", sellerRva=0x" + frame.FunctionBegin.ToString("X") + ", frame=0x" + frame.Allocation.ToString("X") +
                ", player displacement 0x" + before.Displacement.ToString("X") + "->0x" + before.Required.ToString("X") +
                "; flags/settings unchanged; startup-only, no later retry");
        }

        // The live seller prolog equals the disk code (no foreign entry detour), and the live site holds the
        // backend's jump to this stub.
        private static void ValidateEntry(ulong image, FixesWheatAnalysis.PeImage disk,
            FixesWheatAnalysis.SellerFrame frame, ulong site, ulong stub, int displacedLength)
        {
            FixesWheatAnalysis.RequireEqual(FixesWheatMemory.Read(image + frame.FunctionBegin, (int)frame.PrologLength),
                disk.Read(frame.FunctionBegin, (int)frame.PrologLength), "live seller prolog");
            FixesWheatAnalysis.RequireEqual(FixesWheatMemory.Read(site, displacedLength),
                FixesWheatAnalysis.ExpectedEntry(stub, displacedLength), "installed Fixes entry and padding");
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

        // Loaded/disk provenance of every assembly whose private members are read here. This binds the
        // inspected metadata to the loaded module; it is no version or file-hash allowlist. The private
        // backend semantics are checked where they are used: startup stack and state, hook handle against
        // live and disk bytes, flag array against the decoded stub operand.
        internal static void VerifyProvenance(Assembly fixesAssembly)
        {
            Internal.InstalledAssemblyIdentity.VerifyModule(fixesAssembly, fixesAssembly.Location);
            Assembly extender = typeof(SHCDESE.API.LowLevel.CrusaderLibrary).Assembly;
            Internal.InstalledAssemblyIdentity.VerifyModule(extender, extender.Location);
            foreach (Assembly backend in new[] { typeof(Chainloader).Assembly, typeof(X64InlineHook).Assembly,
                typeof(HookHandle<X64InlineHook>).Assembly, typeof(NativeFlagArray).Assembly,
                typeof(MonoMod.RuntimeDetour.ILHook).Assembly })
                Internal.InstalledAssemblyIdentity.VerifyModule(backend, backend.Location);
            Internal.InstalledAssemblyIdentity.VerifyModule(typeof(EngineInterface).Assembly,
                Internal.InstalledAssemblyIdentity.GameAssemblyPath(Paths.GameRootPath));
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
