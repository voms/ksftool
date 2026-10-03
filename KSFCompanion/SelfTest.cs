using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using KsfCompanion.Ui;

namespace KsfCompanion
{
    /// <summary>
    /// --selftest: the Linux side of KSF Companion against stand-ins - a made-up Steam folder, a fake game console, a
    /// demo written here, a stand-in game process - and the dashboard drawn without a display. Nothing on the PC is
    /// touched: everything happens in a temporary folder.
    /// </summary>
    static class SelfTest
    {
        static int failures;
        static TextWriter output;

        static void Check(string what, bool ok, string detail = null)
        {
            output.WriteLine((ok ? "ok    " : "FAIL  ") + what + (ok || detail == null ? "" : "  (" + detail + ")"));
            if (!ok) failures++;
        }

        public static int Run(TextWriter writer)
        {
            output = writer;
            var root = Path.Combine(Path.GetTempPath(), "ksfc-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Section("vdf", () => Vdf_());
                Section("steam", () => Steam(root));
                Section("json", () => Json_());
                Section("game config", () => Config(root));
                Section("rcon", () => Rcon(root));
                Section("open files", () => Open(root));
                Section("live hud", () => Hud(root));
                Section("game process", () => GameProcess(root));
                Section("autostart", () => Start(root));
                Section("single instance", () => Single(root));
                Section("ui", () => Ui(root));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
            output.WriteLine(failures == 0 ? "all good" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        static void Section(string name, Action test)
        {
            output.WriteLine("-- " + name);
            try { test(); }
            catch (Exception ex)
            {
                Check(name + " ran without an error", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Vdf_()
        {
            var node = Vdf.Parse("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/me/.local/share/Steam\"\n\t\t\"apps\" { \"240\" \"4520\" }\n\t}\n" +
                                 "\t// a comment\n\t\"1\" { \"path\" \"D:\\\\Games \\\"Steam\\\"\" }\n}\n");
            Check("nested keys", node.TextAt("libraryfolders", "0", "path") == "/home/me/.local/share/Steam");
            Check("keys any case", node.TextAt("LibraryFolders", "0", "APPS", "240") == "4520");
            Check("escaped backslashes and quotes", node.TextAt("libraryfolders", "1", "path") == "D:\\Games \"Steam\"");
            Check("a cut-off file reads what's there", Vdf.Parse("\"a\" { \"b\" \"c\"").TextAt("a", "b") == "c");
        }

        static void Steam(string root)
        {
            var home = Path.Combine(root, "home");
            var steam = Path.Combine(home, ".local", "share", "Steam");
            var library = Path.Combine(root, "games", "SteamLibrary");
            var cstrike = Path.Combine(library, "steamapps", "common", "Counter-Strike Source", "cstrike");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(steam, "config"));
            Directory.CreateDirectory(Path.Combine(cstrike, "cfg"));
            Directory.CreateDirectory(Path.Combine(home, ".steam"));
            File.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), steam);
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\" {{ \"path\" \"{steam}\" }}\n\t\"1\" {{ \"path\" \"{library}\" }}\n}}\n");
            File.WriteAllText(Path.Combine(home, ".steam", "registry.vdf"),
                "\"Registry\" { \"HKCU\" { \"Software\" { \"Valve\" { \"Steam\" { \"ActiveProcess\" { \"pid\" \"1234\" \"ActiveUser\" \"123456\" } } } } } }");
            File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"),
                "\"users\" { \"76561197960389185\" { \"AccountName\" \"me\" \"MostRecent\" \"1\" } }");
            Directory.CreateDirectory(Path.Combine(steam, "userdata", "123456", "config"));
            File.WriteAllText(Path.Combine(steam, "userdata", "123456", "config", "localconfig.vdf"),
                "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"240\" { \"LaunchOptions\" \"-novid -usercon\" } \"440\" { } } } } } }");

            using (new Environment_("HOME", home, "XDG_DATA_HOME", null, "STEAM_DIR", null))
            {
                Check("finds CS:S in a second Steam library", SteamLocator.FindCstrikeDir("auto") == cstrike, SteamLocator.FindCstrikeDir("auto"));
                Check("each Steam folder once (~/.steam/steam is a link)", SteamLocator.SteamRoots().Count() == 1, string.Join(", ", SteamLocator.SteamRoots()));
                Check("game_dir may name the game folder or cstrike", SteamLocator.FindCstrikeDir(Path.GetDirectoryName(cstrike)) == cstrike);
                var id = SteamLocator.FindSteamId("auto");
                Check("the logged-in account from registry.vdf", id == "STEAM_0:0:61728", id);
                File.WriteAllText(Path.Combine(home, ".steam", "registry.vdf"), "\"Registry\" { \"HKCU\" { \"Software\" { \"Valve\" { \"Steam\" { \"ActiveProcess\" { \"ActiveUser\" \"0\" } } } } } }");
                Check("logged out: the last account from loginusers.vdf", SteamLocator.FindSteamId("auto") == "STEAM_0:1:61728", SteamLocator.FindSteamId("auto"));
                File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"), "\"users\" { \"76561197960389185\" { \"AccountName\" \"me\" } }");
                Check("neither says: the account Steam saved settings for last", SteamLocator.FindSteamId("auto") == "STEAM_0:0:61728", SteamLocator.FindSteamId("auto"));
                var options = SteamLocator.LaunchOptions("STEAM_0:0:61728");
                Check("CS:S's launch options", options == "-novid -usercon", options);
                Check("-usercon is there", SteamLocator.HasLaunchOption(options, "-usercon") && !SteamLocator.HasLaunchOption("-usercontent", "-usercon"));
                Check("no config for another account", SteamLocator.LaunchOptions("STEAM_0:1:5") == null);
            }
            Check("steam ids", SteamLocator.ParseSteamId("[U:1:123456]") == "STEAM_0:0:61728" && SteamLocator.ParseSteamId("76561197960389184") == "STEAM_0:0:61728"
                               && SteamLocator.AccountId("STEAM_0:1:61728") == 123457);
        }

