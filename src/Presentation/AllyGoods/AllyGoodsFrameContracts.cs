using System;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>Consumer-supplied layout metrics for a shared goods-frame presentation.
    /// Values use the goods root's logical units. Insets/overflow/margin/spacing must be
    /// finite and nonnegative; maximumExtensionHeight must be finite and positive.
    /// No brushes, resource keys, templates or theme are selected by these options.</summary>
    public sealed class AllyGoodsFrameOptions
    {
        /// <summary>Creates explicit immutable content, paint-overflow and viewport metrics.
        /// Throws ArgumentOutOfRangeException for invalid metrics.</summary>
        public AllyGoodsFrameOptions(Thickness contentPadding, Thickness visualOverflow,
            float maximumExtensionHeight, float viewportMargin, float sectionSpacing)
        {
            if (!Valid(contentPadding)) throw new ArgumentOutOfRangeException(nameof(contentPadding));
            if (!Valid(visualOverflow)) throw new ArgumentOutOfRangeException(nameof(visualOverflow));
            if (!Positive(maximumExtensionHeight)) throw new ArgumentOutOfRangeException(nameof(maximumExtensionHeight));
            if (!Nonnegative(viewportMargin)) throw new ArgumentOutOfRangeException(nameof(viewportMargin));
            if (!Nonnegative(sectionSpacing)) throw new ArgumentOutOfRangeException(nameof(sectionSpacing));
            ContentPadding = contentPadding; VisualOverflow = visualOverflow;
            MaximumExtensionHeight = maximumExtensionHeight; ViewportMargin = viewportMargin; SectionSpacing = sectionSpacing;
        }
        /// <summary>Padding around the shared goods-anchored content, inside its scroll surface.</summary>
        public Thickness ContentPadding { get; }
        /// <summary>Painted overhang outside the decoration's assigned bounds. Bottom overflow
        /// is reserved before subsequent sections and the viewport edge; other sides describe
        /// the consumer's decoration and never move the original goods anchor.</summary>
        public Thickness VisualOverflow { get; }
        /// <summary>Maximum logical height of the additional content surface, excluding painted overflow.</summary>
        public float MaximumExtensionHeight { get; }
        /// <summary>Minimum space between the painted lower edge and the viewport edge.</summary>
        public float ViewportMargin { get; }
        /// <summary>Vertical gap between goods-anchored section containers.</summary>
        public float SectionSpacing { get; }
        private static bool Nonnegative(float value) => AllyGoodsPanelLayoutPolicy.Finite(value) && value >= 0;
        private static bool Positive(float value) => Nonnegative(value) && value > 0;
        private static bool Valid(Thickness value) => Nonnegative(value.Left) && Nonnegative(value.Top) &&
            Nonnegative(value.Right) && Nonnegative(value.Bottom);
    }

    /// <summary>Unity-thread factory context. OriginalFrame is a borrowed semantic anchor:
    /// inspect it to choose a design, but do not mutate it or retain it across HUD replacement.</summary>
    public sealed class AllyGoodsFrameContext
    {
        internal AllyGoodsFrameContext(FrameworkElement host, FrameworkElement originalFrame, AllyGoodsPanelContext selection)
        { Host = host; OriginalFrame = originalFrame; Selection = selection; }
        /// <summary>Attached root supplying inherited resources. Its layout and DataContext remain broker-owned.</summary>
        public FrameworkElement Host { get; }
        /// <summary>Original backdrop, with its original geometry. Returning null from the factory declines this layout.</summary>
        public FrameworkElement OriginalFrame { get; }
        /// <summary>Factory-time selection. Re-read the panel context before any later consumer action.</summary>
        public AllyGoodsPanelContext Selection { get; }
    }

    /// <summary>Process-lifetime frame provider. Methods require initialized Unity. Initially disabled.
    /// Enabled providers compete by order/ordinal owner/ID; the first owns the shared anchored-group
    /// presentation. It is used only when that group has visible content. Failure does not select
    /// a lower-priority provider: the original frame and standard layout remain instead.</summary>
    public sealed class AllyGoodsFrameRegistration
    {
        private readonly AllyGoodsFrameEntry entry;
        internal AllyGoodsFrameRegistration(AllyGoodsFrameEntry entry) { this.entry = entry; }
        /// <summary>Enables this provider's participation without recreating its decoration.</summary>
        public void SetEnabled(bool enabled)
        { AllyGoodsPanelExtensions.RequireUnity(); if (entry.Enabled == enabled) return; entry.Enabled = enabled; AllyGoodsPanelExtensions.InvalidateFrame(); }
        /// <summary>Recreates this provider's decoration and retries a declined/failed factory on the next eligible GUI pass.</summary>
        public void RequestContentRefresh()
        { AllyGoodsPanelExtensions.RequireUnity(); entry.Revision++; AllyGoodsPanelExtensions.InvalidateFrame(); }
    }
}
