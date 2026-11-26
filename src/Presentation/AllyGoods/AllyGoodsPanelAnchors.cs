using System;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>Explicit semantic roles for compatible goods-dialog layouts; no gameplay authority.</summary>
    public enum AllyGoodsPanelAnchorRole
    {
        /// <summary>No shared anchor.</summary>
        None = 0,
        /// <summary>The visible goods-dialog backdrop, excluding optional extension space.</summary>
        GoodsFrame = 1,
        /// <summary>The bottom goods-information/confirmation box with a free lower strip.</summary>
        Footer = 2,
        /// <summary>Optional direct decorative child of the marked footer. The footer broker excludes
        /// only this explicit element from occupancy checks; unmarked foreign buttons are not decoration.</summary>
        FooterBackground = 3
    }

    /// <summary>Additive Unity-thread layout markers. Preserves names, content, tags, commands and DataContexts.
    /// Replacement UIs may annotate their corresponding elements. GoodsFrame and Footer must occur exactly
    /// once below the shared bound root; FooterBackground is optional and may occur at most once directly
    /// inside Footer. Duplicates disable only role-dependent features until host recreation.</summary>
    public static class AllyGoodsPanelAnchors
    {
        /// <summary>Attached role, initially None. Set before the bound host's first layout resolution.</summary>
        public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
            "Role", typeof(AllyGoodsPanelAnchorRole), typeof(AllyGoodsPanelAnchors), new PropertyMetadata(AllyGoodsPanelAnchorRole.None));
        /// <summary>Reads the explicit role. Requires the initialized UI thread.</summary>
        public static AllyGoodsPanelAnchorRole GetRole(DependencyObject element) => (AllyGoodsPanelAnchorRole)element.GetValue(RoleProperty);
        /// <summary>Sets a supported role on Unity; does not change layout or register a section.</summary>
        public static void SetRole(DependencyObject element, AllyGoodsPanelAnchorRole role)
        {
            if (role < AllyGoodsPanelAnchorRole.None || role > AllyGoodsPanelAnchorRole.FooterBackground)
                throw new ArgumentOutOfRangeException(nameof(role));
            element.SetValue(RoleProperty, role);
        }
    }
}
