using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;

namespace APIShared.Wildlife
{
    // Vanilla blocks a Hunter's own player's animals at three points (hash FBCB9319, static audit of
    // 2026-11-08): the query owner check, the explicit attack's team check and the arrow's team check.
    // These purely native gates admit a type flagged in one per-type table when owner == Hunter's owner.
    // Installed lazily, on the first registration that asks for it, so an older ImprovedHunters that
    // patches the same spans itself (it compares live bytes first, as these gates do) keeps working when
    // nobody registers; whoever comes second refuses. Never unpatched; an empty table restores Vanilla.
    internal static unsafe class HunterOwnerGates
    {
        internal const string SiteGroup = "Wildlife.HunterOwnerGates";
        internal const int QueryRva = 0x18AFD8, AttackRva = 0x18EBDA, ArrowRva = 0x192AA7, SpanLength = 14;
        private const int QueryRejectRva = 0x18B0DD, AttackRejectRva = 0x18ECA7, ArrowMissRva = 0x1929D0, ArrowContinueRva = 0x192B93;
        private const int ArrowGateRva = 0x192B00, ArrowGateLength = 0x93;
        private const string ArrowGateSha256 = "C3BCA2C9F2F5E9C4FD3A879193881A81AC75DE7083A3C38251FB940743AE225F";
        // Image-relative projectile record fields read through R11 (= image base) at the arrow site; the gate
        // block above reads the same two displacements (0x192B2C, 0x192B66), which its hash pins.
        internal const int ProjectileSourceDisplacement = 0x2F7A736, ProjectileHeightDisplacement = 0x2F7A6D0;

        private static readonly byte[] QueryBytes = HunterNativeSites.Hex("6683BBF6FDFFFF00 0F85F7000000");
        private static readonly byte[] QueryContinuation = HunterNativeSites.Hex("0FB783EEFDFFFF");
        // cmp [r9+rcx*4+0x37EDF3C], eax; je 0x18ECA7
        private static readonly byte[] AttackBytes = HunterNativeSites.Hex("413984893CDF7E03 0F84BF000000");
        private static readonly byte[] AttackContinuation = HunterNativeSites.Hex("4183FC06");
        private static readonly byte[] ArrowBytes = HunterNativeSites.Hex("433984193CDF7E03 440FB75C2478");
        private static readonly byte[] ArrowContinuation = HunterNativeSites.Hex("7549");

        private static readonly object Gate = new object();
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> QueryHandle = new HookHandle<X64InlineHook>();
        private static readonly HookHandle<X64InlineHook> AttackHandle = new HookHandle<X64InlineHook>();
        private static readonly HookHandle<X64InlineHook> ArrowHandle = new HookHandle<X64InlineHook>();
        // Never freed: published stubs read it until the process ends.
        private static readonly IntPtr ownTypes = Marshal.AllocHGlobal(HunterNativeAssembly.TypeCount);
        private static string diagnostic = "";
        private static bool attempted;

        internal static bool IsAvailable => Publication.IsReady;

