using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Internal;
using APIShared.Steam;

namespace APIShared.DependencyBootstrap
{
    internal static class SteamDependencyManifest
    {
        internal const string Property = "SteamAppDependencies";

        // Returns the reason to skip registration, or null to continue through the original loader.
        internal static string GetBlockReason(string json, Func<bool> isClientAsset, Func<uint, SteamAppAccess> query)
        {
            if (!DependencyFreeJson.TryReadTopLevelProperty(json, Property, out object declaration))
                return null;
            if (!(declaration is List<object> entries)) throw new InvalidDataException(Property + " must be an array of Steam app IDs.");
            if (entries.Count == 0) return null;
            if (!isClientAsset()) return Property + " requires a client-side Asset manifest without a matching BepInEx plugin.";

            var distinct = new HashSet<uint>();
            var ids = new List<uint>();
            foreach (object entry in entries)
            {
                long id = entry is int small ? small : entry is long wide ? wide : 0;
                if (id <= 0 || id > uint.MaxValue)
                    throw new InvalidDataException(Property + " requires positive integer Steam app IDs in the UInt32 range.");
                if (distinct.Add((uint)id)) ids.Add((uint)id);
            }
            foreach (uint id in ids)
            {
                SteamAppAccess access = query(id);
                if (access == SteamAppAccess.NotAvailable) return "Steam reports no access to app " + id + ".";
                if (access != SteamAppAccess.Available) return "Steam access could not be checked for app " + id + ". Restart after Steam is available.";
            }
            return null;
        }
    }
}
