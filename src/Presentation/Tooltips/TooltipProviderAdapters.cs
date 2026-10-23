using System;
using System.Collections.Generic;

namespace APIShared.Presentation
{
    // Separate target caches prevent inactive alternatives from invalidating active text each GUI pass.
    internal sealed class TooltipTextProvider
    {
        private readonly Func<TooltipContext, string> provider;
        private readonly Dictionary<TooltipTarget, State> states = new Dictionary<TooltipTarget, State>();
        internal TooltipTextProvider(Func<TooltipContext, string> provider) { this.provider = provider; }
        internal IReadOnlyList<TooltipLine> Get(TooltipContext context)
        {
            string text = provider(context);
            if (!states.TryGetValue(context.Target, out var state)) states[context.Target] = state = new State();
            if (text != state.Text)
            {
                state.Text = text;
                state.Lines = string.IsNullOrWhiteSpace(text) ? Array.Empty<TooltipLine>() :
                    new[] { new TooltipLine(new TooltipText(text)) };
            }
            return state.Lines;
        }
        private sealed class State
        {
            internal string Text;
            internal IReadOnlyList<TooltipLine> Lines = Array.Empty<TooltipLine>();
        }
    }

    internal sealed class LegacyBuildingTooltipProvider
    {
        private readonly Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> provider;
        private BuildingTooltipContext previousContext;
        private IReadOnlyList<BuildingTooltipLine> previousLines;
        private IReadOnlyList<TooltipLine> converted = Array.Empty<TooltipLine>();
        internal LegacyBuildingTooltipProvider(Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> provider)
        { this.provider = provider; }
        internal IReadOnlyList<TooltipLine> Get(TooltipContext context, bool selectedVariant)
        {
            // Preserve the old single callback per presentation pass, even when no building is active.
            if (!selectedVariant) return Array.Empty<TooltipLine>();
            var variant = context.Target == TooltipTarget.BuildingDetailed ? BuildingTooltipVariant.Detailed : BuildingTooltipVariant.Compact;
            if (previousContext == null || previousContext.BuildingType != context.BuildingType ||
                previousContext.Variant != variant || previousContext.IsSymbolHover != context.IsSymbolHover)
                previousContext = new BuildingTooltipContext(context.BuildingType, variant, context.IsSymbolHover);
            var lines = provider(previousContext);
            if (lines == null || lines.Count == 0)
            { previousLines = null; converted = Array.Empty<TooltipLine>(); return converted; }
            if (!BuildingTooltipRegistry.Same(previousLines ?? Array.Empty<BuildingTooltipLine>(), lines))
            {
                var copy = new BuildingTooltipLine[lines.Count]; var result = new TooltipLine[lines.Count];
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i] ?? throw new ArgumentException("Null tooltip line.");
                    var segments = new TooltipSegment[line.Segments.Count];
                    for (int j = 0; j < segments.Length; j++)
                    {
                        var segment = line.Segments[j];
                        segments[j] = new TooltipText(segment.Text, Convert.ToUInt32(segment.Foreground.Substring(1), 16));
                    }
                    copy[i] = line; result[i] = new TooltipLine(true, segments);
                }
                previousLines = copy; converted = result;
            }
            return converted;
        }
    }
}
