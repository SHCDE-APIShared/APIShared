using System;
using APIShared.Events;
using Noesis;

namespace APIShared.Presentation
{
    internal sealed class AllyGoodsFrameEntry
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<AllyGoodsFrameContext, FrameworkElement> Factory;
        internal readonly AllyGoodsFrameOptions Options;
        internal bool Enabled;
        internal int Revision;
        internal int AttemptRevision = -1;
        internal FrameworkElement Visual;
        internal AllyGoodsFrameEntry(string owner, string id, int order,
            Func<AllyGoodsFrameContext, FrameworkElement> factory, AllyGoodsFrameOptions options)
        { Owner = owner; Id = id; Order = order; Factory = factory; Options = options; }
        internal static readonly Func<AllyGoodsFrameEntry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
    }

    internal sealed class AllyGoodsFrameRegistry
    {
        internal AllyGoodsFrameEntry[] Entries = Array.Empty<AllyGoodsFrameEntry>();
        internal bool TryRegister(string owner, string id, Func<AllyGoodsFrameContext, FrameworkElement> factory,
            AllyGoodsFrameOptions options, int order, out AllyGoodsFrameEntry entry, out string reason)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || factory == null || options == null)
            { reason = "A nonempty frame owner/ID, factory and explicit metrics are required."; return false; }
            if (OrderedRegistrations.Contains(Entries, AllyGoodsFrameEntry.Key, owner, id))
            { reason = "Duplicate frame provider: " + owner + "/" + id; return false; }
            entry = new AllyGoodsFrameEntry(owner, id, order, factory, options);
            Entries = OrderedRegistrations.WithAdded(Entries, entry, AllyGoodsFrameEntry.Key);
            reason = string.Empty; return true;
        }
        internal AllyGoodsFrameEntry FirstEnabled()
        { foreach (var entry in Entries) if (entry.Enabled) return entry; return null; }
    }

    // The lease is independent of Noesis allocation so restoration and foreign writes
    // can be exercised without constructing an unattached native UI tree.
    internal interface IAllyGoodsFrameOpacity
    {
        bool HasBinding { get; }
        object LocalValue { get; }
        void SetHidden();
        void Restore(object previous);
    }
    internal sealed class AllyGoodsFrameOpacityLease
    {
        private readonly IAllyGoodsFrameOpacity target;
        private object previous;
        private bool acquired;
        internal AllyGoodsFrameOpacityLease(IAllyGoodsFrameOpacity target) { this.target = target; }
        internal bool IsOwned => acquired && !target.HasBinding && target.LocalValue is float value && value == 0;
        internal bool TryAcquire()
        {
            if (acquired) return IsOwned;
            if (target.HasBinding) return false;
            previous = target.LocalValue;
            acquired = true;
            try { target.SetHidden(); return true; }
            catch { Release(); throw; }
        }
        internal void Release()
        {
            if (IsOwned) target.Restore(previous);
            acquired = false; previous = null;
        }
    }
}
