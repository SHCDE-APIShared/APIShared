using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using APIShared.Patching;
using APIShared.Steam;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.DependencyBootstrap
{
    internal static class SteamDependencyTests
    {
        internal static void Run(string game)
        {
            int calls = 0;
            Func<uint, SteamAppAccess> query = id => { calls++; return id == 2140020 ? SteamAppAccess.Available : SteamAppAccess.NotAvailable; };
            string Check(string json) => SteamDependencyManifest.GetBlockReason(json, () => true, query);
            Require(Check("{\"Name\":\"old\",\"Name\":\"new\",\"Extra\":{\"a\":1e500,\"a\":18446744073709551616,\"SteamAppDependencies\":null}}") == null && calls == 0,
                "Unrelated accepted legacy metadata must not affect access.");
            Require(Check("{\"SteamAppDependencies\":[]}") == null && calls == 0, "Empty list is optional.");
            Require(Check("{\"SteamAppDependen\\u0063ies\":[2140020,2140020]}") == null && calls == 1, "Query each declared ID once.");
            Require(Check("{\"SteamAppDependencies\":[2140020,1]}")?.Contains("app 1") == true && calls == 3, "All IDs must pass.");
            Require(SteamDependencyManifest.GetBlockReason("{\"SteamAppDependencies\":[2140020]}", () => false, query) != null && calls == 3, "Reject unsupported manifest without Steam query.");
            Require(SteamDependencyManifest.GetBlockReason("{\"SteamAppDependencies\":[2140020]}", () => true,
                _ => SteamAppAccess.SteamUnavailable)?.Contains("could not be checked") == true, "Unavailable differs from no access.");
            foreach (string json in new[] { "{\"SteamAppDependencies\":null}", "{\"SteamAppDependencies\":[0]}",
                "{\"SteamAppDependencies\":[1.0]}", "{\"SteamAppDependencies\":[-1]}",
                "{\"SteamAppDependencies\":[4294967296]}", "{\"SteamAppDependencies\":[\"1\"]}",
                "{\"SteamAppDependencies\":[],\"SteamAppDependencies\":[]}" })
            {
                try { Check(json); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Invalid Steam dependency accepted: " + json);
            }

            var extender = Assembly.LoadFrom(Path.Combine(game, "BepInEx/plugins/000shcdese/SHCDESE.dll"));
            var gate = SteamDependencyRegistration.Validate(extender);
            using (var copy = new DynamicMethodDefinition(gate.Target))
            using (var il = new ILContext(copy.Definition))
            {
                var first = il.Body.Instructions[0];
                Require(SafeIlHook.TryEdit(il, SteamDependencyRegistration.AddRegistrationGate, out var error),
                    "Gate could not be emitted for the installed Extender: " + error);
                object branch = il.Body.Instructions[2].Operand;
                Require(il.Body.Instructions[2].OpCode == OpCodes.Brtrue && (branch is ILLabel label ? label.Target : branch) == first &&
                    il.Body.Instructions[3].OpCode == OpCodes.Ret && il.Body.Instructions[4] == first,
                    "Allowed registration must begin at the original entry; denial must return before it.");
                copy.Generate();
            }
            try
            {
                SteamDependencyRegistration.Validate(typeof(SteamDependencyTests).Assembly);
                throw new Exception("Unrelated loader binary accepted.");
            }
            catch (TypeLoadException) { }

            // A refused candidate must release its held FileShare.Read handle.
            var read = gate.Target.DeclaringType.GetMethod("TryReadCandidate", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            string path = Path.Combine(Path.GetTempPath(), "as-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".semod");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var entry = new StreamWriter(archive.CreateEntry("info.json").Open()))
                    entry.Write("{\"GUID\":\"SteamDependencyProbe\",\"Name\":\"Probe\",\"Version\":\"1.0\",\"SteamAppDependencies\":[2140020]}");
                object candidate = read.Invoke(null, new object[] { path });
                Require(candidate != null, "Fixture must be accepted by the Extender.");
                string reason = null;
                Require(gate.Allow(candidate, _ => SteamAppAccess.Available, _ => { }), "Available package proceeds.");
                Require(!gate.Allow(candidate, _ => SteamAppAccess.NotAvailable, value => reason = value) &&
                    reason.Contains("SteamDependencyProbe"), "Denied package skips with a diagnostic.");
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            finally { File.Delete(path); }
            CheckLobbyCompatibility(extender);
            Console.WriteLine("PASS: Steam dependency manifest, Extender registration IL, archive release, and lobby compatibility");
        }

        private static void CheckLobbyCompatibility(Assembly extender)
        {
            var entry = extender.GetType("SHCDESE.API.Components.ModManager.LobbyModMetadataEntry", true);
            var list = typeof(List<>).MakeGenericType(entry);
            var present = (IList)Activator.CreateInstance(list);
            var absent = (IList)Activator.CreateInstance(list);
            object mod = Activator.CreateInstance(entry, nonPublic: true);
            entry.GetProperty("Guid").SetValue(mod, "SteamDependencyProbe");
            entry.GetProperty("Clientside").SetValue(mod, true);
            present.Add(mod);
            var compare = extender.GetType("SHCDESE.API.GameNetworkAPI", true)
                .GetMethod("AreLobbyModsCompatible", BindingFlags.Static | BindingFlags.NonPublic);
            bool Compatible(IList local, IList remote) =>
                (bool)compare.Invoke(null, new object[] { local, remote, null });
            Require(Compatible(present, absent) && Compatible(absent, present),
                "Current lobby metadata must tolerate an asset mod active on only one peer.");
            entry.GetProperty("Clientside").SetValue(mod, false);
            Require(!Compatible(present, absent) && !Compatible(absent, present),
                "The same mismatch must reject gameplay mods.");
        }

        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