        static void Json_()
        {
            var root = Json.Parse("{\"name\":\"surf_x\",\"tier\":3,\"big\":12345678901,\"time\":61.25,\"isLinear\":true,\"mappers\":[{\"name\":\"a\"},{\"name\":\"b\"}],\"none\":null,\"when\":\"2025-11-23T10:00:00Z\"}")
                as Dictionary<string, object>;
            Check("objects and text", Json.Str(root, "name") == "surf_x");
            Check("numbers: int, long and double", Json.Get(root, "tier") is int && Json.Get(root, "big") is long && Json.Num(root, "time") == 61.25 && Json.Int(root, "time") == 61);
            Check("true, arrays, null", Json.Bool(root, "isLinear") && Json.Objects(Json.Get(root, "mappers")).Count() == 2 && Json.Get(root, "none") == null);
            Check("dates", Json.Date(root, "when")?.ToUniversalTime() == new DateTime(2025, 11, 23, 10, 0, 0, DateTimeKind.Utc));
            var refused = false;
            try { Json.Parse("<html>busy</html>"); }
            catch (ArgumentException) { refused = true; }
            Check("an error page counts as a failed request", refused);
        }

        static void Config(string root)
        {
            var cstrike = Path.Combine(root, "cs", "cstrike");
            Directory.CreateDirectory(Path.Combine(cstrike, "cfg"));
            File.WriteAllText(Path.Combine(cstrike, "cfg", "config.cfg"), "bind \"F5\" \"jpeg\"\nbind \"r\" \"+reload\"\nname \"surfer\"\n");
            File.WriteAllText(Path.Combine(cstrike, "cfg", "autoexec.cfg"), "rate 786432\n");
            var settingsFile = Path.Combine(root, "settings.ini");
            var settings = new Settings(settingsFile);
            var config = new GameConfig(cstrike);
            config.Install(settings);
            var autoexec = File.ReadAllText(Path.Combine(cstrike, "cfg", "autoexec.cfg"));
            var password = settings.Get("rcon_password");
            Check("autoexec keeps your lines and adds the block", autoexec.StartsWith("rate 786432\n", StringComparison.Ordinal) && config.IsInstalled);
            Check("the block opens the game's console to this PC", autoexec.Contains("ip 0.0.0.0") && autoexec.Contains("hostport 27015")
                                                                   && autoexec.Contains($"rcon_password \"{password}\"") && autoexec.Contains("net_start"));
            Check("a password of its own, kept", password.Length >= 12 && GameConfig.RconPassword(settings) == password);
            Check("settings.ini is yours only", OperatingSystem.IsWindows() || (File.GetUnixFileMode(settingsFile) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);
            Check("cfg files with Linux line ends", !File.ReadAllText(Path.Combine(cstrike, "cfg", "ksf_companion.cfg")).Contains('\r'));
            Check("your F5 bind remembered", config.OriginalBind(settings, "F5") == "jpeg");
            Check("your in-game name", config.PlayerName() == "surfer");
            config.Install(settings);
            Check("installing twice leaves one block", File.ReadAllText(Path.Combine(cstrike, "cfg", "autoexec.cfg")).Split("KSF Companion >>>").Length == 2);
            File.WriteAllText(Path.Combine(cstrike, "cfg", "config.cfg"), "bind \"F5\" \"ksf_save\"\nbind \"r\" \"+reload\"\n");
            config.Uninstall(settings);
            Check("uninstall takes the block out", File.ReadAllText(Path.Combine(cstrike, "cfg", "autoexec.cfg")).Trim() == "rate 786432");
            Check("uninstall gives F5 back", File.ReadAllText(Path.Combine(cstrike, "cfg", "config.cfg")).Contains("bind \"F5\" \"jpeg\""));
            Check("uninstall removes its cfgs", !File.Exists(Path.Combine(cstrike, "cfg", "ksf_companion.cfg")));
        }

        /// <summary>A stand-in for the game's remote console: the password, echo, a long answer, and the end marker.</summary>
        sealed class FakeConsole : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly string password;
            public readonly List<string> Commands = new List<string>();

            public FakeConsole(string password)
            {
                this.password = password;
                listener.Start();
                _ = Task.Run(AcceptAsync);
            }

            public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

            async Task AcceptAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var authed = false;
                    try
                    {
                        while (true)
                        {
                            var packet = await RconClient.ReadPacketAsync(stream, CancellationToken.None);
                            byte[] Reply(int id, int type, string body) => RconClient.Encode(id, type, body);
                            if (packet.Type == RconClient.Auth)
                            {
                                authed = packet.Body == password;
                                await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, ""));
                                await stream.WriteAsync(Reply(authed ? packet.Id : -1, RconClient.AuthResponse, ""));
                            }
                            else if (packet.Type == RconClient.ExecCommand && authed)
                            {
                                lock (Commands) Commands.Add(packet.Body);
                                if (packet.Body.StartsWith("echo ", StringComparison.Ordinal)) await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, packet.Body.Substring(5) + "\n"));
                                else if (packet.Body == "long")
                                    for (var i = 0; i < 3; i++) await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, new string((char)('a' + i), 4000)));
                                else if (packet.Body == "status")
                                    await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, "hostname: KSF - Beginner EU\nudp/ip  : 192.0.2.10:27015\n"));
                                else if (packet.Body == "busy")
                                {
                                    // Loading a map: half an answer, then nothing for a while.
                                    var answer = Reply(packet.Id, RconClient.ResponseValue, "loading");
                                    await stream.WriteAsync(answer.AsMemory(0, 6));
                                    await Task.Delay(TimeSpan.FromSeconds(3));
                                }
                            }
                            else if (packet.Type == RconClient.ResponseValue)
                            {
                                // The end marker: mirrored, then the odd extra packet the game sends after it.
                                await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, ""));
                                await stream.WriteAsync(Reply(packet.Id, RconClient.ResponseValue, "\0\u0001"));
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }

            public void Dispose() => listener.Stop();
        }

        static void Rcon(string root)
        {
            using var console = new FakeConsole("s3cret-pass");
            using (var wrong = new GameLink(console.Port, "nope"))
                Check("a wrong password is told apart", wrong.OpenAsync().GetAwaiter().GetResult() == LinkProblem.BadPassword);
            using (var link = new GameLink(console.Port, "s3cret-pass"))
            {
                Check("logs in", link.OpenAsync().GetAwaiter().GetResult() == LinkProblem.None);
                Check("a command's answer comes back", link.SendAsync("echo \"[ksf.surf] hi\"", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() == "\"[ksf.surf] hi\"\n");
                var status = link.SendAsync("status", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check("status, in order after the one before", status != null && status.StartsWith("hostname: KSF", StringComparison.Ordinal), status);
                var big = link.SendAsync("long", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Check("a long answer in several packets", big?.Length == 12000 && big[0] == 'a' && big[11999] == 'c', big?.Length.ToString());
                Check("a command with no answer", link.SendAsync("sm_rtv", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() == "");
                lock (console.Commands) Check("the commands arrive as sent", console.Commands.Last() == "sm_rtv");
                Check("a busy game times out", link.SendAsync("busy", TimeSpan.FromSeconds(0.5)).GetAwaiter().GetResult() == null && !link.Lost);
                Check("and the next command still gets its own answer", link.SendAsync("echo after", TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() == "after\n");
            }
            var unused = new TcpListener(IPAddress.Loopback, 0);
            unused.Start();
            var closedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
            unused.Stop();
            using (var nobody = new GameLink(closedPort, "x"))
            {
                Check("nothing listening is told apart", nobody.OpenAsync().GetAwaiter().GetResult() == LinkProblem.NotListening);
                Check("and counts as lost", nobody.SendAsync("status", TimeSpan.FromSeconds(1)).GetAwaiter().GetResult() == null && nobody.Lost);
            }
        }

        static void Open(string root)
        {
            var writing = Path.Combine(root, "ksfc_live.dem");
            var reading = Path.Combine(root, "ksfc_live_2.dem");
            File.WriteAllText(reading, "x");
            using (new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (new FileStream(reading, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var open = OpenFiles.WrittenBy(Environment.ProcessId);
                Check("sees a demo open for writing", open != null && open.Contains("ksfc_live.dem"));
                Check("not one open for reading", open != null && !open.Contains("ksfc_live_2.dem"));
            }
            Check("nothing once it's closed", OpenFiles.WrittenBy(Environment.ProcessId)?.Contains("ksfc_live.dem") == false);
            Check("no game, nothing open", OpenFiles.WrittenBy(0)?.Count == 0);
        }

        /// <summary>The bits as the game packs them: <paramref name="shift"/> bits in, least significant first.</summary>
        static byte[] Packed(byte[] text, int shift)
        {
            var bits = new byte[text.Length + 2];
            for (var i = 0; i < text.Length * 8; i++)
            {
                var bit = (text[i / 8] >> (i % 8)) & 1;
                var at = i + shift;
                bits[at / 8] |= (byte)(bit << (at % 8));
            }
            return bits;
        }

        static void Hud(string root)
        {
            var dir = Path.Combine(root, "demos");
            Directory.CreateDirectory(dir);
            var demo = Path.Combine(dir, LiveHud.DemoName + ".dem");
            var header = new byte[8 + 4 + 4 + 260 + 260 + 260];
            Encoding.ASCII.GetBytes("HL2DEMO\0").CopyTo(header, 0);
            Encoding.ASCII.GetBytes("surf_sample").CopyTo(header, 8 + 4 + 4 + 260 + 260);
            var noise = new byte[300];
            new Random(5).NextBytes(noise);
            var body = new List<byte>(header);
            body.AddRange(noise);
            body.AddRange(Packed(Encoding.ASCII.GetBytes("\u0001- Stage 4 -\nTimeleft: 8 minutes\u0002"), 3));
            body.AddRange(noise);
            body.AddRange(Packed(Encoding.ASCII.GetBytes("\u0001Finished [Stage 4 - Glide]: 00:17:06\n(WR +00:02:09)\u0002"), 5));
            File.WriteAllBytes(demo, body.ToArray());

            Check("the map from the demo's header", LiveHud.MapOf(demo) == "surf_sample", LiveHud.MapOf(demo));
            var hud = new LiveHud(dir);
            var zones = new List<int>();
            var finishes = new List<(int, double)>();
            var minutes = new List<int>();
            hud.ZoneChanged += zones.Add;
            hud.ZoneFinished += (zone, time, live) => finishes.Add((zone, time));
            hud.TimeLeftShown += (n, previous, ago) => minutes.Add(n);
            hud.Watch(demo, alreadyRunning: false);
            hud.ReadWholeDemo();
            Check("the stage you're on, wherever its bits start", zones.SequenceEqual(new[] { 4 }), string.Join(",", zones));
            Check("a stage finish and its time", finishes.Count == 1 && finishes[0].Item1 == 4 && Math.Abs(finishes[0].Item2 - 17.06) < 0.001,
                string.Join(",", finishes));
            Check("the panel's time left", minutes.FirstOrDefault() == 8);

            // Finished demos are cleared out before a new recording; one the game is writing stays.
            var old = Path.Combine(dir, LiveHud.DemoName + "_2.dem");
            File.WriteAllText(old, "x");
            using (new FileStream(demo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                hud.GamePid = Environment.ProcessId;
                Check("finds the demo being recorded", hud.FindRecordingDemo() == demo, hud.FindRecordingDemo());
                hud.DeleteFinishedDemos();
                Check("clears finished demos, keeps the one being written", !File.Exists(old) && File.Exists(demo));
            }
            Check("nothing recording once it's closed", hud.FindRecordingDemo() == null);
        }

        static void GameProcess(string root)
        {
            if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            {
                output.WriteLine("skip  no /bin/sh for a stand-in game");
                return;
            }
            // A stand-in with a name of its own: Linux names a process after the file it runs (15 characters at most),
            // a script too - as long as it doesn't exec something else.
            var standIn = Path.Combine(root, "ksfc_fake_game");
            File.WriteAllText(standIn, "#!/bin/sh\nwhile :; do sleep 1; done\n");
            File.SetUnixFileMode(standIn, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var game = Process.Start(new ProcessStartInfo(standIn) { UseShellExecute = false });
            try
            {
                Thread.Sleep(200);
                using (new Environment_("KSFC_GAME_PROCESS", "ksfc_fake_game"))
                    Check("finds the game's process", GameBridge.FindGameProcessId() == game.Id);
                Check("no CS:S running here", GameBridge.FindGameProcessId() == 0);
            }
            finally
            {
                game.Kill(entireProcessTree: true);
                game.WaitForExit();
            }
            using (new Environment_("KSFC_GAME_PROCESS", "ksfc_fake_game"))
                Check("sees the game close", GameBridge.FindGameProcessId() == 0);
        }

        static void Start(string root)
        {
            var config = Path.Combine(root, "xdg-config");
            var system = Path.Combine(root, "xdg-system");
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, Program.AppId), "");
            var entry = Path.Combine(config, "autostart", Program.AppId + ".desktop");
            using (new Environment_("XDG_CONFIG_HOME", config, "XDG_CONFIG_DIRS", system, "KSFC_AUTOSTART", null, "PATH", bin + ":/usr/bin"))
            {
                Check("off to begin with", !Autostart.IsEnabled && !Autostart.IsManaged);
                Autostart.Set(true);
                Check("on: an entry in ~/.config/autostart", Autostart.IsEnabled && File.ReadAllText(entry).Contains($"Exec={Program.AppId} --background"));
                Autostart.Set(false);
                Check("off: the entry is gone", !Autostart.IsEnabled && !File.Exists(entry));

                Directory.CreateDirectory(Path.Combine(system, "autostart"));
                File.WriteAllText(Path.Combine(system, "autostart", Program.AppId + ".desktop"), "[Desktop Entry]\nExec=ksf-companion --background\n");
                Check("on through the system's entry (the NixOS module)", Autostart.IsEnabled);
                Autostart.Set(false);
                Check("off: the user's own Hidden entry over it", !Autostart.IsEnabled && File.ReadAllText(entry).Contains("Hidden=true"));
                Autostart.Set(true);
                Check("on again: the Hidden entry is gone", Autostart.IsEnabled && !File.Exists(entry));
            }
            using (new Environment_("KSFC_AUTOSTART", "systemd"))
                Check("started by a systemd service: managed there", Autostart.IsManaged && Autostart.IsEnabled);
        }

        static void Single(string root)
        {
            var basePath = Path.Combine(root, "run", Program.AppId);
            using var first = SingleInstance.TryStart(basePath);
            Check("the first copy runs", first != null);
            using (var second = SingleInstance.TryStart(basePath))
                Check("a second copy doesn't", second == null);
            var shown = new ManualResetEventSlim();
            first.ShowRequested += shown.Set;
            SingleInstance.AskToShow(basePath);
            Check("starting it again shows the dashboard of the first", shown.Wait(TimeSpan.FromSeconds(3)));

            // A folder too deep for a Unix socket's path (107 bytes).
            var deep = Path.Combine(root, new string('d', 60), new string('e', 60), Program.AppId);
            using var deepFirst = SingleInstance.TryStart(deep);
            Check("a copy runs from a deep folder", deepFirst != null);
            var deepShown = new ManualResetEventSlim();
            deepFirst.ShowRequested += deepShown.Set;
            SingleInstance.AskToShow(deep);
            Check("and can still be asked to show itself", deepShown.Wait(TimeSpan.FromSeconds(3)));
        }

        static void Ui(string root)
        {
            Cli.StartHeadless();
            BarFill.Animate = false;
            foreach (var page in new[] { "dashboard", "nominate", "binds" })
            {
                using (new Environment_("KSFC_PREVIEW_PAGE", page))
                {
                    var vm = SampleData.Dashboard();
                    var png = Path.Combine(root, page + ".png");
                    var result = Cli.Render(vm, new KeyNames { Save = "F5", Card = "F6", List = "F7" }, png, 1440, 940, TextWriter.Null);
                    Check($"draws the {page} page", result == 0 && File.Exists(png) && new FileInfo(png).Length > 30000,
                        File.Exists(png) ? new FileInfo(png).Length + " bytes" : "no picture");
                }
            }

            // Hiding a part (Layout[...] bindings) and the Simple view reach the window.
            var model = SampleData.Dashboard();
            var window = new DashboardWindow(model, new KeyNames { Save = "F5", Card = "F6", List = "F7" }) { Width = 1440, Height = 940 };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            ContentControl Part(string tag) => window.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Tag as string == tag);
            Check("the leaderboard is on show", Part("leaderboard")?.IsVisible == true);
            model.Layout["leaderboard"] = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check("hiding it takes it away", Part("leaderboard")?.IsVisible == false);
            model.Layout.ShowAll();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check("Customize brings it back", Part("leaderboard")?.IsVisible == true);
            window.AllowClose = true;
            window.Close();
        }

        /// <summary>Sets environment variables (null unsets) and puts them back afterwards.</summary>
        sealed class Environment_ : IDisposable
        {
            readonly List<(string Name, string Value)> saved = new List<(string, string)>();

            public Environment_(params string[] pairs)
            {
                for (var i = 0; i + 1 < pairs.Length; i += 2)
                {
                    saved.Add((pairs[i], Environment.GetEnvironmentVariable(pairs[i])));
                    Environment.SetEnvironmentVariable(pairs[i], pairs[i + 1]);
                }
            }

            public void Dispose()
            {
                foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
