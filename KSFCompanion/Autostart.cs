using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KsfCompanion
{
    /// <summary>
    /// The optional "Start when I log in": an XDG autostart entry (~/.config/autostart/ksf-companion.desktop) that
    /// starts KSF Companion hidden in the tray. The NixOS module can put one in /etc/xdg/autostart instead; turning it
    /// off then writes the user's own "Hidden" entry over it, as the XDG spec has it. Started by a systemd user
    /// service (the Home Manager module sets KSFC_AUTOSTART=systemd), it's managed there and can't be changed here.
    /// </summary>
    static class Autostart
    {
        const string FileName = Program.AppId + ".desktop";

        static string ConfigHome
        {
            get
            {
                var value = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : Path.Combine(Program.Home, ".config");
            }
        }

        static string UserEntry => Path.Combine(ConfigHome, "autostart", FileName);

        /// <summary>Entries the system put in place (XDG_CONFIG_DIRS, /etc/xdg by default).</summary>
        static IEnumerable<string> SystemEntries()
        {
            var dirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
            if (string.IsNullOrWhiteSpace(dirs)) dirs = "/etc/xdg";
            return dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d, "autostart", FileName))
                .Where(File.Exists);
        }

        /// <summary>Started some other way you set up (a systemd user service, or a read-only entry from your Nix config).</summary>
        public static bool IsManaged =>
            Environment.GetEnvironmentVariable("KSFC_AUTOSTART") == "systemd" || IsReadOnly(UserEntry);

        public static bool IsEnabled
        {
            get
            {
                if (Environment.GetEnvironmentVariable("KSFC_AUTOSTART") == "systemd") return true;
                if (File.Exists(UserEntry)) return !IsHidden(UserEntry);
                return SystemEntries().Any(e => !IsHidden(e));
            }
        }

        public static void Set(bool enabled)
        {
            if (IsManaged) return;
            var system = SystemEntries().FirstOrDefault(e => !IsHidden(e));
            if (enabled)
            {
                // The system's entry starts it already: just take away the user's "Hidden" over it.
                if (system != null) Delete(UserEntry);
                else Write(UserEntry, Entry(hidden: false));
            }
            else if (system != null) Write(UserEntry, Entry(hidden: true));
            else Delete(UserEntry);
        }

        static string Entry(bool hidden)
        {
            var lines = new List<string>
            {
                "[Desktop Entry]",
                "Type=Application",
                "Name=" + Program.AppName,
                "Comment=KSF surf dashboard for Counter-Strike: Source, started in the tray",
                $"Exec={Launcher} --background",
                "Icon=" + Program.AppId,
                "Terminal=false",
                "X-GNOME-Autostart-enabled=" + (hidden ? "false" : "true"),
            };
            if (hidden) lines.Add("Hidden=true");
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>
        /// How to start KSF Companion from a desktop entry: its command name when that's on PATH (stays right across
        /// Nix updates), else the launcher the Nix wrapper names (KSFC_LAUNCHER), else this program itself.
        /// </summary>
        static string Launcher
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (path.Split(':', StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir, Program.AppId))))
                    return Program.AppId;
                var launcher = Environment.GetEnvironmentVariable("KSFC_LAUNCHER");
                var exe = !string.IsNullOrEmpty(launcher) ? launcher : Environment.ProcessPath;
                return exe.Contains(' ') ? "\"" + exe + "\"" : exe;
            }
        }

        static bool IsHidden(string file)
        {
            try
            {
                return File.ReadLines(file).Select(l => l.Trim()).Any(l =>
                    l.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) || l.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>A link into the Nix store (Home Manager's xdg.autostart) can't be changed from here.</summary>
        static bool IsReadOnly(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return info.Exists && (info.LinkTarget?.StartsWith("/nix/store/", StringComparison.Ordinal) == true || info.IsReadOnly);
            }
            catch (IOException) { return false; }
        }

        static void Write(string file, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file) && new FileInfo(file).LinkTarget != null) File.Delete(file);
            File.WriteAllText(file, content);
        }

        static void Delete(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
