using APIShared.Workshop.Proton;
using BepInEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace APIShared.ProtonWorkshopFix
{
    // Process-lifetime callback only. The Extender still owns consent and game shutdown.
    internal static class ExtenderDeployment
    {
        internal const string Protocol = "APISHARED-SE-PROTON-1";
        private const string ResourceName = "APIShared.ProtonWorkshopFix.extender-updater.sh";
        private const string CacheRelative = "BepInEx/cache/APIShared.ProtonWorkshopFix";
        private static readonly ConditionalWeakTable<object, RetrySnapshot> retrySnapshots = new ConditionalWeakTable<object, RetrySnapshot>();
        private static readonly object contextGate = new object();
        private static ProtonExecutionContext processContext;
        private static string contextRoot;

        private sealed class RetrySnapshot { internal string Hash; }
        private sealed class Prepared
        {
            internal ProtonExecutionContext Context;
            internal string Directory, Script, Request, Hash, ScriptHash;
        }

        internal static bool PreflightRetry(object manager)
        {
            Prepared prepared = null;
            try
            {
                if (!EligiblePlatform()) return false;
                string root = GameRoot();
                string staging = Field(manager, "_stagingDirectory");
                string manifest = Field(manager, "_deleteManifestPath");
                var context = Context(root);
                string hostRoot = context.Paths.ToHostExisting(root).TrimEnd('/');
                if (context.Paths.ToHostExisting(Field(manager, "_seDirectory")) != hostRoot + "/_SE" ||
                    context.Paths.ToHostTarget(Field(manager, "_deployAttemptsPath")) != hostRoot + "/_SE/.deploy_attempts")
                    throw new InvalidDataException("Extender deployment fields do not identify this game.");
                string executable;
                using (var self = Process.GetCurrentProcess()) executable = self.MainModule.FileName;
                prepared = Prepare(staging, root, manifest, executable);
                var start = prepared.Context.CreateStartInfo(prepared.Context.Paths.ToHostExisting(prepared.Script),
                    new[] { prepared.Context.Paths.ToHostExisting(prepared.Request), prepared.Hash, "--probe" });
                ProtonProcess.Capture(start, TimeSpan.FromSeconds(5));
                retrySnapshots.Remove(manager);
                retrySnapshots.Add(manager, new RetrySnapshot { Hash = prepared.Hash });
                return true;
            }
            catch (Exception error)
            {
                PatcherLog.Warning("Linux deployment retry preflight refused: " + error.Message);
                return false;
            }
            finally { CleanupProbe(prepared); }
        }

        internal static bool Launch(object manager, string staging, string target, string deleteManifest, string gameExe, int gamePid)
        {
            Prepared prepared = null;
            Process child = null;
            try
            {
                if (!EligiblePlatform() || !RetryGate.CanLaunch(manager)) return false;
                prepared = Prepare(staging, target, deleteManifest, gameExe);
                RetrySnapshot snapshot;
                if (retrySnapshots.TryGetValue(manager, out snapshot) && snapshot.Hash != prepared.Hash)
                    throw new IOException("Prepared Workshop files changed after the retry preflight; installation was refused.");
                using (var self = Process.GetCurrentProcess())
                    if (gamePid != self.Id) throw new InvalidDataException("Extender game PID differs from this process.");
                var start = prepared.Context.CreateStartInfo(prepared.Context.Paths.ToHostExisting(prepared.Script),
                    new[] { prepared.Context.Paths.ToHostExisting(prepared.Request), prepared.Hash });
                // The child journals failures to its own run directory. It does not inherit pipes
                // whose closure would prevent the parent from leaving after confirmed readiness.
                child = Process.Start(start);
                if (child == null) throw new IOException("Linux deployment process did not start.");
                string ready = PatcherFiles.Inside(prepared.Directory, "ready.flag");
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (child.HasExited) throw new IOException("Linux deployment rejected its request (exit " + child.ExitCode + "). Inspect " + prepared.Directory);
                    try
                    {
                        PatcherFiles.CheckLinks(ready);
                        if (File.Exists(ready) && File.ReadAllText(ready) == prepared.Hash)
                        {
                            // Recheck child state after reading readiness; a leftover flag from a
                            // process that already failed cannot authorize the Extender's Kill().
                            if (child.HasExited) throw new IOException("Linux deployment exited before handoff confirmation.");
                            PatcherLog.Info("Linux deployment handoff confirmed; Extender may close the game. Evidence: " + prepared.Directory);
                            return true;
                        }
                    }
                    catch (IOException) { if (child.HasExited) throw; }
                    Thread.Sleep(100);
                }
                throw new TimeoutException("Linux deployment readiness timed out; game remains running. Inspect " + prepared.Directory);
            }
            catch (Exception error)
            {
                Cancel(prepared);
                if (child != null)
                {
                    try { if (!child.HasExited) { child.Kill(); child.WaitForExit(1000); } }
                    catch (Exception stopError) { PatcherLog.Warning("Could not stop owned Linux updater after cancellation: " + stopError.Message); }
                }
                PatcherLog.Error("Linux deployment was not handed off: " + error.Message);
                return false;
            }
            finally { child?.Dispose(); }
        }

        private static bool EligiblePlatform() => WinePathInterop.Instance.IsWine &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId"));

        private static string GameRoot()
        {
            string root = Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            PatcherFiles.CheckLinks(root);
            return root;
        }

        private static string Field(object owner, string name)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var field = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(string)) throw new MissingFieldException(owner.GetType().FullName, name);
            return (string)field.GetValue(owner);
        }

        private static ProtonExecutionContext Context(string root)
        {
            lock (contextGate)
            {
                if (processContext != null)
                {
                    if (!PatcherFiles.SamePath(root, contextRoot)) throw new InvalidDataException("Game directory changed inside one process.");
                    return processContext;
                }
                var candidate = PatcherRuntime.Context;
                if (candidate == null)
                    candidate = ProtonExecutionContext.Create(root, ProtonConfiguration.Load(Paths.ConfigPath), TimeSpan.FromSeconds(5));
                processContext = candidate;
                contextRoot = root;
                return candidate;
            }
        }

        private static Prepared Prepare(string staging, string target, string deleteManifest, string gameExe)
        {
            string root = GameRoot();
            PatcherFiles.CheckLinks(target);
            PatcherFiles.CheckLinks(staging);
            if (!Directory.Exists(staging)) throw new DirectoryNotFoundException("Extender staging directory is absent.");
            ProtonExecutionContext context = Context(root);
            context.ValidateGameIdentity(TimeSpan.FromSeconds(5));
            string hostRoot = context.Paths.ToHostExisting(root).TrimEnd('/');
            if (context.Paths.ToHostExisting(target).TrimEnd('/') != hostRoot)
                throw new InvalidDataException("Extender target does not identify this game.");
            string hostStaging = context.Paths.ToHostExisting(staging);
            if (hostStaging != hostRoot + "/_SE/.staging") throw new InvalidDataException("Translated staging escaped the active game directory.");
            if (!string.IsNullOrEmpty(deleteManifest) &&
                context.Paths.ToHostTarget(deleteManifest) != hostStaging + "/delete_list.txt")
                throw new InvalidDataException("Unexpected translated Extender delete manifest.");
            if (!File.Exists(gameExe) || context.Paths.ToHostExisting(Path.GetDirectoryName(gameExe)).TrimEnd('/') != hostRoot)
                throw new InvalidDataException("Game executable does not belong to the active game directory.");
            string executable = Path.GetFileName(gameExe);
            CheckValue(executable);
            var lines = new List<string> { Protocol, "game\t" + hostRoot, "staging\t" + hostStaging, "exe\t" + executable };
            // Find is executed directly with a fixed program string and a separate path
            // argument. Refuse staged links/devices/FIFOs before opening files for hashing.
            const string typeProbe = "find \"$1\" -mindepth 1 ! -type d ! -type f -print -quit";
            var probe = new ProcessStartInfo(context.BashPath, string.Join(" ",
                new[] { "-c", typeProbe, "apishared-staging-types", hostStaging }.Select(ProtonProcess.Quote)));
            if (ProtonProcess.Capture(probe, TimeSpan.FromSeconds(5)).Length != 0)
                throw new InvalidDataException("Extender staging contains a link or nonregular file; no source was opened.");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var elapsed = Stopwatch.StartNew();
            string stagedManifest = Path.Combine(staging, "delete_list.txt");
            var deleteEntries = new List<string>();
            if (File.Exists(stagedManifest))
            {
                if (string.IsNullOrEmpty(deleteManifest)) throw new InvalidDataException("Existing delete list was omitted from the Extender context.");
                PatcherFiles.CheckLinks(stagedManifest);
                byte[] manifestBytes = File.ReadAllBytes(stagedManifest);
                using (var sha = SHA256.Create())
                    lines.Add("manifest\tdelete_list.txt\t" + BitConverter.ToString(sha.ComputeHash(manifestBytes)).Replace("-", "").ToLowerInvariant());
                // Parse exactly the bytes bound into the request, rather than reopening
                // a potentially changed delete list between hashing and translation.
                using (var memory = new MemoryStream(manifestBytes))
                using (var reader = new StreamReader(memory, Encoding.UTF8, true))
                    for (string line; (line = reader.ReadLine()) != null;) deleteEntries.Add(line);
            }
            foreach (string file in EnumerateFiles(staging).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (PatcherFiles.SamePath(file, Path.Combine(staging, "delete_list.txt"))) continue;
                string relative = Relative(staging, file);
                CheckRelative(relative);
                if (!targets.Add(relative)) throw new InvalidDataException("Colliding staged paths: " + relative);
                lines.Add("write\t" + relative + "\t" + PatcherFiles.Hash(file));
                ProtonProcess.Remaining(elapsed, TimeSpan.FromSeconds(20));
            }
            if (deleteEntries.Count != 0)
            {
                foreach (string entry in deleteEntries)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    // A valid victim may use another Wine drive alias for the same game.
                    // Reject lexical traversal first, then use canonical host containment.
                    ProtonPaths.WindowsAbsolute(entry);
                    PatcherFiles.CheckLinks(entry);
                    string host = context.Paths.ToHostTarget(entry);
                    if (!host.StartsWith(hostRoot + "/", StringComparison.Ordinal)) throw new InvalidDataException("Translated deletion escaped the active game directory.");
                    string relative = host.Substring(hostRoot.Length + 1);
                    CheckRelative(relative);
                    if (!targets.Add(relative)) throw new InvalidDataException("Conflicting delete/write destination: " + relative);
                    lines.Add("delete\t" + relative);
                }
            }
            if (targets.Count == 0) throw new InvalidDataException("Extender deployment request contains no operations.");
            foreach (string value in new[] { hostRoot, hostStaging }) CheckValue(value);
            string cache = PatcherFiles.Inside(root, CacheRelative);
            Directory.CreateDirectory(cache); PatcherFiles.CheckLinks(cache);
            string directory = PatcherFiles.Inside(cache, "r-" + Guid.NewGuid().ToString("N").Substring(0, 16));
            if (Directory.Exists(directory)) throw new IOException("Owned deployment run already exists.");
            Directory.CreateDirectory(directory); PatcherFiles.CheckLinks(directory);
            var prepared = new Prepared { Context = context, Directory = directory,
                Script = PatcherFiles.Inside(directory, "extender-updater.sh"), Request = PatcherFiles.Inside(directory, "request.tsv") };
            using (var resource = typeof(ExtenderDeployment).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (resource == null) throw new InvalidDataException("Embedded Linux updater is absent.");
                using (var output = new FileStream(prepared.Script, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) resource.CopyTo(output);
            }
            prepared.ScriptHash = PatcherFiles.Hash(prepared.Script);
            using (var output = new FileStream(prepared.Request, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                foreach (string line in lines) writer.WriteLine(line);
            }
            prepared.Hash = PatcherFiles.Hash(prepared.Request);
            return prepared;
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count != 0)
            {
                string directory = pending.Pop(); PatcherFiles.CheckLinks(directory);
                foreach (string file in Directory.GetFiles(directory)) { PatcherFiles.CheckLinks(file); yield return file; }
                foreach (string child in Directory.GetDirectories(directory)) { PatcherFiles.CheckLinks(child); pending.Push(child); }
            }
        }

        private static string Relative(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escaped game/staging directory: " + path);
            PatcherFiles.CheckLinks(full);
            return full.Substring(prefix.Length).Replace('\\', '/');
        }

        private static void CheckValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || c == 127)) throw new InvalidDataException("Empty protocol value or control character.");
        }

        private static void CheckRelative(string relative)
        {
            CheckValue(relative);
            string[] parts = relative.Split('/');
            if (relative.StartsWith("/", StringComparison.Ordinal) || relative.Contains("\\") || relative.Contains(":") ||
                parts.Any(p => p.Length == 0 || p == "." || p == "..")) throw new InvalidDataException("Invalid relative deployment path: " + relative);
            string lower = relative.ToLowerInvariant();
            if (lower == "bepinex/cache" || lower.StartsWith("bepinex/cache/", StringComparison.Ordinal) ||
                lower == "bepinex/config" || lower == "bepinex/config/apishared.protonpaths.cfg" || lower == "bepinex/config/apishared.protonworkshopfix" ||
                lower.StartsWith("bepinex/config/apishared.protonworkshopfix/", StringComparison.Ordinal) ||
                lower == "bepinex/patchers" || lower == "bepinex/patchers/apishared.protonworkshopfix.dll")
                throw new InvalidDataException("Deployment would replace its own infrastructure: " + relative);
            bool payload = parts.Length >= 2 && parts[0] == "BepInEx";
            bool metadata = parts.Length == 2 && parts[0] == "_SE" && parts[1].EndsWith(".json", StringComparison.Ordinal);
            if (!payload && !metadata) throw new InvalidDataException("Unexpected Extender staging destination: " + relative);
        }

        private static void Cancel(Prepared prepared)
        {
            if (prepared == null) return;
            try
            {
                string file = PatcherFiles.Inside(prepared.Directory, "cancel.flag");
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = Encoding.ASCII.GetBytes(prepared.Hash); output.Write(bytes, 0, bytes.Length); output.Flush(true);
                }
            }
            catch (Exception error) { PatcherLog.Warning("Could not persist owned updater cancellation: " + error.Message); }
        }

        private static void CleanupProbe(Prepared prepared)
        {
            if (prepared == null) return;
            try
            {
                // Probe mode creates no journal/ready files. Remove only the exact two files
                // we created; an unexpected file keeps the directory for inspection.
                if (File.Exists(prepared.Request) && PatcherFiles.Hash(prepared.Request) == prepared.Hash) File.Delete(prepared.Request);
                PatcherFiles.CheckLinks(prepared.Script);
                if (File.Exists(prepared.Script) && PatcherFiles.Hash(prepared.Script) == prepared.ScriptHash) File.Delete(prepared.Script);
                if (!Directory.EnumerateFileSystemEntries(prepared.Directory).Any()) Directory.Delete(prepared.Directory);
            }
            catch (Exception error) { PatcherLog.Warning("Could not remove owned Linux preflight files: " + error.Message); }
        }
    }
}
