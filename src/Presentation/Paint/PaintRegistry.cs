using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Presentation
{
    internal interface IPaintRegistry
    {
        string Status { get; }
        bool IsInstalled { get; }
        long CallbackFailures { get; }
    }

    // One registration. The non-generic base lets every paint value name claim owners without knowing its site.
    internal class PaintEntry
    {
        internal long Failures;
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly int[] Files;
        internal PaintEntry(string owner, string id, int order, int[] files)
        { Owner = owner; Id = id; Order = order; Files = files; }
    }

    internal sealed class PaintEntry<TPaint> : PaintEntry where TPaint : struct
    {
        internal readonly PaintHandler<TPaint> Handler;
        internal PaintEntry(string owner, string id, int order, PaintHandler<TPaint> handler, int[] files)
            : base(owner, id, order, files) { Handler = handler; }
    }

    // Registrations of one paint site. Paint calls read one immutable table and index it by sprite file,
    // so dispatch cost does not grow with registrations for other files. Registration is rare and
    // serialized; it rebuilds the table and installs the site's hook on the first success.
    internal sealed class PaintRegistry<TPaint> : IPaintRegistry where TPaint : struct, IPaintNotification
    {
        // Sprite files (GM numbers) addressable by a file filter. The installed game names 0..233.
        internal const int FileTableSize = 1024;

        private readonly object sync = new object();
        private readonly List<PaintEntry<TPaint>> registrations = new List<PaintEntry<TPaint>>();
        private readonly string siteName;
        private readonly Func<string> install;
        // Index f: the registrations for sprite file f, in dispatch order. Last index: the registrations without
        // a file filter, used for files outside the table and by sites whose call has no file. Rebuilt whole and
        // published by a volatile write; a volatile field read is a plain load on x64.
        private static readonly PaintEntry<TPaint>[][] EmptyTable = { Array.Empty<PaintEntry<TPaint>>() };
        private volatile PaintEntry<TPaint>[][] table = EmptyTable;
        private string status = "Dormant";
        private bool installed;
        private long failures;

        // install returns null once the hook is published, otherwise the refusal reason. It runs under the
        // registry lock at most until it succeeds; a refused site stays unhooked and a later registration retries.
        internal PaintRegistry(string siteName, Func<string> install)
        { this.siteName = siteName; this.install = install; }

        public string Status => Volatile.Read(ref status);
        public bool IsInstalled { get { lock (sync) return installed; } }
        public long CallbackFailures => Interlocked.Read(ref failures);

        // Hot path: one field load, one compare, one element load.
        internal PaintEntry<TPaint>[] ForFile(int file)
        {
            PaintEntry<TPaint>[][] current = table;
            int unfiltered = current.Length - 1;
            return current[(uint)file < (uint)unfiltered ? file : unfiltered];
        }

        // Sites whose call has no sprite file accept no filters, so every registration is unfiltered.
        internal PaintEntry<TPaint>[] ForAll()
        {
            PaintEntry<TPaint>[][] current = table;
            return current[current.Length - 1];
        }

        internal bool TryRegister(string owner, string id, PaintHandler<TPaint> handler, int order, int[] files,
            bool allowScopeOnly, out string reason)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            { reason = "Owner GUID and registration ID are required."; return false; }
            if (handler == null && !allowScopeOnly)
            { reason = "A paint callback is required."; return false; }
            int[] filter = null;
            if (files != null)
            {
                if (files.Length == 0) { reason = "Pass null for every file or at least one file."; return false; }
                var unique = new SortedSet<int>();
                foreach (int file in files)
                {
                    if ((uint)file >= FileTableSize)
                    { reason = "File " + file + " is outside 0.." + (FileTableSize - 1) + "; pass null to receive every file."; return false; }
                    unique.Add(file);
                }
                filter = new int[unique.Count];
                unique.CopyTo(filter);
            }
            lock (sync)
            {
                foreach (PaintEntry<TPaint> existing in registrations)
                    if (existing.Owner == owner && existing.Id == id)
                    { reason = "This owner already registered that ID."; return false; }
                if (!installed)
                {
                    string refusal;
                    try { refusal = install(); }
                    catch (Exception ex) { refusal = ex.GetType().Name + ": " + ex.Message; }
                    if (refusal != null)
                    {
                        reason = siteName + " unavailable: " + refusal;
                        Volatile.Write(ref status, "Unavailable: " + refusal);
                        return false;
                    }
                    installed = true;
                    Volatile.Write(ref status, "Installed");
                }
                registrations.Add(new PaintEntry<TPaint>(owner, id, order, handler, filter));
                registrations.Sort(Compare);
                table = Build(registrations);
            }
            reason = string.Empty;
            return true;
        }

        private static int Compare(PaintEntry<TPaint> a, PaintEntry<TPaint> b)
        {
            int comparison = a.Order.CompareTo(b.Order);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        // Files named by no filter share the unfiltered array; scope-only registrations (no handler) are never notified.
        private static PaintEntry<TPaint>[][] Build(List<PaintEntry<TPaint>> sorted)
        {
            var all = new List<PaintEntry<TPaint>>();
            foreach (PaintEntry<TPaint> entry in sorted) if (entry.Handler != null) all.Add(entry);
            if (all.Count == 0) return EmptyTable;
            var unfiltered = new List<PaintEntry<TPaint>>();
            var named = new bool[FileTableSize];
            foreach (PaintEntry<TPaint> entry in all)
            {
                if (entry.Files == null) unfiltered.Add(entry);
                else foreach (int file in entry.Files) named[file] = true;
            }
            PaintEntry<TPaint>[] shared = unfiltered.Count == 0 ? Array.Empty<PaintEntry<TPaint>>() : unfiltered.ToArray();
            var byFile = new PaintEntry<TPaint>[FileTableSize + 1][];
            byFile[FileTableSize] = shared;
            var matching = new List<PaintEntry<TPaint>>();
            for (int file = 0; file < FileTableSize; file++)
            {
                if (!named[file]) { byFile[file] = shared; continue; }
                matching.Clear();
                foreach (PaintEntry<TPaint> entry in all)
                    if (entry.Files == null || Array.BinarySearch(entry.Files, file) >= 0) matching.Add(entry);
                byFile[file] = matching.ToArray();
            }
            return byFile;
        }

        // Each registration sees the claim state left by the earlier ones. A throwing callback is counted,
        // its claim is rolled back and the next registration still runs; its renderer writes are not undone.
        internal void Notify(PaintEntry<TPaint>[] entries, ref TPaint paint)
        {
            for (int index = 0; index < entries.Length; index++)
            {
                paint.Enter(index + 1);
                try { entries[index].Handler(ref paint); }
                catch (Exception ex)
                {
                    paint.Rollback();
                    Fail(entries[index], ex);
                }
            }
        }

        private void Fail(PaintEntry entry, Exception error)
        {
            Interlocked.Increment(ref failures);
            long count = Interlocked.Increment(ref entry.Failures);
            if ((count & (count - 1)) != 0) return;
            // Logging must never compromise the paint or another registration.
            try { PaintDiagnostics.ReportCallback(siteName + " " + entry.Owner + "/" + entry.Id + " (failure " + count + ")", error); } catch { }
        }
    }
}
