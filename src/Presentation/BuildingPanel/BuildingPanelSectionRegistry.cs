using System;
using System.Collections.Generic;
using APIShared.Events;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    // Registration metadata, independent of Noesis construction; immutable ordered snapshots.
    internal sealed class BuildingPanelSectionRegistry
    {
        private BuildingPanelSectionEntry[] entries = Array.Empty<BuildingPanelSectionEntry>();
        internal BuildingPanelSectionEntry[] Entries => entries;

        internal bool TryRegister(BuildingPanelSectionOptions options, out BuildingPanelSectionEntry entry, out string reason)
        {
            entry = null;
            if (options == null || string.IsNullOrWhiteSpace(options.OwnerGuid) || string.IsNullOrWhiteSpace(options.RegistrationId) ||
                options.Factory == null)
            { reason = "Options with a nonempty owner GUID, section ID and factory are required."; return false; }
            if (options.BuildingTypes == null || options.BuildingTypes.Count == 0)
            { reason = "At least one building type is required."; return false; }
            if (OrderedRegistrations.Contains(entries, BuildingPanelSectionEntry.Key, options.OwnerGuid, options.RegistrationId))
            { reason = "Duplicate building panel section: " + options.OwnerGuid + "/" + options.RegistrationId; return false; }
            entry = new BuildingPanelSectionEntry(options);
            entries = OrderedRegistrations.WithAdded(entries, entry, BuildingPanelSectionEntry.Key);
            reason = "";
            return true;
        }
    }

    // Unity-thread state of one section for the currently bound building HUD.
    internal sealed class BuildingPanelSectionEntry
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly HashSet<eStructs> Types;
        internal readonly Func<BuildingPanelSectionFactoryContext, FrameworkElement> Factory;
        internal readonly Func<BuildingPanelContext, bool> ShowWhen;
        internal readonly Action<BuildingPanelSectionEventArgs> Opened, Updated, Closed;
        internal FrameworkElement Content;
        internal long ContentGeneration = -1;
        internal BuildingPanelSectionEventArgs Shown;
        // The context in which a callback failed; the section stays hidden until the context changes.
        internal BuildingPanelContext Faulted;

        internal BuildingPanelSectionEntry(BuildingPanelSectionOptions options)
        {
            Owner = options.OwnerGuid; Id = options.RegistrationId; Order = options.Order;
            Types = new HashSet<eStructs>(options.BuildingTypes);
            Factory = options.Factory; ShowWhen = options.ShowWhen;
            Opened = options.Opened; Updated = options.Updated; Closed = options.Closed;
        }

        internal static readonly Func<BuildingPanelSectionEntry, RegistrationKey> Key =
            entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
    }

    internal enum BuildingPanelSectionTransition { None, Open, Update, Close, Reopen }

    internal static class BuildingPanelSectionPolicy
    {
        // Vanilla's MainViewModel.setUpInbuilding picks the building panel from app_sub_mode: the
        // InBuildingModes INSIDE_BARRACKS (1) through INSIDE_BEDOUIN_STOCKADE (63) belong to a building
        // in in_structure; INSIDE_NULL, Peace Time, INSIDE_CHIMP and the report pages also use the
        // building HUD but show no building.
        internal const int FirstBuildingSubMode = 1, LastBuildingSubMode = 63;

        internal static bool IsBuildingSubMode(int subMode) => subMode >= FirstBuildingSubMode && subMode <= LastBuildingSubMode;

        // Contexts are reused while nothing changes, so a different instance means another building,
        // owner, viewpoint or HUD instance and therefore a new showing.
        internal static BuildingPanelSectionTransition Plan(BuildingPanelContext shown, BuildingPanelContext next, bool show)
        {
            if (shown == null) return show ? BuildingPanelSectionTransition.Open : BuildingPanelSectionTransition.None;
            if (!show) return BuildingPanelSectionTransition.Close;
            return ReferenceEquals(shown, next) ? BuildingPanelSectionTransition.Update : BuildingPanelSectionTransition.Reopen;
        }
    }
}
