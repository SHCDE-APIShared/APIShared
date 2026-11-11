using APIShared.Lobby;
using APIShared.Localization;
using APIShared.Networking;
using APIShared.Savegames;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace APIShared
{
    internal static partial class MapRecordLoadCoordinator
    {
        private sealed class LobbySnapshot
        {
            internal readonly ulong Lobby;
            internal readonly string Map, Settings, Aiv, MaxPlayers, GameType, ModFingerprint, Fingerprint;
            internal readonly int? Crc;
            internal readonly int AiPlayers, TrailId, Mission;
            // Customized launch origin of this peer: kind, trail and mission; all zero without one.
            internal readonly int OriginKind, OriginTrail, OriginMission;
            internal readonly byte[] Digests;
            internal readonly bool Trail, OrderSwapped;
            internal readonly KeyValuePair<ulong, int>[] Slots;
            internal readonly Dictionary<string, Dictionary<string, byte[]>> Mods;

            internal LobbySnapshot(Platform_Multiplayer.MPLobby lobby)
            {
                Lobby = lobby.id.m_SteamID;
                Map = lobby.mapFileName ?? "";
                Crc = int.TryParse(lobby.crc, NumberStyles.Integer, CultureInfo.InvariantCulture, out int crc) ? (int?)crc : null;
                Settings = lobby.settings ?? "";
                Aiv = lobby.AIVDataChecksum() ?? "";
                MaxPlayers = lobby.maxPlayers ?? "";
                // Vanilla publishes CountAIPlayers() to Steam but never maintains the host's AIPlayers field.
                AiPlayers = lobby.members != null && lobby.members.All(member => member != null) ? lobby.CountAIPlayers() : -1;
                GameType = lobby.gameTypeCoop ?? "";
                Trail = lobby.coopTrailGame;
                // Vanilla leaves inactive trail fields at different defaults on the host and joining client.
                TrailId = Trail ? lobby.coopTrailID : 0;
                Mission = Trail ? lobby.coopSelectedMission : 0;
                OrderSwapped = Trail && lobby.coopOrderSwapped;
                // Every peer classifies the mission from its own launch-origin provider. Peers that
                // disagree there would run different gameplay rules, so the origin is part of the
                // agreement and a difference blocks the start like any other Coop difference.
                var origin = GameModes.CustomizedLaunchOrigins.Capture();
                OriginKind = origin.IsInvalid ? -1 : origin.Origin;
                OriginTrail = OriginKind > 0 ? origin.TrailId : 0;
                OriginMission = OriginKind > 0 ? origin.MissionId : 0;
                Slots = LobbyReadinessSupport.ObservedHumans(lobby).Select(id => new KeyValuePair<ulong, int>(id, lobby.getThisPlayerFromSteamID(id))).ToArray();
                Mods = SavegameModSettings.CaptureHostSettingsForReadiness();
                ModFingerprint = SavegameLoadAgreement.Fingerprint(Mods);
                Digests = new byte[Categories.Length * DigestLength];
                using (var memory = new MemoryStream())
                using (var writer = new BinaryWriter(memory))
                using (var sha = SHA256.Create())
                {
                    // Vanilla already publishes FileHeader.fileName without the file extension.
                    writer.Write(Map.ToUpperInvariant());
                    writer.Write(Crc.HasValue);
                    if (Crc.HasValue) writer.Write(Crc.Value);
                    WriteDigest(0, memory, sha);
                    writer.Write(Settings); writer.Write(MaxPlayers); writer.Write(GameType);
                    WriteDigest(1, memory, sha);
                    writer.Write(AiPlayers); writer.Write(Aiv);
                    WriteDigest(2, memory, sha);
                    writer.Write(Trail); writer.Write(TrailId); writer.Write(Mission); writer.Write(OrderSwapped);
                    // Written only for a customized launch: an ordinary lobby keeps the digest of
                    // builds that did not compare the origin.
                    if (OriginKind != 0) { writer.Write(OriginKind); writer.Write(OriginTrail); writer.Write(OriginMission); }
                    WriteDigest(3, memory, sha);
                    writer.Write(Slots.Length);
                    foreach (var slot in Slots) { writer.Write(slot.Key); writer.Write(slot.Value); }
                    WriteDigest(4, memory, sha);
                    writer.Write(ModFingerprint);
                    WriteDigest(5, memory, sha);
                }
                Fingerprint = Digest(Digests);
            }

            private void WriteDigest(int category, MemoryStream memory, SHA256 sha)
            {
                byte[] digest = sha.ComputeHash(memory.GetBuffer(), 0, (int)memory.Length);
                Buffer.BlockCopy(digest, 0, Digests, category * DigestLength, DigestLength);
                memory.SetLength(0);
            }

            internal IEnumerable<string> PropertyDigests()
            {
                foreach (var mod in Mods.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    yield return "mod=" + mod.Key + " properties=" + mod.Value.Count;
                    foreach (var property in mod.Value.OrderBy(x => x.Key, StringComparer.Ordinal))
                        yield return "mod=" + mod.Key + " property=" + property.Key + " sha256=" + Digest(property.Value);
                }
            }

            internal string Summary() => $"map={Map}, crc={Crc}, vanillaSettings={Digest(System.Text.Encoding.UTF8.GetBytes(Settings))}, " +
                $"ai={AiPlayers}, aiv={Aiv}, maxPlayers={MaxPlayers}, gameType={GameType}, trail={Trail}/{TrailId}/{Mission}/{OrderSwapped}, customizedOrigin={OriginKind}/{OriginTrail}/{OriginMission}, " +
                $"slots={string.Join(",", Slots.Select(slot => slot.Key + ":" + slot.Value))}, modSettings={ModFingerprint}, fingerprint={Fingerprint}";

            internal List<string> DescribeChanges(LobbySnapshot current, List<string> detail = null)
            {
                var categories = Differences(Digests, current.Digests).ToList();
                if (ModFingerprint == current.ModFingerprint) return categories;
                if (detail == null) return categories;
                foreach (string mod in Mods.Keys.Union(current.Mods.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                {
                    Mods.TryGetValue(mod, out var before);
                    current.Mods.TryGetValue(mod, out var after);
                    if (before == null || after == null) detail.Add("mod " + mod + (before == null ? " added" : " removed"));
                    foreach (string property in (before?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                        .Union(after?.Keys.AsEnumerable() ?? Enumerable.Empty<string>(), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        byte[] oldValue = null, newValue = null;
                        before?.TryGetValue(property, out oldValue);
                        after?.TryGetValue(property, out newValue);
                        if (oldValue != null && newValue != null && oldValue.SequenceEqual(newValue)) continue;
                        detail.Add(mod + "." + property + ": " + Digest(oldValue) + " -> " + Digest(newValue));
                    }
                }
                return categories;
            }

            internal static string Digest(byte[] bytes)
            {
                if (bytes == null) return "missing";
                return Sha256Hex.Of(bytes);
            }
        }
    }
}