        // Unity thread, from a registration before the first mission (NativeInlinePatchWindow). Idempotent;
        // a failed attempt is not retried (fail closed).
        internal static bool TryEnsureInstalled(ManualLogSource log, out string reason)
        {
            lock (Gate)
            {
                if (IsAvailable) { reason = ""; return true; }
                if (attempted) { reason = diagnostic; return false; }
                // Inline stubs patch without suspending threads; a running simulation could execute the spans.
                if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
                attempted = true;
                var group = new NativeInlineStubGroup(SiteGroup, Publication);
                try
                {
                    var context = HunterNativeSites.LoadContext;
                    if (!HunterNativeSites.IsAvailable || context == null)
                        throw new InvalidOperationException("The Hunter native sites are unavailable: " + HunterNativeSites.Diagnostic);
                    ulong image = HunterNativeSites.Image;
                    new Span<byte>((void*)ownTypes, HunterNativeAssembly.TypeCount).Clear();
                    if (Sha256Hex.Of(context.Memory.Slice(ArrowGateRva, ArrowGateLength).ToArray()) != ArrowGateSha256)
                        throw new InvalidOperationException("The arrow hit gate block differs from the audit.");
                    HunterNativeSites.CheckSite(context, QueryRva, QueryBytes, QueryContinuation);
                    HunterNativeSites.CheckSite(context, AttackRva, AttackBytes, AttackContinuation);
                    HunterNativeSites.CheckSite(context, ArrowRva, ArrowBytes, ArrowContinuation);
                    HunterNativeSites.ProbeLength(QueryRva, SpanLength);
                    HunterNativeSites.ProbeLength(AttackRva, SpanLength);
                    HunterNativeSites.ProbeLength(ArrowRva, SpanLength);
                    group.Claim(Spans());
                    group.Begin(context.Region);
                    group.Add(QueryHandle, image + QueryRva, (a, original, back) => GenerateQuery(a, original), SpanLength);
                    group.Add(AttackHandle, image + AttackRva, (a, original, back) => GenerateAttack(a, original), SpanLength);
                    group.Add(ArrowHandle, image + ArrowRva, (a, original, back) => GenerateArrow(a, original), SpanLength);
                    var result = group.Commit("Hunter owner gate preparation was incomplete.");
                    Verify(false);
                    group.Publish(result, typeof(HunterOwnerGates), () => Verify(true));
                    NativeApiLog.Info(log, "Hunter owner gates published: query=0x" + QueryRva.ToString("X") + ", attack=0x" + AttackRva.ToString("X") +
                        ", arrow=0x" + ArrowRva.ToString("X") + "; native per-type table, owner equality.");
                    reason = "";
                    return true;
                }
                catch (Exception error)
                {
                    diagnostic = "Own-player prey is unavailable: " + error.Message;
                    group.RollbackUnpublished(null);
                    NativeApiLog.Error(log, diagnostic);
                    reason = diagnostic;
                    return false;
                }
            }
        }

        internal static NativeSites.NativeSiteSpan[] Spans() => new[]
        {
            new NativeSites.NativeSiteSpan(QueryRva, SpanLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(AttackRva, SpanLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(ArrowRva, SpanLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(ArrowGateRva, ArrowGateLength, NativeSites.NativeSiteMode.Observe)
        };

        // Null clears every type. Written at mission start and end, outside running simulation updates.
        internal static void SetTypes(byte[] flags)
        {
            if (!IsAvailable) return;
            var target = new Span<byte>((void*)ownTypes, HunterNativeAssembly.TypeCount);
            if (flags == null) target.Clear();
            else new ReadOnlySpan<byte>(flags, 0, HunterNativeAssembly.TypeCount).CopyTo(target);
        }

        private static ulong Address(int rva) => HunterNativeSites.Image + (uint)rva;
        private static ulong Table => unchecked((ulong)ownTypes.ToInt64());

        private static void GenerateQuery(Assembler a, ReadOnlySpan<Instruction> original) =>
            HunterNativeAssembly.EmitOwnerQuery(a, original, Table, Address(QueryRejectRva));

        private static void GenerateAttack(Assembler a, ReadOnlySpan<Instruction> original) =>
            HunterNativeAssembly.EmitOwnerAttack(a, original, Table, Address(AttackRejectRva));

        private static void GenerateArrow(Assembler a, ReadOnlySpan<Instruction> original) =>
            HunterNativeAssembly.EmitOwnerArrow(a, original, Table, Address(ArrowContinueRva), Address(ArrowMissRva),
                ProjectileSourceDisplacement, ProjectileHeightDisplacement);

        private static void Verify(bool installed)
        {
            HunterNativeSites.Verify(QueryHandle, QueryRva, SpanLength, QueryBytes, GenerateQuery, installed);
            HunterNativeSites.Verify(AttackHandle, AttackRva, SpanLength, AttackBytes, GenerateAttack, installed);
            HunterNativeSites.Verify(ArrowHandle, ArrowRva, SpanLength, ArrowBytes, GenerateArrow, installed);
        }
    }
}
