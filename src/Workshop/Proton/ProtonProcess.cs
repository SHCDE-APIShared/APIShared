using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace APIShared.Workshop.Proton
{
    internal static class ProtonProcess
    {
        // Quote for CreateProcess/Wine argv parsing, not for a shell command string.
        internal static string Quote(string value)
        {
            if (value == null || value.Any(c => c < 32 || c == 127)) throw new ArgumentException("Invalid process argument.");
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }

        internal static bool IsLinuxWine(IWinePathApi api)
        {
            if (api == null || !api.IsWine) return false;
            string kernel;
            if (!api.TryToWindows("/proc/sys/kernel/ostype", out kernel)) return false;
            try { return File.ReadAllText(kernel).Trim() == "Linux"; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool ConfirmLinuxWine(IWinePathApi api, TimeSpan budget)
        {
            if (api == null || !api.IsWine) return false;
            if (IsLinuxWine(api)) return true;
            var elapsed = Stopwatch.StartNew();
            foreach (string host in new[] { "/bin/bash", "/usr/bin/bash" }) {
                string shell;
                if (!api.TryToWindows(host, out shell) || !File.Exists(shell)) continue;
                return RunHost(api, shell, new[] { "-c", "uname -s" }, Remaining(elapsed, budget)).Trim() == "Linux";
            }
            return false;
        }

        // Resolves exactly one Wine drive link ("<prefix>/dosdevices/<x>:") on the host. The
        // fixed program text receives the link as a separate argument; ProtonPaths validates
        // and caches the result, so this runs at most once per drive and process.
        internal static string ResolveDriveLink(IWinePathApi api, string bash, string link, TimeSpan budget)
        {
            string output = RunHost(api, bash, new[] { "-c", "realpath -e -- \"$1\"", "apishared-drive-root", link }, budget);
            return output.EndsWith("\n", StringComparison.Ordinal) ? output.Substring(0, output.Length - 1) : output;
        }

        internal static TimeSpan Remaining(Stopwatch elapsed, TimeSpan budget)
        {
            TimeSpan remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Linux path/interpreter verification exceeded its time budget.");
            return remaining;
        }

        // Runs host Bash with these arguments and returns its standard output. A nonzero
        // exit throws IOException with the standard error text; an unfinished run throws
        // TimeoutException and is left running, because it cannot be killed (see HostRun).
        internal static string RunHost(IWinePathApi api, string bash, IEnumerable<string> bashArguments, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) throw new TimeoutException("No time remains for the Linux host process.");
            using (var run = HostRun.Start(api, bash, bashArguments)) return run.Wait(budget);
        }
    }

    // Under Wine a started Linux program has no Windows process: ntdll's fork_and_exec
    // (Proton 10 and 11) returns process handle 0 and, for CREATE_NO_WINDOW or a GUI parent,
    // connects the program's stdin/stdout to /dev/null. Unity's Mono System.dll then reports
    // such a Process as exited at once with ExitCode 0 and empty output, and Kill cannot
    // reach it. Host programs therefore report through files in an owned run directory and
    // the Process object is never consulted. The protocol behaves the same with a native
    // Linux or Windows Bash in tests. A run cannot be stopped from here; long-running
    // updaters observe their own cancel files instead.
    internal sealed class HostRun : IDisposable
    {
        // Fixed program text; every value arrives as a separate argument. $BASH is the
        // interpreter running this wrapper, so the payload runs in the same host Bash.
        private const string Wrapper =
            "d=$1; shift; \"$BASH\" \"$@\" </dev/null >\"$d/out\" 2>\"$d/err\"; c=$?; " +
            "printf '%s\\n' \"$c\" >\"$d/code.tmp\" && mv -f -- \"$d/code.tmp\" \"$d/code\"";
        private static readonly string[] OwnedFiles = { "out", "err", "code", "code.tmp" };
        internal string RunDirectory { get; }
        private HostRun(string directory) { RunDirectory = directory; }

        internal static HostRun Start(IWinePathApi api, string bash, IEnumerable<string> bashArguments) =>
            Start(api, bash, bashArguments, Path.Combine(Path.GetTempPath(), "apishared-host"));

        internal static HostRun Start(IWinePathApi api, string bash, IEnumerable<string> bashArguments, string baseDirectory)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (string.IsNullOrEmpty(bash)) throw new ArgumentException("A host Bash is required.", nameof(bash));
            var arguments = (bashArguments ?? Enumerable.Empty<string>()).ToArray();
            Directory.CreateDirectory(baseDirectory);
            Prune(baseDirectory);
            string directory = Path.Combine(baseDirectory, "r-" + Guid.NewGuid().ToString("N").Substring(0, 16));
            if (Directory.Exists(directory)) throw new IOException("Owned host run already exists: " + directory);
            Directory.CreateDirectory(directory);
            try {
                // Wine's own path form suffices: Bash may write through Wine's drive links.
                string host;
                if (!api.TryToUnix(directory, out host)) throw new IOException("Wine could not translate the host run directory: " + directory);
                host = ProtonPaths.HostAbsolute(host);
                var start = new ProcessStartInfo(bash, string.Join(" ",
                    new[] { "-c", Wrapper, "apishared-host", host }.Concat(arguments).Select(ProtonProcess.Quote))) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process.Start(start)) { }
            } catch {
                DeleteOwned(directory); // The program did not start, so nothing writes here.
                throw;
            }
            return new HostRun(directory);
        }

        // True once the wrapper has atomically published the program's exit code.
        internal bool TryGetExit(out int code)
        {
            code = 0;
            string path = Path.Combine(RunDirectory, "code");
            if (!File.Exists(path)) return false;
            string text;
            try { text = File.ReadAllText(path).Trim(); }
            catch (IOException) { return false; }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code))
                throw new IOException("Linux host run published an invalid exit record: " + path);
            return true;
        }

        internal string Output => Read("out");
        internal string Error => Read("err");

        internal string Wait(TimeSpan budget)
        {
            var elapsed = Stopwatch.StartNew();
            int code;
            while (!TryGetExit(out code)) {
                if (elapsed.Elapsed >= budget) throw new TimeoutException("Linux host process did not finish within its time budget.");
                Thread.Sleep(20);
            }
            if (code != 0) throw new IOException("Linux host process failed (exit " + code + "): " + Error.Trim());
            return Output;
        }

        // A run without an exit record may still write; it stays until a later prune.
        public void Dispose()
        {
            if (File.Exists(Path.Combine(RunDirectory, "code"))) DeleteOwned(RunDirectory);
        }

        private string Read(string name)
        {
            string path = Path.Combine(RunDirectory, name);
            try { return File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false)) : ""; }
            catch (IOException) { return ""; }
        }

        // Removes only the owned file names; any other entry keeps its directory.
        private static void DeleteOwned(string directory)
        {
            try {
                foreach (string name in OwnedFiles) {
                    string file = Path.Combine(directory, name);
                    if (File.Exists(file)) File.Delete(file);
                }
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Handoff runs outlive the game and abandoned runs never finish; drop them after a day.
        private static void Prune(string baseDirectory)
        {
            try {
                DateTime limit = DateTime.UtcNow.AddDays(-1);
                foreach (string directory in Directory.GetDirectories(baseDirectory, "r-*"))
                    if (Directory.GetLastWriteTimeUtc(directory) < limit) DeleteOwned(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class ProtonExecutionContext
    {
        internal ProtonPaths Paths { get; }
        internal string BashPath { get; }
        internal IWinePathApi Api { get; }
        private ProtonExecutionContext(ProtonPaths paths, string bash, IWinePathApi api) { Paths = paths; BashPath = bash; Api = api; }

        internal static readonly TimeSpan DriveLinkBudget = TimeSpan.FromSeconds(2);

        // Configured WineBashPath first, otherwise the reverse translation of the host Bash.
        internal static string FindHostBash(IWinePathApi api, string configured)
        {
            if (!string.IsNullOrEmpty(configured)) {
                string bash = ProtonPaths.WindowsAbsolute(configured);
                if (!File.Exists(bash)) throw new FileNotFoundException("Configured WineBashPath does not exist.", bash);
                return bash;
            }
            foreach (string host in new[] { "/bin/bash", "/usr/bin/bash" }) {
                string candidate;
                if (api.TryToWindows(host, out candidate) && File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Host Bash could not be resolved through Wine. Configure WineBashPath in APIShared.ProtonPaths.cfg.");
        }

        internal static ProtonExecutionContext Create(string windowsGameRoot, ProtonPathOptions options, TimeSpan budget)
        {
            return Create(windowsGameRoot, options, budget, WinePathInterop.Instance);
        }
        internal static ProtonExecutionContext Create(string windowsGameRoot, ProtonPathOptions options, TimeSpan budget, IWinePathApi api)
        {
            // Native Windows must not inspect configuration, paths or host interpreters.
            if (api == null || !api.IsWine) throw new PlatformNotSupportedException("This updater correction is only available under Linux Wine/Proton.");
            var elapsed = Stopwatch.StartNew();
            options = options ?? new ProtonPathOptions();
            string bash = null;
            // Construction validates the explicit values without calling Wine. The drive-link
            // resolver needs the host Bash, so no path is translated before Linux is confirmed.
            var paths = new ProtonPaths(windowsGameRoot, options, api, link => ProtonProcess.ResolveDriveLink(api, bash, link, DriveLinkBudget));
            bash = FindHostBash(api, options.WineBashPath);
            if (!ProtonProcess.IsLinuxWine(api)) {
                string kernel = ProtonProcess.RunHost(api, bash, new[] { "-c", "uname -s" }, ProtonProcess.Remaining(elapsed, budget));
                if (kernel.Trim() != "Linux") throw new PlatformNotSupportedException("The Wine host was not confirmed as Linux.");
            }
            paths.ToHostExisting(paths.WindowsGameRoot);
            ProtonProcess.Remaining(elapsed, budget);
            var context = new ProtonExecutionContext(paths, bash, api);
            context.ValidateGameIdentity(ProtonProcess.Remaining(elapsed, budget));
            return context;
        }

        // Starts a host script ("$BASH" script arguments...) whose completion is observed
        // only through the returned run (see HostRun).
        internal HostRun StartScript(string hostScript, IEnumerable<string> arguments) =>
            HostRun.Start(Api, BashPath, new[] { ProtonPaths.HostAbsolute(hostScript) }.Concat(arguments ?? Enumerable.Empty<string>()));

        internal string RunScript(string hostScript, IEnumerable<string> arguments, TimeSpan budget) =>
            ProtonProcess.RunHost(Api, BashPath, new[] { ProtonPaths.HostAbsolute(hostScript) }.Concat(arguments ?? Enumerable.Empty<string>()), budget);

        internal void ValidateGameIdentity(TimeSpan budget)
        {
            if (!Paths.HasGameOverride) return;
            var elapsed = Stopwatch.StartNew();
            // An explicit override must identify this very game directory. The marker
            // is ours, is random, and is never copied into a package or transaction.
            string directory = Path.Combine(Paths.WindowsGameRoot, "BepInEx", "cache", "APIShared.ProtonPaths");
            ProtonPaths.CheckUnlinked(directory); Directory.CreateDirectory(directory);
            string token = Guid.NewGuid().ToString("N"), marker = Path.Combine(directory, token + ".probe");
            ProtonPaths.CheckUnlinked(marker);
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) {
                byte[] bytes = Encoding.ASCII.GetBytes(token); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            try {
                string hostMarker = Paths.ToHostExisting(marker);
                const string command = "test \"$(cat -- \"$1\")\" = \"$2\"";
                ProtonProcess.RunHost(Api, BashPath, new[] { "-c", command, "apishared-path-probe", hostMarker, token }, ProtonProcess.Remaining(elapsed, budget));
            } catch (Exception error) {
                throw new IOException("Configured HostGameDirectory does not identify the active game directory; automatic installation was refused.", error);
            } finally {
                ProtonPaths.CheckUnlinked(marker);
                if (File.Exists(marker) && File.ReadAllText(marker) == token) File.Delete(marker);
            }
        }
    }
}
