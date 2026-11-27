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
using System.Globalization;
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
        private int[] shown = new int[9], next = new int[9];
        private string shownNegativeSign;
        private bool hasSignature;
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
            if (IsShown(quote) && Visibility == Noesis.Visibility.Visible) return;
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

        // Compares the quote with the shown one and then records it as shown. This is the value
        // form of a former culture-formatted string signature, so the culture's negative sign
        // also takes part while any value is negative.
        private bool IsShown(BuildingRepairQuote quote)
        {
            next[0] = quote.BuildingGlobalId; next[1] = quote.CurrentHealth; next[2] = quote.MaxHealth;
            next[3] = quote.Iron; next[4] = quote.Pitch; next[5] = quote.Gold;
            next[6] = quote.AvailableIron; next[7] = quote.AvailablePitch; next[8] = quote.AvailableGold;
            string negativeSign = null;
            foreach (int value in next)
                if (value < 0) { negativeSign = NumberFormatInfo.CurrentInfo.NegativeSign; break; }
            bool same = hasSignature && negativeSign == shownNegativeSign;
            for (int i = 0; same && i < next.Length; i++) same = next[i] == shown[i];
            int[] previous = shown; shown = next; next = previous;
            shownNegativeSign = negativeSign;
            hasSignature = true;
            return same;
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
            hasSignature = false;
            Visibility = Noesis.Visibility.Collapsed;
            Costs.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Costs)));
        }
    }
}
