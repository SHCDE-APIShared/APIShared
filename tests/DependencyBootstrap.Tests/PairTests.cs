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
        internal static void Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
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
            Console.WriteLine("PASS: " + checks + " actual pair-contract cases; fixtures " + root);
        }
    }
}
