using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Modifiers;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;

namespace APIShared.Wildlife
{
    // Chicken target (static audit, hash FBCB9319, 2026-11-14; see UpdateToNewDLL.md "Chicken population").
    // 0xD29E0 is reached only from the scheduled tick step 0xB4 of 0xD1AD0 (simulation thread, DLL_RunTick)
    // and loops over players 1..8. One purely native inline stub over [0xD2AB4,0xD2AC5) calls a rooted managed
    // callback only while a handle is enabled (one demand dword). Installed on the first registration inside
    // NativeInlinePatchWindow: the routine runs only in a map's simulation. Never unpatched.
    internal static unsafe class ChickenPopulationRuntime
    {
        internal const string SiteGroup = "Wildlife.ChickenPopulation";
        internal const int SpanRva = 0xD2AB4, SpanLength = 17;
        private const int MaximumPlayerId = 8;
        private const string Label = "Chicken target site";

        // cmp dword ptr [rip+0x3593525], 0; cmovne eax, r13d; cmp eax, dword ptr [rdi+0x2048]
        internal static readonly byte[] SpanBytes = WildlifeInlineSite.Hex("833D2535590300 410F45C5 3B8748200000");
        // jle 0xD2BA7
        internal static readonly byte[] SpanContinuation = WildlifeInlineSite.Hex("0F8EDC000000");
        private static readonly (int Rva, byte[] Bytes)[] Anchors =
        {
            // Player loop: mov ebp, 1 ... mov ebx, ebp ... lea rdi, [rcx+0x1345D0] ... lea r13d, [rbp+0x63]
            (0xD2A07, WildlifeInlineSite.Hex("BD01000000")),
            (0xD2A10, WildlifeInlineSite.Hex("8BDD")),
            (0xD2A16, WildlifeInlineSite.Hex("488DB9D0451300")),
            (0xD2A34, WildlifeInlineSite.Hex("448D6D63")),
            // jge 0xD2BFA at the jle target reuses the compare's flags (removal below the target is skipped).
            (0xD2BA7, WildlifeInlineSite.Hex("7D51")),
            // inc ebx; add rdi, 0x583C; cmp ebx, 9
            (0xD2BFA, WildlifeInlineSite.Hex("FFC3 4881C73C580000 83FB09"))
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TargetCallback(int playerId, int vanillaTarget, int currentChickens);

        private static readonly object Gate = new object();
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> SpanHandle = new HookHandle<X64InlineHook>();
        // Rooted for the process lifetime: the published stub calls it until exit.
        private static readonly TargetCallback Callback = Decide;
        private static readonly ulong CallbackAddress = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Callback).ToInt64());
        // Number of enabled handles; never freed, the published stub reads it.
        private static readonly IntPtr demand = Marshal.AllocHGlobal(8);
        private static readonly Func<ChickenPopulationModifierHandle, RegistrationKey> HandleKey =
            handle => new RegistrationKey(handle.Owner, handle.Id, handle.Order);
        private static ChickenPopulationModifierHandle[] handles = Array.Empty<ChickenPopulationModifierHandle>();
        // Simulation thread only (the stub's sole caller); grows outside the fixed-value fast path at most once per size.
        private static ValueModifierContribution[] contributions = new ValueModifierContribution[4];
        private static ManualLogSource log;
        private static CrusaderLibraryLoadContext loadContext;
        private static ulong image;
        private static int validated, confirmed;
        private static bool attempted;
        private static string unavailable = "Chicken population modifiers have not been initialized.";

        internal static bool IsAvailable
        {
            get { lock (Gate) return Volatile.Read(ref validated) != 0 && (Publication.IsReady || !attempted && NativeInlinePatchWindow.IsOpen); }
        }

        internal static string UnavailableReason
        {
            get
            {
                lock (Gate)
                {
                    if (Publication.IsReady) return "";
                    if (Volatile.Read(ref validated) == 0 || attempted) return unavailable;
                    return NativeInlinePatchWindow.IsOpen ? "" : NativeInlinePatchWindow.ClosedReason;
                }
            }
        }

