using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace APIShared.Presentation
{
    /// <summary>The existing Vanilla building tooltip selected by the HUD.</summary>
    public enum BuildingTooltipVariant
    {
        /// <summary>The compact hover/placement cost line.</summary>
        Compact,
        /// <summary>The expanded building tooltip with description and existing background.</summary>
        Detailed
    }

    /// <summary>Immutable synchronous Unity-thread context; contains no retained HUD objects.</summary>
    public sealed class BuildingTooltipContext
    {
        /// <summary>Constructs a context. BuildingType uses Vanilla structure identifiers (eStructs), not mapper IDs.</summary>
        public BuildingTooltipContext(int buildingType, BuildingTooltipVariant variant, bool isSymbolHover)
        { BuildingType = buildingType; Variant = variant; IsSymbolHover = isSymbolHover; }
        /// <summary>Vanilla structure identifier, or zero when no building tooltip is active. Providers may retire selection state on zero; their rows are not displayed. Consumers resolve their own supported types.</summary>
        public int BuildingType { get; }
        /// <summary>Current Vanilla tooltip variant.</summary>
        public BuildingTooltipVariant Variant { get; }
        /// <summary>True when Vanilla chose HoverStruct/HoverString over selected-building information.</summary>
        public bool IsSymbolHover { get; }
    }

    /// <summary>Immutable plain text with ARGB foreground. No markup, custom background or commands.</summary>
    public sealed class BuildingTooltipText
    {
        /// <summary>Creates a segment; default colour matches Vanilla supplementary text.</summary>
        public BuildingTooltipText(string text, uint foregroundArgb = 0xFFEFF3C6)
        { Text = text ?? throw new ArgumentNullException(nameof(text)); Foreground = "#" + foregroundArgb.ToString("X8", CultureInfo.InvariantCulture); }
        /// <summary>Localized text; supply directional isolates for numeric fragments when needed.</summary>
        public string Text { get; }
        /// <summary>ARGB brush string used by the shared presentation template.</summary>
        public string Foreground { get; }
    }

    /// <summary>Immutable supplementary line. Segments retain their order; text wraps with the game's flow direction.</summary>
    public sealed class BuildingTooltipLine
    {
        /// <summary>Copies the segment sequence. Null segments are rejected.</summary>
        public BuildingTooltipLine(params BuildingTooltipText[] segments)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            var copy = (BuildingTooltipText[])segments.Clone();
            foreach (var segment in copy) if (segment == null) throw new ArgumentException("Null tooltip segment.", nameof(segments));
            Segments = new ReadOnlyCollection<BuildingTooltipText>(copy);
        }
        /// <summary>Copied immutable text segments.</summary>
        public IReadOnlyList<BuildingTooltipText> Segments { get; }
    }
}
