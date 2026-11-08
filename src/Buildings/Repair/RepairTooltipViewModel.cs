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
    /// <summary>One resource displayed by the shared repair tooltip.</summary>
    public sealed class RepairTooltipEntry
    {
        /// <summary>Required resource amount.</summary>
        public string Required { get; set; }
        /// <summary>Available resource amount.</summary>
        public string Available { get; set; }
        /// <summary>Vanilla goods icon.</summary>
        public ImageSource Icon { get; set; }
    }

    /// <summary>Noesis binding source for extra costs in the Vanilla repair rollover.</summary>
    public sealed class RepairTooltipViewModel : INotifyPropertyChanged
    {
        private Noesis.Visibility visibility = Noesis.Visibility.Collapsed;
        private string signature;
        /// <summary>Raised when extra repair costs change.</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>Extra resource costs after Vanilla's wood and stone.</summary>
        public ObservableCollection<RepairTooltipEntry> Costs { get; } = new ObservableCollection<RepairTooltipEntry>();
        internal IReadOnlyList<TooltipLine> Lines { get; private set; } = Array.Empty<TooltipLine>();
        /// <summary>Visibility of the extra inline costs.</summary>
        public Noesis.Visibility Visibility
        {
            get => visibility;
            private set { if (visibility != value) { visibility = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility))); } }
        }

        /// <summary>Shows only resources absent from Vanilla's repair rollover.</summary>
        public void Show(BuildingRepairQuote quote)
        {
            if (quote.Iron <= 0 && quote.Pitch <= 0 && quote.Gold <= 0)
            {
                Clear();
                return;
            }
            string next = string.Join(":", new[] { quote.BuildingGlobalId, quote.CurrentHealth, quote.MaxHealth,
                quote.Iron, quote.Pitch, quote.Gold,
                quote.AvailableIron, quote.AvailablePitch, quote.AvailableGold });
            if (signature == next && Visibility == Noesis.Visibility.Visible) return;
            signature = next;
            Costs.Clear();
            Add(eGoods.STORED_IRON_INGOTS, quote.Iron, quote.AvailableIron);
            Add(eGoods.STORED_PITCH_RAW, quote.Pitch, quote.AvailablePitch);
            Add(eGoods.STORED_GOLD, quote.Gold, quote.AvailableGold);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Costs)));
            var segments = new List<TooltipSegment>(Costs.Count * 3);
            foreach (var cost in Costs)
            {
                segments.Add(new TooltipText(cost.Required)); segments.Add(new TooltipSymbol(cost.Icon));
                segments.Add(new TooltipText(cost.Available));
            }
            Lines = new[] { new TooltipLine(segments.ToArray()) };
            Visibility = Noesis.Visibility.Visible;
        }

        private void Add(eGoods good, int required, int available)
        {
            if (required <= 0) return;
            Costs.Add(new RepairTooltipEntry
            {
                Required = "  " + required + " ",
                Available = "(" + available + ")",
                Icon = MainViewModel.Instance.getSmallGoodsIcon((int)good)
            });
        }

        /// <summary>Hides the extra inline costs.</summary>
        public void Clear()
        {
            Lines = Array.Empty<TooltipLine>();
            if (Visibility == Noesis.Visibility.Collapsed) return;
            signature = null;
            Visibility = Noesis.Visibility.Collapsed;
            Costs.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Costs)));
        }
    }
}
