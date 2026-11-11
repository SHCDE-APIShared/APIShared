using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop.Proton
{
    internal sealed class ProtonPathOptions
    {
        public string HostGameDirectory = "";
        public string WineBashPath = "";
    }

    internal sealed class ProtonPaths
    {
        private readonly IWinePathApi api;
        private readonly Func<string, bool> exists;
        private readonly Func<string, string> resolveDriveLink;
        private readonly Dictionary<string, string> driveRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly string hostOverride;
        internal string WindowsGameRoot { get; }
        internal bool HasGameOverride => hostOverride != null;

        internal ProtonPaths(string windowsGameRoot, ProtonPathOptions options, IWinePathApi api, Func<string, string> resolveDriveLink = null)
            : this(windowsGameRoot, options, api, p => File.Exists(p) || Directory.Exists(p), resolveDriveLink) { }

        // Existence is the filesystem boundary, injectable for portable translation tests.
        // resolveDriveLink receives "<prefix>/dosdevices/<x>:" and returns the host target of
        // Wine's own drive link; it is required only where Wine reports that unresolved form.
        internal ProtonPaths(string windowsGameRoot, ProtonPathOptions options, IWinePathApi api, Func<string, bool> exists,
            Func<string, string> resolveDriveLink = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.exists = exists ?? throw new ArgumentNullException(nameof(exists));
            this.resolveDriveLink = resolveDriveLink;
            WindowsGameRoot = WindowsAbsolute(windowsGameRoot).TrimEnd('\\');
            if (WindowsGameRoot.Length == 2) WindowsGameRoot += "\\";
            if (!string.IsNullOrEmpty(options?.HostGameDirectory)) hostOverride = HostAbsolute(options.HostGameDirectory);
        }

        internal string ToHostExisting(string windowsPath)
        {
            string path = WindowsAbsolute(windowsPath);
            if (!exists(path)) throw new FileNotFoundException("Wine path does not exist: " + path, path);
            string suffix;
            if (hostOverride != null && WithinGame(path, out suffix)) return JoinHost(hostOverride, suffix);
            string host;
            if (!api.IsWine || !api.TryToUnix(path, out host)) throw new IOException("Wine could not translate path to the Linux host: " + path);
            host = Canonical(path, HostAbsolute(host));
            string roundTrip, translated;
            if (!api.TryToWindows(host, out roundTrip) || !api.TryToUnix(roundTrip, out translated) ||
                !string.Equals(Canonical(roundTrip, HostAbsolute(translated)), host, StringComparison.Ordinal))
                throw new IOException("Wine path translation failed its round-trip check: " + path);
            return host;
        }

        // Wine before 10.13 (Proton 10 and older) reports every drive except a Z: mapped to
        // "/" as "<prefix>/dosdevices/<x>:/...", whose drive component is a symlink that the
        // host updaters' link checks refuse. Later Wine reports the server's realpath, which
        // never matches here. Only Wine's own drive link is resolved; links below the drive
        // root stay visible to every later link check.
        private string Canonical(string windowsPath, string host)
        {
            if (windowsPath == null || windowsPath.Length < 3 || windowsPath[1] != ':' || !char.IsLetter(windowsPath[0])) return host;
            var match = DriveLink.Match(host);
            if (!match.Success) return host;
            if (match.Groups[2].Value[0] != char.ToLowerInvariant(windowsPath[0]))
                throw new IOException("Wine reported another drive link for " + windowsPath + ": " + host);
            string link = match.Groups[1].Value, root;
            lock (driveRoots)
            {
                if (!driveRoots.TryGetValue(link, out root))
                {
                    if (resolveDriveLink == null) throw new IOException("Wine drive link cannot be resolved for the Linux host: " + link);
                    string resolved = resolveDriveLink(link);
                    try { root = HostAbsolute(resolved ?? ""); }
                    catch (ArgumentException error) { throw new IOException("Wine drive link resolved to an invalid Linux path: " + link, error); }
                    driveRoots.Add(link, root);
                }
            }
            string rest = match.Groups[3].Value;
            return root == "/" ? (rest.Length == 0 ? "/" : rest) : root + rest;
        }
        private static readonly System.Text.RegularExpressions.Regex DriveLink = new System.Text.RegularExpressions.Regex(
            "^(/.+?/dosdevices/([a-z]):)(/.*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        internal string ToHostTarget(string windowsPath)
        {
            string path = WindowsAbsolute(windowsPath);
            var tail = new List<string>();
            string parent = path;
            while (!exists(parent)) {
                int separator = parent.LastIndexOf('\\');
                if (separator < 2 || (separator == 2 && parent.Length == 3))
                    throw new DirectoryNotFoundException("Wine drive or parent directory is unavailable: " + path);
                tail.Add(parent.Substring(separator + 1));
                parent = separator == 2 ? parent.Substring(0, 3) : parent.Substring(0, separator);
            }
            string host = ToHostExisting(parent);
            for (int i = tail.Count - 1; i >= 0; i--) host = host.TrimEnd('/') + "/" + tail[i];
            return HostAbsolute(host);
        }

        internal string ToWindowsExisting(string hostPath)
        {
            string host = HostAbsolute(hostPath), windows, again;
            if (!api.IsWine || !api.TryToWindows(host, out windows)) throw new IOException("Wine could not translate Linux path: " + host);
            windows = WindowsAbsolute(windows);
            if (!exists(windows) || !api.TryToUnix(windows, out again) || !string.Equals(Canonical(windows, HostAbsolute(again)), host, StringComparison.Ordinal))
                throw new IOException("Linux path does not identify the same existing Wine file: " + host);
            return windows;
        }

        private bool WithinGame(string path, out string suffix)
        {
            suffix = "";
            if (string.Equals(path.TrimEnd('\\'), WindowsGameRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = WindowsGameRoot.TrimEnd('\\') + "\\";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            suffix = path.Substring(prefix.Length).Replace('\\', '/'); return true;
        }
        private static string JoinHost(string root, string suffix) => HostAbsolute(root.TrimEnd('/') + (suffix.Length == 0 ? "/" : "/" + suffix));

        internal static string HostAbsolute(string value)
        {
            CheckText(value);
            if (!value.StartsWith("/", StringComparison.Ordinal) || value.Split('/').Any(p => p == "." || p == ".."))
                throw new ArgumentException("An absolute Linux path without traversal is required: " + value);
            return value.Length > 1 ? value.TrimEnd('/') : value;
        }
        internal static string WindowsAbsolute(string value)
        {
            CheckText(value); value = value.Replace('/', '\\');
            bool drive = value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '\\';
            bool wineDevice = value.StartsWith(@"\\?\unix\", StringComparison.OrdinalIgnoreCase);
            if ((!drive && !wineDevice) || value.Split('\\').Any(p => p == "." || p == ".."))
                throw new ArgumentException("An absolute Wine path without traversal is required: " + value);
            return value;
        }
        internal static void CheckText(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || c == 127)) throw new ArgumentException("Empty path or control character.");
        }
        internal static void CheckUnlinked(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked path is not supported for automatic installation: " + current); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
