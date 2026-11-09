using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Two permanent, purely native stubs (no managed transition) in the Hunter code, hash FBCB9319:
    //  - Query entry [0x18AF50,0x18AF5E): records the querying unit id (EDX) and the caller's return
    //    address for the Hunter-query identity fix and target policies. No inbound branch; RAX/R10 and
    //    the flags are free at a function entry. Always active: the record is correct for every caller.
    //  - Corpse arrival [0x1306AE,0x1306BD) in HunterUpdate state 0xB: every butchered animal passes it
    //    exactly once, right after the corpse's global id matched the Hunter's target (0x1306A4). Records
    //    Hunter global, corpse global (EAX) and corpse type per Hunter id (RBX). Gated by a demand byte;
    //    flags are dead at entry because the replayed cmp sets them again.
    // Both are claimed under one APIShared native-site group and never unpatched.
    internal static unsafe class HunterNativeSites
    {
        internal const string SiteGroup = "Wildlife.HunterSites";
        internal const int QueryEntryRva = 0x18AF50, QueryEntryLength = 14;
        internal const int CorpseArrivalRva = 0x1306AE, CorpseArrivalLength = 15;
        // Return address of the only non-simulation caller: 0x1940C0 (idle reason of the selected
        // unit for its speech) calls the query at 0x1941B4.
        internal const int PresentationQueryReturnRva = 0x1941B9;
        internal const int UnitCapacity = HunterNativeAssembly.UnitCapacity;
        internal const int EntrySize = HunterNativeAssembly.EntrySize;

        private static readonly byte[] QueryEntryBytes =
            Hex("48895C2418 55 56 57 4154 4155 4156");
        private static readonly byte[] QueryEntryContinuation = Hex("4157 4883EC40");
        // cmp word ptr [rcx+r14+0x6E6], 0x2C; mov eax, 0x3E8
        private static readonly byte[] CorpseArrivalBytes = Hex("664283BC31E60600002C B8E8030000");
        // mov word ptr [rcx+r14+0x920], ax
        private static readonly byte[] CorpseArrivalContinuation = Hex("664289843120090000");

        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> QueryHandle = new HookHandle<X64InlineHook>();
        private static readonly HookHandle<X64InlineHook> CorpseHandle = new HookHandle<X64InlineHook>();
        private static ulong image;
        private static CrusaderLibraryLoadContext loadContext;
        // Never freed: published stubs write them until the process ends.
        private static readonly IntPtr querySlot = Marshal.AllocHGlobal(16);
        private static readonly IntPtr demand = Marshal.AllocHGlobal(8);
        private static readonly IntPtr table = Marshal.AllocHGlobal(UnitCapacity * EntrySize);
        // Per unit type: RemoveCorpseFlag marks the corpse for deletion when a Hunter starts butchering it.
        private static readonly IntPtr corpseFlags = Marshal.AllocHGlobal(HunterNativeAssembly.TypeCount);
        private static string diagnostic = "Hunter native sites have not initialized.";

        internal static bool IsAvailable => Publication.IsReady;
        internal static string Diagnostic => diagnostic;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (Publication.HasStarted) { if (IsAvailable) return; throw new InvalidOperationException(diagnostic); }
            new Span<byte>((void*)querySlot, 16).Clear();
            Marshal.WriteInt64(demand, 0);
            ClearTable();
            SetCorpseFlags(null);
            var group = new NativeInlineStubGroup(SiteGroup, Publication);
            try
            {
                if (!string.Equals(hash, HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Hunter native sites are audited only for native image " + HuntablePreyNativeContract.AuditedHash + ".");
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                loadContext = context;
                CheckSite(context, QueryEntryRva, QueryEntryBytes, QueryEntryContinuation);
                CheckSite(context, CorpseArrivalRva, CorpseArrivalBytes, CorpseArrivalContinuation);
                ProbeLength(QueryEntryRva, QueryEntryLength);
                ProbeLength(CorpseArrivalRva, CorpseArrivalLength);
                group.Claim(Spans());
                group.Begin(context.Region);
                group.Add(QueryHandle, image + QueryEntryRva, (a, original, back) => GenerateQueryEntry(a, original), QueryEntryLength);
                group.Add(CorpseHandle, image + CorpseArrivalRva, (a, original, back) => GenerateCorpseArrival(a, original), CorpseArrivalLength);
                var result = group.Commit("Hunter native site preparation was incomplete.");
                Verify(false);
                group.Publish(result, typeof(HunterNativeSites), () => Verify(true));
                diagnostic = "";
                NativeApiLog.Info(log, "Hunter native sites published: queryEntry=0x" + QueryEntryRva.ToString("X") +
                    ", corpseArrival=0x" + CorpseArrivalRva.ToString("X") + "; native records only, no managed transition.");
            }
            catch (Exception error)
            {
                diagnostic = error.Message;
                group.RollbackUnpublished(null);
                throw;
            }
        }

        internal static NativeSites.NativeSiteSpan[] Spans() => new[]
        {
            new NativeSites.NativeSiteSpan(QueryEntryRva, QueryEntryLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(CorpseArrivalRva, CorpseArrivalLength, NativeSites.NativeSiteMode.Exclusive)
        };

        // The unit id of the Hunter query currently running on this (simulation) thread, and whether it
        // runs for the simulation. Valid only inside that query, e.g. in OnUnitHunterQueryTarget.
        internal static bool TryGetCurrentQuery(out int queryingUnitId, out bool isSimulation)
        {
            queryingUnitId = Marshal.ReadInt32(querySlot);
            ulong returnAddress = unchecked((ulong)Marshal.ReadInt64(querySlot, 8));
            isSimulation = returnAddress != image + PresentationQueryReturnRva;
            return IsAvailable && queryingUnitId > 0 && queryingUnitId < UnitCapacity;
        }

        internal static void SetCaptureDemand(bool value) => Marshal.WriteByte(demand, value ? (byte)1 : (byte)0);

        internal static void ClearTable() => new Span<byte>((void*)table, UnitCapacity * EntrySize).Clear();

        // Null clears every type. Written outside running simulation updates (mission start and end).
        internal static void SetCorpseFlags(byte[] flags)
        {
            var target = new Span<byte>((void*)corpseFlags, HunterNativeAssembly.TypeCount);
            if (flags == null) target.Clear();
            else new ReadOnlySpan<byte>(flags, 0, HunterNativeAssembly.TypeCount).CopyTo(target);
        }

        internal static bool TryGetButchered(int hunterUnitId, out uint hunterGlobal, out uint corpseGlobal, out int corpseType)
        {
            hunterGlobal = corpseGlobal = 0; corpseType = 0;
            if (hunterUnitId <= 0 || hunterUnitId >= UnitCapacity) return false;
            byte* entry = (byte*)table + hunterUnitId * EntrySize;
            if (Volatile.Read(ref *(int*)(entry + 12)) == 0) return false;
            hunterGlobal = *(uint*)entry; corpseGlobal = *(uint*)(entry + 4); corpseType = *(int*)(entry + 8);
            return true;
        }

        internal static void SetButchered(int hunterUnitId, uint hunterGlobal, uint corpseGlobal, int corpseType)
        {
            if (hunterUnitId <= 0 || hunterUnitId >= UnitCapacity) return;
            byte* entry = (byte*)table + hunterUnitId * EntrySize;
            *(uint*)entry = hunterGlobal; *(uint*)(entry + 4) = corpseGlobal; *(int*)(entry + 8) = corpseType;
            Volatile.Write(ref *(int*)(entry + 12), 1);
        }

        internal static void GenerateQueryEntry(Assembler a, ReadOnlySpan<Instruction> original) =>
            HunterNativeAssembly.EmitQueryEntry(a, original, unchecked((ulong)querySlot.ToInt64()));

        internal static void GenerateCorpseArrival(Assembler a, ReadOnlySpan<Instruction> original) =>
            HunterNativeAssembly.EmitCorpseArrival(a, original, unchecked((ulong)demand.ToInt64()), unchecked((ulong)table.ToInt64()),
                unchecked((ulong)corpseFlags.ToInt64()));

        internal static ulong Image => image;
        internal static CrusaderLibraryLoadContext LoadContext => loadContext;

        private const string Label = "Hunter native site";

        internal static void CheckSite(CrusaderLibraryLoadContext context, int rva, byte[] bytes, byte[] continuation) =>
            WildlifeInlineSite.CheckSite(context, image, rva, bytes, continuation, Label);

        internal static void ProbeLength(int rva, int length) => WildlifeInlineSite.ProbeLength(image, rva, length, Label);

        private static void Verify(bool installed)
        {
            Verify(QueryHandle, QueryEntryRva, QueryEntryLength, QueryEntryBytes, GenerateQueryEntry, installed);
            Verify(CorpseHandle, CorpseArrivalRva, CorpseArrivalLength, CorpseArrivalBytes, GenerateCorpseArrival, installed);
        }

        internal static void Verify(HookHandle<X64InlineHook> handle, int rva, int length, byte[] bytes, WildlifeInlineSite.Generator generate,
            bool installed) => WildlifeInlineSite.Verify(handle, image, rva, length, bytes, generate, installed, Label);

        internal static byte[] Hex(string text) => WildlifeInlineSite.Hex(text);
    }
}
