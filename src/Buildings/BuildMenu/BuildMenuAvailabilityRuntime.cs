using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Wildlife;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;

namespace APIShared.Buildings
{
    // Build-menu availability (static audit, hash FBCB9319, 2026-11-14; see UpdateToNewDLL.md "Build-menu
    // availability"). 0xFFC70(record, mapper) is Vanilla's only availability query: the export
    // DLL_IsMapperAvailable (0x84A20, managed EngineInterface.IsMapperAvailable under the engine lock, used
    // by the build menu, hotkeys and placement), 0xC2030 (mappers 0x60/0x61) and the UI state producer
    // 0x19D960 (DLL_RunTick, recruitment mappers). All pass the global view record 0x32C7730. One purely
    // native inline stub wraps the entry [0xFFC70,0xFFC7E) and reads one decision byte per mapper; no managed
    // transition. Installed on the first registration inside NativeInlinePatchWindow on the Unity thread:
    // outside a mission no simulation calls the function, and the Unity thread, the only other caller, is the
    // one writing. Never unpatched; an all-zero table is Vanilla.
    internal static unsafe class BuildMenuAvailabilityRuntime
    {
        internal const string SiteGroup = "Buildings.MapperAvailability";
        internal const int EntryRva = 0xFFC70, EntryLength = 14;
        private const string Label = "Mapper availability entry";

        // lea eax,[rdx-0xB2]; mov r8,rcx; cmp eax,1; jbe 0xFFC92
        internal static readonly byte[] EntryBytes = WildlifeInlineSite.Hex("8D824EFFFFFF 4C8BC1 83F801 7614");
        // cmp edx,0x35; je 0xFFC92
        internal static readonly byte[] EntryContinuation = WildlifeInlineSite.Hex("83FA35 740F");
        // Final branch: cmp edx,0x1B7; ja 0xFFD84; movsxd rax,edx; movsx eax,word ptr [r8+rax*2+0x22C]; ret
        private static readonly (int Rva, byte[] Bytes)[] Anchors =
        {
            (0xFFF92, WildlifeInlineSite.Hex("81FAB7010000 0F87E6FDFFFF 4863C2 410FBF84402C020000 C3")),
            // DLL_IsMapperAvailable: mov edx,ecx; lea rcx,[0x32C7730]; jmp 0xFFC70
            (0x84A20, WildlifeInlineSite.Hex("8BD1 488D0D072D2403 E942B20700"))
        };

