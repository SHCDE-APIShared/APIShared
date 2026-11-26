using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using APIShared.Workshop.Install;

// Process host for tools/Validation/Test-WorkshopInstall.ps1: the script kills these commands at
// random moments and checks that the next "preloader" run always ends in the complete new version
// and that no moment ever exposes a partial plugin folder. Same source file as APIShared.dll
// and the preloader (src/Workshop/Install/WorkshopSafeInstall.cs).
internal static class Program
{
    private const ulong Id = 77;
    private static readonly string[] Targets = { "BepInEx/plugins/PkgA", "BepInEx/plugins/PkgB" };

    private static int Main(string[] args)
    {
        try
        {
            string game = args[1];
            int delay = args.Length > 2 && int.TryParse(args[2], out int ms) ? ms : 0;
            switch (args[0])
            {
                case "prepare": Prepare(game); break;
                case "apply":
                    if (delay > 0) InstallSwap.Fault = _ => Thread.Sleep(delay);
                    InstallSwap.Apply(new InstallPaths(game), Id, _ => { });
                    break;
                case "preloader":
                    if (delay > 0) InstallSwap.Fault = _ => Thread.Sleep(delay);
                    InstallRunner.Run(game, _ => { }, message => throw new InvalidOperationException(message));
                    break;
                case "stage":
                    if (delay > 0) InstallStaging.AfterCopy = _ => Thread.Sleep(delay);
                    StageBig(game);
                    break;
                case "check": Console.WriteLine(Check(game)); break;
                case "group-prepare":
                    StageGroup(game, 1);
                    if (!InstallRunner.Run(game, _ => { }, message => throw new InvalidOperationException(message))) return 1;
                    StageGroup(game, 2);
                    break;
                case "group-run":
                    if (args.Length > 2)
                        InstallBatch.Fault = step => {
                            if (step != args[2]) return;
                            File.WriteAllText(Path.Combine(game, "..", "paused"), step);
                            Thread.Sleep(Timeout.Infinite);
                        };
                    if (!InstallRunner.Run(game, _ => { }, message => Console.WriteLine(message))) return 1;
                    break;
                case "group-check": Console.WriteLine(CheckGroup(game)); break;
                default: Console.Error.WriteLine("usage: prepare|apply|preloader|stage|check|group-prepare|group-run|group-check <game> [delayMs|step]"); return 2;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static void StageGroup(string game, int version)
    {
        var paths = new InstallPaths(game);
        ulong[] ids = { 1, 2 };
        foreach (ulong id in ids)
        {
            string source = Path.Combine(game, "..", "group-source", id.ToString());
            string relative = "mod.dll";
            Write(source, new Dictionary<string, string> { [relative] = id + ":" + version });
            string file = Path.Combine(source, relative);
            var root = new InstallRoot("BepInEx/plugins/Member" + id, source, new[] { new InstallFile(relative, new FileInfo(file).Length, InstallTree.Sha256(file)) });
            InstallStaging.StageInstall(paths, id, "Member" + id, new[] { root }, version.ToString(), _ => { }, request: false);
        }
        InstallStaging.PublishBatch(paths, ids);
    }

    private static string CheckGroup(string game)
    {
        var paths = new InstallPaths(game);
        string[] contents = new ulong[] { 1, 2 }.Select(id => File.ReadAllText(Path.Combine(paths.Target("BepInEx/plugins/Member" + id), "mod.dll")) +
            "/" + File.ReadAllText(paths.Receipt(id))).ToArray();
        bool pending = InstallStaging.Pending(paths, 1) || InstallStaging.Pending(paths, 2) || InstallRunner.RequiresRecovery(game);
        return string.Join(",", contents) + (pending ? "|pending" : "|clean");
    }

    private static Dictionary<string, string> Files(int v) => new Dictionary<string, string>
    {
        ["PkgA/a.dll"] = "a" + v, ["PkgA/only-" + v + ".txt"] = "v" + v, ["PkgA/settings.json"] = "default-" + v,
        ["PkgA/apishared-keep.txt"] = "settings.json\nconfig/\n", ["PkgA/sub/deep.dll"] = "d" + v, ["PkgB/b.dll"] = "b" + v,
    };

    private static void Write(string root, IDictionary<string, string> files)
    {
        foreach (var file in files)
        {
            string path = Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, file.Value);
        }
    }

    private static string Stage(InstallPaths paths, string source, IDictionary<string, string> files, int v)
    {
        Write(source, files);
        var roots = Targets.Select(target => {
            string dir = Path.Combine(source, target.Split('/')[2]);
            return new InstallRoot(target, dir, InstallTree.Files(dir).Select(p => new InstallFile(p, new FileInfo(Path.Combine(dir, p)).Length, InstallTree.Sha256(Path.Combine(dir, p)))).ToList());
        }).ToList();
        return InstallStaging.StageInstall(paths, Id, "PkgId", roots, "{\"Version\":\"" + v + "\"}", _ => { });
    }

    // Installed version 1 with player files, staged version 2, and the expected trees.
    private static void Prepare(string game)
    {
        var paths = new InstallPaths(game);
        Stage(paths, Path.Combine(game, "..", "src1"), Files(1), 1);
        InstallSwap.Apply(paths, Id, _ => { });
        var player = new Dictionary<string, string> { ["PkgA/settings.json"] = "player", ["PkgA/config/user.cfg"] = "player cfg", ["PkgA/junk.log"] = "junk" };
        Write(Path.Combine(game, "BepInEx", "plugins"), player);
        Stage(paths, Path.Combine(game, "..", "src2"), Files(2), 2);
        var oldTree = Files(1); foreach (var p in player) oldTree[p.Key] = p.Value;
        var newTree = Files(2);
        newTree["PkgA/settings.json"] = "player"; newTree["PkgA/config/user.cfg"] = "player cfg";
        Write(Path.Combine(game, "..", "expect-old"), oldTree);
        Write(Path.Combine(game, "..", "expect-new"), newTree);
    }

    // Large staging source for kill tests during the copy.
    private static void StageBig(string game)
    {
        var files = new Dictionary<string, string>();
        for (int i = 0; i < 400; i++) files["PkgA/data/f" + i + ".bin"] = new string((char)('a' + i % 26), 2000 + i);
        files["PkgB/b.dll"] = "b";
        Stage(new InstallPaths(game), Path.Combine(game, "..", "srcbig"), files, 9);
    }

    private static string TreeId(string root)
    {
        if (!Directory.Exists(root)) return "missing";
        var text = new StringBuilder();
        foreach (string p in InstallTree.Files(root)) text.Append(p).Append('=').Append(InstallTree.Sha256(Path.Combine(root, p))).Append('\n');
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    // Per root: old|new|missing|partial, then the receipt version and leftovers.
    private static string Check(string game)
    {
        var paths = new InstallPaths(game);
        var states = Targets.Select(target => {
            string name = target.Split('/')[2];
            string actual = TreeId(paths.Target(target));
            if (actual == "missing") return "missing";
            if (actual == TreeId(Path.Combine(game, "..", "expect-old", name))) return "old";
            if (actual == TreeId(Path.Combine(game, "..", "expect-new", name))) return "new";
            return "partial";
        });
        string receipt = File.Exists(paths.Receipt(Id)) ? File.ReadAllText(paths.Receipt(Id)) : "none";
        bool leftovers = File.Exists(paths.Journal(Id)) || File.Exists(paths.Request(Id)) || Directory.Exists(paths.Container(Id)) ||
            Enumerable.Range(0, Targets.Length).Any(i => Directory.Exists(paths.Backup(Id, i)));
        return string.Join(",", states) + "|" + receipt + "|" + (leftovers ? "leftovers" : "clean");
    }
}
