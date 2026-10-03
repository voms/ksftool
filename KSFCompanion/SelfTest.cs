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
                Section("times", () => Times());
                Section("groups", () => Groups());
                Section("leaving a server", () => Leaving());
                Section("private servers", () => PrivateServers());
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
            // Player names cut off in the middle of an emoji: half a surrogate pair, which JavaScriptSerializer let through.
            var names = Json.Objects(Json.Parse("[{\"name\":\"ab\\ud83d\"},{\"name\":\"\\ude00c\"},{\"name\":\"ok \\ud83d\\ude00\"},{\"name\":\"\\\\ud83d\\n\"}]")).Select(o => Json.Str(o, "name")).ToList();
            Check("names with half an emoji still read", names.Count == 4 && names[0] == "ab\uFFFD" && names[1] == "\uFFFDc" && names[2] == "ok \U0001F600" && names[3] == "\\ud83d\n",
                string.Join(" | ", names));
            var refused = false;
            try { Json.Parse("<html>busy</html>"); }
            catch (ArgumentException) { refused = true; }
            Check("an error page counts as a failed request", refused);
        }

        static void Times()
        {
            Check("cut off like the game's timer, never rounded up", Format.Short(10.199954) == "10.199" && Format.Time(35.624759) == "0:35.624");
            Check("a time that already is whole milliseconds stays as it is", Format.Short(10.199) == "10.199" && Format.Short(0.3) == "0.300");
            Check("minutes and hours when needed", Format.Short(62.0009) == "1:02.000" && Format.Time(3723.4569) == "1:02:03.456");
            Check("a gap is between the times as they're shown", Format.Gap(10.26002, 10.199954) == "+0.061" && Format.Gap(10.199954, 10.26002) == "-0.061");
            Check("and gets minutes when it's long", Format.Gap(100.5, 35) == "+1:05.500");
            Check("times add up as they're shown", Format.Time(Format.Sum(new[] { 10.1999, 20.5009 })) == "0:30.699");
        }

        static void Groups()
        {
            // Where ksf.surf had each group end on 66 tick in October 2026: the top 10, then groups 1 to 6.
            int?[] Ends(int total) => Enumerable.Range(0, 7).Select(g => KsfGroups.LastRank(g, total)).ToArray();
            void Same(string what, int total, params int?[] ends) =>
                Check(what, Ends(total).SequenceEqual(ends), string.Join(" ", Ends(total)));
            Same("groups on a big map (surf_utopia_njv)", 23254, 10, 494, 978, 1947, 3883, 7757, 15506);
            Same("groups 1-4 reach the 20th to 100th place on a smaller one (surf_bugs)", 465, 10, 20, 35, 60, 100, 161, 313);
            Same("and group 5 the 150th (surf_dragonfall)", 352, 10, 20, 35, 60, 100, 150, 238);
            Same("but nobody past two thirds is in one (surf_kraken)", 159, 10, 20, 35, 60, 100, 109, null);
            Check("where a group starts", KsfGroups.FirstRank(1, 465) == 11 && KsfGroups.FirstRank(4, 465) == 61 && KsfGroups.FirstRank(0, 465) == 1);
            Check("a rank's group", KsfGroups.Of(87, 465) == 4 && KsfGroups.Of(7, 465) == 0 && KsfGroups.Of(313, 465) == 6 && KsfGroups.Of(314, 465) == null);
            Check("a map only the top 10 have finished has no groups", KsfGroups.LastRank(1, 10) == null && KsfGroups.LastRank(0, 7) == 7);

            // The group tile: the group you pick, or the next one up from yours.
            Check("group_goal in settings.ini", GroupGoal.Picked("auto") == null && GroupGoal.Picked("top10") == 0 && GroupGoal.Picked("3") == 3 && GroupGoal.Picked("9") == null);
            var bugs = new MapReport { Info = new MapInfo { Name = "surf_bugs" } };
            var unfinished = GroupGoal.For(bugs, null, 465);
            Check("before you've finished: group 6, and the time at its end", unfinished.Group == 6 && unfinished.LastRank == 313 && unfinished.NeedsCutoff);
            bugs.Zones.Add(new ZoneRecord { ZoneId = 0, Time = 38.475, Rank = 87, TotalRanks = 465, Group = 4 });
            var next = GroupGoal.For(bugs, null, 465);
            Check("in group 4: group 3 is next", next.Group == 3 && next.FirstRank == 36 && next.LastRank == 60 && next.NeedsCutoff);
            Check("one you're in already needs no lookup", !GroupGoal.For(bugs, 5, 465).NeedsCutoff && GroupGoal.For(bugs, 5, 465).YourGroup == 4);

            // KSF's own cutoffs, as ksf.surf's leaderboard page hands them to its table - the same as the rule's.
            var page = "<script>self.__next_f.push([1,\"...\\\"map\\\":\\\"surf_bugs\\\",\\\"zone\\\":0,\\\"cutOffs\\\":[10,20,35,60,100,161,313]}]\"])</script>";
            var published = KsfApi.ParseGroupEnds(page);
            Check("ksf.surf's cutoffs read off its page", published != null && published.SequenceEqual(new[] { 10, 20, 35, 60, 100, 161, 313 }), published == null ? "none" : string.Join(",", published));
            Check("they're what the rule works out", published != null && published.SequenceEqual(KsfGroups.Ends(465)));
            Check("a page without them, or nonsense, gives none", KsfApi.ParseGroupEnds("<html></html>") == null && KsfApi.ParseGroupEnds("\"cutOffs\":[10,5,1]") == null);
            var short100 = KsfGroups.Checked(KsfApi.ParseGroupEnds("\"zone\":0,\"cutOffs\":[10,20,35,60,80]}"));
            Check("groups left out at the end are empty (surf_bugs on 100 tick)", short100 != null && short100.SequenceEqual(new[] { 10, 20, 35, 60, 80, 80, 80 })
                && KsfGroups.LastRank(4, short100) == 80 && KsfGroups.LastRank(5, short100) == null && short100.SequenceEqual(KsfGroups.Ends(116)));
            Check("the page for 100 tick", KsfApi.GroupEndsPage("surf_bugs", "css100t") == "/maps/surf_bugs/records?game=100T"
                && KsfApi.GroupEndsPage("surf_bugs", "css") == "/maps/surf_bugs/records?game=66T");
            var withEnds = GroupGoal.For(new MapReport { Info = new MapInfo { Name = "surf_bugs" } }, 4, 0, published);
            Check("with KSF's cutoffs the size of the leaderboard isn't needed", withEnds.FirstRank == 61 && withEnds.LastRank == 100, $"{withEnds.FirstRank}-{withEnds.LastRank}");

            // No time on the map: no rank and no group, whatever ksf.surf's record says.
            var drift = new MapReport { Info = new MapInfo { Name = "surf_drift" } };
            drift.Zones.Add(new ZoneRecord { ZoneId = 0, TotalRanks = 24 });
            var noTime = GroupGoal.For(drift, null, 24);
            Check("not finished: you're in no group, and the easiest one there is is the goal", noTime.YourRank == null && noTime.YourGroup == null && noTime.Group == 1 && noTime.LastRank == 19,
                $"{noTime.YourRank} {noTime.YourGroup} {noTime.Group} {noTime.LastRank}");
            var top10 = GroupGoal.For(drift, 0, 24);
            var tile = new DashboardViewModel();
            tile.ShowGroupGoal(top10);
            Check("and the top 10 isn't \"in\" without a time", !tile.GroupGoalReached && tile.GroupGoalTime != "IN", tile.GroupGoalTime);

            // The server list's progress bars.
            var progress = MapProgress.From(new[]
            {
                new ZoneRecord { ZoneId = 0, Time = 62.5 }, new ZoneRecord { ZoneId = 1, Time = 10 }, new ZoneRecord { ZoneId = 3, Time = 12 },
                new ZoneRecord { ZoneId = 32, Time = 20 }, new ZoneRecord { ZoneId = 2 },
            }, linear: false, stages: 4, bonuses: 2);
            Check("stages and bonuses done, in order", progress.Stages == "1010" && progress.Bonuses == "01" && progress.Time == 62.5, progress.Stages + " " + progress.Bonuses);
            var linear = MapProgress.From(new ZoneRecord[0], linear: true, stages: 7, bonuses: 0);
            Check("a linear map is one bar, the map itself", linear.Stages == "0" && linear.Bonuses == "" && linear.Time == null);
        }

        /// <summary>As the game's console log has it (your console log, October 2026): the demo stops when you leave a server.</summary>
        static void Leaving()
        {
            var t0 = new DateTime(2026, 10, 3, 15, 0, 0);
            DateTime At(double seconds) => t0.AddSeconds(seconds);

            // "Completed demo" and then nothing: the main menu. Nobody answers "status" - asked twice, then you've left,
            // as of when the demo stopped.
            var check = new LeaveCheck();
            check.Hint(At(0), TimeSpan.FromSeconds(3), At(0));
            Check("nothing asked straight away", !check.Tick(At(1)).Ask && check.Pending);
            Check("\"status\" a moment later", check.Tick(At(3)).Ask);
            Check("no answer: asked once more", check.Tick(At(9.5)).Ask);
            var left = check.Tick(At(16)).LeftAt;
            Check("no answer again: left, from when the demo stopped", left == At(0) && !check.Pending, left?.ToString("HH:mm:ss"));

            // "Completed demo", then "Connecting to ..." straight away: another server, nothing to ask.
            check.Hint(At(100), TimeSpan.FromSeconds(3), At(100));
            check.Cancel();
            Check("switching servers asks nothing", !check.Tick(At(104)).Ask && !check.Pending);

            // KSF's servers advertise each other in chat - a private one (not on ksf.surf's list) too: that's how it's known.
            Check("KSF's server ads name the servers", Companion.AdvertisedServer("[Surf Timer] - Expert - surf_boreas (7/60) IP: 167.114.158.6:27016") == "167.114.158.6:27016"
                && Companion.AdvertisedServer("[Surf Timer] - Beginner US Central - surf_eternity (22/60) IP: 74.91.115.159:27015") == "74.91.115.159:27015");
            Check("other timer lines aren't ads", Companion.AdvertisedServer("[Surf Timer] - voms finished in 01:47:25 (WR +00:17:39). Improving by 02:55:11") == null
                && Companion.AdvertisedServer("[Surf Timer] - Stage 'Stage 2' 00:06:85 (PR +00:00:29)") == null
                && Companion.AdvertisedServer("[Casual] someone :  [Surf Timer] - X - surf_y (1/60) IP: 1.2.3.4:27015") == null);

            // A demo stopped on the server you're still on (or ksf.surf's list lagging): the server answers.
            check.Hint(At(200), TimeSpan.Zero, At(200));
            Check("asked", check.Tick(At(200)).Ask);
            check.Answered(At(200.4));
            var still = check.Tick(At(207));
            Check("answered: still on the server", !still.Ask && still.LeftAt == null && !check.Pending);
        }

        /// <summary>A private KSF server isn't on ksf.surf's list: it's asked itself (A2S), as a server browser does.</summary>
        static void PrivateServers()
        {
            using var server = new FakeSourceServer();
            var address = "127.0.0.1:" + server.Port;
            var info = A2s.InfoAsync(address, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();
            Check("its name, map and players, after the challenge it asks for", info?.Name == "Private" && info.Map == "surf_drift" && info.Players == 6
                && info.Bots == 4 && info.MaxPlayers == 21, info == null ? "no answer" : $"{info.Name} {info.Map} {info.Players}/{info.MaxPlayers} {info.Bots} bots");
            var players = A2s.PlayersAsync(address, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();
            Check("who's on it, from an answer in two pieces", players?.Count == 6 && players[4].Name == "voms" && Math.Abs(players[4].Seconds - 2505.5) < 0.01
                && players[1].Name == "WR | P1nkE ❤ˡᵒᵛᵉ ʸᵒ", players == null ? "no answer" : string.Join(", ", players.Select(p => p.Name)));
            Check("its replay bots and SourceTV aren't players", players != null && players.Where(p => !A2s.LooksLikeBot(p.Name)).Select(p => p.Name).SequenceEqual(new[] { "voms", "ember" }));
            Check("nor are KSF's other replay bots", A2s.LooksLikeBot("Map | levi") && A2s.LooksLikeBot("NOF | Map | SYNKI") && A2s.LooksLikeBot("WRB #3 | Nazar")
                && A2s.LooksLikeBot("2X | WR | Caff") && !A2s.LooksLikeBot("KSF | someone") && !A2s.LooksLikeBot("Mapper"));
            // A bot by a name that doesn't give it away: it joined with the others, and the server counts one person fewer.
            var people = A2s.People(new A2sInfo { Players = 6, Bots = 3 }, new[]
            {
                new A2sPlayer { Name = "WR | x", Seconds = 9000 }, new A2sPlayer { Name = "SurfTimer Replay", Seconds = 9000 },
                new A2sPlayer { Name = "Caff's run", Seconds = 9000.4 }, new A2sPlayer { Name = "voms", Seconds = 2505 },
                new A2sPlayer { Name = "ember", Seconds = 61 }, new A2sPlayer { Name = "kite", Seconds = 9001 },
            });
            Check("and one by another name, by when it joined", people.Select(p => p.Name).SequenceEqual(new[] { "voms", "ember", "kite" }), string.Join(", ", people.Select(p => p.Name)));
            // A long name cut off at 32 bytes in the middle of a letter.
            var cut = new List<byte> { 0x44, 1, 0 };
            cut.AddRange(Encoding.UTF8.GetBytes("Пара"));
            cut.RemoveAt(cut.Count - 1);
            cut.Add(0);
            cut.AddRange(BitConverter.GetBytes(0));
            cut.AddRange(BitConverter.GetBytes(12f));
            Check("a name cut off mid-letter loses the broken bit", A2s.ParsePlayers(cut.ToArray())?.SingleOrDefault()?.Name == "Пар");
            Check("one that doesn't answer", A2s.InfoAsync("127.0.0.1:9", TimeSpan.FromMilliseconds(600), CancellationToken.None).GetAwaiter().GetResult() == null);

            // One that doesn't answer, while you're on it: as the game's own "status" showed it (your console log, October 2026).
            var you = StatusAnswer.Player("#    136 \"voms\"              [U:1:6]       00:15       45    4 active");
            var ember = StatusAnswer.Player("#     85 \"ember\"        [U:1:7]      3:53:52       96    0 active");
            Check("status: its players", you?.Name == "voms" && you?.Account == 6 && you?.Connected == "00:15" && ember?.Connected == "3:53:52"
                && StatusAnswer.Player("#    133 \"WR | P1nkE ❤ˡᵒᵛᵉ ʸᵒ\" BOT                       active") == null);
            Check("status: how many", StatusAnswer.HumansIn("players : 1 humans, 4 bots (21 max)") == 1 && StatusAnswer.HumansIn("players : 41 humans, 4 bots (61 max)") == 41
                && StatusAnswer.HumansIn("map     : surf_drift at: 0 x, 0 y, 0 z") == null);
            Check("status: time on the server", StatusAnswer.Seconds("00:15") == 15 && StatusAnswer.Seconds("48:58") == 2938 && StatusAnswer.Seconds("3:53:52") == 14032
                && StatusAnswer.Seconds("") == null && StatusAnswer.Seconds("1::2") == null);
            var status = new StatusAnswer { Name = "Private", Address = "192.0.2.7:27068" };
            status.Add("voms", SteamLocator.FromAccountId(6), "00:15");
            status.Add("voms", SteamLocator.FromAccountId(6), "00:15");
            var later = status.PlayersAt(status.At.AddMinutes(1));
            Check("status: a player once, their time going on", later.Count == 1 && later[0].SteamId == "STEAM_0:0:3" && later[0].ConnectedSeconds == 75,
                string.Join(", ", later.Select(p => $"{p.SteamId} {p.ConnectedSeconds}")));
            var list = Companion.ParseKsfServers("192.0.2.7:27068 192.0.2.5:27015@100 nonsense 192.0.2.9");
            Check("ksf_servers: 66 tick unless @100", list.Count == 2 && list["192.0.2.7:27068"] == "css" && list["192.0.2.5:27015"] == "css100t");

            // In the list: no time left or stages from it (only ksf.surf has those), its map from KSF's map list.
            var vm = new DashboardViewModel();
            var row = new KsfServer { Game = "css", Name = "Private", Address = address, Map = "surf_drift", Tier = 5, IsLinear = false, StageCount = 3, PlayerCount = 2, FromKsf = false };
            row.Players.Add(new KsfServerPlayer { Name = "voms", SteamId = "STEAM_0:0:3", ConnectedSeconds = 2505 });
            row.Players.Add(new KsfServerPlayer { Name = "ember", ConnectedSeconds = 61 });
            vm.YourSteamId = "STEAM_0:0:3";
            vm.SetServers(new List<KsfServer> { row }, null, new HashSet<string>());
            vm.ToggleServerCommand.Execute(address);
            var shown = vm.Servers[0];
            Check("a private server in the list", shown.TimeLeft == "private" && shown.Kind == "staged · 3 stages" && shown.Tier == "T5"
                && shown.PlayerRows.Count == 2 && shown.PlayerRows.All(p => p.Zone == "") && shown.PlayerRows[0].IsYou, $"{shown.TimeLeft} / {shown.Kind} / {shown.PlayerRows.Count}");
            vm.SetLiveServer(row, "STEAM_0:0:3", "surf_drift");
            Check("and as your server", vm.LiveSubtitle == "surf_drift  ·  2 playing  ·  private server" && vm.LivePlayers.Count == 2, vm.LiveSubtitle);
            // One that keeps who's on it to itself, on a map KSF's list doesn't have.
            var quiet = new KsfServer { Game = "css", Name = "Quiet", Address = "192.0.2.8:27015", Map = "surf_unknown", PlayerCount = 3, FromKsf = false };
            vm.SetServers(new List<KsfServer> { row, quiet }, null, new HashSet<string>());
            vm.ToggleServerCommand.Execute(quiet.Address);
            var quietRow = vm.Servers.First(r => r.Address == quiet.Address);
            Check("a private server that doesn't say who's on it", quietRow.PlayersNote == "it doesn't say who's on it" && quietRow.Tier == "T?" && quietRow.Kind == "private server",
                $"{quietRow.PlayersNote} / {quietRow.Tier} / {quietRow.Kind}");
        }

        /// <summary>A stand-in Source server answering A2S as servers do now: a challenge number first, a long answer in pieces.</summary>
        sealed class FakeSourceServer : IDisposable
        {
            static readonly byte[] Challenge = { 0x12, 0x34, 0x56, 0x78 };
            readonly UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            public FakeSourceServer() => _ = Task.Run(ServeAsync);
            public int Port => ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            public void Dispose() => udp.Dispose();

            async Task ServeAsync()
            {
                while (true)
                {
                    UdpReceiveResult query;
                    try { query = await udp.ReceiveAsync(); }
                    catch (Exception) { return; }
                    var asked = query.Buffer;
                    if (asked.Length < 9) continue;
                    if (!asked.AsSpan(asked.Length - 4).SequenceEqual(Challenge))
                        await udp.SendAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41 }.Concat(Challenge).ToArray(), 9, query.RemoteEndPoint);
                    else if (asked[4] == 0x54)
                    {
                        var info = Info();
                        await udp.SendAsync(info, info.Length, query.RemoteEndPoint);
                    }
                    else if (asked[4] == 0x55)
                        // The second piece first: they're put back in order.
                        foreach (var piece in Pieces(Players(), 2).Reverse())
                            await udp.SendAsync(piece, piece.Length, query.RemoteEndPoint);
                }
            }

            static byte[] Info()
            {
                var b = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17 };
                void Text(string s)
                {
                    b.AddRange(Encoding.UTF8.GetBytes(s));
                    b.Add(0);
                }
                Text("Private");
                Text("surf_drift");
                Text("cstrike");
                Text("Counter-Strike: Source");
                b.AddRange(BitConverter.GetBytes((short)240));
                b.AddRange(new byte[] { 6, 21, 4, (byte)'d', (byte)'l', 0, 1 });
                Text("11003710");
                return b.ToArray();
            }

            static byte[] Players()
            {
                var b = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x44, 6 };
                void Player(string name, int score, float seconds)
                {
                    b.Add(0);
                    b.AddRange(Encoding.UTF8.GetBytes(name));
                    b.Add(0);
                    b.AddRange(BitConverter.GetBytes(score));
                    b.AddRange(BitConverter.GetBytes(seconds));
                }
                Player("Kamikaze TV (Auto-Recording)", 0, 9000);
                Player("WR | P1nkE ❤ˡᵒᵛᵉ ʸᵒ", 0, 9000);
                Player("SurfTimer Replay", 0, 9000);
                Player("SurfTimer Replay", 0, 9000);
                Player("voms", 3, 2505.5f);
                Player("ember", 0, 61);
                return b.ToArray();
            }

            /// <summary>An answer in pieces: FE FF FF FF, an id, how many pieces, which one, the most a piece holds, the piece.</summary>
            static IEnumerable<byte[]> Pieces(byte[] whole, int count)
            {
                var size = (whole.Length + count - 1) / count;
                for (var i = 0; i < count; i++)
                    yield return new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }.Concat(BitConverter.GetBytes(77)).Concat(new[] { (byte)count, (byte)i })
                        .Concat(BitConverter.GetBytes((short)1248)).Concat(whole.Skip(i * size).Take(size)).ToArray();
            }
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
            // Held, the card key repeats while the console is open: the card prints once, until the key is let go.
            var keysCfg = File.ReadAllText(Path.Combine(cstrike, "cfg", "ksf_companion.cfg"));
            Check("the card prints once per press", keysCfg.Contains("alias +ksf_card \"ksf_card_go\"") && keysCfg.Contains("alias ksf_card_go ksf_card_show")
                && keysCfg.Contains("alias ksf_card_show \"exec ksf_card; showconsole; alias ksf_card_go ksf_held\"")
                && keysCfg.Contains("alias -ksf_card \"hideconsole; gameui_hide; alias ksf_card_go ksf_card_show\""));
            Check("the map-load commands are /m and /pr", settings.Get("server_commands") == "sm_m; sm_pr" && GameConfig.ServerCommandsInChat(settings) == "/m and /pr");
            var oldFile = Path.Combine(root, "old-settings.ini");
            File.WriteAllText(oldFile, "server_commands = sm_m; sm_mrank\nview = simple\n");
            var old = new Settings(oldFile);
            Check("an old settings.ini moves on to /pr and loses the Simple view", old.Get("server_commands") == "sm_m; sm_pr" && !File.ReadAllText(oldFile).Contains("view ="));
            File.WriteAllText(oldFile, "server_commands = sm_wr\n");
            Check("commands you picked yourself stay", new Settings(oldFile).Get("server_commands") == "sm_wr");
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

            // The group tile: your best against the time at the end of the group.
            var tile = new DashboardViewModel();
            tile.ShowGroupGoal(new GroupGoal { Group = 3, Total = 465, FirstRank = 36, LastRank = 60, Cutoff = 37.696815, YourTime = 38.475, YourRank = 87, YourGroup = 4 });
            Check("the group tile says how much faster", tile.GroupGoalTitle == "TO GROUP 3" && tile.GroupGoalTime == "-0.779" && tile.GroupGoalDetail == "beat 0:37.696"
                && tile.GroupGoalNote == "ranks 36-60 of 465", $"{tile.GroupGoalTitle} / {tile.GroupGoalTime} / {tile.GroupGoalDetail} / {tile.GroupGoalNote}");
            tile.ShowGroupGoal(new GroupGoal { Group = 4, Total = 465, FirstRank = 61, LastRank = 100, YourTime = 38.475, YourRank = 87, YourGroup = 4 });
            Check("or that you're in it", tile.GroupGoalReached && tile.GroupGoalTime == "IN" && tile.GroupGoalDetail == "you're in it at #87", tile.GroupGoalDetail);

            // The server list: a server clicked open shows its players, the first 12 until you ask for everyone.
            var lists = new DashboardViewModel();
            var busy = new KsfServer { Game = "css", Name = "Busy", Address = "192.0.2.1:27015", Map = "surf_x", PlayerCount = 30 };
            for (var i = 0; i < 30; i++) busy.Players.Add(new KsfServerPlayer { SteamId = "STEAM_0:0:" + i, Name = "p" + i, Zone = i == 29 ? -1 : i % 5 });
            lists.SetServers(new List<KsfServer> { busy }, null, new HashSet<string>());
            Check("servers start closed", !lists.Servers[0].IsExpanded && lists.Servers[0].PlayerRows.Count == 0);
            lists.ToggleServerCommand.Execute(busy.Address);
            Check("clicked open: its players", lists.Servers[0].IsExpanded && lists.Servers[0].PlayerRows.Count == 12
                && lists.Servers[0].PlayersMore.StartsWith("+ 17 more surfing, 1 spectating"), $"{lists.Servers[0].PlayerRows.Count} / {lists.Servers[0].PlayersMore}");
            lists.ShowEveryoneCommand.Execute(busy.Address);
            Check("and everyone on it, the spectators after the surfers", lists.Servers[0].PlayerRows.Count == 30 && lists.Servers[0].PlayersMore == "show fewer"
                && lists.Servers[0].PlayerRows[29].Zone == "SPEC" && lists.Servers[0].PlayerRows[29].Name == "p29" && lists.Servers[0].PlayerRows.Take(29).All(r => r.Zone != "SPEC"));
            lists.SetMapProgress("css", "surf_x", MapProgress.From(new[] { new ZoneRecord { ZoneId = 0, Time = 40.5 } }, linear: true, stages: 0, bonuses: 2));
            Check("your progress on its map", lists.Servers[0].HasProgress && lists.Servers[0].YourTime == "0:40.500" && lists.Servers[0].StagePattern == "1" && lists.Servers[0].BonusPattern == "00");
            lists.SetLiveServer(busy, "STEAM_0:0:3", "surf_x");
            Check("your server's card: the first 12, then everyone", lists.LivePlayers.Count == 12 && lists.HasLiveMore && lists.LivePlayers[0].IsYou);
            lists.ShowEveryoneCommand.Execute("live");
            Check("on asking", lists.LivePlayers.Count == 30 && lists.LiveMore == "show fewer" && lists.LiveSubtitle.EndsWith("29 surfing  ·  1 spectating"), lists.LiveSubtitle);
            var watchers = new KsfServer { Game = "css", Name = "Expert", Address = "192.0.2.2:27015", Map = "surf_boreas", IsLinear = true };
            foreach (var (name, zone) in new[] { ("meow", 1), ("Doodito", 1), ("you", -1), ("a", -1), ("b", -1) })
                watchers.Players.Add(new KsfServerPlayer { SteamId = name == "you" ? "STEAM_0:0:3" : "STEAM_0:0:" + name, Name = name, Zone = zone });
            lists.SetLiveServer(watchers, "STEAM_0:0:3", "surf_boreas");
            Check("spectators by name, you first among them", lists.LivePlayers.Count == 5 && !lists.HasLiveMore && lists.LivePlayers[2].Name == "you"
                && lists.LivePlayers[2].IsYou && lists.LivePlayers[2].Zone == "SPEC" && lists.LivePlayers[0].Zone == "CP 1", string.Join(", ", lists.LivePlayers.Select(r => r.Name + " " + r.Zone)));

            // The session counts the time on servers only.
            var session = new DashboardViewModel();
            session.SetSession(TimeSpan.FromMinutes(10), null, 2, 1, 0);
            Check("off a server the session waits", session.SessionTime == "10:00" && session.SessionTimeLabel.StartsWith("paused"), session.SessionTime + " " + session.SessionTimeLabel);
            session.SetSession(TimeSpan.FromMinutes(10), DateTime.Now.AddMinutes(-5), 3, 1, 0);
            Check("and goes on from there", session.SessionTime == "15:00" && session.SessionTimeLabel == "played", session.SessionTime);
            session.EndSession(TimeSpan.FromMinutes(15));
            Check("the game closed: the last session's time", session.SessionTitle == "LAST SESSION" && session.SessionTime == "15:00");

            // Hiding a part (Layout[...] bindings) reaches the window.
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