        private static readonly object Gate = new object();
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> EntryHandle = new HookHandle<X64InlineHook>();
        // Never freed: the published stub reads it until the process ends.
        private static readonly IntPtr table = Marshal.AllocHGlobal(BuildMenuAvailabilityAssembly.MapperCapacity);
        private static BuildMenuAvailabilityRule[] rules = Array.Empty<BuildMenuAvailabilityRule>();
        private static ManualLogSource log;
        private static CrusaderLibraryLoadContext loadContext;
        private static ulong image;
        private static int validated;
        private static bool attempted;
        private static string unavailable = "Build-menu availability has not been initialized.";

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
            new NativeSites.NativeSiteSpan(EntryRva, EntryLength, NativeSites.NativeSiteMode.Exclusive)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            log = logger;
            new Span<byte>((void*)table, BuildMenuAvailabilityAssembly.MapperCapacity).Clear();
            try
            {
                if (!string.Equals(hash, HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Build-menu availability is audited only for native image " + HuntablePreyNativeContract.AuditedHash + ".");
                if (context == null) throw new InvalidOperationException("The native load image is unavailable.");
                foreach (var anchor in Anchors)
                    if (!NativeInlineStub.SnapshotMatches(context.Memory, anchor.Rva, anchor.Bytes))
                        throw new InvalidOperationException("The mapper availability code at RVA 0x" + anchor.Rva.ToString("X") + " differs from the audit.");
                loadContext = context;
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                Volatile.Write(ref validated, 1);
                NativeApiLog.Info(log, "Build-menu availability ready: mapper query 0x" + EntryRva.ToString("X") + " installs on the first rule registration.");
            }
            catch (Exception error)
            {
                unavailable = "Build-menu availability is unavailable: " + error.Message;
                NativeApiLog.Error(log, unavailable);
            }
        }

        internal static bool TryRegister(string owner, string id, out BuildMenuAvailabilityRule rule, out string reason)
        {
            rule = null;
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Build-menu rules must register on the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID and registration ID are required."; return false; }
            lock (Gate)
            {
                foreach (var old in rules)
                    if (StringComparer.Ordinal.Equals(old.OwnerGuid, owner) && StringComparer.Ordinal.Equals(old.RegistrationId, id))
                    { reason = "This owner already registered that build-menu rule ID."; return false; }
                if (!TryEnsureInstalled(out reason)) return false;
                rule = new BuildMenuAvailabilityRule(owner, id);
                var next = new BuildMenuAvailabilityRule[rules.Length + 1];
                Array.Copy(rules, next, rules.Length);
                next[next.Length - 1] = rule;
                rules = next;
            }
            NativeApiLog.Info(log, "Build-menu rule registered: owner=" + owner + ", id=" + id + ".");
            reason = "";
            return true;
        }

        internal static bool TrySet(BuildMenuAvailabilityRule rule, int mapper, BuildMenuAvailabilityDecision decision, out string reason)
        {
            if (rule == null) { reason = "The rule is required."; return false; }
            if (mapper < 1 || mapper > BuildMenuAvailability.MaximumMapper)
            { reason = "The mapper must be from 1 through " + BuildMenuAvailability.MaximumMapper + "."; return false; }
            if (decision != BuildMenuAvailabilityDecision.Default && decision != BuildMenuAvailabilityDecision.Allow &&
                decision != BuildMenuAvailabilityDecision.Veto)
            { reason = "Unknown decision."; return false; }
            lock (Gate)
            {
                Volatile.Write(ref rule.Decisions[mapper], (byte)decision);
                Recompute(mapper);
            }
            reason = "";
            return true;
        }

        internal static void SetEnabled(BuildMenuAvailabilityRule rule, bool value)
        {
            if (rule == null) return;
            lock (Gate)
            {
                if (!rule.SetEnabledFlag(value)) return;
                for (int mapper = 1; mapper < BuildMenuAvailabilityAssembly.MapperCapacity; mapper++)
                    if (rule.Decisions[mapper] != 0) Recompute(mapper);
            }
        }

        internal static void Clear(BuildMenuAvailabilityRule rule)
        {
            if (rule == null) return;
            lock (Gate)
                for (int mapper = 1; mapper < BuildMenuAvailabilityAssembly.MapperCapacity; mapper++)
                {
                    if (rule.Decisions[mapper] == 0) continue;
                    Volatile.Write(ref rule.Decisions[mapper], (byte)0);
                    Recompute(mapper);
                }
        }

        // Callers hold Gate. One byte write: the native reader sees the old or the new decision.
        private static void Recompute(int mapper)
        {
            byte combined = 0;
            foreach (var rule in rules)
            {
                if (!rule.IsEnabled) continue;
                byte decision = rule.Decisions[mapper];
                if (decision == BuildMenuAvailabilityAssembly.Veto) { combined = decision; break; }
                if (decision == BuildMenuAvailabilityAssembly.Allow) combined = decision;
            }
            Volatile.Write(ref *((byte*)table + mapper), combined);
        }

        // Callers hold Gate. One attempt per process (fail closed).
        private static bool TryEnsureInstalled(out string reason)
        {
            if (Publication.IsReady) { reason = ""; return true; }
            if (Volatile.Read(ref validated) == 0) { reason = unavailable; return false; }
            if (attempted) { reason = unavailable; return false; }
            if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
            attempted = true;
            var group = new NativeInlineStubGroup(SiteGroup, Publication);
            try
            {
                WildlifeInlineSite.CheckSite(loadContext, image, EntryRva, EntryBytes, EntryContinuation, Label);
                WildlifeInlineSite.ProbeLength(image, EntryRva, EntryLength, Label);
                group.Claim(Spans());
                group.Begin(loadContext.Region);
                group.Add(EntryHandle, image + EntryRva, GenerateEntry, EntryLength);
                var result = group.Commit("Mapper availability preparation was incomplete.");
                Verify(false);
                group.Publish(result, typeof(BuildMenuAvailabilityRuntime), () => Verify(true));
                NativeApiLog.Info(log, "Build-menu availability published: entry=0x" + EntryRva.ToString("X") + "; native per-mapper decision table.");
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                unavailable = "Build-menu availability is unavailable: " + error.Message;
                group.RollbackUnpublished(null);
                NativeApiLog.Error(log, unavailable);
                reason = unavailable;
                return false;
            }
        }

        private static void GenerateEntry(Assembler a, ReadOnlySpan<Instruction> original, ulong back) =>
            BuildMenuAvailabilityAssembly.EmitEntry(a, original, back, unchecked((ulong)table.ToInt64()));

        private static void Verify(bool installed) =>
            NativeInlineStub.Verify(EntryHandle, image + EntryRva, EntryLength, installed,
                stub => NativeInlineStub.ExpectedStub(image + EntryRva, EntryBytes, EntryLength, GenerateEntry, stub, Label + " boundary differs."),
                check => Label + " " + check + " differs at 0x" + EntryRva.ToString("X") + ".");
    }
}
