using APIShared.Presentation;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Marshal = System.Runtime.InteropServices.Marshal;

namespace APIShared
{
    internal sealed unsafe partial class BuildingRepairService
    {
        internal const int CanRepairRva = 0xD51A0;
        private const int WoodCostOffset = 0x31B824;
        private const int StoneCostOffset = 0x31B828;
        private const string BigButtonId = "APIShared.VanillaRepairButton";
        internal static readonly byte[] CanRepairEntry =
        {
            0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x30,
            0x48, 0x63, 0x1D, 0xE3, 0x31, 0x71, 0x06
        };

        private delegate EngineInterface.PlayState CopyStateDelegate(
            EngineInterface.PlayStateReturnData source, int[] selectedChimps);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong NativeCanRepairDelegate();

        private sealed class PendingRepair
        {
            internal int PlayerId;
            internal int BuildingId;
            internal int GlobalId;
            internal int PreviousHealth;
            internal int Iron;
            internal int Pitch;
            internal int Gold;
        }

        [ThreadStatic] private static PendingRepair pendingRepair;

        private readonly object ownersSync = new object();
        private readonly object errorSync = new object();
        private readonly HashSet<string> seenErrors = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> pendingErrors = new Queue<string>();
        private readonly Dictionary<string, OwnerView> owners = new Dictionary<string, OwnerView>(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<EngineInterface.PlayState, BuildingRepairQuote> snapshots =
            new ConditionalWeakTable<EngineInterface.PlayState, BuildingRepairQuote>();
        private readonly RepairTooltipViewModel tooltip;
        private readonly ManualLogSource log;
        private readonly NativeCanRepairDelegate nativeCanRepair;
        private Hook copyHook;
        private CopyStateDelegate originalCopy;
        private IDisposable repairSubscription;
        private int activeOwners;
        private string hoveredButton;
        private Button lastBigButton;
        private bool postStartupLogged, published;

        private BuildingRepairService(long moduleBase, ManualLogSource logger, RepairTooltipViewModel tooltipViewModel)
        {
            log = logger;
            tooltip = tooltipViewModel ?? throw new ArgumentNullException(nameof(tooltipViewModel));
            nativeCanRepair = Marshal.GetDelegateForFunctionPointer<NativeCanRepairDelegate>(
                new IntPtr(checked(moduleBase + CanRepairRva)));
        }

        internal IBuildingRepairCapability Bind(string ownerGuid)
        {
            lock (ownersSync)
            {
                if (owners.TryGetValue(ownerGuid, out OwnerView existing)) return existing;
                var view = new OwnerView(this, ownerGuid);
                owners.Add(ownerGuid, view);
                Interlocked.Increment(ref activeOwners);
                return view;
            }
        }

        private void SetOwnerActive(OwnerView view, bool active)
        {
            lock (ownersSync)
            {
                if (view.Active == active) return;
                view.Active = active;
                if (active) Interlocked.Increment(ref activeOwners);
                else if (Interlocked.Decrement(ref activeOwners) == 0) hoveredButton = null;
            }
        }

        private void RecordError(string message)
        {
            lock (errorSync)
            {
                if (seenErrors.Add(message)) pendingErrors.Enqueue(message);
            }
        }

        private void FlushErrors()
        {
            lock (errorSync)
            {
                while (pendingErrors.Count > 0)
                    NativeApiLog.Error(log, pendingErrors.Dequeue());
            }
        }

        private sealed class OwnerView : IBuildingRepairCapability
        {
            private readonly BuildingRepairService service;
            internal bool Active = true;
            internal OwnerView(BuildingRepairService service, string ownerGuid)
            {
                this.service = service;
                OwnerGuid = ownerGuid;
            }
            internal string OwnerGuid { get; }
            public void SetActive(bool active) => service.SetOwnerActive(this, active);
            public bool TryGetSelectedQuote(out BuildingRepairQuote quote)
            {
                if (Active) return service.TryGetSelectedQuote(out quote);
                quote = null;
                return false;
            }
            public void BeginHover(string buttonId) { if (Active) service.BeginHover(buttonId); }
            public void EndHover(string buttonId) => service.EndHover(buttonId);
        }
    }
}
