using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Assembly.Stateful;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Corpse lifetimes of HuntablePrey (static audit, hash FBCB9319, 2026-11-14; see UpdateToNewDLL.md
    // "Corpse lifetimes"). In AI state 0x6E (fading) the rabbit, camel and chicken updates add 1 (chicken 2)
    // to the fade byte +0x986 and the counter +0x920 per update and compare both with one constant 0x20.
    //  - Camel [0x1584BE,0x1584D1) and chicken [0x16346B,0x16347F): two purely native inline stubs load that
    //    constant from a cell instead. Installed on the first registration that asks for a lifetime, inside
    //    NativeInlinePatchWindow (the spans run only in a map's simulation); never unpatched. A cell holding
    //    0x20 is Vanilla.
    //  - Rabbit: the constant at 0x157EDC is the Script Extender's GameGlobalsManager.RabbitDespawnTickTime;
    //    APIShared only places an owner-bound override through the Extender's own site, never its own patch.
    // Apply runs from HuntablePreyRuntime.Activate/EndWorld, which the mission owner calls under the engine
    // monitor before the first and after the last simulation tick of a mission, so no thread executes the
    // rabbit instruction while the Extender rewrites its immediate and every peer starts with the same values.
    internal static unsafe class CorpseLifetimeNative
    {
        internal const string SiteGroup = "Wildlife.CorpseLifetimes";
        internal const int RabbitImmediateRva = 0x157EDC;
        internal const int CamelSpanRva = 0x1584BE, CamelSpanLength = 19;
        internal const int ChickenSpanRva = 0x16346B, ChickenSpanLength = 20;
        private const string Label = "Corpse lifetime site";

        // inc byte ptr [rbx+rbp+0x986]; mov ecx, 0x20; cmp byte ptr [rbx+rbp+0x986], cl
        internal static readonly byte[] CamelBytes = WildlifeInlineSite.Hex("FE842B86090000 B920000000 388C2B86090000");
        // movzx eax, byte ptr [rbx+rbp+0x986] (then cmovg eax, ecx)
        internal static readonly byte[] CamelContinuation = WildlifeInlineSite.Hex("0FB6842B86090000 0F4FC1");
        // inc byte ptr [rbx+rbp+0x986]; mov ecx, 0x20; inc word ptr [rbx+rbp+0x920]
        internal static readonly byte[] ChickenBytes = WildlifeInlineSite.Hex("FE842B86090000 B920000000 66FF842B20090000");
        // inc byte ptr [rbx+rbp+0x986]; cmp byte ptr [rbx+rbp+0x986], cl
        internal static readonly byte[] ChickenContinuation = WildlifeInlineSite.Hex("FE842B86090000 388C2B86090000");
        // Branch entries of both fading branches (cmp si/di, 0x6E; jne) and the rabbit sequence
        // (inc dl; mov ecx, 0x20; cmp dl, cl), compared in the unmodified load image only.
        private static readonly (int Rva, byte[] Bytes)[] Anchors =
        {
            (0x1584B8, WildlifeInlineSite.Hex("6683FE6E 754D")),
            (0x163465, WildlifeInlineSite.Hex("6683FF6E 7555")),
            (RabbitImmediateRva - 2, WildlifeInlineSite.Hex("FEC2 B920000000 3AD1")),
            // Counter compare and deletion of the camel and chicken branches: cmp word ptr [rbx+rbp+0x920], cx
            (0x1584EB, WildlifeInlineSite.Hex("66398C2B20090000 0F8E5B010000")),
            (0x1634A7, WildlifeInlineSite.Hex("66398C2B20090000 7E22"))
        };

        private static readonly object Gate = new object();
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> CamelHandle = new HookHandle<X64InlineHook>();
        private static readonly HookHandle<X64InlineHook> ChickenHandle = new HookHandle<X64InlineHook>();
        // Camel threshold at +0, chicken threshold at +4. Never freed: published stubs read it until exit.
        private static readonly IntPtr cells = Marshal.AllocHGlobal(8);
        private static readonly object RabbitOwner = new object();
        private static ManualLogSource log;
        private static CrusaderLibraryLoadContext loadContext;
        private static ulong image;
        private static ManagedAssemblyImmediate<short> rabbitSite;
        private static AssemblyValueOverrideScope rabbitOverride;
        private static int validated, rabbitValue = CorpseLifetimeNativeAssembly.VanillaThreshold;
        private static bool attempted;
        private static string unavailable = "Corpse lifetimes have not been initialized.";
        private static string stubFailure = "";

        internal static NativeSites.NativeSiteSpan[] Spans() => new[]
        {
            new NativeSites.NativeSiteSpan(CamelSpanRva, CamelSpanLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(ChickenSpanRva, ChickenSpanLength, NativeSites.NativeSiteMode.Exclusive)
        };

        internal static readonly int Rabbit = (int)eChimps.CHIMP_TYPE_RABBIT;
        internal static readonly int Camel = (int)eChimps.CHIMP_TYPE_CAMEL;
        internal static readonly int Chicken = (int)eChimps.CHIMP_TYPE_CHICKEN;

        // Fading-counter steps per update; 0 for a type without an audited site.
        internal static int StepOf(int unitType) => unitType == Rabbit || unitType == Camel ? 1 : unitType == Chicken ? 2 : 0;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            log = logger;
            Marshal.WriteInt32(cells, 0, CorpseLifetimeNativeAssembly.VanillaThreshold);
            Marshal.WriteInt32(cells, 4, CorpseLifetimeNativeAssembly.VanillaThreshold);
            try
            {
                if (!string.Equals(hash, HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Corpse lifetimes are audited only for native image " + HuntablePreyNativeContract.AuditedHash + ".");
                if (context == null) throw new InvalidOperationException("The native load image is unavailable.");
                ReadOnlySpan<byte> memory = context.Memory;
                foreach (var anchor in Anchors)
                    if (!NativeInlineStub.SnapshotMatches(memory, anchor.Rva, anchor.Bytes))
                        throw new InvalidOperationException("The corpse fading code at RVA 0x" + anchor.Rva.ToString("X") + " differs from the audit.");
                loadContext = context;
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                ManagedAssemblyImmediate<short> site = GameGlobalsManager.Instance?.RabbitDespawnTickTime;
                if (site == null || unchecked((ulong)site.InstructionAddress.ToInt64()) != image + RabbitImmediateRva ||
                    site.OriginalValue != CorpseLifetimeNativeAssembly.VanillaThreshold)
                    throw new InvalidOperationException("The Script Extender's RabbitDespawnTickTime is not the audited constant at RVA 0x" +
                        RabbitImmediateRva.ToString("X") + ".");
                rabbitSite = site;
                Volatile.Write(ref validated, 1);
                NativeApiLog.Info(log, "Corpse lifetimes ready: rabbit through the Script Extender at 0x" + RabbitImmediateRva.ToString("X") +
                    "; camel 0x" + CamelSpanRva.ToString("X") + " and chicken 0x" + ChickenSpanRva.ToString("X") + " install on the first registration.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref unavailable, "Corpse lifetimes are unavailable: " + error.Message);
                NativeApiLog.Error(log, Volatile.Read(ref unavailable));
            }
        }

        // Whether a registration for this type could request a lifetime now (camels and chickens: the stubs are
        // published, or their one installation attempt can still run inside the patch window).
        internal static bool IsSupported(int unitType)
        {
            if (Volatile.Read(ref validated) == 0 || StepOf(unitType) == 0) return false;
            if (unitType == Rabbit) return true;
            lock (Gate) return Publication.IsReady || !attempted && NativeInlinePatchWindow.IsOpen;
        }

        // Unity thread, from a registration. Rabbits need only the Extender's site; camels and chickens need the
        // stubs, which install once inside NativeInlinePatchWindow. A failed attempt is not retried (fail closed).
        internal static bool TryEnsureSupported(int unitType, out string reason)
        {
            if (StepOf(unitType) == 0) { reason = "A corpse lifetime is supported for rabbits, camels and chickens only."; return false; }
            if (Volatile.Read(ref validated) == 0) { reason = Volatile.Read(ref unavailable); return false; }
            if (unitType == Rabbit) { reason = ""; return true; }
            lock (Gate)
            {
                if (Publication.IsReady) { reason = ""; return true; }
                if (attempted) { reason = stubFailure; return false; }
                if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
                attempted = true;
                var group = new NativeInlineStubGroup(SiteGroup, Publication);
                try
                {
                    WildlifeInlineSite.CheckSite(loadContext, image, CamelSpanRva, CamelBytes, CamelContinuation, Label);
                    WildlifeInlineSite.CheckSite(loadContext, image, ChickenSpanRva, ChickenBytes, ChickenContinuation, Label);
                    WildlifeInlineSite.ProbeLength(image, CamelSpanRva, CamelSpanLength, Label);
                    WildlifeInlineSite.ProbeLength(image, ChickenSpanRva, ChickenSpanLength, Label);
                    group.Claim(Spans());
                    group.Begin(loadContext.Region);
                    group.Add(CamelHandle, image + CamelSpanRva, (a, original, back) => GenerateCamel(a, original), CamelSpanLength);
                    group.Add(ChickenHandle, image + ChickenSpanRva, (a, original, back) => GenerateChicken(a, original), ChickenSpanLength);
                    var result = group.Commit("Corpse lifetime site preparation was incomplete.");
                    Verify(false);
                    group.Publish(result, typeof(CorpseLifetimeNative), () => Verify(true));
                    NativeApiLog.Info(log, "Corpse lifetime sites published: camel=0x" + CamelSpanRva.ToString("X") + ", chicken=0x" +
                        ChickenSpanRva.ToString("X") + "; native threshold cells, Vanilla 0x20.");
                    reason = "";
                    return true;
                }
                catch (Exception error)
                {
                    stubFailure = "Camel and chicken corpse lifetimes are unavailable: " + error.Message;
                    group.RollbackUnpublished(null);
                    NativeApiLog.Error(log, stubFailure);
                    reason = stubFailure;
                    return false;
                }
            }
        }

        // Thresholds of the catalog's corpse lifetimes; null restores Vanilla. Called only by HuntablePreyRuntime
        // at mission activation and end (engine monitor held, see the class comment). Idempotent.
        internal static void Apply(HuntablePreyCatalog catalog)
        {
            if (Volatile.Read(ref validated) == 0) return;
            int rabbit = Threshold(catalog, Rabbit), camel = Threshold(catalog, Camel), chicken = Threshold(catalog, Chicken);
            if (Publication.IsReady)
            {
                Volatile.Write(ref *(int*)cells, camel);
                Volatile.Write(ref *(int*)((byte*)cells + 4), chicken);
            }
            lock (Gate)
            {
                if (rabbit == rabbitValue) return;
                try
                {
                    rabbitOverride?.Dispose();
                    rabbitOverride = null;
                    if (rabbit != CorpseLifetimeNativeAssembly.VanillaThreshold)
                        rabbitOverride = rabbitSite.SetOverride(RabbitOwner, (short)rabbit, reason: "APIShared HuntablePrey corpse lifetime");
                    rabbitValue = rabbit;
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "The rabbit corpse lifetime could not be applied; the Script Extender keeps its value: " + error.Message);
                }
            }
        }

        private static int Threshold(HuntablePreyCatalog catalog, int unitType)
        {
            if (catalog == null || !catalog.TryGet(unitType, out HuntablePreyEntry entry) || !entry.MinimumCorpseLifetime.HasValue)
                return CorpseLifetimeNativeAssembly.VanillaThreshold;
            return CorpseLifetimeNativeAssembly.EncodeThreshold(entry.MinimumCorpseLifetime.Value, StepOf(unitType));
        }

        private static ulong Cell(int offset) => unchecked((ulong)cells.ToInt64()) + (uint)offset;

        private static void GenerateCamel(Assembler a, ReadOnlySpan<Instruction> original) =>
            CorpseLifetimeNativeAssembly.EmitThreshold(a, original, Cell(0));

        private static void GenerateChicken(Assembler a, ReadOnlySpan<Instruction> original) =>
            CorpseLifetimeNativeAssembly.EmitThreshold(a, original, Cell(4));

        private static void Verify(bool installed)
        {
            WildlifeInlineSite.Verify(CamelHandle, image, CamelSpanRva, CamelSpanLength, CamelBytes, GenerateCamel, installed, Label);
            WildlifeInlineSite.Verify(ChickenHandle, image, ChickenSpanRva, ChickenSpanLength, ChickenBytes, GenerateChicken, installed, Label);
        }
    }
}
