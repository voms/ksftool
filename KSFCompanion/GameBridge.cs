using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace KsfCompanion
{
    /// <summary>Why the game isn't taking commands (yet).</summary>
    enum LinkProblem { None, NotListening, BadPassword }

    /// <summary>
    /// Finds the running CS:S - the native Linux build, or the Windows build under Proton - by looking through /proc.
    /// </summary>
    static class GameBridge
    {
        // Linux shows at most 15 characters of a process name, so cstrike_win64.exe shows as cstrike_win64.e there.
        static readonly string[] ProcessNames = { "cstrike_linux64", "cstrike_win64.exe", "cstrike.exe" };
        // Older builds ran every Source game as hl2_linux / hl2.exe: only CS:S's (-game cstrike) counts.
        static readonly string[] SharedNames = { "hl2_linux", "hl2.exe" };
        static int lastPid;

        public static int FindGameProcessId()
        {
            if (!OperatingSystem.IsLinux()) return 0;
            // Lets the app be exercised against a stand-in process without touching a real running game.
            var testProcess = Environment.GetEnvironmentVariable("KSFC_GAME_PROCESS");
            if (lastPid != 0 && IsGame(lastPid, testProcess)) return lastPid;
            lastPid = 0;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
                    if (IsGame(pid, testProcess)) return lastPid = pid;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 0;
        }

        static bool IsGame(int pid, string testProcess)
        {
            string comm;
            try { comm = File.ReadAllText($"/proc/{pid}/comm").Trim(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }

            bool Is(string name) => comm.Equals(Short(name), StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(testProcess)) return Is(testProcess) && !IsGone(pid);
            if (ProcessNames.Any(Is)) return !IsGone(pid);
            // Under Proton the arguments are Windows ones: C:\...\hl2.exe -game cstrike
            return SharedNames.Any(Is) && !IsGone(pid) && PlaysCstrike(Arguments(pid));
        }

        static string Short(string name) => name.Length > 15 ? name.Substring(0, 15) : name;

        /// <summary>A process that has exited but hasn't been cleaned up yet (a zombie) isn't running.</summary>
        static bool IsGone(int pid)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var end = stat.LastIndexOf(')');
                return end < 0 || end + 2 >= stat.Length || stat[end + 2] == 'Z' || stat[end + 2] == 'X';
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return true; }
        }

        static string[] Arguments(int pid)
        {
            try { return File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new string[0]; }
        }

        static bool PlaysCstrike(string[] args)
        {
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i].Equals("-game", StringComparison.OrdinalIgnoreCase) && args[i + 1].TrimEnd('/', '\\').EndsWith("cstrike", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Hands console commands to the running game over its own remote console (RCON) on this PC - the Linux stand-in
    /// for Windows' -hijack window message. The game listens for it only when it's started with -usercon, and only takes
    /// commands with the password KSF Companion puts in autoexec.cfg. This never reads or writes game memory.
    /// </summary>
    sealed class GameLink : IDisposable
    {
        static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
        readonly int port;
        readonly string password;
        RconClient client;

        public GameLink(int port, string password)
        {
            this.port = port;
            this.password = password;
        }

        /// <summary>The last command didn't get there because the game can't be reached any more (not just busy).</summary>
        public bool Lost { get; private set; }

        /// <summary>Connects and logs in (if not already). What stood in the way, if anything.</summary>
        public async Task<LinkProblem> OpenAsync()
        {
            if (client != null) return LinkProblem.None;
            try
            {
                client = await RconClient.ConnectAsync("127.0.0.1", port, password, ConnectTimeout, CancellationToken.None);
                return LinkProblem.None;
            }
            catch (RconAuthException)
            {
                return LinkProblem.BadPassword;
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is OperationCanceledException)
            {
                return LinkProblem.NotListening;
            }
        }

        /// <summary>
        /// Runs a command in the game and returns what it printed (possibly nothing), or null if it couldn't be sent
        /// (the connection is dropped then, to be opened again).
        /// </summary>
        public async Task<string> SendAsync(string command, TimeSpan timeout)
        {
            if (client == null && await OpenAsync() != LinkProblem.None)
            {
                Lost = true;
                return null;
            }
            try
            {
                var output = await client.ExecuteAsync(command, timeout, CancellationToken.None);
                Lost = false;
                return output;
            }
            catch (OperationCanceledException)
            {
                // Busy (loading a map): the command still runs once the game gets to it. The next one goes over a new
                // connection, so a half-read answer can't get mixed into it.
                Close();
                return null;
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException)
            {
                Program.Trace("game link lost: " + ex.Message);
                Lost = true;
                Close();
                return null;
            }
        }

        public void Close()
        {
            client?.Dispose();
            client = null;
        }

        public void Dispose() => Close();
    }
}
