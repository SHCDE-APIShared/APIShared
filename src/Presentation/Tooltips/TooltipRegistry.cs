using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Presentation
{
    // No UI operations: production ordering, validation and failure isolation are tested independently.
    internal sealed class TooltipRegistry
    {
        private readonly object gate = new object();
        private Entry[] entries = Array.Empty<Entry>();
        private readonly List<Block> scratch = new List<Block>();
        private readonly Dictionary<TooltipTarget, Block[]> previous = new Dictionary<TooltipTarget, Block[]>();
        private bool collecting;
        internal bool HasDemand => Volatile.Read(ref entries).Length != 0;
        internal static bool Supports(TooltipTarget targets, TooltipPlacement placement)
        {
            const TooltipTarget all = TooltipTarget.BuildingCompact | TooltipTarget.BuildingDetailed |
                TooltipTarget.Recruitment | TooltipTarget.SiegeConstruction | TooltipTarget.Repair;
            if (targets == 0 || (targets & ~all) != 0) return false;
            switch (placement)
            {
                case TooltipPlacement.BeforeMainLine:
                case TooltipPlacement.AfterMainLine:
                case TooltipPlacement.InlineAfterCosts: return true;
                case TooltipPlacement.BeforeDescription:
                case TooltipPlacement.AfterDescription: return targets == TooltipTarget.BuildingDetailed;
                default: return false;
            }
        }
        internal bool TryRegister(string owner, string id, TooltipTarget targets, TooltipPlacement placement,
            Func<TooltipContext, IReadOnlyList<TooltipLine>> provider, int order, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || provider == null)
            { reason = "Nonempty owner GUID, contribution ID and provider are required."; return false; }
            if (!Supports(targets, placement)) { reason = "Unsupported tooltip target/placement combination."; return false; }
            lock (gate)
            {
                foreach (var entry in entries)
                    if (entry.Owner == owner && entry.Id == id) { reason = "Duplicate tooltip contribution: " + owner + "/" + id; return false; }
                var next = new Entry[entries.Length + 1]; Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(owner, id, targets, placement, provider, order);
                Array.Sort(next, (a, b) => { int c = a.Order.CompareTo(b.Order); if (c == 0) c = string.CompareOrdinal(a.Owner, b.Owner); return c == 0 ? string.CompareOrdinal(a.Id, b.Id) : c; });
                Volatile.Write(ref entries, next);
            }
            reason = string.Empty; return true;
        }
        internal Block[] Collect(TooltipContext context, Action<string, Exception> report)
        {
            if (collecting) throw new InvalidOperationException("Recursive tooltip collection is unsupported.");
            collecting = true; scratch.Clear();
            try
            {
                foreach (var entry in Volatile.Read(ref entries))
                {
                    if ((entry.Targets & context.Target) == 0) continue;
                    int start = scratch.Count;
                    try
                    {
                        var lines = entry.Provider(context);
                        if (lines == null) continue;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var line = lines[i];
                            if (line == null) throw new ArgumentException("Null tooltip line.");
                            if (HasContent(line) && context.IsActive) scratch.Add(new Block(entry.Placement, line));
                        }
                    }
                    catch (Exception error)
                    {
                        scratch.RemoveRange(start, scratch.Count - start);
                        if (!entry.Reported) { entry.Reported = true; try { report?.Invoke(entry.Owner + "/" + entry.Id, error); } catch { } }
                    }
                }
                previous.TryGetValue(context.Target, out Block[] last);
                if (!Same(last, scratch)) previous[context.Target] = last = scratch.Count == 0 ? Array.Empty<Block>() : scratch.ToArray();
                return last;
            }
            finally { scratch.Clear(); collecting = false; }
        }
        internal static bool HasContent(TooltipLine line)
        {
            for (int i = 0; i < line.Segments.Count; i++)
            {
                var segment = line.Segments[i];
                if (segment is TooltipText text && !string.IsNullOrWhiteSpace(text.Text)) return true;
                if (segment is TooltipSymbol icon && (icon.Source != null || !string.IsNullOrWhiteSpace(icon.FallbackText))) return true;
            }
            return false;
        }
        internal static bool Same(IReadOnlyList<Block> a, IReadOnlyList<Block> b)
        {
            if (a == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Placement != b[i].Placement || !SameLine(a[i].Line, b[i].Line)) return false;
            }
            return true;
        }
        internal static bool SameLine(TooltipLine a, TooltipLine b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.LegacyBuildingStyle != b.LegacyBuildingStyle || a.Segments.Count != b.Segments.Count) return false;
            for (int i = 0; i < a.Segments.Count; i++)
            {
                if (a.Segments[i] is TooltipText x && b.Segments[i] is TooltipText y)
                { if (x.Text != y.Text || x.ForegroundArgb != y.ForegroundArgb) return false; }
                else if (a.Segments[i] is TooltipSymbol p && b.Segments[i] is TooltipSymbol q)
                { if (!ReferenceEquals(p.Source, q.Source) || p.FallbackText != q.FallbackText) return false; }
                else return false;
            }
            return true;
        }
        internal readonly struct Block
        {
            internal readonly TooltipPlacement Placement;
            internal readonly TooltipLine Line;
            internal Block(TooltipPlacement placement, TooltipLine line) { Placement = placement; Line = line; }
        }
        private sealed class Entry
        {
            internal readonly string Owner, Id;
            internal readonly TooltipTarget Targets;
            internal readonly TooltipPlacement Placement;
            internal readonly Func<TooltipContext, IReadOnlyList<TooltipLine>> Provider;
            internal readonly int Order;
            internal bool Reported;
            internal Entry(string owner, string id, TooltipTarget targets, TooltipPlacement placement, Func<TooltipContext, IReadOnlyList<TooltipLine>> provider, int order)
            { Owner = owner; Id = id; Targets = targets; Placement = placement; Provider = provider; Order = order; }
        }
    }
}
