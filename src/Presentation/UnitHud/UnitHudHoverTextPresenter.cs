using System;

namespace APIShared
{
    // Own only the text we published. Never clear a replacement HUD or another control's rollover.
    internal sealed class UnitHudHoverTextPresenter
    {
        private object owner, source;
        private string text;
        private Func<string> read;
        private Action close;
        internal bool IsOwnedBy(object context) => ReferenceEquals(owner, context);
        internal void Show(object context, object origin, string value, Func<string> current, Action<string> publish, Action hide)
        {
            if (context == null || origin == null || string.IsNullOrWhiteSpace(value)) return;
            if (ReferenceEquals(owner, context) && ReferenceEquals(source, origin) && text == value) return;
            Close();
            publish(value);
            owner = context; source = origin; text = value; read = current; close = hide;
        }
        internal void Close(object origin = null)
        {
            if (origin != null && !ReferenceEquals(source, origin)) return;
            string expected = text; Func<string> current = read; Action hide = close;
            owner = source = null; text = null; read = null; close = null;
            if (current != null && string.Equals(current(), expected, StringComparison.Ordinal)) hide();
        }
    }
}
