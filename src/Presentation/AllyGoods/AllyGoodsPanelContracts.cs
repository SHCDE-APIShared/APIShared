using System;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    /// <summary>Explicit section anchor. Existing registration overloads retain PanelRootBottom.</summary>
    public enum AllyGoodsSectionPlacement
    {
        /// <summary>Below the original entire menu root; preserves the legacy layout contract.</summary>
        PanelRootBottom = 0,
        /// <summary>Below the marked visible goods frame; requires unambiguous goods-frame and footer anchors.</summary>
        GoodsDialogBottom = 1
    }
    /// <summary>Immutable UI selection, not an authorization or a completed transfer.</summary>
    public sealed class AllyGoodsPanelContext
    {
        internal AllyGoodsPanelContext(bool open, bool send, bool request, int local, int viewed,
            int recipient, eGoods? good, int? amount, long generation)
        {
            IsOpen = open; IsSendMode = send; IsRequestMode = request;
            LocalPlayerId = local; ViewedPlayerId = viewed; RecipientPlayerId = recipient;
            Good = good; Amount = amount; PanelGeneration = generation;
        }
        /// <summary>Whether the existing goods dialog is open.</summary>
        public bool IsOpen { get; }
        /// <summary>True only for the open sending dialog.</summary>
        public bool IsSendMode { get; }
        /// <summary>True only for the open goods-request dialog.</summary>
        public bool IsRequestMode { get; }
        /// <summary>Locally controlled player, or -1 if unavailable or spectating. This grants no command rights.</summary>
        public int LocalPlayerId { get; }
        /// <summary>Currently viewed player, independently of local participation; -1 if unknown.</summary>
        public int ViewedPlayerId { get; }
        /// <summary>Selected one-based ally ID; zero when no valid selection is available.</summary>
        public int RecipientPlayerId { get; }
        /// <summary>Selected goods enum, or null before selection. Not a promise that the good can be transferred.</summary>
        public eGoods? Good { get; }
        /// <summary>Selected quantity, or null before selection. Zero is a valid selected quantity.</summary>
        public int? Amount { get; }
        /// <summary>Whether recipient, ware and a nonnegative amount are selected, regardless of current inventory.</summary>
        public bool HasSelection => IsOpen && RecipientPlayerId > 0 && Good.HasValue && Amount.HasValue;
        /// <summary>Changes when the bound Noesis host is replaced; never a mission or network identity.</summary>
        public long PanelGeneration { get; }
        internal bool Same(bool open, bool send, bool request, int local, int viewed,
            int recipient, eGoods? good, int? amount, long generation) =>
            IsOpen == open && IsSendMode == send && IsRequestMode == request && LocalPlayerId == local &&
            ViewedPlayerId == viewed && RecipientPlayerId == recipient && Good == good && Amount == amount &&
            PanelGeneration == generation;
    }

    /// <summary>Unity-thread factory context. Create a fresh, unattached control; do not retain the host across recreation.</summary>
    public sealed class AllyGoodsSectionContext
    {
        internal AllyGoodsSectionContext(FrameworkElement host, AllyGoodsPanelContext selection)
        { Host = host; Selection = selection; }
        /// <summary>APIShared's host with inherited Vanilla resources. Do not mutate its layout or DataContext.</summary>
        public FrameworkElement Host { get; }
        /// <summary>Selection when the factory ran. Read TryGetContext again before executing a command.</summary>
        public AllyGoodsPanelContext Selection { get; }
    }

    /// <summary>Logical process-lifetime registration. All methods require the initialized Unity thread.</summary>
    public sealed class AllyGoodsSectionRegistration
    {
        private readonly AllyGoodsSectionEntry entry;
        private readonly Action invalidate;
        internal AllyGoodsSectionRegistration(AllyGoodsSectionEntry entry, Action invalidate)
        { this.entry = entry; this.invalidate = invalidate; }
        /// <summary>Hidden sections occupy no layout space; initially true.</summary>
        public void SetVisible(bool visible)
        { AllyGoodsPanelExtensions.RequireUnity(); if (entry.Visible == visible) return; entry.Visible = visible; invalidate(); }
        /// <summary>Changes interaction availability of this section only; initially true. Commands must still authorize themselves.</summary>
        public void SetEnabled(bool enabled)
        { AllyGoodsPanelExtensions.RequireUnity(); if (entry.Enabled == enabled) return; entry.Enabled = enabled; invalidate(); }
        /// <summary>Recreates this section and retries a failed factory on the next open-dialog GUI update.</summary>
        public void RequestContentRefresh()
        { AllyGoodsPanelExtensions.RequireUnity(); entry.Revision++; invalidate(); }
        /// <summary>Reads current layout availability on Unity, independently of visibility or gameplay permission.
        /// False supplies a diagnostic reason, not localized player text. Read again after a notification.</summary>
        public bool TryGetLayoutAvailability(out string reason)
        { AllyGoodsPanelExtensions.RequireUnity(); reason = entry.LayoutReason; return entry.LayoutAvailable; }
        /// <summary>Runs on Unity when layout availability/reason changes, including closure and host recreation.
        /// There is no subscription replay: subscribe, then read TryGetLayoutAvailability. Subscriber failures
        /// are isolated. This notification grants no gameplay authorization and never submits a command.</summary>
        public event Action LayoutAvailabilityChanged
        {
            add { AllyGoodsPanelExtensions.RequireUnity(); entry.LayoutChanged += value; }
            remove { AllyGoodsPanelExtensions.RequireUnity(); entry.LayoutChanged -= value; }
        }
    }

    /// <summary>Process-lifetime, noninteractive goods-marker registration. Access requires the initialized Unity thread.</summary>
    public sealed class AllyGoodsAdornmentRegistration
    {
        private readonly AllyGoodsAdornmentEntry entry;
        private readonly Action invalidate;
        internal AllyGoodsAdornmentRegistration(AllyGoodsAdornmentEntry entry, Action invalidate)
        { this.entry = entry; this.invalidate = invalidate; }
        /// <summary>Requests rearrangement and retries failed factories on the next open goods-dialog GUI update.
        /// Successful controls remain cached until host recreation. Update their Visibility or bound presentation
        /// state on Unity before calling this method; this method invokes no consumer refresh callback.</summary>
        public void RequestRefresh()
        { AllyGoodsPanelExtensions.RequireUnity(); entry.Revision++; invalidate(); }
    }
}
