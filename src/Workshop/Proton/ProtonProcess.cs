using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                return Capture(new ProcessStartInfo(shell, Quote("-c") + " " + Quote("uname -s")), Remaining(elapsed, budget)).Trim() == "Linux";
            }
            return false;
        }

        internal static TimeSpan Remaining(Stopwatch elapsed, TimeSpan budget)
        {
            TimeSpan remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Linux path/interpreter verification exceeded its time budget.");
            return remaining;
        }

        internal static string Capture(ProcessStartInfo start, TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero) throw new TimeoutException("No time remains for the Linux host process.");
            var elapsed = Stopwatch.StartNew();
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using (var process = Process.Start(start)) {
                if (process == null) throw new IOException("Linux host process did not start.");
                try {
                    Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                    int milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, Remaining(elapsed, budget).TotalMilliseconds));
                    if (!process.WaitForExit(milliseconds))
                        throw new TimeoutException("Linux host process did not finish within its time budget.");
                    if (!Task.WaitAll(new Task[] { output, error }, (int)Math.Min(int.MaxValue, Math.Max(1, Remaining(elapsed, budget).TotalMilliseconds))))
                        throw new TimeoutException("Linux host process output did not close within its time budget.");
                    if (process.ExitCode != 0) throw new IOException("Linux host process failed (exit " + process.ExitCode + "): " + error.Result.Trim());
                    return output.Result;
                } catch {
                    // Only the child created above is owned here. Close redirected
                    // streams too: an exited shell's descendant may retain its pipe.
                    try { if (!process.HasExited) process.Kill(); }
                    catch (Exception cleanup) when (cleanup is InvalidOperationException || cleanup is System.ComponentModel.Win32Exception || cleanup is NotSupportedException) { }
                    try { process.StandardOutput.Close(); } catch (Exception cleanup) when (cleanup is IOException || cleanup is InvalidOperationException) { }
                    try { process.StandardError.Close(); } catch (Exception cleanup) when (cleanup is IOException || cleanup is InvalidOperationException) { }
                    throw;
                }
            }
        }
    }

    internal sealed class ProtonExecutionContext
    {
        internal ProtonPaths Paths { get; }
        internal string BashPath { get; }
        private ProtonExecutionContext(ProtonPaths paths, string bash) { Paths = paths; BashPath = bash; }

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
            var paths = new ProtonPaths(windowsGameRoot, options, api);
            paths.ToHostExisting(paths.WindowsGameRoot);
            ProtonProcess.Remaining(elapsed, budget);
            string bash = options.WineBashPath;
            if (!string.IsNullOrEmpty(bash)) {
                bash = ProtonPaths.WindowsAbsolute(bash);
                if (!File.Exists(bash)) throw new FileNotFoundException("Configured WineBashPath does not exist.", bash);
            } else {
                foreach (string host in new[] { "/bin/bash", "/usr/bin/bash" }) {
                    string candidate;
                    if (api.TryToWindows(host, out candidate) && File.Exists(candidate)) { bash = candidate; break; }
                }
                if (string.IsNullOrEmpty(bash)) throw new FileNotFoundException("Host Bash could not be resolved through Wine. Configure WineBashPath in APIShared.ProtonPaths.cfg.");
            }
            if (!ProtonProcess.IsLinuxWine(api)) {
                string kernel = ProtonProcess.Capture(new ProcessStartInfo(bash,
                    ProtonProcess.Quote("-c") + " " + ProtonProcess.Quote("uname -s")), ProtonProcess.Remaining(elapsed, budget));
                if (kernel.Trim() != "Linux") throw new PlatformNotSupportedException("The Wine host was not confirmed as Linux.");
            }
            var context = new ProtonExecutionContext(paths, bash);
            context.ValidateGameIdentity(ProtonProcess.Remaining(elapsed, budget));
            return context;
        }

        internal ProcessStartInfo CreateStartInfo(string hostScript, IEnumerable<string> arguments)
        {
            hostScript = ProtonPaths.HostAbsolute(hostScript);
            return new ProcessStartInfo(BashPath, string.Join(" ", new[] { hostScript }.Concat(arguments ?? Enumerable.Empty<string>()).Select(ProtonProcess.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
        }

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
                var start = new ProcessStartInfo(BashPath, string.Join(" ", new[] { "-c", command, "apishared-path-probe", hostMarker, token }.Select(ProtonProcess.Quote)));
                ProtonProcess.Capture(start, ProtonProcess.Remaining(elapsed, budget));
            } catch (Exception error) {
                throw new IOException("Configured HostGameDirectory does not identify the active game directory; automatic installation was refused.", error);
            } finally {
                ProtonPaths.CheckUnlinked(marker);
                if (File.Exists(marker) && File.ReadAllText(marker) == token) File.Delete(marker);
            }
        }
    }
}
