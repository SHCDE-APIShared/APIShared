using System;
using System.IO;
using System.Collections.Generic;
using APIShared.Internal;

namespace APIShared.DependencyBootstrap
{
    internal static class PairTests
    {
        private static int checks;
        private static void Check(bool valid, string name) { if (!valid) throw new Exception("Pair: " + name); checks++; }
        private static void Write(string root, string relative, string text)
        {
            string path = PairContract.SafePath(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text);
        }
        private static void Copy(string source, string target, string relative) => Write(target, relative, File.ReadAllText(PairContract.SafePath(source, relative)));
        private static void Install(string source, string target)
        {
            foreach (var path in new[] { PairContract.RuntimePath, PairContract.BootstrapPath, PairContract.ManifestPath }) Copy(source, target, path);
        }
        private static bool Valid(string root)
        {
            try { PairContract.Read(root); return true; } catch { return false; }
        }
        private static void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name);
        }
        internal static void Run(string game)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
            try
            {
            PairActivation.VerifyPatcherFiles(Path.Combine(game, "BepInEx", "patchers"),
                Path.Combine(game, "BepInEx", "patchers", "APIShared.DependencyBootstrap.dll")); checks++;
            string patchers = Path.Combine(root, "patchers");
            Directory.CreateDirectory(Path.Combine(patchers, "helpers"));
            string helper = Path.Combine(patchers, "helpers", "native.dll");
            File.Copy(Path.Combine(game, "BepInEx", "patchers", "SHCDESE.KillSwitch", "blake3_dotnet.dll"), helper);
            PairActivation.VerifyPatcherFiles(patchers, "unused"); checks++;
            string candidate = Path.Combine(patchers, "renamed.dll");
            foreach (string name in new[] { "Unrelated.Helper", "APIShared.DependencyBootstrap", "DependencyBootstrapProbe.Patcher" })
            {
                using (var assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly(new Mono.Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Mono.Cecil.ModuleKind.Dll))
                    assembly.Write(candidate);
                if (name == "Unrelated.Helper") { PairActivation.VerifyPatcherFiles(patchers, "unused"); checks++; }
                else Reject(() => PairActivation.VerifyPatcherFiles(patchers, "unused"), "renamed competitor " + name);
            }
            File.WriteAllText(candidate, "broken image");
            Reject(() => PairActivation.VerifyPatcherFiles(patchers, "unused"), "malformed DLL does not bypass ownership");
            File.Delete(candidate);
            using (var locked = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => PairActivation.VerifyPatcherFiles(patchers, "unused"), "unreadable DLL rejects ownership");
            File.Copy(helper, Path.Combine(patchers, "APIShared.DependencyBootstrap.dll"));
            Reject(() => PairActivation.VerifyPatcherFiles(patchers, "unused"), "native image at bootstrap path rejected");
            string a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), live = Path.Combine(root, "live");
            foreach (string revision in new[] { a, b })
            {
                Write(revision, PairContract.RuntimePath, "runtime " + revision);
                Write(revision, PairContract.BootstrapPath, "bootstrap " + revision);
                Write(revision, PairContract.ManifestPath, DependencyFreeJson.Serialize(new Dictionary<string, object> {
                    {"Schema",1},{"Guid","APIShared_Serp"},{"Version","1.2.3"},
                    {"RuntimeSHA256",PairContract.Hash(PairContract.SafePath(revision,PairContract.RuntimePath))},
                    {"BootstrapSHA256",PairContract.Hash(PairContract.SafePath(revision,PairContract.BootstrapPath))} }));
            }
            string[] paths = { PairContract.RuntimePath, PairContract.BootstrapPath, PairContract.ManifestPath };
            foreach (var order in new[] { "012", "021", "102", "120", "201", "210" })
                foreach (bool first in new[] { false, true })
                {
                    string target = Path.Combine(root, order + first);
                    if (!first) Install(a, target);
                    for (int i = 0; i < 3; i++) { Copy(b, target, paths[order[i] - '0']); Check(Valid(target) == (i == 2), "copy prefix " + order + first + i); }
                }
            Install(a, live);
            var pair = PairContract.Read(live);
            Check(pair.MatchesSelected("APIShared_Serp", "1.2.3", PairContract.SafePath(live, PairContract.RuntimePath)), "actual selected runtime");
            Check(!pair.MatchesSelected("other", "1.2.3", PairContract.SafePath(live, PairContract.RuntimePath)), "foreign guid");
            Check(!pair.MatchesSelected("APIShared_Serp", "9.0", PairContract.SafePath(live, PairContract.RuntimePath)), "foreign version");
            Check(!pair.MatchesSelected("APIShared_Serp", "1.2.3", PairContract.SafePath(a, PairContract.RuntimePath)), "identical bytes in other path");
            pair.VerifyBootstrap(PairContract.SafePath(live, PairContract.BootstrapPath), 1, false); checks++;
            Reject(() => pair.VerifyBootstrap(PairContract.SafePath(live, PairContract.BootstrapPath), 2, false), "duplicate bootstrap");
            Reject(() => pair.VerifyBootstrap(PairContract.SafePath(live, PairContract.BootstrapPath), 1, true), "probe conflict");
            Reject(() => pair.VerifyBootstrap(PairContract.SafePath(a, PairContract.BootstrapPath), 1, false), "renamed bootstrap");
            foreach (string path in paths)
            {
                Install(a, live); File.Delete(PairContract.SafePath(live, path)); Check(!Valid(live), "missing " + path);
                Install(a, live); Copy(b, live, path); Check(!Valid(live), "same version wrong bytes " + path);
            }
            foreach (string malformed in new[] { "{", "{}", "{\"Schema\":1}", File.ReadAllText(PairContract.SafePath(a, PairContract.ManifestPath)).Replace("\"Schema\": 1", "\"Schema\": 2") })
            {
                Install(a, live); Write(live, PairContract.ManifestPath, malformed);
                if (malformed.Contains("RuntimeSHA256") && malformed == File.ReadAllText(PairContract.SafePath(a, PairContract.ManifestPath)))
                    throw new Exception("Schema mutation did not change input");
                Check(!Valid(live), "malformed manifest");
            }
            Install(a, live); Copy(b, live, PairContract.RuntimePath);
            Check(!pair.MatchesSelected("APIShared_Serp", "1.2.3", PairContract.SafePath(live, PairContract.RuntimePath)), "change after initial validation");
            Install(b, live); Check(Valid(live), "manual complete repair");
            Install(a, live);
            pair = PairContract.Read(live);
            bool ownerFails = false;
            var activation = new PairActivation(pair, () => { if (ownerFails) throw new IOException("ownership unavailable"); });
            var winner = new ShadowPlugin { Guid = "APIShared_Serp", Version = "1.2.3", Path = PairContract.SafePath(live, PairContract.RuntimePath) };
            Check(!activation.CanCorrect(), "unbound activation rejected");
            Check(activation.Bind(new[] { winner }) && activation.CanCorrect(), "selected activation accepted");
            ownerFails = true;
            Check(!activation.CanCorrect(), "late owner exception rejected");
            ownerFails = false;
            Check(!activation.CanCorrect(), "rejection remains inactive without rebinding");
            Check(!activation.Bind(new[] { winner, winner }), "ambiguous owner rejected");
            Check(activation.Bind(new[] { winner }), "valid owner can be explicitly bound");
            File.AppendAllText(PairContract.SafePath(live, PairContract.ManifestPath), " ");
            Check(!activation.CanCorrect(), "manifest changed after binding rejected");
            Install(a, live);
            activation = new PairActivation(PairContract.Read(live), () => { });
            Check(activation.Bind(new[] { winner }), "fresh pair binds");
            Copy(b, live, PairContract.RuntimePath);
            var original = new System.Collections.Hashtable();
            Check(ReferenceEquals(original, ShadowRuntime.SelectIndex(original, () => { throw new Exception("must not prepare"); }, activation.CanCorrect())), "late changed runtime keeps original index");
            // New packages bind Content bytes too; the preceding cases retain coverage of
            // historical packages without Content rather than rewriting their contract.
            Install(a, live);
            Write(live, PairContract.ContentPath, "content revision a");
            Check(!Valid(live), "unclaimed Content rejected");
            var manifest = (Dictionary<string, object>)DependencyFreeJson.Parse(File.ReadAllText(PairContract.SafePath(live, PairContract.ManifestPath)));
            manifest.Add("ContentSHA256", PairContract.Hash(PairContract.SafePath(live, PairContract.ContentPath)));
            Write(live, PairContract.ManifestPath, DependencyFreeJson.Serialize(manifest));
            pair = PairContract.Read(live); checks++;
            Write(live, PairContract.ContentPath, "content revision b");
            Check(!Valid(live), "same-version mixed Content rejected");
            Reject(pair.VerifyFiles, "Content change after initial validation rejected");
            File.Delete(PairContract.SafePath(live, PairContract.ContentPath));
            Check(!Valid(live), "missing declared Content rejected");
            Write(live, PairContract.ContentPath, "content revision a");
            Check(Valid(live), "complete Content repair accepted");
            manifest["ContentSHA256"] = "invalid digest";
            Write(live, PairContract.ManifestPath, DependencyFreeJson.Serialize(manifest));
            Check(!Valid(live), "malformed Content digest rejected");
            Console.WriteLine("PASS: " + checks + " actual pair-contract cases, including installed native helper and renamed competitors");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
