using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsfCompanion
{
    static class SteamLocator
    {
        const ulong SteamId64Base = 76561197960265728UL;
        /// <summary>Counter-Strike: Source on Steam.</summary>
        public const string CssAppId = "240";

        /// <summary>
        /// Steam's folders on this PC: the usual install (~/.steam/steam, ~/.local/share/Steam - also what NixOS's
        /// programs.steam uses), the Flatpak and the Snap. STEAM_DIR, when set, comes first. Each one only once.
        /// </summary>
        public static IEnumerable<string> SteamRoots()
        {
            var home = Program.Home;
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataHome)) dataHome = Path.Combine(home, ".local", "share");
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("STEAM_DIR"),
                Path.Combine(home, ".steam", "root"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(dataHome, "Steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam"),
                Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                var real = RealPath(candidate);
                if (!seen.Add(real)) continue;
                if (Directory.Exists(Path.Combine(real, "steamapps")) || Directory.Exists(Path.Combine(real, "config"))) yield return real;
            }
        }

        /// <summary>Finds .../Counter-Strike Source/cstrike, either from settings or across all Steam libraries.</summary>
        public static string FindCstrikeDir(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && !configured.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var dir = ExpandHome(configured.Trim().Trim('"'));
                if (Directory.Exists(Path.Combine(dir, "cstrike"))) dir = Path.Combine(dir, "cstrike");
                return Directory.Exists(Path.Combine(dir, "cfg")) ? dir : null;
            }

            foreach (var library in SteamLibraries())
            {
                var dir = Path.Combine(library, "steamapps", "common", "Counter-Strike Source", "cstrike");
                if (Directory.Exists(Path.Combine(dir, "cfg"))) return dir;
            }
            return null;
        }

        static string ExpandHome(string path) =>
            path == "~" ? Program.Home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Program.Home, path.Substring(2)) : path;

        /// <summary>Every Steam library: each Steam folder itself and the libraries its libraryfolders.vdf lists.</summary>
        static IEnumerable<string> SteamLibraries()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var steam in SteamRoots())
            {
                if (seen.Add(steam)) yield return steam;
                foreach (var file in new[] { Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Path.Combine(steam, "config", "libraryfolders.vdf") })
                {
                    var folders = Vdf.Load(file);
                    var list = folders?.NodeAt("libraryfolders") ?? folders?.NodeAt("LibraryFolders");
                    if (list == null) continue;
                    foreach (var entry in list.Values)
                    {
                        // Newer Steam: "0" { "path" "/mnt/games/SteamLibrary" ... }; older: "1" "/mnt/games/SteamLibrary".
                        var path = entry is Vdf.Node node ? node.TextAt("path") : entry as string;
                        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                        var real = RealPath(path);
                        if (seen.Add(real)) yield return real;
                    }
                }
            }
        }

        /// <summary>The folder with symlinks (like ~/.steam/steam) followed, so the same Steam isn't looked at twice.</summary>
        static string RealPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd('/');
                var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                return target != null ? Path.GetFullPath(target.FullName).TrimEnd('/') : full;
            }
            catch (IOException) { return path; }
            catch (UnauthorizedAccessException) { return path; }
        }

        /// <summary>
        /// The player's SteamID in the STEAM_0:X:Y form KSF uses. "auto" means whoever is logged into Steam right now
        /// (Steam's registry.vdf), or else the account that logged in last (loginusers.vdf).
        /// </summary>
        public static string FindSteamId(string configured)
        {
            configured = configured?.Trim() ?? "";
            if (configured.Length > 0 && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return ParseSteamId(configured);

            var home = Program.Home;
            foreach (var registry in new[]
                     {
                         Path.Combine(home, ".steam", "registry.vdf"),
                         Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "registry.vdf"),
                         Path.Combine(home, "snap", "steam", "common", ".steam", "registry.vdf"),
                     })
            {
                var active = Vdf.Load(registry)?.TextAt("Registry", "HKCU", "Software", "Valve", "Steam", "ActiveProcess", "ActiveUser");
                if (uint.TryParse(active, out var account) && account != 0) return FromAccountId(account);
            }

            foreach (var steam in SteamRoots())
            {
                var users = Vdf.Load(Path.Combine(steam, "config", "loginusers.vdf"))?.NodeAt("users");
                if (users == null) continue;
                var recent = users.FirstOrDefault(u => u.Value is Vdf.Node user && user.TextAt("MostRecent") == "1").Key;
                if (recent != null && ParseSteamId(recent) is string id) return id;
            }

            // Neither says (newer Steam, or files it writes differently): the account whose settings Steam saved last.
            var newest = SteamRoots()
                .SelectMany(steam => SafeDirectories(Path.Combine(steam, "userdata")))
                .Select(dir => (account: uint.TryParse(Path.GetFileName(dir), out var a) ? a : 0, config: Path.Combine(dir, "config", "localconfig.vdf")))
                .Where(u => u.account != 0 && File.Exists(u.config))
                .OrderByDescending(u => File.GetLastWriteTimeUtc(u.config))
                .FirstOrDefault();
            return newest.account != 0 ? FromAccountId(newest.account) : null;
        }

        static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        /// <summary>
        /// Counter-Strike: Source's launch options as set in Steam for this account ("" when there are none), or null
        /// when Steam's config for it can't be found.
        /// </summary>
        public static string LaunchOptions(string steam2)
        {
            if (!(AccountId(steam2) is uint account)) return null;
            foreach (var steam in SteamRoots())
            {
                var config = Vdf.Load(Path.Combine(steam, "userdata", account.ToString(System.Globalization.CultureInfo.InvariantCulture), "config", "localconfig.vdf"));
                var apps = config?.NodeAt("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                if (apps == null) continue;
                return apps.NodeAt(CssAppId)?.TextAt("LaunchOptions") ?? "";
            }
            return null;
        }

        /// <summary>Whether launch options include a flag, as a word of its own ("-usercon", not "-usercontent").</summary>
        public static bool HasLaunchOption(string options, string flag) =>
            options != null && Regex.IsMatch(options, @"(^|\s)" + Regex.Escape(flag) + @"(\s|$)", RegexOptions.IgnoreCase);

        public static string ParseSteamId(string text)
        {
            var steam2 = Regex.Match(text, @"^STEAM_[0-5]:([01]):(\d+)$", RegexOptions.IgnoreCase);
            if (steam2.Success) return $"STEAM_0:{steam2.Groups[1].Value}:{steam2.Groups[2].Value}";

            var steam3 = Regex.Match(text, @"^\[?U:1:(\d+)\]?$", RegexOptions.IgnoreCase);
            if (steam3.Success && uint.TryParse(steam3.Groups[1].Value, out var account)) return FromAccountId(account);

            if (ulong.TryParse(text, out var id64) && id64 > SteamId64Base) return FromAccountId((uint)(id64 - SteamId64Base));
            return null;
        }

        public static string FromAccountId(uint accountId) => $"STEAM_0:{accountId & 1}:{accountId >> 1}";

        /// <summary>STEAM_0:Y:Z back to the account number the game's "status" shows as [U:1:number].</summary>
        public static uint? AccountId(string steam2)
        {
            var m = Regex.Match(steam2 ?? "", @"^STEAM_[0-5]:([01]):(\d+)$");
            return m.Success && uint.TryParse(m.Groups[2].Value, out var z) ? z * 2 + uint.Parse(m.Groups[1].Value) : (uint?)null;
        }
    }
}
