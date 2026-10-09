using System;
using System.Collections.Generic;

namespace APIShared.GameModes
{
    /// <summary>The source of a customized launch, using the game's existing trail families.</summary>
    public enum CustomizedLaunchOriginKind
    {
        /// <summary>No customized launch is currently supplied by this provider.</summary>
        None,
        /// <summary>A custom trail (trail IDs 90 through 92, mission IDs starting at 1).</summary>
        CustomTrail,
        /// <summary>A cooperative trail (trail IDs 0 through 3, mission IDs 1 through 10).</summary>
        CoopTrail,
        /// <summary>A built-in trail (trail types 0 through 2, nonnegative trail and mission IDs).</summary>
        VanillaTrail,
        /// <summary>A Sands of Time trail (trail types 11 through 18, nonnegative trail and mission IDs).</summary>
        SandsOfTime
    }

    /// <summary>One coherent snapshot of a consumer's customized launch. This describes origin; it does not grant permission to another mod.</summary>
    public readonly struct CustomizedLaunchOrigin
    {
        /// <summary>Creates origin evidence. Invalid kinds or IDs are rejected during capture and mark the origin as conflicting.</summary>
        public CustomizedLaunchOrigin(CustomizedLaunchOriginKind kind, int trailType, int trailId,
            int missionId, bool restoredFromSave, bool launchPending,
            bool supportsBuiltInOrigins = true)
        {
            Kind = kind;
            TrailType = trailType;
            TrailId = trailId;
            MissionId = missionId;
            RestoredFromSave = restoredFromSave;
            LaunchPending = launchPending;
            SupportsBuiltInOrigins = supportsBuiltInOrigins;
        }

        /// <summary>The customized trail family, or None when this provider is inactive.</summary>
        public CustomizedLaunchOriginKind Kind { get; }
        /// <summary>The game's trail type; ignored for custom and cooperative trails.</summary>
        public int TrailType { get; }
        /// <summary>The trail ID in the basis documented by Kind.</summary>
        public int TrailId { get; }
        /// <summary>The mission ID in the basis documented by Kind.</summary>
        public int MissionId { get; }
        /// <summary>Whether this evidence was restored from a saved game.</summary>
        public bool RestoredFromSave { get; }
        /// <summary>Whether a customized launch or restart is still pending.</summary>
        public bool LaunchPending { get; }
        /// <summary>Whether this provider tracks built-in trails too. False preserves native-only classification when no such tracker exists.</summary>
        public bool SupportsBuiltInOrigins { get; }
    }

    /// <summary>Process-wide providers of customized launch evidence, independent of consumer names or assemblies.</summary>
    public static class CustomizedLaunchOrigins
    {
        private static readonly CustomizedLaunchOriginRegistry Registry = new CustomizedLaunchOriginRegistry();

        /// <summary>
        /// Registers one provider per exact, nonblank plugin GUID. Register during startup before
        /// the first mission load. The callback is rooted until process exit and runs synchronously
        /// on the mission capture thread, outside the registry lock; it must return a coherent
        /// snapshot without changing game state. There is no replay or implicit thread dispatch.
        /// Repeating the same owner and delegate is harmless; replacing an owner's provider throws.
        /// Callback failures, invalid evidence and simultaneous active providers mark capture as
        /// conflicting rather than enabling customized gameplay. An inactive provider returns None.
        /// </summary>
        public static void Register(string ownerGuid, Func<CustomizedLaunchOrigin> capture) =>
            Registry.Register(ownerGuid, capture);

        internal static ExternalCustomizedOrigin Capture() => Registry.Capture();
    }

    internal sealed class CustomizedLaunchOriginRegistry
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Func<CustomizedLaunchOrigin>> providers =
            new Dictionary<string, Func<CustomizedLaunchOrigin>>(StringComparer.Ordinal);

