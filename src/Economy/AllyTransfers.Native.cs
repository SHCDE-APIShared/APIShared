using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.NativeSites;
using APIShared.Simulation;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // SE is the only native gift owner. APIShared replaces its one managed
    // Original.Invoke callsite, after SE Pre and before the unmodified SE Post.
    internal static unsafe class AllyTransfersNative
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const string SiteName = "Economy.AllyTransfers";
        internal const int TransferRva = 0xD7AD0, CapacityRva = 0xC5AD0;
        internal static readonly byte[] TransferBytes = Convert.FromBase64String(
            "QFVWQVRIg+xASIlcJGBFM+RIiXwkaEGL+EyJbCRwTIl0JDhEi/JJY+lMiXwkMEyL/Uhj8YP6D3VPTWnXPFgAAEyNJflGWQNNA9RMjS3/x0kIRTmCzPASAEEPTLrM8BIAQQG8tcCtJwBIacY8WAAAQgG8IMzwEgBBKbrM8BIAQQG8reStJwDreEGD/ghIjQ1NUD8GuwcAAABEi89BD0Xei9ZEi8PoN939/0SLz0SJZCQgRIvDSI0NJVA/BovV6O4B/v9MjSV3RlkDRIvPSYvMRIvDi9bo52///0yNLXDHSQhEi89BAYS1wK0nAESLw4vWSYvM6Mhv//9BAYSt5K0nADLbOzWYwYAIdW//FXA3EwBIjQ0ZhPL/OYSpkFuQAHNZswH/FVg3EwAFMHUAAEiNFfyD8v+JhKqQW5AAQY1G+oP4EnckSJgPtoQCKH0NAIuMghx9DQBIA8r/4bokAAAA6wy6JwAAAOsFuiMAAABIjQ1MyD4G6Pes8v+LBUHPSQiFwHRkg/hjdF87NRLBgAh1M4TbdC9EiXQkKESLzUSLxol8JCC6CgAAAEmLzehQTPT/SI0VGaUTAEiNDUL2gAjoXXHz/zst18CACHUcRIl0JChEi85Ei8WJfCQgug8AAABJi83oGUz0/0yLbCRwSIt8JGhIi1wkYElpxx4sAABMi3wkMEgDxkiNDABCD7+EIeQjEwBEO/BMi3QkOHUUM8BmQomEIeQjEwBmQomEIfgjEwBEi8VIjQ1GTPcDi9ZIg8RAQVxeXelXe/b/");
        internal static readonly byte[] CapacityBytes = Convert.FromBase64String(
            "iVQkEFNXQVVBV0iD7DhNY+iL+kGL1UyL+bv/////6D2i//+JRCR4i9CD+Ch1ELhAQg8ASIPEOEFfQV1fW8NIiWwkYL0BAAAAQTlvUA+O2wAAAEiJdCQwSI0NcgQiAEyJZCQoSY23XgQAAEyJdCQgTo0kqU2Nt6gEAABPjTSuZpBmg376AnV9D78GO8d1dg+/Tvw7ynVuM/+D+/8PRfuD+Qt1FkWLxYvVSYvP6EAUAACLVCR4jRwH60eD+RN1HEG5+gAAAEWLxYvVSYvP6M94//+LVCR4jRwH6yaDvq4AAAAAdBdBiwaL34XAdBRBiwwkO8F9DIvZK9jrBEGLHCQD34t8JGj/xUiBxiwDAABJgcYsAwAAQTtvUA+MYv///0yLdCQgTItkJChIi3QkMEiLbCRgi8NIg8Q4QV9BXV9bww==");

        internal delegate void TransferCall(int recipient, int good, int amount, int sender);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CapacityCall(IntPtr buildings, int recipient, int good);
        internal static readonly IAllyTransfersBackend EngineBackend = new Backend();
        private static readonly AllyTransfersEngine admission = new AllyTransfersEngine(EngineBackend, () => SimulationMutationScope.IsActive);
        private static ILHook hook;
        private static AllyTransfersSeBridge bridge;
        private static CapacityCall capacity;
        private static IntPtr module;
        private static string hash;
        private static ManualLogSource logger;
        private static Exception startupFailure;
        private static int initialized, ready, callFailures;
        internal static bool IsAvailable => Volatile.Read(ref ready) != 0 && Volatile.Read(ref startupFailure) == null;
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(TransferRva, TransferBytes.Length, NativeSiteMode.CallOnly),
            new NativeSiteSpan(CapacityRva, CapacityBytes.Length, NativeSiteMode.Observe) };

        internal static void Initialize(IntPtr moduleHandle, ScanRegion region, ReadOnlySpan<byte> pristine,
            string binaryHash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            {
                if (module != moduleHandle || !string.Equals(hash, binaryHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ally transfers cannot change their process image.");
                RequireReady(); return;
            }
            module = moduleHandle; hash = binaryHash; logger = log;
            bool applying = false, claimed = false;
            NativeOwnershipRegistry sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            try
            {
                if (module == IntPtr.Zero || region == null || !string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ally transfers require the audited native image and LibraryLoaded initialization.");
                ValidatePristine(pristine);
                // The SE gift entry is already patched; only the independent capacity body
                // must still match live. Original gift provenance is checked through SE's owner.
                VerifyBytes(module + CapacityRva, CapacityBytes);
                var method = AllyTransfersSeContract.Resolve();
                AllyTransfersSeContract.Validate(method);
                AllyTransfersSeContract.ValidateNativeOwner(method, module + TransferRva);
                var entry = (AllyTransferSeEntry)Delegate.CreateDelegate(typeof(AllyTransferSeEntry), method);
                var forward = AllyTransfersSeContract.CreateForwarder(AllyTransfersSeContract.OriginalDelegateType(method));
                var candidateBridge = new AllyTransfersSeBridge(entry, forward, ReadStock,
                    admission.IsStillAdmissible, AllyTransferEvents.Registry);
                using (var definition = new DynamicMethodDefinition(method))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(context => AllyTransfersSeContract.Apply(context, candidateBridge.Invoke, candidateBridge.EnterWrapper, candidateBridge.LeaveWrapper));
                    definition.Generate();
                }
                ApiSharedNativeSites.Claim(sites, SiteName, Spans(), message => new InvalidOperationException(message));
                claimed = true;
                capacity = (CapacityCall)Marshal.GetDelegateForFunctionPointer(module + CapacityRva, typeof(CapacityCall));
                bridge = candidateBridge;
                hook = new ILHook(method, il => AllyTransfersSeContract.Apply(il, Invoke, candidateBridge.EnterWrapper, candidateBridge.LeaveWrapper),
                    new ILHookConfig { ManualApply = true, ID = "APIShared.Economy.AllyTransfers.SEOriginal" });
                applying = true; // Root before publication; never undo a published candidate.
                hook.Apply();
                if (!hook.IsApplied) throw new InvalidOperationException("The SE ally-transfer managed adapter did not apply.");
                Volatile.Write(ref ready, 1);
                NativeApiLog.Info(log, "Ally transfers prepared: SE is native owner at 0xD7AD0; one validated managed Original.Invoke adapter; capacity reference-rva=0xC5AD0.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref startupFailure, error);
                if (!applying)
                {
                    hook?.Dispose(); hook = null; bridge = null; capacity = null;
                    if (claimed) ApiSharedNativeSites.Release(sites, SiteName);
                }
                NativeApiLog.Error(log, "Ally transfers unavailable; the existing SE native gift owner is retained: " + error);
                throw;
            }
        }
        private static int Invoke(Delegate original, int recipient, eGoods good, int amount, int sender)
        {
            // A partially published adapter never activates policies, but still has its exact
            // prepared forwarding thunk. The SE chain remains executable for process lifetime.
            return bridge.Invoke(original, recipient, good, amount, sender);
        }
        internal static void ValidatePristine(ReadOnlySpan<byte> memory)
        {
            VerifyImageFunction(memory, TransferRva, TransferBytes);
            VerifyImageFunction(memory, CapacityRva, CapacityBytes);
            if ((int)eGoods.STORED_GOLD != 15 || (int)eGoods.Count != 25)
                throw new InvalidOperationException("The native goods enum contract changed.");
        }
        private static void VerifyImageFunction(ReadOnlySpan<byte> memory, int rva, byte[] expected)
        {
            if (rva < 0 || rva > memory.Length - expected.Length || !memory.Slice(rva, expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("The pristine ally-transfer body changed at RVA 0x" + rva.ToString("X") + ".");
        }
        internal static void VerifyBytes(IntPtr target, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i]) throw new InvalidOperationException("The native ally-transfer capacity body changed or is occupied.");
        }
        internal static void RequireReady()
        {
            if (!IsAvailable) throw new InvalidOperationException("Ally transfers are not initialized or startup validation failed.", Volatile.Read(ref startupFailure));
        }
        private static bool ReadStock(int player, eGoods good, out int amount)
        {
            amount = 0;
            if (!AllyTransfersEngine.ValidPlayer(player) || good <= eGoods.STORED_NULL || good >= eGoods.Count ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(player, out GamePlayerResources* resource) || resource == null) return false;
            amount = GamePlayerManagerAPI.Instance.GetGoodAmount(player, good); return true;
        }
        internal static void ReportCallFailure(Exception error)
        {
            int count = Interlocked.Increment(ref callFailures);
            if ((count & (count - 1)) == 0)
                try { NativeApiLog.Error(logger, "Ally-transfer call-chain failure; no retry or rollback: " + error); } catch { }
        }
        private sealed class Backend : IAllyTransfersBackend
        {
            public bool IsReady => IsAvailable;
            public bool AreAlliedLiving(int sender, int recipient) => Living(sender) && Living(recipient) && GamePlayerManagerAPI.Instance.IsPlayerAlliedTo(sender, recipient);
            private static bool Living(int player)
            {
                if (!GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(player, out GamePlayerResources* resource) || resource == null ||
                    resource->r_WinLossState != WinLossState.None || resource->r_LordUnitId == 0 || resource->r_LordUnitId > int.MaxValue) return false;
                return UnitAccess.TryGetById(GameUnitManagerAPI.Instance, (int)resource->r_LordUnitId, out GameUnit* unit, out _) &&
                    UnitAccess.IsReallyAlive(unit) && unit->r_UnitChimp == eChimps.CHIMP_TYPE_LORD && unit->r_CurrentHealth > 0 &&
                    unit->r_ControllableForPlayerId == player && unit->r_GlobalId != 0;
            }
            public bool TryReadStock(int player, eGoods good, out int amount) => ReadStock(player, good, out amount);
            public int GetFreeCapacity(int recipient, eGoods good)
            {
                RequireReady(); var buildings = GameBuildingManagerAPI.Instance.GetBuildingManager();
                if (buildings.Pointer == null) throw new InvalidOperationException("The native building manager is unavailable.");
                return capacity((IntPtr)buildings.Pointer, recipient, (int)good);
            }
            public AllyTransferResult Execute(AllyTransferRequest request, int amount)
            { RequireReady(); return bridge.Execute(request, amount); }
        }
    }
}