        internal static NativeSites.NativeSiteSpan[] Spans() => new[]
        {
            new NativeSites.NativeSiteSpan(SpanRva, SpanLength, NativeSites.NativeSiteMode.Exclusive)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            log = logger;
            Marshal.WriteInt64(demand, 0);
            try
            {
                if (!string.Equals(hash, HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The chicken population is audited only for native image " + HuntablePreyNativeContract.AuditedHash + ".");
                if (context == null) throw new InvalidOperationException("The native load image is unavailable.");
                foreach (var anchor in Anchors)
                    if (!NativeInlineStub.SnapshotMatches(context.Memory, anchor.Rva, anchor.Bytes))
                        throw new InvalidOperationException("The chicken routine at RVA 0x" + anchor.Rva.ToString("X") + " differs from the audit.");
                loadContext = context;
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                Volatile.Write(ref validated, 1);
                NativeApiLog.Info(log, "Chicken population modifiers ready: target compare 0x" + SpanRva.ToString("X") + " installs on the first registration.");
            }
            catch (Exception error)
            {
                unavailable = "Chicken population modifiers are unavailable: " + error.Message;
                NativeApiLog.Error(log, unavailable);
            }
        }

        internal static bool TryRegister(string owner, string id, Func<ChickenPopulationContext, ValueModifierContribution> provider, int order,
            out ChickenPopulationModifierHandle handle, out string reason)
        {
            handle = null;
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Chicken population modifiers must register on the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 || provider == null ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID, registration ID and provider are required."; return false; }
            lock (Gate)
            {
                if (OrderedRegistrations.Contains(handles, HandleKey, owner, id))
                { reason = "This owner already registered that chicken population ID."; return false; }
                if (!TryEnsureInstalled(out reason)) return false;
                handle = new ChickenPopulationModifierHandle(owner, id, order, provider);
                Volatile.Write(ref handles, OrderedRegistrations.WithAdded(handles, handle, HandleKey));
            }
            NativeApiLog.Info(log, "Chicken population modifier registered: owner=" + owner + ", id=" + id + ", order=" + order + ".");
            reason = "";
            return true;
        }

        internal static void SetEnabled(ChickenPopulationModifierHandle handle, bool value)
        {
            if (handle == null) return;
            lock (Gate)
            {
                if (!handle.SetEnabledFlag(value)) return;
                int count = 0;
                foreach (var item in handles) if (item.IsEnabled) count++;
                Volatile.Write(ref *(long*)demand, count);
            }
        }

        // Simulation thread, from the native stub only. Never throws into native code: any failure keeps
        // Vanilla's target. The fixed-value path allocates nothing.
        private static int Decide(int playerId, int vanillaTarget, int currentChickens)
        {
            try
            {
                if (playerId < 1 || playerId > MaximumPlayerId) return vanillaTarget;
                var current = Volatile.Read(ref handles);
                if (contributions.Length < current.Length) contributions = new ValueModifierContribution[current.Length];
                var context = new ChickenPopulationContext(playerId, vanillaTarget, currentChickens);
                int count = 0;
                bool factors = false;
                foreach (var handle in current)
                {
                    if (!handle.IsEnabled) continue;
                    ValueModifierContribution value;
                    try { value = handle.Provider(context); }
                    catch (Exception error) { Report(handle, error); continue; }
                    if (value.Kind == ValueModifierKind.None) continue;
                    if (value.Kind != ValueModifierKind.FixedValue) factors = true;
                    contributions[count++] = value;
                }
                int result = vanillaTarget;
                if (count != 0)
                {
                    decimal composed;
                    if (!factors) composed = Math.Round(contributions[count - 1].Value, MidpointRounding.AwayFromZero);
                    else composed = ValueModifierCalculator.Calculate(vanillaTarget, new ArraySegment<ValueModifierContribution>(contributions, 0, count),
                        0m, ChickenPopulationModifiers.MaximumTarget);
                    result = (int)Math.Max(0m, Math.Min(ChickenPopulationModifiers.MaximumTarget, composed));
                }
                if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0)
                    NativeApiLog.Info(log, "Chicken population bridge confirmed: player=" + playerId + ", vanilla=" + vanillaTarget +
                        ", chickens=" + currentChickens + ", target=" + result + ".");
                return result;
            }
            catch
            {
                return vanillaTarget;
            }
        }

        private static void Report(ChickenPopulationModifierHandle handle, Exception error)
        {
            if (Interlocked.Exchange(ref handle.FailureReported, 1) != 0) return;
            try { NativeApiLog.Error(log, "Chicken population provider " + handle.Owner + "/" + handle.Id + " failed; its contribution is skipped: " + error); }
            catch { }
        }

        // Callers hold Gate. One attempt per process (fail closed).
        private static bool TryEnsureInstalled(out string reason)
        {
            if (Publication.IsReady) { reason = ""; return true; }
            if (Volatile.Read(ref validated) == 0 || attempted) { reason = unavailable; return false; }
            if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
            attempted = true;
            var group = new NativeInlineStubGroup(SiteGroup, Publication);
            try
            {
                WildlifeInlineSite.CheckSite(loadContext, image, SpanRva, SpanBytes, SpanContinuation, Label);
                WildlifeInlineSite.ProbeLength(image, SpanRva, SpanLength, Label);
                group.Claim(Spans());
                group.Begin(loadContext.Region);
                group.Add(SpanHandle, image + SpanRva, (a, original, back) => Generate(a, original), SpanLength);
                var result = group.Commit("Chicken target preparation was incomplete.");
                VerifyStub(false);
                group.Publish(result, typeof(ChickenPopulationRuntime), () => VerifyStub(true));
                NativeApiLog.Info(log, "Chicken population bridge published: span=0x" + SpanRva.ToString("X") + "; native demand gate.");
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                unavailable = "Chicken population modifiers are unavailable: " + error.Message;
                group.RollbackUnpublished(null);
                NativeApiLog.Error(log, unavailable);
                reason = unavailable;
                return false;
            }
        }

        private static void Generate(Assembler a, ReadOnlySpan<Instruction> original) =>
            ChickenPopulationNativeAssembly.EmitTarget(a, original, unchecked((ulong)demand.ToInt64()), CallbackAddress);

        private static void VerifyStub(bool installed) =>
            WildlifeInlineSite.Verify(SpanHandle, image, SpanRva, SpanLength, SpanBytes, Generate, installed, Label);
    }
}
