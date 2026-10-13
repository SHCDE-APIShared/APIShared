namespace APIShared.Presentation
{
    internal static class BuildingTooltipSelection
    {
        internal static int Resolve(int hover, string hoverText, int selected, string selectedText,
            bool otherVisible, bool otherPriority, bool detailed, out bool hovering)
        {
            hovering = hover != 0 || !string.IsNullOrEmpty(hoverText);
            int type = hovering ? hover : selected;
            string text = hovering ? hoverText : selectedText;
            if (type <= 0 || ((string.IsNullOrEmpty(text) || (otherVisible && otherPriority && !detailed)) && otherVisible)) return 0;
            return type;
        }
    }
}
