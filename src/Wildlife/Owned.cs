using System;
using APIShared.Events;

namespace APIShared.Wildlife
{
    // Owner-bound herd/hunter callback shared by HerdRuntime and HuntablePreyRuntime.
    // FailureLogged makes failure logging one-shot per registration.
    internal sealed class Owned<T>
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly T Callback;
        internal int FailureLogged;
        internal Owned(string owner, string id, int order, T callback) { Owner = owner; Id = id; Order = order; Callback = callback; }
        internal static readonly Func<Owned<T>, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
    }
}
