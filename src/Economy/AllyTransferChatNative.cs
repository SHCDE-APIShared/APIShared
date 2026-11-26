using System;
using System.Runtime.InteropServices;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;

namespace APIShared.Economy
{
    internal sealed class AllyTransferChatNative
    {
        internal const string Site = "Economy.AllyTransferChat";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Filter(ulong returnAddress, int category);
        private static readonly Filter Callback = ShouldSuppress;
        private static AllyTransferChatNative instance;
        private readonly ulong image;
        private readonly HookHandle<X64InlineHook> handle = new HookHandle<X64InlineHook>();
        private readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private readonly NativeInlineStubGroup group;
        private readonly ulong callback;
        internal static bool IsAvailable
        {
            get
            {
                var owner = instance;
                // Admission must also fail closed if a later mod replaces our entry.
                // The shared mission-start watcher supplies the detailed conflict log.
                return owner != null && owner.publication.IsReady &&
                    NativeInlineStub.JumpMatches((IntPtr)(long)owner.handle.Hook.TargetAddress, owner.handle.Hook.StubAddress);
            }
        }
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(AllyTransferChatAssembly.Rva, AllyTransferChatAssembly.Length, NativeSiteMode.Exclusive),
            new NativeSiteSpan(AllyTransferChatAssembly.Rva + AllyTransferChatAssembly.Length,
                AllyTransferChatAssembly.Body.Length - AllyTransferChatAssembly.Length, NativeSiteMode.Observe) };
        private AllyTransferChatNative(IntPtr module)
        {
            image = unchecked((ulong)module.ToInt64()); group = new NativeInlineStubGroup(Site, publication);
            callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Callback).ToInt64());
        }
        internal static void Initialize(IntPtr module, ScanRegion region, ReadOnlySpan<byte> memory, string hash, ManualLogSource log)
        {
            if (instance != null) return;
            var candidate = new AllyTransferChatNative(module); instance = candidate;
            try
            {
                if (!string.Equals(hash, AllyTransfersNative.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Gift chat suppression requires the audited native image.");
                AllyTransferChatAssembly.ValidatePristine(memory);
                candidate.Install(region);
                NativeApiLog.Info(log, "Gift chat suppression prepared: reference-rva=0x1C8D0 span=14; exact gift returns D7C80/D7CB7; process lifetime, initially dormant.");
            }
            catch (Exception error)
            {
                candidate.group.RollbackUnpublished(null);
                NativeApiLog.Error(log, "Gift chat suppression unavailable; ordinary transfers and Vanilla messages retained: " + error);
            }
        }
        private void Generate(Iced.Intel.Assembler a, ReadOnlySpan<Iced.Intel.Instruction> original, ulong back) =>
            AllyTransferChatAssembly.Generate(a, original, back, unchecked((ulong)AllyTransferChatScope.DemandAddress.ToInt64()), callback);
        private byte[] Expected(ulong stub) => NativeInlineStub.ExpectedStub(image + AllyTransferChatAssembly.Rva,
            new ReadOnlySpan<byte>(AllyTransferChatAssembly.Body, 0, AllyTransferChatAssembly.Length).ToArray(),
            AllyTransferChatAssembly.Length, Generate, stub, "Gift chat instruction boundary differs.");
        private void Install(ScanRegion region)
        {
            Expected(image + NativeInlineStub.PreflightStubOffset);
            group.Claim(Spans()); group.Begin(region);
            group.Add(handle, image + AllyTransferChatAssembly.Rva, Generate, AllyTransferChatAssembly.Length);
            var result = group.Commit("Gift chat hook preparation incomplete.");
            if (!NativeInlineStub.IsPrepared(handle, false, image + AllyTransferChatAssembly.Rva, AllyTransferChatAssembly.Length) ||
                !NativeInlineStub.StubMatches(handle.Hook.StubAddress, Expected(unchecked((ulong)handle.Hook.StubAddress.ToInt64()))))
                throw new InvalidOperationException("Prepared gift chat stub differs.");
            group.Publish(result, this, () => {
                if (!NativeInlineStub.IsPrepared(handle, true, image + AllyTransferChatAssembly.Rva, AllyTransferChatAssembly.Length) ||
                    !NativeInlineStub.JumpMatches((IntPtr)(long)handle.Hook.TargetAddress, handle.Hook.StubAddress) ||
                    !NativeInlineStub.PaddingMatches((IntPtr)(long)handle.Hook.TargetAddress, AllyTransferChatAssembly.Length))
                    throw new InvalidOperationException("Installed gift chat hook differs.");
            });
        }
        private static int ShouldSuppress(ulong returnAddress, int category)
        {
            var owner = instance;
            return owner != null && owner.publication.IsReady && AllyTransferChatScope.IsSuppressed &&
                AllyTransferChatAssembly.IsGiftReturn(returnAddress, category, owner.image) ? 1 : 0;
        }
    }
}
