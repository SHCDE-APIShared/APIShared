using System;
using APIShared.Events;

namespace APIShared.Economy
{
    /// <summary>Native market helper direction, including AI trade planning/execution and ally transfer valuation.</summary>
    public enum MarketPriceDirection
    {
        /// <summary>Purchase helper.</summary>
        Buy,
        /// <summary>Sale/ally valuation helper.</summary>
        Sell
    }

    /// <summary>A synchronous price query. Inputs are unchanged native values, not a trade acknowledgement.</summary>
    public sealed class MarketPricePreEventArgs : InterceptionPreEventArgs
    {
        private int replacement;
        internal MarketPricePreEventArgs(MarketPriceDirection direction, IntPtr manager, int playerId, int good, int amount)
        { Direction = direction; HasNativeManager = manager != IntPtr.Zero; PlayerId = playerId; Good = good; Amount = amount; }
        /// <summary>Native helper being called.</summary>
        public MarketPriceDirection Direction { get; }
        /// <summary>Whether the native manager argument is nonzero. This does not establish pointer validity or a game session.</summary>
        public bool HasNativeManager { get; }
        /// <summary>Unmodified native player argument; the helper itself does not validate or use it.</summary>
        public int PlayerId { get; }
        /// <summary>Unmodified native goods index; validate before accessing a goods table.</summary>
        public int Good { get; }
        /// <summary>Unmodified signed native amount.</summary>
        public int Amount { get; }
        /// <summary>Result used when Pre vetoes the helper; defaults to zero. Assignment alone does not veto. Later Pre callbacks may compose a replacement; setting SkipOriginalFunction false cannot undo a veto.</summary>
        public int ReplacementTotal { get => replacement; set { CheckMutable(); replacement = value; } }
        internal override object Capture() => replacement;
        internal override void Restore(object snapshot) { replacement = (int)snapshot; }
    }

    /// <summary>Final price query result, including a replacement. This is not a purchase, sale or network acknowledgement.</summary>
    public sealed class MarketPricePostEventArgs
    {
        internal MarketPricePostEventArgs(MarketPricePreEventArgs pre, int result, object state)
        { Direction = pre.Direction; HasNativeManager = pre.HasNativeManager; PlayerId = pre.PlayerId; Good = pre.Good;
            Amount = pre.Amount; Result = result; WasSkipped = pre.SkipOriginalFunction; State = state; }
        /// <summary>Native helper direction.</summary>
        public MarketPriceDirection Direction { get; }
        /// <summary>Whether the original native manager argument was nonzero.</summary>
        public bool HasNativeManager { get; }
        /// <summary>Unmodified native player argument.</summary>
        public int PlayerId { get; }
        /// <summary>Unmodified goods index.</summary>
        public int Good { get; }
        /// <summary>Unmodified signed quantity.</summary>
        public int Amount { get; }
        /// <summary>Final signed total, with Vanilla's unchecked arithmetic unless explicitly replaced.</summary>
        public int Result { get; }
        /// <summary>Whether Pre replaced/skipped the helper.</summary>
        public bool WasSkipped { get; }
        /// <summary>This registration's own Pre state.</summary>
        public object State { get; }
    }

    /// <summary>Shared process owner of the two audited native price helpers. Consumer-specific player/setting policies remain in consumers.</summary>
    public static class MarketPriceEvents
    {
        // Static factory with explicit context: an observed query creates no closure or delegate.
        private static readonly Func<Completion, object, MarketPricePostEventArgs> PostFactory =
            (completion, state) => new MarketPricePostEventArgs(completion.Pre, completion.Result, state);
        internal static readonly InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs> Registry =
            new InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs>(MarketPriceNativeRuntime.EnsureInstalled);
        /// <summary>Registers on the startup thread after the extender LibraryLoaded event. Requires the audited native image; unknown images or occupied helper entries fail closed. Installs both permanent hooks on first use. An activation or post-write validation failure keeps any published hooks and originals alive, disables notifications, and prevents installation retries until process exit. Runs on the actual native caller thread, ordered by order/ordinal owner/ID, without replay. Pre vetoes are sticky; failing Pre mutations roll back. Post observes executed and vetoed queries. Recursive queries from callbacks use the original without notifications. Use the same deterministic policy on every multiplayer peer and across planning and execution; the sell helper also values ally transfers. Returns false with a diagnostic on duplicate identity or installation failure.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<MarketPricePreEventArgs> pre,
            Action<MarketPricePostEventArgs> post, out string reason, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order);
        /// <summary>Isolated callback error count.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        /// <summary>Vanilla helper arithmetic: signed division before multiplication, with unchecked Int32 overflow. Does not read or change prices.</summary>
        public static int CalculateTradeTotal(int basePrice, int amount) => unchecked((basePrice / 5) * amount);
        internal static int Dispatch(MarketPriceDirection direction, IntPtr manager, int playerId, int good, int amount,
            MarketPriceNativeRuntime.PriceCall original)
        {
            if (!Registry.CanNotify) return original(manager, playerId, good, amount);
            return DispatchObserved(Registry, direction, manager, playerId, good, amount, original);
        }
        // Production observed path; contract tests pass their own registry.
        internal static int DispatchObserved(InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs> registry,
            MarketPriceDirection direction, IntPtr manager, int playerId, int good, int amount, MarketPriceNativeRuntime.PriceCall original)
        {
            var invocation = registry.Begin(new MarketPricePreEventArgs(direction, manager, playerId, good, amount));
            int result = invocation.Pre.SkipOriginalFunction ? invocation.Pre.ReplacementTotal : original(manager, playerId, good, amount);
            if (invocation.HasPostObservers)
                invocation.Complete(new Completion(invocation.Pre, result), PostFactory);
            return result;
        }
        private readonly struct Completion
        {
            internal readonly MarketPricePreEventArgs Pre;
            internal readonly int Result;
            internal Completion(MarketPricePreEventArgs pre, int result) { Pre = pre; Result = result; }
        }
    }
}
