using System;
using System.Collections.Generic;
using APIShared.Events;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    // Game-independent registration and dispatch of BuildingActions: immutable ordered snapshots, executors run
    // outside the lock and every failure stays isolated to its action.
    internal sealed class BuildingActionRegistry
    {
        internal sealed class Entry
        {
            internal readonly string Owner, Id;
            internal readonly HashSet<eStructs> Types;
            internal readonly BuildingActionExecutor Execute;
            internal readonly int WireKey;
            internal long Failures;

            internal Entry(BuildingActionOptions options)
            {
                Owner = options.OwnerGuid; Id = options.ActionId; Execute = options.Execute;
                Types = new HashSet<eStructs>(options.BuildingTypes);
                WireKey = KeyOf(Owner, Id);
            }

            internal static readonly Func<Entry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id);
        }

        private readonly object sync = new object();
        private readonly Action<string, Exception> report;
        private Entry[] entries = Array.Empty<Entry>();

        internal BuildingActionRegistry(Action<string, Exception> report) => this.report = report;

        internal bool TryRegister(BuildingActionOptions options, out string reason)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.OwnerGuid) || string.IsNullOrWhiteSpace(options.ActionId) ||
                options.Execute == null)
            { reason = "Options with a nonempty owner GUID, action ID and executor are required."; return false; }
            if (options.BuildingTypes == null || options.BuildingTypes.Count == 0)
            { reason = "At least one building type is required."; return false; }
            lock (sync)
            {
                if (OrderedRegistrations.Contains(entries, Entry.Key, options.OwnerGuid, options.ActionId))
                { reason = "Duplicate building action: " + options.OwnerGuid + "/" + options.ActionId; return false; }
                entries = OrderedRegistrations.WithAdded(entries, new Entry(options), Entry.Key);
            }
            reason = "";
            return true;
        }

        internal Entry Find(string owner, string id)
        {
            foreach (Entry entry in entries)
                if (entry.Owner == owner && entry.Id == id) return entry;
            return null;
        }

        // Null when no or several registered actions share the key; a peer must not guess between them.
        internal Entry FindByKey(int key)
        {
            Entry found = null;
            foreach (Entry entry in entries)
            {
                if (entry.WireKey != key) continue;
                if (found != null) return null;
                found = entry;
            }
            return found;
        }

        // FNV-1a (32 bit) over the UTF-16 code units of owner, '/', id: identical in every process and version.
        internal static int KeyOf(string owner, string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in owner ?? "") hash = (hash ^ c) * 16777619;
                hash = (hash ^ '/') * 16777619;
                foreach (char c in id ?? "") hash = (hash ^ c) * 16777619;
                return (int)hash;
            }
        }

        // The caller validated the building against the entry's types and the acting player.
        internal bool TryExecute(Entry entry, BuildingActionRequest request, out string reason)
        {
            try
            {
                if (entry.Execute(request, out reason)) { reason = reason ?? ""; return true; }
                reason = string.IsNullOrEmpty(reason) ? entry.Owner + "/" + entry.Id + " refused the request." : reason;
                return false;
            }
            catch (Exception error)
            {
                // Executors can run inside a Chore; no exception may cross that native boundary.
                if (FailureThrottle.ShouldReport(ref entry.Failures))
                    try { report?.Invoke(entry.Owner + "/" + entry.Id + " building action", error); } catch { }
                reason = entry.Owner + "/" + entry.Id + " failed.";
                return false;
            }
        }
    }
}
