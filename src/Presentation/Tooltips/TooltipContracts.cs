using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>Existing in-game tooltip surfaces. Flags select registration targets; a context contains one flag.</summary>
    [Flags]
    public enum TooltipTarget
    {
        /// <summary>Compact building hover or placement text.</summary>
        BuildingCompact = 1,
        /// <summary>Expanded building hover with description.</summary>
        BuildingDetailed = 2,
        /// <summary>Recruitment cost text in the active recruiting panel.</summary>
        Recruitment = 4,
        /// <summary>Siege construction rollover in the troops panel.</summary>
        SiegeConstruction = 8,
        /// <summary>Building repair rollover, independently of building placement.</summary>
        Repair = 16
    }

    /// <summary>Semantic positions relative to unchanged Vanilla text, never arbitrary XAML selectors.</summary>
    public enum TooltipPlacement
    {
        /// <summary>Additional lines above the original main line.</summary>
        BeforeMainLine,
        /// <summary>Additional lines below the original main line.</summary>
        AfterMainLine,
        /// <summary>Inline suffix following the original costs.</summary>
        InlineAfterCosts,
        /// <summary>Additional lines before the expanded building description.</summary>
        BeforeDescription,
        /// <summary>Additional lines after the expanded building description.</summary>
        AfterDescription
    }

    /// <summary>Immutable Unity-thread presentation inputs; no HUD objects or simulation authority.</summary>
    public sealed class TooltipContext
    {
        /// <summary>Constructs a snapshot. BuildingType is an eStructs identifier, UnitType an eChimps identifier; null means no unit.</summary>
        public TooltipContext(TooltipTarget target, bool isActive, int buildingType, int? unitType,
            int localPlayerId, int quantity, bool isSymbolHover)
        { Target = target; IsActive = isActive; BuildingType = buildingType; UnitType = unitType;
          LocalPlayerId = Math.Max(0, localPlayerId); Quantity = Math.Max(1, quantity); IsSymbolHover = isSymbolHover; }
        /// <summary>One concrete target.</summary>
        public TooltipTarget Target { get; }
        /// <summary>False on hover retirement or when Vanilla no longer displays this context. Returned content then cannot render.</summary>
        public bool IsActive { get; }
        /// <summary>Vanilla structure type, or zero outside building presentation.</summary>
        public int BuildingType { get; }
        /// <summary>Vanilla unit type, or null outside unit presentation.</summary>
        public int? UnitType { get; }
        /// <summary>Resolved local player, or zero when unavailable. Never infer player one.</summary>
        public int LocalPlayerId { get; }
        /// <summary>Current recruitment quantity; one for other targets.</summary>
        public int Quantity { get; }
        /// <summary>Whether Vanilla selected a hovered building symbol rather than its placement selection.</summary>
        public bool IsSymbolHover { get; }
    }

    /// <summary>Immutable literal text or shareable image. The renderer owns all visual controls.</summary>
    public abstract class TooltipSegment
    {
        internal TooltipSegment() { }
    }
    /// <summary>Literal localized text; no XAML or markup is interpreted.</summary>
    public sealed class TooltipText : TooltipSegment
    {
        /// <summary>Creates text; null is rejected. Null foreground inherits the target's colour.</summary>
        public TooltipText(string text, uint? foregroundArgb = null)
        { Text = text ?? throw new ArgumentNullException(nameof(text)); ForegroundArgb = foregroundArgb; }
        /// <summary>Literal text, including deliberate spaces and line breaks.</summary>
        public string Text { get; }
        /// <summary>Optional ARGB colour override.</summary>
        public uint? ForegroundArgb { get; }
    }
    /// <summary>Shareable image source with a literal fallback, not an attached Image control.</summary>
    public sealed class TooltipSymbol : TooltipSegment
    {
        /// <summary>Creates a symbol. A missing source renders fallbackText; both may be empty to omit it.</summary>
        public TooltipSymbol(ImageSource source, string fallbackText = "")
        { Source = source; FallbackText = fallbackText ?? string.Empty; }
        /// <summary>Shareable source, resolved by the provider on the Unity thread.</summary>
        public ImageSource Source { get; }
        /// <summary>Literal replacement if the source is absent.</summary>
        public string FallbackText { get; }
    }
    /// <summary>Immutable ordered sequence of text and symbols. Providers may reuse unchanged lines.</summary>
    public sealed class TooltipLine
    {
        /// <summary>Copies the sequence; null segments are rejected.</summary>
        public TooltipLine(params TooltipSegment[] segments) : this(false, segments) { }
        internal bool LegacyBuildingStyle { get; }
        internal TooltipLine(bool legacyBuildingStyle, TooltipSegment[] segments)
        {
            LegacyBuildingStyle = legacyBuildingStyle;
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            var copy = (TooltipSegment[])segments.Clone();
            foreach (var segment in copy) if (segment == null) throw new ArgumentException("Null tooltip segment.", nameof(segments));
            Segments = new ReadOnlyCollection<TooltipSegment>(copy);
        }
        /// <summary>Copied immutable segments.</summary>
        public IReadOnlyList<TooltipSegment> Segments { get; }
    }
}
