using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Sight
{
    // Native side of SightBlockingBuildings (static audit, hash FBCB9319, 2026-11-14; see UpdateToNewDLL.md
    // "Sight-blocking buildings"). One purely native inline stub at the dispatch reader 0x6BA0C of the obstacle-height
    // helper 0x6B990 reads one byte per building type from `overrides` (SightNativeAssembly.EmitDispatchOverride);
    // no managed transition. Validated at LibraryLoaded, installed on the first registration inside
    // NativeInlinePatchWindow, never unpatched; enabling a rule only rewrites its type's byte.
    internal static unsafe class SightBlockingBuildingsNative
    {
        internal const string SiteGroup = "Sight.BuildingHeightDispatch";
        internal const int HelperRva = 0x6B990, ImageBaseLeaRva = 0x6B9F1, TypeSwitchRva = 0x6B9F8;
        internal const int ReaderRva = 0x6BA0C, ReaderLength = 17, ContinuationRva = 0x6BA1D;
        internal const int SpecialCaseRva = 0x6BA3D, OrdinaryCaseRva = 0x6BA41;
        internal const int CaseTargetTableRva = 0x6BAB4, CaseTargetCount = 4, TypeCaseTableRva = 0x6BAC4;
        internal const int HeightTableRva = 0x2E8C60;
        private const string FeatureLabel = "Sight-blocking building dispatch";

        // lea r8,[rip-0x6B9F8] (image base)
        internal static readonly byte[] ImageBaseLeaBytes = NativeInlineStub.Hex("4C8D050846F9FF");
        // movsx r11,word ptr [rcx+r10+0x12E] (building type); lea eax,[r11-7]; cmp eax,0x47; ja 0x6BA41; cdqe
        internal static readonly byte[] TypeSwitchBytes = NativeInlineStub.Hex("4E0FBF9C112E010000418D43F983F84777374898");
        // movzx eax,byte ptr [r8+rax+0x6BAC4]; mov ecx,dword ptr [r8+rax*4+0x6BAB4]
        internal static readonly byte[] ReaderBytes = NativeInlineStub.Hex("410FB68400C4BA0600418B8C80B4BA0600");
        // add rcx,r8; jmp rcx
        internal static readonly byte[] ContinuationBytes = NativeInlineStub.Hex("4903C8FFE1");
        // Case 0: test esi,esi; jne 0x6BA97 (mode 1, the sight core, keeps only the terrain height).
        internal static readonly byte[] SpecialCaseBytes = NativeInlineStub.Hex("85F67556");
        // Case 3: mov r9d,dword ptr [r8+r11*4+0x2E8C60]; jmp 0x6BA97 (the type's building-height table value).
        internal static readonly byte[] OrdinaryCaseBytes = NativeInlineStub.Hex("478B8C98608C2E00EB4C");
        // Case targets 0x6BA3D, 0x6BA30, 0x6BA22, 0x6BA41 as image offsets.
        private static readonly int[] CaseTargets = { SpecialCaseRva, 0x6BA30, 0x6BA22, OrdinaryCaseRva };

        private static readonly object Gate = new object();
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> Handle = new HookHandle<X64InlineHook>();
        // Never freed: the published stub reads it until the process ends.
        private static readonly IntPtr overrides = Marshal.AllocHGlobal(SightNativeAssembly.DispatchTypeCount);
        private static readonly SightBlockingTable table = new SightBlockingTable((byte*)overrides);
        private static readonly List<SightBlockingBuildingRule> rules = new List<SightBlockingBuildingRule>();
        private static readonly Dictionary<SightBlockingBuildingRule, bool> enabled = new Dictionary<SightBlockingBuildingRule, bool>();
        // Vanilla's case byte per dispatched type, read from the validated load image.
        private static readonly byte[] vanillaCases = new byte[SightNativeAssembly.DispatchTypeCount];
        private static ManualLogSource log;
        private static CrusaderLibraryLoadContext loadContext;
        private static ulong image;
        private static int available;
        private static string unavailable = "The sight-blocking building service has not been initialized.";
        private static string installFailure = "";
        private static bool installAttempted;

        internal static bool IsAvailable => Volatile.Read(ref available) != 0;
        internal static string UnavailableReason => IsAvailable ? "" : Volatile.Read(ref unavailable);

        internal static NativeSiteSpan[] Spans() => new[]
        {
            new NativeSiteSpan(ImageBaseLeaRva, TypeSwitchRva + TypeSwitchBytes.Length - ImageBaseLeaRva, NativeSiteMode.Observe),
            new NativeSiteSpan(ReaderRva, ReaderLength, NativeSiteMode.Exclusive),
            new NativeSiteSpan(ContinuationRva, ContinuationBytes.Length, NativeSiteMode.Observe),
            new NativeSiteSpan(CaseTargetTableRva, CaseTargetCount * 4 + SightNativeAssembly.DispatchTypeCount, NativeSiteMode.Observe)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            try
            {
                if (!string.Equals(hash, LineOfSightNative.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Sight-blocking buildings are audited only for native image " + LineOfSightNative.AuditedHash + ".");
                if (context == null || context.ModuleHandle == IntPtr.Zero)
                    throw new InvalidOperationException("The native load image is unavailable.");
                ValidateLoadImage(context.Memory);
                loadContext = context;
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                Volatile.Write(ref available, 1);
                NativeApiLog.Info(log, "Sight-blocking buildings ready: dispatch reader 0x" + ReaderRva.ToString("X") +
                    " installs on the first registration; exempt types: " + ExemptList() + ".");
            }
            catch (Exception error)
            {
                Volatile.Write(ref unavailable, "Sight-blocking buildings are unavailable: " + error.Message);
                NativeApiLog.Error(log, "Sight-blocking buildings unavailable; Vanilla's building sight heights stay: " + error.Message);
            }
        }

        // Every instruction and table the stub relies on, in the unmodified load image.
        private static void ValidateLoadImage(ReadOnlySpan<byte> memory)
        {
            Require(memory, ImageBaseLeaRva, ImageBaseLeaBytes, "image-base lea");
            Require(memory, TypeSwitchRva, TypeSwitchBytes, "type switch");
            Require(memory, ReaderRva, ReaderBytes, "dispatch reader");
            Require(memory, ContinuationRva, ContinuationBytes, "dispatch jump");
            Require(memory, SpecialCaseRva, SpecialCaseBytes, "special case");
            Require(memory, OrdinaryCaseRva, OrdinaryCaseBytes, "ordinary case");
            if (CaseTargetTableRva + CaseTargetCount * 4 > memory.Length || TypeCaseTableRva + SightNativeAssembly.DispatchTypeCount > memory.Length)
                throw new InvalidOperationException(FeatureLabel + " tables lie outside the image.");
            for (int index = 0; index < CaseTargetCount; index++)
                if (BitConverter.ToInt32(memory.Slice(CaseTargetTableRva + index * 4, 4).ToArray(), 0) != CaseTargets[index])
                    throw new InvalidOperationException(FeatureLabel + " case target " + index + " differs from the audit.");
            for (int index = 0; index < SightNativeAssembly.DispatchTypeCount; index++)
            {
                byte value = memory[TypeCaseTableRva + index];
                if (value >= CaseTargetCount)
                    throw new InvalidOperationException(FeatureLabel + " case of type " + (index + SightNativeAssembly.FirstDispatchType) + " is out of range.");
                vanillaCases[index] = value;
            }
        }

        private static void Require(ReadOnlySpan<byte> memory, int rva, byte[] bytes, string name)
        {
            if (!NativeInlineStub.SnapshotMatches(memory, rva, bytes))
                throw new InvalidOperationException(FeatureLabel + " " + name + " at RVA 0x" + rva.ToString("X") + " differs from the audit.");
        }

        private static string ExemptList()
        {
            var names = new List<string>();
            for (int index = 0; index < vanillaCases.Length; index++)
                if (vanillaCases[index] == 0) names.Add(((eStructs)(index + SightNativeAssembly.FirstDispatchType)).ToString());
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }

        private static bool TryIndex(eStructs type, out int index)
        {
            index = (int)type - SightNativeAssembly.FirstDispatchType;
            return index >= 0 && index < SightNativeAssembly.DispatchTypeCount;
        }

        internal static bool IsExemptInVanilla(eStructs type) =>
            IsAvailable && TryIndex(type, out int index) && vanillaCases[index] == 0;

        internal static bool IsBlocking(eStructs type)
        {
            if (!TryIndex(type, out int index)) return false;
            lock (Gate) return table.IsActive(index);
        }

        internal static bool IsEnabled(SightBlockingBuildingRule rule)
        {
            lock (Gate) return rule != null && enabled.TryGetValue(rule, out bool value) && value;
        }

        internal static bool TryRegister(string owner, string id, eStructs type, out SightBlockingBuildingRule rule, out string reason)
        {
            rule = null;
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Sight-blocking buildings must register on the initialized Unity thread."; return false; }
            if (!IsAvailable) { reason = UnavailableReason; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID and registration ID are required."; return false; }
            if (!IsExemptInVanilla(type))
            { reason = "Building type " + type + " is not exempt from Vanilla's sight test; it already blocks by its own rule."; return false; }
            lock (Gate)
            {
                foreach (var old in rules)
                    if (StringComparer.Ordinal.Equals(old.OwnerGuid, owner) && StringComparer.Ordinal.Equals(old.RegistrationId, id))
                    { reason = "This owner already registered that ID."; return false; }
                if (rules.Count >= SightBlockingBuildings.MaximumRegistrations)
                { reason = "No more sight-blocking building rules can be registered."; return false; }
                if (!TryEnsureInstalled(out reason)) return false;
                rule = new SightBlockingBuildingRule(owner, id, type);
                rules.Add(rule);
                enabled.Add(rule, false);
            }
            NativeApiLog.Info(log, "Sight-blocking building rule registered (disabled): owner=" + owner + ", id=" + id + ", type=" + type + ".");
            return true;
        }

        internal static void SetEnabled(SightBlockingBuildingRule rule, bool value)
        {
            if (rule == null || !TryIndex(rule.BuildingType, out int index)) return;
            bool changed;
            lock (Gate)
            {
                if (!enabled.TryGetValue(rule, out bool current) || current == value) return;
                enabled[rule] = value;
                changed = table.Change(index, value, SightNativeAssembly.OrdinaryHeightCase);
            }
            if (changed)
                NativeApiLog.Info(log, "Sight-blocking building " + rule.BuildingType + " now " + (IsBlocking(rule.BuildingType) ? "blocks" : "follows Vanilla") +
                    " (owner=" + rule.OwnerGuid + ", id=" + rule.RegistrationId + ", enabled=" + value + ").");
        }

        // Callers hold Gate. One attempt per process (fail closed).
        private static bool TryEnsureInstalled(out string reason)
        {
            if (Publication.IsReady) { reason = ""; return true; }
            if (installAttempted) { reason = installFailure; return false; }
            if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
            installAttempted = true;
            var group = new NativeInlineStubGroup(SiteGroup, Publication);
            try
            {
                CheckLive();
                int displaced = NativeInlineStub.ProbeDisplacement(image + ReaderRva, ReaderLength);
                if (displaced != ReaderLength)
                    throw new InvalidOperationException(FeatureLabel + " span differs: " + displaced + " bytes.");
                group.Claim(Spans());
                group.Begin(loadContext.Region);
                group.Add(Handle, image + ReaderRva, (a, original, back) => Generate(a, original), ReaderLength);
                var result = group.Commit(FeatureLabel + " preparation was incomplete.");
                Verify(false);
                group.Publish(result, typeof(SightBlockingBuildingsNative), () => Verify(true));
                NativeApiLog.Info(log, FeatureLabel + " published at 0x" + ReaderRva.ToString("X") + " (" + ReaderLength + " bytes); native per-type table.");
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                installFailure = "Sight-blocking buildings are unavailable: " + error.Message;
                group.RollbackUnpublished(null);
                NativeApiLog.Error(log, installFailure);
                reason = installFailure;
                return false;
            }
        }

        // Live: our own span unchanged; another owner's hook may already begin at the following boundary.
        private static void CheckLive()
        {
            var live = new ReadOnlySpan<byte>((void*)(image + ReaderRva), ReaderLength);
            if (!live.SequenceEqual(new ReadOnlySpan<byte>(ReaderBytes)))
                throw new InvalidOperationException(FeatureLabel + " is already patched at 0x" + ReaderRva.ToString("X") + ".");
            var next = new ReadOnlySpan<byte>((void*)(image + ContinuationRva), 6);
            if (!NativeInlineStub.AcceptsLiveBoundary(next, ContinuationBytes))
                throw new InvalidOperationException(FeatureLabel + " continuation changed at 0x" + ContinuationRva.ToString("X") + ".");
        }

        private static void Generate(Assembler a, ReadOnlySpan<Instruction> original) =>
            SightNativeAssembly.EmitDispatchOverride(a, original, unchecked((ulong)overrides.ToInt64()));

        private static void Verify(bool installed) =>
            NativeInlineStub.Verify(Handle, image + ReaderRva, ReaderLength, installed,
                stub => NativeInlineStub.ExpectedStub(image + ReaderRva, ReaderBytes, ReaderLength, (a, original, back) => Generate(a, original),
                    stub, FeatureLabel + " boundary differs."),
                check => FeatureLabel + " " + check + " differs at 0x" + ReaderRva.ToString("X") + ".");
    }

    // Per-type override bytes read by the native stub. A type's byte is the replacement case while at least one
    // enabled rule exists for it, otherwise VanillaCase. Single-byte stores are atomic for the native reader.
    // Not thread-safe; SightBlockingBuildingsNative serializes every call.
    internal sealed unsafe class SightBlockingTable
    {
        private readonly byte* native;
        private readonly int[] counts = new int[SightNativeAssembly.DispatchTypeCount];

        internal SightBlockingTable(byte* native)
        {
            this.native = native;
            new Span<byte>(native, SightNativeAssembly.DispatchTypeCount).Fill(SightNativeAssembly.VanillaCase);
        }

        internal bool IsActive(int index) => (uint)index < (uint)counts.Length && counts[index] > 0;

        // Returns whether the native byte changed.
        internal bool Change(int index, bool enable, byte replacementCase)
        {
            if ((uint)index >= (uint)counts.Length || !enable && counts[index] == 0) return false;
            counts[index] += enable ? 1 : -1;
            byte wanted = counts[index] > 0 ? replacementCase : SightNativeAssembly.VanillaCase;
            if (native[index] == wanted) return false;
            Volatile.Write(ref native[index], wanted);
            return true;
        }
    }
}
