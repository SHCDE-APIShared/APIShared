using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Presentation
{
    // No UI dependencies: production registry is also exercised by standalone tests.
    internal sealed class BuildingTooltipRegistry
    {
        private readonly object sync = new object();
        private Entry[] entries = Array.Empty<Entry>();
        private readonly List<BuildingTooltipLine> scratch = new List<BuildingTooltipLine>();
        private BuildingTooltipLine[] lastResult = Array.Empty<BuildingTooltipLine>();
        private bool collecting;
        internal bool HasDemand => Volatile.Read(ref entries).Length != 0;
        internal bool TryRegister(string owner, string id, int order,
            Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> provider, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || provider == null)
            { reason = "Nonempty owner GUID, contribution ID and provider are required."; return false; }
            lock (sync)
            {
                foreach (var item in entries)
                    if (item.Owner == owner && item.Id == id) { reason = "Duplicate tooltip contribution: " + owner + "/" + id; return false; }
                var next = new Entry[entries.Length + 1]; Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(owner, id, order, provider);
                Array.Sort(next, Compare);
                Volatile.Write(ref entries, next);
            }
            reason = ""; return true;
        }
        private static int Compare(Entry a, Entry b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = string.CompareOrdinal(a.Owner, b.Owner);
            return result == 0 ? string.CompareOrdinal(a.Id, b.Id) : result;
        }
        internal BuildingTooltipLine[] Collect(BuildingTooltipContext context, Action<string, Exception> report)
        {
            // Collection is synchronous on Unity's thread. Never expose or retain the reusable
            // buffer in a callback: published results remain independent immutable snapshots.
            if (collecting) throw new InvalidOperationException("Recursive tooltip collection is unsupported.");
            collecting = true;
            scratch.Clear();
            try
            {
                foreach (var item in Volatile.Read(ref entries))
                {
                    int start = scratch.Count;
                    try
                    {
                        var lines = item.Provider(context);
                        if (lines == null) continue;
                        int count = lines.Count;
                        if (count < 0) throw new ArgumentException("Invalid tooltip line count.");
                        for (int i = 0; i < count; i++)
                        {
                            var line = lines[i];
                            if (line == null) throw new ArgumentException("Null tooltip line.");
                            bool text = false;
                            for (int j = 0; j < line.Segments.Count; j++) text |= !string.IsNullOrEmpty(line.Segments[j].Text);
                            if (text) scratch.Add(line);
                        }
                    }
                    catch (Exception error)
                    {
                        // Reject the entire faulty contribution, retaining other owners' rows.
                        scratch.RemoveRange(start, scratch.Count - start);
                        if (!item.Reported) { item.Reported = true; try { report?.Invoke(item.Owner + "/" + item.Id, error); } catch { } }
                    }
                }
                if (!Same(lastResult, scratch)) lastResult = scratch.ToArray();
                return lastResult;
            }
            finally { scratch.Clear(); collecting = false; }
        }
        internal static bool Same(IReadOnlyList<BuildingTooltipLine> a, IReadOnlyList<BuildingTooltipLine> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Segments.Count != b[i].Segments.Count) return false;
                for (int j = 0; j < a[i].Segments.Count; j++)
                    if (a[i].Segments[j].Text != b[i].Segments[j].Text || a[i].Segments[j].Foreground != b[i].Segments[j].Foreground) return false;
            }
            return true;
        }
        private sealed class Entry
        {
            internal readonly string Owner, Id; internal readonly int Order;
            internal readonly Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> Provider;
            internal bool Reported;
            internal Entry(string owner, string id, int order, Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> provider)
            { Owner = owner; Id = id; Order = order; Provider = provider; }
        }
    }
}