        internal void Register(string ownerGuid, Func<CustomizedLaunchOrigin> capture)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid))
                throw new ArgumentException("A non-empty plugin GUID is required.", nameof(ownerGuid));
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            lock (sync)
            {
                if (providers.TryGetValue(ownerGuid, out Func<CustomizedLaunchOrigin> existing))
                {
                    if (existing == capture) return;
                    throw new InvalidOperationException("A launch-origin provider is already registered for " + ownerGuid + ".");
                }
                providers.Add(ownerGuid, capture);
            }
        }

        internal ExternalCustomizedOrigin Capture()
        {
            Func<CustomizedLaunchOrigin>[] snapshot;
            lock (sync)
            {
                snapshot = new Func<CustomizedLaunchOrigin>[providers.Count];
                providers.Values.CopyTo(snapshot, 0);
            }
            ExternalCustomizedOrigin active = default;
            bool hasActive = false;
            bool supportsBuiltInOrigins = false;
            foreach (Func<CustomizedLaunchOrigin> capture in snapshot)
            {
                ExternalCustomizedOrigin candidate;
                try { candidate = Validate(capture()); }
                catch { return ExternalCustomizedOrigin.InvalidProvider; }
                if (candidate.IsInvalid) return ExternalCustomizedOrigin.InvalidProvider;
                supportsBuiltInOrigins |= candidate.SupportsBuiltInOrigins;
                if (candidate.Origin == ExternalCustomizedOrigin.None) continue;
                if (hasActive) return ExternalCustomizedOrigin.InvalidProvider;
                active = candidate;
                hasActive = true;
            }
            return hasActive ? active : snapshot.Length != 0
                ? ExternalCustomizedOrigin.AvailableProvider(supportsBuiltInOrigins) : default;
        }

        private static ExternalCustomizedOrigin Validate(CustomizedLaunchOrigin value)
        {
            if (value.Kind == CustomizedLaunchOriginKind.None)
                return ExternalCustomizedOrigin.AvailableProvider(value.SupportsBuiltInOrigins);
            bool valid;
            switch (value.Kind)
            {
                case CustomizedLaunchOriginKind.CustomTrail:
                    valid = value.MissionId >= 1 && value.TrailId >= 90 && value.TrailId <= 92;
                    break;
                case CustomizedLaunchOriginKind.CoopTrail:
                    valid = value.MissionId >= 1 && value.MissionId <= 10 && value.TrailId >= 0 && value.TrailId <= 3;
                    break;
                case CustomizedLaunchOriginKind.VanillaTrail:
                    valid = value.SupportsBuiltInOrigins && value.TrailType >= 0 && value.TrailType <= 2 && value.TrailId >= 0 && value.MissionId >= 0;
                    break;
                case CustomizedLaunchOriginKind.SandsOfTime:
                    valid = value.SupportsBuiltInOrigins && value.TrailType >= 11 && value.TrailType <= 18 && value.TrailId >= 0 && value.MissionId >= 0;
                    break;
                default:
                    valid = false;
                    break;
            }
            return valid ? new ExternalCustomizedOrigin((int)value.Kind, value.TrailType, value.TrailId,
                value.MissionId, value.RestoredFromSave, value.LaunchPending,
                supportsBuiltInOrigins: value.SupportsBuiltInOrigins) : ExternalCustomizedOrigin.InvalidProvider;
        }
    }

    internal readonly struct ExternalCustomizedOrigin
    {
        internal const int None = 0;
        internal const int CustomTrail = 1;
        internal const int CoopTrail = 2;
        internal const int VanillaTrail = 3;
        internal const int SandsOfTime = 4;

        internal static ExternalCustomizedOrigin InvalidProvider =>
            new ExternalCustomizedOrigin(-1, -1, -1, -1, false, false, isInvalid: true);

        internal static ExternalCustomizedOrigin AvailableProvider(bool supportsBuiltInOrigins) =>
            new ExternalCustomizedOrigin(
                None, -1, -1, -1, false, false,
                supportsBuiltInOrigins: supportsBuiltInOrigins);

        internal ExternalCustomizedOrigin(
            int origin,
            int trailType,
            int trailId,
            int missionId,
            bool restoredFromSave,
            bool launchPending = false,
            bool isInvalid = false,
            bool supportsBuiltInOrigins = false)
        {
            Origin = origin;
            TrailType = trailType;
            TrailId = trailId;
            MissionId = missionId;
            RestoredFromSave = restoredFromSave;
            LaunchPending = launchPending;
            IsInvalid = isInvalid;
            SupportsBuiltInOrigins = supportsBuiltInOrigins;
        }

        internal int Origin { get; }
        internal int TrailType { get; }
        internal int TrailId { get; }
        internal int MissionId { get; }
        internal bool RestoredFromSave { get; }
        internal bool LaunchPending { get; }
        internal bool IsInvalid { get; }
        internal bool SupportsBuiltInOrigins { get; }
    }

}
