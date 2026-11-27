using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using APIShared.NativeSites;

namespace APIShared.Units.AiMilitary.Quota
{
    // Process-owned. All world data methods run under the caller's engine monitor;
    // native callbacks already run inside that same serialized simulation operation.
    internal static unsafe partial class AiMilitaryQuotaRuntime
    {
        internal const string SiteName = "ai-military-recruitment-quotas";
        private const int StateSize = 64 + 10000 * 4;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ContextCallback(int site, IntPtr context);
        private static readonly ContextCallback Callback = Invoke;
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly AiQuotaSnapshot Snapshot = new AiQuotaSnapshot();
        private static readonly HashSet<int> MarkedSlots = new HashSet<int>();
        private static readonly HookHandle<X64InlineHook>[] Handles = new HookHandle<X64InlineHook>[23];
        private static readonly Instruction[][] Instructions = new Instruction[23][];
        private static IntPtr module, state;
        private static HookTransaction transaction;
        private static Func<AiUnitRef, bool> isAdditional;
        private static ManualLogSource logger;
        private static long generation;
        private static bool naturalFold;
        internal static bool IsReady => Publication.IsReady;
        internal static IntPtr DemandAddress => state;
        internal static bool HasDemand => state != IntPtr.Zero && *(int*)state != 0;
        internal static NativeSiteSpan[] Spans()
        {
            var result = new NativeSiteSpan[AiQuotaNativeContract.Sites.Length];
            for (int i = 0; i < result.Length; i++) { var site = AiQuotaNativeContract.Sites[i]; result[i] = new NativeSiteSpan(site.Rva, site.Bytes.Length, NativeSiteMode.Exclusive); }
            return result;
        }
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log,
            Func<AiUnitRef, bool> additional, Action<AiUnitRef, bool> markProduct)
        {
            Publication.ThrowIfUnavailable(); if (Publication.IsReady) return;
            if (!string.Equals(hash, AiQuotaNativeContract.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AI quota accounting requires the audited image.");
            if (additional == null) throw new ArgumentNullException(nameof(additional));
            AiQuotaNativeContract.Validate(context.Memory);
            module = context.ModuleHandle; logger = log; isAdditional = additional;
            var spans = new NativeSiteSpan[Handles.Length];
            for (int i = 0; i < spans.Length; i++)
            {
                var site = AiQuotaNativeContract.Sites[i];
                VerifyLive(site);
                Instructions[i] = AiQuotaNativeContract.Decode(site.Bytes, Address(site.Rva));
                using (var probe = new X64InlineHook(Address(site.Rva), site.Bytes.Length))
                    if (probe.DisplacedByteCount != site.Bytes.Length) throw new InvalidOperationException("AI quota displacement differs.");
                // Preflight assembly needs only a representable data address; allocate
                // process data after every byte/span/ownership validation has succeeded.
                AiQuotaAssembly.Expected(i, Address(0), 0x123400000UL, CallbackAddress, Address(0) + 0x20000000);
                spans[i] = new NativeSiteSpan(site.Rva, site.Bytes.Length, NativeSiteMode.Exclusive);
            }
            var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, SiteName, spans);
            if (!claim.Succeeded) throw new InvalidOperationException(claim.Reason);
            try
            {
                state = Marshal.AllocHGlobal(StateSize); new Span<byte>((void*)state, StateSize).Clear();
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true });
                for (int i = 0; i < Handles.Length; i++)
                {
                    int index = i; var site = AiQuotaNativeContract.Sites[index]; Handles[index] = new HookHandle<X64InlineHook>();
                    transaction.AddInline(Handles[index], HookTarget.FromAddress(Address(site.Rva)),
                        (a, original, back) => AiQuotaAssembly.Generate(a, original, back, index, Address(0),
                            unchecked((ulong)state.ToInt64()), CallbackAddress), hookSize: site.Bytes.Length);
                }
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess) throw new InvalidOperationException("AI quota preparation was incomplete.");
                for (int i = 0; i < Handles.Length; i++) ValidatePrepared(i);
                NativeHookPublication.Enable(transaction, result, Publication, typeof(AiMilitaryQuotaRuntime), ValidateInstalled);
                Engineering.AiMilitaryEngineeringRuntime.Initialize(context, hash, log, additional, markProduct);
            }
            catch
            {
                Publication.RollbackUnpublished(transaction, () => {
                    NativeSiteRegistry.Unregister(APISharedPlugin.PluginGuid, SiteName);
                    Marshal.FreeHGlobal(state); state = IntPtr.Zero; transaction = null;
                });
                throw;
            }
        }
        private static ulong CallbackAddress => unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Callback).ToInt64());
        private static ulong Address(int rva) => unchecked((ulong)(module.ToInt64() + rva));
        internal static void Clear(long worldGeneration)
        {
            generation = worldGeneration; naturalFold = false; Snapshot.Clear(); MarkedSlots.Clear();
            if (state != IntPtr.Zero) new Span<byte>((void*)state, StateSize).Clear();
            Engineering.AiMilitaryEngineeringRuntime.Clear(worldGeneration);
        }
        internal static void SetAdditional(AiUnitRef unit, bool additional)
        {
            if (unit.UnitId <= 0 || unit.UnitId >= 10000 || unit.GlobalId == 0) throw new ArgumentOutOfRangeException(nameof(unit));
            if (state == IntPtr.Zero) return;
            uint* ids = (uint*)((byte*)state + 64);
            if (additional)
            { ids[unit.UnitId] = unit.GlobalId; MarkedSlots.Add(unit.UnitId); *(int*)state = 1; }
            else if (ids[unit.UnitId] == unit.GlobalId)
            { ids[unit.UnitId] = 0; MarkedSlots.Remove(unit.UnitId); }
            Engineering.AiMilitaryEngineeringRuntime.OnQuotaMarkerChanged(unit, additional);
            // Keep demand after removing the last marker: the native vector and its
            // extra contributions remain the previous fold until the next producer.
        }
        private static void Invoke(int index, IntPtr pointer)
        {
            ulong* c = (ulong*)pointer;
            try
            {
                if (index == 0)
                {
                    // Fixes attaches replacement crews asynchronously. Reconcile only
                    // tracked provenance here, before this exact fold counts the new
                    // machine; no unit fields/orders or whole-world scan are involved.
                    Engineering.AiMilitaryEngineeringRuntime.Reconcile();
                    Snapshot.BeginFold(); naturalFold = true; return;
                }
                if (index == 1)
                {
                    byte* raw = (byte*)c[11]; long delta = (long)raw - (module.ToInt64() + AiQuotaNativeContract.UnitManager);
                    if (delta <= 0 || delta % AiQuotaNativeContract.UnitStride != 0) return;
                    int id = checked((int)(delta / AiQuotaNativeContract.UnitStride));
                    if (id >= 10000) return;
                    var unit = new AiUnitRef(id, *(uint*)(raw + 0x6F0));
                    if (!isAdditional(unit)) return;
                    Snapshot.Count((int)c[5], *(ushort*)(raw + 0x6E6), *(ushort*)(raw + 0xA82)); return;
                }
                if (index == 22)
                {
                    byte* group = (byte*)(c[2] + c[14]);
                    uint regular = (uint)RegularGroup((int)c[4], group, *(short*)(group + 0x5C));
                    c[16] = AiQuotaNativeContract.CompareFlags(c[16], regular, 2); return;
                }
                EmulateOriginal(index, c);
                var site = AiQuotaNativeContract.Sites[index];
                var code = Instructions[index];
                Instruction operand = code[index == 3 ? 5 : 0];
                ulong address = Effective(c, operand);
                int physical = index == 7 ? *(short*)address : *(int*)address;
                int regularCount;
                if (index == 7)
                {
                    long offset = unchecked((long)address) - (module.ToInt64() + 0x7CC6720 + 0x5C);
                    int id = checked((int)(offset / 0x688));
                    regularCount = RegularGroup(id, (byte*)address - 0x5C, physical);
                }
                else
                {
                    long offset = unchecked((long)address) - (module.ToInt64() + AiQuotaNativeContract.Fields[site.Column]);
                    if (offset < 0 || offset % AiQuotaNativeContract.PlayerStride != 0) return;
                    int player = checked((int)(offset / AiQuotaNativeContract.PlayerStride));
                    regularCount = Snapshot.Regular(physical, player, site.Column);
                }
                if (index == 4) c[2] = (uint)regularCount; // MOV ECX, count
                else if (index == 7) c[0] = (uint)regularCount; // MOVSX EAX, physical group count
                else
                {
                    Register right = operand.Op1Register;
                    uint target = (uint)c[RegisterIndex(right)];
                    c[16] = AiQuotaNativeContract.CompareFlags(c[16], (uint)regularCount, target);
                }
            }
            catch (Exception error)
            {
                // Failing callbacks must still emulate the removed instruction. Native
                // count/flags are a conservative fallback, never a zero quota success.
                try { EmulateOriginal(index, c); } catch { }
                Report(error);
            }
        }
        private static void EmulateOriginal(int index, ulong* c)
        {
            if (index < 2) return;
            if (index == 22) { c[16] = AiQuotaNativeContract.CompareFlags(c[16], (uint)*(ushort*)(c[14] + c[2] + 0x5C), 2); return; }
            var site = AiQuotaNativeContract.Sites[index];
            var code = Instructions[index]; var operand = code[index == 3 ? 5 : 0];
            ulong address = Effective(c, operand);
            if (index == 4) c[2] = *(uint*)address;
            else if (index == 7) c[0] = unchecked((uint)*(short*)address);
            else c[16] = AiQuotaNativeContract.CompareFlags(c[16], *(uint*)address, (uint)c[RegisterIndex(operand.Op1Register)]);
        }
        private static ulong Effective(ulong* c, Instruction instruction) => unchecked(
            (instruction.MemoryBase == Register.None ? 0 : c[RegisterIndex(instruction.MemoryBase)]) +
            (instruction.MemoryIndex == Register.None ? 0 : c[RegisterIndex(instruction.MemoryIndex)] * (uint)instruction.MemoryIndexScale) + instruction.MemoryDisplacement64);
        internal static int RegisterIndex(Register register)
        {
            switch (register)
            {
                case Register.RAX: case Register.EAX: return 0; case Register.RBX: case Register.EBX: return 1;
                case Register.RCX: case Register.ECX: return 2; case Register.RDX: case Register.EDX: return 3;
                case Register.RSI: case Register.ESI: return 4; case Register.RDI: case Register.EDI: return 5;
                case Register.RBP: case Register.EBP: return 6; case Register.RSP: case Register.ESP: return 7;
                case Register.R8: case Register.R8D: return 8; case Register.R9: case Register.R9D: return 9;
                case Register.R10: case Register.R10D: return 10; case Register.R11: case Register.R11D: return 11;
                case Register.R12: case Register.R12D: return 12; case Register.R13: case Register.R13D: return 13;
                case Register.R14: case Register.R14D: return 14; case Register.R15: case Register.R15D: return 15;
                default: throw new InvalidOperationException("Unexpected quota register.");
            }
        }
        private static int RegularGroup(int id, byte* group, int physical)
        {
            if (id <= 0 || id >= 4500) return physical;
            uint gid = *(uint*)(group + 0x34); int extra = 0;
            foreach (int slot in MarkedSlots)
            {
                byte* unit = (byte*)(module + AiQuotaNativeContract.UnitManager + slot * AiQuotaNativeContract.UnitStride);
                uint global = *(uint*)(unit + 0x6F0);
                if (*(ushort*)(unit + 0x930) == id && *(uint*)(unit + 0x93C) == gid &&
                    (*(ushort*)(group + 0x60 + (slot >> 4) * 2) & (1 << (slot & 15))) != 0 &&
                    isAdditional(new AiUnitRef(slot, global))) extra++;
            }
            return Math.Max(0, physical - extra);
        }
        internal static byte[] Capture()
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); Snapshot.Write(writer); WriteVector(writer);
                byte[] engineering = Engineering.AiMilitaryEngineeringRuntime.Capture();
                writer.Write(engineering.Length); writer.Write(engineering); return stream.ToArray();
            }
        }
        internal static void Restore(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported quota snapshot schema.");
                AiQuotaSnapshot restored = AiQuotaSnapshot.Read(reader); bool matches = true;
                for (int p = 0; p < AiQuotaSnapshot.Players; p++) for (int c = 0; c < AiQuotaSnapshot.Columns; c++)
                    if (reader.ReadInt32() != ReadCounter(p, c)) matches = false;
                int length = reader.ReadInt32(); if (length < 0 || length > 4 * 1024 * 1024 || length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid engineering history length.");
                byte[] engineering = reader.ReadBytes(length);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing quota snapshot data.");
                Engineering.AiMilitaryEngineeringRuntime.Restore(engineering);
                // A natural fold in this generation supersedes a serialized mirror, even
                // if that fold happens to have exactly the same physical counter vector.
                if (!naturalFold && (state == IntPtr.Zero || *((int*)state + 1) == 0) && matches)
                { Snapshot.Restore(restored); if (restored.HasSnapshot && state != IntPtr.Zero) *(int*)state = 1; }
            }
        }
        private static int ReadCounter(int p, int c) => module == IntPtr.Zero ? 0 :
            *(int*)(module + AiQuotaNativeContract.Fields[c] + p * AiQuotaNativeContract.PlayerStride);
        private static void WriteVector(BinaryWriter writer)
        { for (int p = 0; p < AiQuotaSnapshot.Players; p++) for (int c = 0; c < AiQuotaSnapshot.Columns; c++) writer.Write(ReadCounter(p, c)); }
        private static void VerifyLive(AiQuotaNativeContract.Site site)
        { var bytes = new byte[site.Bytes.Length]; Marshal.Copy(module + site.Rva, bytes, 0, bytes.Length);
            if (!bytes.AsSpan().SequenceEqual(site.Bytes)) throw new InvalidOperationException("AI quota site occupied: 0x" + site.Rva.ToString("X")); }
        private static void ValidatePrepared(int index)
        {
            var site = AiQuotaNativeContract.Sites[index]; var handle = Handles[index]; var hook = handle.Hook;
            if (!handle.Success || hook.IsInstalled || hook.DisplacedByteCount != site.Bytes.Length || hook.TargetAddress != Address(site.Rva) || hook.StubAddress == IntPtr.Zero)
                throw new InvalidOperationException("Prepared quota backend contract differs.");
            byte[] expected = AiQuotaAssembly.Expected(index, Address(0), unchecked((ulong)state.ToInt64()), CallbackAddress, unchecked((ulong)hook.StubAddress.ToInt64()));
            byte[] actual = new byte[expected.Length]; Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Quota emitted stub differs.");
        }
        private static void ValidateInstalled()
        {
            for (int i = 0; i < Handles.Length; i++)
            {
                var site = AiQuotaNativeContract.Sites[i]; var hook = Handles[i].Hook; IntPtr target = module + site.Rva;
                if (!hook.IsInstalled || hook.DisplacedByteCount != site.Bytes.Length || Marshal.ReadByte(target) != 0xFF ||
                    Marshal.ReadByte(target, 1) != 0x25 || Marshal.ReadInt32(target, 2) != 0 || Marshal.ReadIntPtr(target, 6) != hook.StubAddress)
                    throw new InvalidOperationException("Installed quota patch differs.");
                for (int p = 14; p < site.Bytes.Length; p++) if (Marshal.ReadByte(target, p) != 0x90)
                    throw new InvalidOperationException("Installed quota patch padding differs.");
            }
        }
        private static void Report(Exception error)
        { try { Internal.RuntimeHealth.Failure(logger, "ai-military-quota", true, "AI quota callback failed: " + error); } catch { } }
    }
}
