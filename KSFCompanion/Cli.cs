using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using KsfCompanion.Ui;

namespace KsfCompanion
{
    /// <summary>Command-line helpers for setting up and troubleshooting without the tray app.</summary>
    static class Cli
    {
        const string Usage =
            "ksf-companion                  run in the tray (normal use; --background starts without the dashboard)\n" +
            "ksf-companion --status         show what KSF Companion finds: Steam, CS:S, your account, -usercon, the game\n" +
            "ksf-companion --install        add the cfgs/keys to CS:S\n" +
            "ksf-companion --uninstall      remove them again (close CS:S first)\n" +
            "ksf-companion --card <map> [66|100]   print the KSF card for a map\n" +
            "ksf-companion --parse <log>    show which maps/F5 presses a console log contains\n" +
            "ksf-companion --preview <map> <file.png> [width] [height] [66|100]   render the dashboard to an image\n" +
            "               (map \"live\" = the busiest KSF server right now, with the live panels filled in;\n" +
            "                map \"sample\" = made-up data, no network needed)\n" +
            "ksf-companion --push \"<cmd>\"   send a console command to the running game and print its answer\n" +
            "ksf-companion --hud <demo.dem> show the timer text (stage you're on, stage finishes) found in a demo\n" +
            "ksf-companion --records <steamid|auto> [66|100] [file.png]   read a player's records page on ksf.surf as\n" +
            "               the Records tab does (and draw the tab to an image)\n" +
            "ksf-companion --server <ip:port>   ask a server what it tells a server browser (how a private KSF server\n" +
            "               in ksf_servers shows in the server list)\n" +
            "ksf-companion --clock-test | --binds-test | --selftest   the built-in tests";

        public static int Run(string[] args, Settings settings)
        {
            var output = Console.Out;

            try
            {
                switch (args[0])
                {
                    case "--install":
                    case "--uninstall":
                        {
                            var dir = SteamLocator.FindCstrikeDir(settings.Get("game_dir"));
                            if (dir == null)
                            {
                                output.WriteLine("Counter-Strike: Source not found - set game_dir in " + Path.Combine(Program.DataDir, "settings.ini"));
                                return 1;
                            }
                            var config = new GameConfig(dir);
                            if (args[0] == "--install") config.Install(settings);
                            else config.Uninstall(settings);
                            output.WriteLine((args[0] == "--install" ? "installed into " : "removed from ") + dir);
                            if (args[0] == "--install") output.WriteLine("Add -usercon to CS:S's launch options in Steam so KSF Companion can send the game console commands.");
                            return 0;
                        }

                    case "--status":
                        return Status(settings, output);

                    case "--card" when args.Length > 1:
                        {
                            var steamId = args.Length > 3 ? SteamLocator.ParseSteamId(args[3]) : SteamLocator.FindSteamId(settings.Get("steamid"));
                            using var api = new KsfApi();
                            var game = args.Length > 2 && args[2].StartsWith("100") ? "css100t" : args.Length > 2 && args[2].StartsWith("66") ? "css" : settings.FixedGame ?? "css";
                            var report = api.GetReportAsync(args[1].ToLowerInvariant(), steamId, game, settings.GetInt("ksf_style", 0, 3)).GetAwaiter().GetResult();
                            LoadZoneRecords(api, report, settings.GetInt("ksf_style", 0, 3));
                            output.WriteLine($"steamid: {steamId ?? "(unknown)"}   player: {report.PlayerName ?? "-"}");
                            foreach (var line in CardBuilder.Build(report, saved: false, KeyNames.From(settings)))
                                output.WriteLine(GameConfig.Tag + " " + line);
                            return 0;
                        }

                    case "--parse" when args.Length > 1:
                        {
                            var parser = new LogParser();
                            parser.MapChanged += map => output.WriteLine("map:  " + map);
                            parser.SaveRequested += () => output.WriteLine("save: (F5 pressed)");
                            parser.GameStarted += () => output.WriteLine("game: started");
                            foreach (var line in File.ReadAllLines(args[1])) parser.Feed(line);
                            return 0;
                        }

                    case "--preview" when args.Length > 2:
                        return Preview(args, settings, output);

                    case "--server" when args.Length > 1:
                        return Server(args[1], output);

                    case "--records" when args.Length > 1:
                        return Records(args, settings, output);

                    case "--clock-test":
                        return ClockTest(output);
                    case "--binds-test":
                        return BindsTest(output);
                    case "--selftest":
                        return SelfTest.Run(output);
                    case "--hud" when args.Length > 1:
                        {
                            // Reads the demo the way the app reads the live one, in pieces, as if the game were still writing it.
                            var demo = Path.GetFullPath(args[1]);
                            var hud = new LiveHud(Path.GetDirectoryName(demo));
                            hud.Watch(demo, alreadyRunning: false);
                            hud.ZoneChanged += zone => output.WriteLine("on:       " + MapReport.ZoneName(zone));
                            hud.ZoneFinished += (zone, time, live) => output.WriteLine($"finished: {MapReport.ZoneName(zone)} in {Format.Short(time)}{(live ? "" : "  (from before)")}");
                            hud.TimeLeftShown += (minutes, previous, ago) =>
                            {
                                if (previous != minutes) output.WriteLine($"timeleft: {minutes} min{(previous is int p ? $" (was {p})" : "")}");
                            };
                            hud.ReadWholeDemo();
                            return 0;
                        }

                    case "--push" when args.Length > 1:
                        {
                            if (GameBridge.FindGameProcessId() == 0)
                            {
                                output.WriteLine("CS:S isn't running");
                                return 1;
                            }
                            using var link = new GameLink(GameConfig.RconPort(settings), GameConfig.RconPassword(settings));
                            var problem = link.OpenAsync().GetAwaiter().GetResult();
                            if (problem != LinkProblem.None)
                            {
                                output.WriteLine(problem == LinkProblem.BadPassword
                                    ? "the game didn't accept KSF Companion's password - restart CS:S once"
                                    : $"the game isn't listening on port {GameConfig.RconPort(settings)} - is -usercon in CS:S's launch options?");
                                return 1;
                            }
                            var reply = link.SendAsync(args[1], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                            if (reply == null)
                            {
                                output.WriteLine("the game didn't answer (busy loading?)");
                                return 1;
                            }
                            output.Write(reply);
                            if (reply.Length > 0 && !reply.EndsWith("\n", StringComparison.Ordinal)) output.WriteLine();
                            return 0;
                        }

                    default:
                        output.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                output.WriteLine("error: " + ex.Message);
                return 1;
            }
            finally
            {
                output.Flush();
            }
        }

        /// <summary>What KSF Companion finds on this PC - the first thing to look at when something doesn't work.</summary>
        /// <summary>
        /// A player's records page on ksf.surf, read as the Records tab reads it: how many maps, what's done, the best
        /// records - and the tab drawn to a picture, with the maps' pictures, if a file is given.
        /// </summary>
        static int Records(string[] args, Settings settings, TextWriter output)
        {
            var steamId = args[1] == "auto" ? SteamLocator.FindSteamId(settings.Get("steamid")) : SteamLocator.ParseSteamId(args[1]);
            if (steamId == null)
            {
                output.WriteLine("whose records? a Steam ID (STEAM_0:1:123, [U:1:246] or 7656...), or auto for yours");
                return 2;
            }
            var game = args.Length > 2 && args[2].StartsWith("100", StringComparison.Ordinal) ? "css100t" : "css";
            var style = settings.GetInt("ksf_style", 0, 3);
            using var api = new KsfApi();
            // (Off the main thread, like the app's own requests: they carry on where they were asked.)
            var records = Task.Run(() => api.GetRecordsAsync(steamId, game, style)).GetAwaiter().GetResult();
            if (records == null)
            {
                output.WriteLine("ksf.surf's records page had no list of maps in it");
                return 1;
            }
            var done = records.Where(r => r.IsDone).ToList();
            output.WriteLine($"{records.Count} maps, {done.Count} done, {done.Count(r => r.Rank == 1)} WRs, {done.Count(r => r.Rank <= 10)} in the top 10;"
                             + " groups " + string.Join(" ", Enumerable.Range(1, KsfGroups.Count).Select(g => $"{g}:{done.Count(r => r.Group == g)}"))
                             + $"; {records.Count(r => !r.IsDone && (r.Stages.Contains(true) || r.Bonuses.Contains(true)))} not done with zones done");
            static string Bits(bool[] flags) => flags.Length == 0 ? "-" : new string(flags.Select(f => f ? '1' : '0').ToArray());
            foreach (var r in RecordsViewModel.Sorted(records, "points").Take(12))
                output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-30} T{1} {2,-6} {3,10} {4,10} {5,5} {6,6:0} pts {7,4}x {8:yyyy-MM-dd}  stages {9}  bonuses {10}",
                    r.Map, r.Tier, r.IsLinear ? "linear" : "staged", r.Time is double t ? Format.Time(t) : "-", r.WrDiff is double d ? "+" + Format.Short(d) : "",
                    r.Rank is int k ? "#" + k : r.Group is int g ? "g" + g : "", r.Points ?? 0, r.Completions ?? 0, r.Date, Bits(r.Stages), Bits(r.Bonuses)));

            // Below the top 10 the page gives only the group: the Records tab reads each map's place from its own
            // leaderboard. A few of them here, checked against the page (the same run, a place in that group).
            foreach (var r in RecordsViewModel.Sorted(records, "points").Where(r => r.IsDone && r.Rank == null && r.Group != null).Take(6))
            {
                var zones = Task.Run(() => api.GetPlayerZonesAsync(r.Map, steamId, game, style, CancellationToken.None)).GetAwaiter().GetResult();
                var main = zones.FirstOrDefault(z => z.ZoneId == 0);
                var same = main?.Time is double time && MapRankStore.SameTime(time, r.Time.Value);
                if (same && main.Rank is int place)
                {
                    r.Place = place;
                    r.Players = main.TotalRanks;
                }
                var total = main?.TotalRanks ?? 0;
                output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  place on {0,-30} page {1:R}  leaderboard {2}  {3}  #{4} of {5}  group {6} ({7}-{8} by the rule)",
                    r.Map, r.Time, main?.Time?.ToString("R", CultureInfo.InvariantCulture) ?? "-", same ? "same run" : "NOT THE SAME RUN", main?.Rank, main?.TotalRanks,
                    r.Group, KsfGroups.FirstRank(r.Group.Value, total), KsfGroups.LastRank(r.Group.Value, total)));
            }
            if (args.Length < 4) return 0;

            StartHeadless();
            BarFill.Animate = false;
            var images = new ImageCache(api.Http);
            var vm = new DashboardViewModel();
            var nothing = new RelayCommand(_ => { });
            vm.OpenLaterCommand = vm.ToggleSavedCommand = vm.NominateTickCommand = vm.Records.RefreshCommand = nothing;
            var wanted = new List<RecordRow>();
            vm.Records.ThumbsNeeded += rows => wanted.AddRange(rows);
            vm.NominateTick = game;
            vm.Records.SetRecords(records, (game == "css100t" ? "100T" : "66T"), DateTime.Now, loading: false);
            vm.Page = "records";
            var thumbs = Task.Run(() => Task.WhenAll(wanted.Select(row => images.MapAsync(row.Map, 240)))).GetAwaiter().GetResult();
            for (var i = 0; i < wanted.Count; i++) vm.Records.SetThumb(wanted[i], thumbs[i]);
            return Render(vm, KeyNames.From(settings), args[3], 1600, 1100, output);
        }

        /// <summary>What a game server tells a server browser about itself (A2S): what the server list shows of a private KSF server.</summary>
        static int Server(string address, TextWriter output)
        {
            if (!System.Net.IPEndPoint.TryParse(address, out var endPoint) || endPoint.Port == 0)
            {
                output.WriteLine("that's not a server's address - it's ip:port, like 192.0.2.7:27015");
                return 2;
            }
            var timeout = TimeSpan.FromSeconds(3);
            var info = A2s.InfoAsync(address, timeout, CancellationToken.None).GetAwaiter().GetResult();
            if (info == null)
            {
                output.WriteLine($"{address} didn't answer in {timeout.TotalSeconds:0} seconds: the server list can only show it while you're on it (from the game's \"status\")");
                return 1;
            }
            output.WriteLine($"name:    {info.Name}");
            output.WriteLine($"map:     {info.Map}");
            output.WriteLine($"players: {Math.Max(0, info.Players - info.Bots)} ({info.Bots} bots, {info.MaxPlayers} max)");
            var players = A2s.PlayersAsync(address, timeout, CancellationToken.None).GetAwaiter().GetResult();
            if (players == null)
            {
                output.WriteLine("it doesn't say who's on it");
                return 0;
            }
            var people = A2s.People(info, players);
            foreach (var p in players.OrderBy(p => !people.Contains(p)).ThenByDescending(p => p.Seconds))
                output.WriteLine($"  {(people.Contains(p) ? "   " : "bot")}  {Format.Duration(p.Seconds),9}  {p.Name}");
            return 0;
        }

        static int Status(Settings settings, TextWriter output)
        {
            void Line(string what, string value) => output.WriteLine($"{what,-24}{value}");
            Line("settings", Path.Combine(Program.DataDir, "settings.ini"));
            Line("cache", Program.CacheDir);
            var roots = SteamLocator.SteamRoots().ToList();
            Line("steam", roots.Count == 0 ? "not found (looked in ~/.steam, ~/.local/share/Steam, the Flatpak and the Snap)" : string.Join("  ", roots));
            var dir = SteamLocator.FindCstrikeDir(settings.Get("game_dir"));
            Line("counter-strike: source", dir ?? "not found - put your .../Counter-Strike Source/cstrike folder in settings.ini (game_dir)");
            if (dir != null)
                Line("set up in the game", new GameConfig(dir).IsInstalled ? "yes (autoexec.cfg has KSF Companion's block)" : "not yet - it is when KSF Companion starts");
            var steamId = SteamLocator.FindSteamId(settings.Get("steamid"));
            Line("steam account", steamId ?? "unknown - log into Steam, or set steamid in settings.ini");
            var options = steamId == null ? null : SteamLocator.LaunchOptions(steamId);
            Line("cs:s launch options", options == null ? "couldn't read Steam's config for this account" : options.Length == 0 ? "(none)" : options);
            if (options != null)
                Line("-usercon", SteamLocator.HasLaunchOption(options, "-usercon") ? "yes" : "MISSING - add it, so KSF Companion can send the game console commands");
            var pid = GameBridge.FindGameProcessId();
            Line("game", pid == 0 ? "not running" : $"running (process {pid})");
            if (pid != 0)
            {
                var port = GameConfig.RconPort(settings);
                using var link = new GameLink(port, GameConfig.RconPassword(settings));
                var problem = link.OpenAsync().GetAwaiter().GetResult();
                Line("game console (rcon)", problem == LinkProblem.None ? $"connected (port {port})"
                    : problem == LinkProblem.BadPassword ? "the password wasn't accepted - restart CS:S once"
                    : $"nothing listening on port {port} - -usercon missing, or the game started before KSF Companion set it up");
            }
            return 0;
        }

        /// <summary>The app's look without a display: Skia drawing into memory, for --preview and the UI self-test.</summary>
        internal static void StartHeadless()
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
                .SetupWithoutStarting();
        }

        /// <summary>Renders the dashboard with live KSF data (or made-up data for "sample") to a PNG without opening a window.</summary>
        static int Preview(string[] args, Settings settings, TextWriter output)
        {
            var map = args[1].ToLowerInvariant();
            var png = args[2];
            var width = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1440;
            var height = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 940;
            var game = args.Length > 5 && args[5].StartsWith("100") ? "css100t" : settings.FixedGame ?? "css";
            var style = settings.GetInt("ksf_style", 0, 3);
            var steamId = SteamLocator.FindSteamId(settings.Get("steamid")) ?? SteamLocator.ParseSteamId(settings.Get("last_steamid"));

            StartHeadless();
            // The picture is taken straight away, before any bar could fill up.
            BarFill.Animate = false;
            if (map == "sample")
            {
                var sample = SampleData.Dashboard();
                return Render(sample, KeyNames.From(settings), png, width, height, output);
            }
            using var api = new KsfApi();
            var images = new ImageCache(api.Http);
            var later = new PlayLaterList(Path.Combine(Program.DataDir, "play-later.txt"));
            var vm = new DashboardViewModel();

            // "--preview live ..." shows the busiest KSF server right now as if you were on it, with the live panels filled in.
            KsfServer liveServer = null;
            List<KsfServer> everyServer = null;
            if (map == "live")
            {
                everyServer = api.GetServersAsync("css").GetAwaiter().GetResult().Concat(api.GetServersAsync("css100t").GetAwaiter().GetResult()).ToList();
                liveServer = everyServer.OrderByDescending(s => s.Players.Count(p => p.Zone != -1)).FirstOrDefault();
                if (liveServer == null) throw new InvalidOperationException("ksf.surf returned no servers");
                map = liveServer.Map;
                game = liveServer.Game;
            }

            var report = api.GetReportAsync(map, steamId, game, style).GetAwaiter().GetResult();
            LoadZoneRecords(api, report, style);
            // KSFC_PREVIEW_NEW="4=10.5;32=11" shows those zones as times just set in game (not on ksf.surf yet).
            foreach (var pair in (Environment.GetEnvironmentVariable("KSFC_PREVIEW_NEW") ?? "").Split(';'))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2 || !int.TryParse(kv[0], out var zoneId) || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var newTime)) continue;
                var zone = report.Zone(zoneId);
                if (zone == null) report.Zones.Add(zone = new ZoneRecord { ZoneId = zoneId });
                zone.Time = newTime;
                zone.Rank = null;
                zone.Unsynced = true;
            }
            vm.ShowLoading(map, live: true);
            vm.ShowReport(report, later.Contains(map));
            if (report.IsOnKsf)
            {
                // The group tile: the group set in settings.ini (or the next one up from yours), and the time at its end -
                // with KSF's own cutoffs from ksf.surf, next to the ones the rule works out.
                var total = report.Main?.TotalRanks
                    ?? (report.Wr?.SteamId is string holder ? api.GetTotalRanksAsync(report.Info.Name, holder, game, style).GetAwaiter().GetResult() : null) ?? 0;
                var ends = api.GetGroupEndsAsync(report.Info.Name, game, style).GetAwaiter().GetResult();
                output.WriteLine($"group cutoffs: ksf.surf {(ends != null ? string.Join(",", ends) : "-")}, worked out {(total > 0 ? string.Join(",", KsfGroups.Ends(total)) : "-")} ({total} players)");
                var goal = GroupGoal.For(report, GroupGoal.Picked(settings.Get("group_goal")), total, ends);
                if (goal.NeedsCutoff && goal.LastRank is int last)
                    goal.Cutoff = goal.Group == 0 && report.Top.Count >= last ? report.Top[last - 1].Time
                        : api.GetRecordAtRankAsync(report.Info.Name, 0, last, game, style).GetAwaiter().GetResult()?.Time;
                vm.ShowGroupGoal(goal);
                output.WriteLine($"group tile: {vm.GroupGoalTitle}  {vm.GroupGoalTime}  {vm.GroupGoalDetail}  {vm.GroupGoalNote}");
            }
            // KSFC_PREVIEW_STAGE=3 shows stage 3 as the one you're on (31 = bonus 1).
            if (int.TryParse(Environment.GetEnvironmentVariable("KSFC_PREVIEW_STAGE"), out var onZone)) vm.SetCurrentZone(onZone);
            // The leaderboard follows a bonus you're on, as in the app; KSFC_PREVIEW_LEADER=2 picks stage 2's instead.
            var leaderZone = int.TryParse(Environment.GetEnvironmentVariable("KSFC_PREVIEW_LEADER"), out var picked) ? picked : MapReport.IsBonus(onZone) ? onZone : 0;
            if (leaderZone != 0 && report.RecordZones.Contains(leaderZone))
            {
                vm.SetLeaderZone(leaderZone, leaderZone != (MapReport.IsBonus(onZone) ? onZone : 0));
                vm.SetZoneTop(leaderZone, api.GetZoneTopAsync(map, leaderZone, game, style).GetAwaiter().GetResult());
            }
            vm.MapImage = images.MapAsync(map, 1600).GetAwaiter().GetResult();
            vm.SetPlayer(report.PlayerName, report.PlayerCountry, null, null, null);
            vm.SetStatus("Connected to CS:S", Connection.Connected);

            var servers = api.GetServersAsync(game).GetAwaiter().GetResult();
            var saved = new HashSet<string>(later.Items.Select(e => e.Map), StringComparer.OrdinalIgnoreCase);
            foreach (var row in vm.SetPlayLater(later.Items, map, servers))
                row.Thumb = images.MapAsync(row.Map, 240).GetAwaiter().GetResult();
            vm.SetServers(servers, null, saved);
            if (steamId != null)
            {
                vm.SetRecent(api.GetRecentAsync(steamId, game, style).GetAwaiter().GetResult());
                var avatar = api.GetAvatarUrlAsync(steamId).GetAwaiter().GetResult();
                if (avatar != null) vm.Avatar = images.AvatarAsync(avatar).GetAwaiter().GetResult();
            }

            // Your level on both tick rates, from your ksf.surf profile.
            if (steamId != null)
            {
                var rows = new List<(PlayerStanding, int?)>();
                foreach (var tickGame in new[] { "css", "css100t" })
                {
                    var standing = Task.Run(() => api.GetStandingAsync(steamId, tickGame)).GetAwaiter().GetResult();
                    if (standing == null) continue;
                    var index = KsfLevels.IndexOf(standing);
                    int? needed = index > 0 && KsfLevels.Ladder[index - 1].TopRank is int top
                        ? Task.Run(() => api.GetPointsAtRankAsync(top, tickGame, style)).GetAwaiter().GetResult()
                        : null;
                    rows.Add((standing, needed));
                }
                if (rows.Count > 0) vm.SetLevels(rows, game);
            }

            // KSFC_PREVIEW_TIMELEFT="555,80,2": the big countdown with 555 s left of an 80 min limit, extended twice by 10 min.
            var fakeClock = (Environment.GetEnvironmentVariable("KSFC_PREVIEW_TIMELEFT") ?? "").Split(',');
            if (fakeClock.Length == 3 && double.TryParse(fakeClock[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondsLeft)
                && double.TryParse(fakeClock[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var limitMinutes)
                && int.TryParse(fakeClock[2], out var extensions))
            {
                var now = DateTime.Now;
                var clock = new MapClock();
                var limit = limitMinutes - 10 * extensions;
                clock.FromConsole(limit, now.AddSeconds(-(limitMinutes * 60 - secondsLeft) + 30));
                for (var i = 0; i < extensions; i++)
                {
                    clock.Extended(10, now);
                    clock.FromConsole(limit += 10, now);
                }
                clock.Countdown(secondsLeft, now);
                vm.Clock = clock;
                vm.Tick(now);
            }

            // KSFC_PREVIEW_HIDE="live,servers" hides those parts.
            vm.Layout.Load((Environment.GetEnvironmentVariable("KSFC_PREVIEW_HIDE") ?? "").Split(','));

            var celebrate = Environment.GetEnvironmentVariable("KSFC_PREVIEW_CELEBRATE") == "1";
            if (liveServer != null)
            {
                // Stand in for one of the players, so the "you" row shows too.
                var you = liveServer.Players.Where(p => p.Zone != -1).Skip(2).FirstOrDefault()?.SteamId;
                vm.SetServerLine(liveServer);
                var clock = new MapClock();
                clock.FromKsf(liveServer.TimeLimitMinutes, liveServer.TimeLeftSeconds, liveServer.FetchedAt);
                vm.Clock = clock;
                vm.SetLiveServer(liveServer, you, map);
                // Your progress on each server's map (here: that player's), and the busiest other server opened.
                vm.YourSteamId = steamId ?? you;
                vm.SetServers(everyServer, liveServer.Address, saved);
                var progressOf = vm.YourSteamId;
                if (progressOf != null)
                    foreach (var server in everyServer.Take(Environment.GetEnvironmentVariable("KSFC_PREVIEW_PROGRESS") == "0" ? 0 : 30))
                    {
                        // (On another thread, like the other paced lookups: they carry on on the thread that asked.)
                        List<ZoneRecord> zones;
                        try { zones = Task.Run(() => api.GetPlayerZonesAsync(server.Map, progressOf, server.Game, style, CancellationToken.None)).GetAwaiter().GetResult(); }
                        catch (System.Net.Http.HttpRequestException ex)
                        {
                            output.WriteLine($"progress on {server.Map}: {ex.Message}");
                            continue;
                        }
                        vm.SetMapProgress(server.Game, server.Map, MapProgress.From(zones, server.IsLinear, server.StageCount, server.BonusCount));
                    }
                var open = everyServer.Where(s => s != liveServer).OrderByDescending(s => s.Players.Count).FirstOrDefault();
                if (open != null) vm.ToggleServerCommand.Execute(open.Address);
                vm.SetSession(TimeSpan.FromMinutes(71), DateTime.Now.AddMinutes(-12), 4, 3, 1);
                var next = everyServer.FirstOrDefault(s => s != liveServer && s.Map != map);
                if (next != null) vm.SetNextMap(next.Map, next.Tier);
            }
            vm.AmbientImage = images.AmbientAsync(map).GetAwaiter().GetResult();
            if (celebrate)
            {
                vm.CelebrationTitle = "NEW PERSONAL BEST";
                vm.CelebrationDetail = "4:12.33   -1.207   +47 pts";
            }

            // KSFC_PREVIEW_PAGE=nominate shows the nominate page (KSFC_PREVIEW_VIEW=list for the list view,
            // KSFC_PREVIEW_DONE=todo/done for that filter). Its maps: the app's saved map list, or the first 30 of KSF's -
            // or KSFC_PREVIEW_CATALOG=<file>, read in full into that file the first time. The maps you've finished: the
            // app's saved ones, or KSFC_PREVIEW_FINISHED=<file>, read from ksf.surf into that file the first time.
            // KSFC_PREVIEW_PAGE=binds shows the binds page, with KSFC_PREVIEW_BINDS=restart=r|saveloc=MOUSE4 set
            // (KSFC_PREVIEW_SEARCH searches it, KSFC_PREVIEW_CAPTURE=<id> shows that row waiting for a key).
            if (Environment.GetEnvironmentVariable("KSFC_PREVIEW_PAGE") == "binds")
            {
                var appKeys = new Dictionary<string, string> { ["app_save"] = "F5", ["app_card"] = "F6", ["app_list"] = "F7" };
                vm.Binds.Load(BindSet.Parse(Environment.GetEnvironmentVariable("KSFC_PREVIEW_BINDS")), appKeys, 210,
                    key => key == "r" ? "+reload" : key == "e" ? "+use" : null);
                // Your game's own binds (read only).
                var cstrike = SteamLocator.FindCstrikeDir("auto");
                if (cstrike != null) vm.Binds.SetGameBinds(new GameConfig(cstrike).CurrentBinds());
                vm.Binds.Search = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SEARCH") ?? "";
                var capture = Environment.GetEnvironmentVariable("KSFC_PREVIEW_CAPTURE");
                var row = vm.Binds.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Action.Id == capture);
                if (row != null) vm.Binds.CaptureCommand.Execute(row);
                vm.Page = "binds";
            }

            if (Environment.GetEnvironmentVariable("KSFC_PREVIEW_PAGE") == "nominate")
            {
                var dataDir = Program.CacheDir;
                var catalogFile = Environment.GetEnvironmentVariable("KSFC_PREVIEW_CATALOG");
                var catalog = new MapCatalog(catalogFile ?? Path.Combine(dataDir, "maps.txt"));
                if (catalogFile != null && catalog.Count == 0)
                {
                    for (var start = 1; start < 3000; start += 10)
                    {
                        var from = start;
                        var page = Task.Run(() => api.GetMapsPageAsync(from, CancellationToken.None)).GetAwaiter().GetResult();
                        if (page.Count == 0) break;
                        catalog.Add(page);
                    }
                    catalog.MarkComplete();
                    catalog.Save();
                }
                var maps = catalog.Maps;
                if (maps.Count == 0)
                    for (var start = 1; start <= 21; start += 10)
                    {
                        var from = start;
                        maps.AddRange(Task.Run(() => api.GetMapsPageAsync(from, CancellationToken.None)).GetAwaiter().GetResult());
                    }

                var finishedFile = Environment.GetEnvironmentVariable("KSFC_PREVIEW_FINISHED");
                var finished = new FinishedMaps(finishedFile ?? Path.Combine(dataDir, "finished-maps.txt"));
                var finishedKey = FinishedMaps.Key(steamId, game, style);
                if (finishedFile != null && finishedKey != null && finished.ReadAt(finishedKey) == DateTime.MinValue)
                {
                    Task.Run(() => api.GetFinishedMapsAsync(steamId, game, style, page => finished.Merge(finishedKey, page), CancellationToken.None)).GetAwaiter().GetResult();
                    finished.MarkRead(finishedKey);
                    finished.Save();
                }

                vm.ThumbsNeeded += rows =>
                {
                    foreach (var row in rows) vm.SetMapThumb(row, images.MapAsync(row.Map, 240).GetAwaiter().GetResult());
                };
                vm.SetMapsContext(saved, map);
                vm.SetFinishedMaps(finished.Of(finishedKey), loading: false);
                vm.SetMapCatalog(maps, loading: false);
                vm.MapView = Environment.GetEnvironmentVariable("KSFC_PREVIEW_VIEW") == "list" ? "list" : "tiles";
                vm.MapDone = Environment.GetEnvironmentVariable("KSFC_PREVIEW_DONE") ?? "all";
                // KSFC_PREVIEW_SEARCH=<text>: as if typed in the search box.
                vm.MapSearch = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SEARCH") ?? "";
                vm.Page = "nominate";
            }

            return Render(vm, KeyNames.From(settings), png, width, height, output, celebrate);
        }

        /// <summary>Lays the dashboard out at the given size and saves what it draws as a PNG.</summary>
        internal static int Render(DashboardViewModel vm, KeyNames keys, string png, int width, int height, TextWriter output, bool celebrate = false)
        {
            var celebrateStill = celebrate || Environment.GetEnvironmentVariable("KSFC_PREVIEW_CELEBRATE") == "1";
            var window = new DashboardWindow(vm, keys) { Width = width, Height = height };
            if (celebrateStill) window.ShowCelebrationStill();
            window.Show();
            window.Relayout(width);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame == null)
            {
                output.WriteLine("nothing was drawn");
                return 1;
            }
            frame.Save(png);
            output.WriteLine("rendered " + png);
            window.AllowClose = true;
            window.Close();
            return 0;
        }

        /// <summary>The map clock against made-up timelines: joining mid-map, extensions (said and unsaid), the timer's panel and countdown.</summary>
        /// <summary>The binds page's key handling: moving a key between actions, KSF Companion's own keys, your own commands.</summary>
        static int BindsTest(TextWriter output)
        {
            var failures = 0;
            var vm = new BindsViewModel();
            var changes = new List<string>();
            var toasts = new List<string>();
            vm.Changed += c => changes.Add($"{c.Action.Id}:{c.OldKey ?? "-"}>{c.NewKey ?? "-"}");
            vm.Message += toasts.Add;
            vm.Load(BindSet.Parse("restart=r"), new Dictionary<string, string> { ["app_save"] = "F5", ["app_card"] = "F6", ["app_list"] = "F7" }, 210,
                key => key == "r" ? "+reload" : null);
            BindRow Row(string id) => vm.Groups.SelectMany(g => g.Rows).First(r => r.Action.Id == id);
            void Press(string id, string key) { vm.CaptureCommand.Execute(Row(id)); vm.Capture(key); }
            void Check(string what, bool ok)
            {
                output.WriteLine((ok ? "ok    " : "FAIL  ") + what);
                if (!ok) failures++;
            }

            Press("restart_stage", "r");
            Check("a key moves from one action to another", changes.SequenceEqual(new[] { "restart:r>-", "restart_stage:->r" }) && Row("restart").Key == null);
            Check("the moved key says what it replaces", Row("restart_stage").Replaces == "replaces +reload");
            changes.Clear();
            Press("saveloc", "F5");
            Check("KSF Companion's keys can't be taken", changes.Count == 0 && Row("app_save").Key == "F5" && toasts.Last().Contains("give that one another key"));
            Press("app_save", "r");
            Check("KSF Companion's key can't take a used key", changes.Count == 0 && Row("app_save").Key == "F5");
            Press("app_save", "F8");
            Check("KSF Companion's key moves to a free key", changes.SequenceEqual(new[] { "app_save:F5>F8" }));
            changes.Clear();
            Press("turn_left", "w");
            Check("a movement key gets a warning", toasts.Last().StartsWith("Careful") && Row("turn_left").Key == "w");
            vm.ClearCommand.Execute(Row("turn_left"));
            Check("taking a key off", changes.Last() == "turn_left:w>-" && Row("turn_left").Key == null);
            vm.CaptureCommand.Execute(Row("loadloc"));
            vm.CancelCapture();
            Check("Esc / clicking away doesn't change anything", Row("loadloc").Key == null && !vm.IsCapturing);
            vm.OwnCommand = "!b 1";
            vm.AddOwnCommand.Execute(null);
            Check("your own command waits for its key", vm.IsCapturing && Row(BindCatalog.CustomPrefix + "sm_b 1").IsCapturing);
            vm.Capture("KP_END");
            Check("your own command gets it", Row(BindCatalog.CustomPrefix + "sm_b 1").Key == "KP_END");
            vm.OwnCommand = "sm_r; say hi";
            vm.AddOwnCommand.Execute(null);
            Check("a command that chains others is refused", !vm.IsCapturing && toasts.Last().Contains("can't be bound"));
            vm.Search = "saveloc";
            Check("search finds by command", vm.Groups.SelectMany(g => g.Rows).Any(r => r.Action.Id == "saveloc") && vm.Groups.SelectMany(g => g.Rows).Count() <= 3);
            vm.Search = "num 1";
            Check("search finds by key", vm.Groups.SelectMany(g => g.Rows).Select(r => r.Action.Id).SequenceEqual(new[] { BindCatalog.CustomPrefix + "sm_b 1" }));
            vm.Search = "";
            // Your game's own binds show on their rows; taking a key over moves it, taking ours off gives it back.
            var removed = new List<string>();
            vm.GameKeyRemoved += removed.Add;
            vm.SetGameBinds(new Dictionary<string, string> { ["k"] = "sm_saveloc", ["MOUSE4"] = "sm_saveloc", ["MOUSE1"] = "+left", ["l"] = "sm_tele",
                ["v"] = "sm_stuck", ["5"] = "sm_noclip", ["w"] = "+forward", ["h"] = "sm_rtv" });
            Check("your binds show on their rows", Row("saveloc").InGame.Select(c => c.Key).SequenceEqual(new[] { "k", "MOUSE4" })
                && Row("turn_left").InGame.Count == 1 && Row("loadloc").InGame.Count == 1 && Row("restart_stage").InGame.Single().Key == "v");
            Check("other sm_ binds become your own commands", Row(BindCatalog.CustomPrefix + "sm_noclip").InGame.Single().Key == "5");
            Check("the row says Add key next to them", Row("saveloc").KeyLabel == "Add key");
            toasts.Clear();
            Press("saveloc", "k");
            Check("a key that already does it isn't set again", toasts.Last().Contains("already does") && Row("saveloc").Key == null);
            Press("rtv", "l");
            Check("taking over one of your keys moves it off its row", Row("loadloc").InGame.Count == 0 && Row("rtv").Key == "l");
            vm.RemoveGameKeyCommand.Execute(Row("saveloc").InGame.First());
            Check("x takes one of your binds off", removed.SequenceEqual(new[] { "k" }) && Row("saveloc").InGame.Single().Key == "MOUSE4");
            Check("status counts them", vm.Status.EndsWith("keys bound"));

            var set = BindSet.Parse("restart_stage=r|custom:sm_b 1=KP_END|app_save=F9|nope=x|saveloc=;");
            Check("saved binds read back (only real actions and keys)", set.ToString() == "restart_stage=r|custom:sm_b 1=KP_END");
            Check("key names", GameKeys.Label("KP_END") == "Num 1" && GameKeys.Label("MOUSE4") == "Mouse 4" && GameKeys.Normalize("R") == "r" && GameKeys.Normalize("f5") == "F5"
                               && GameKeys.Normalize("ESCAPE") == null && GameKeys.Normalize("`") == null);
            output.WriteLine(failures == 0 ? "all good" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        static int ClockTest(TextWriter output)
        {
            var failures = 0;
            var t = new DateTime(2026, 1, 1, 12, 0, 0);
            void Check(string what, double? actual, double? expected, double tolerance = 0.6)
            {
                var ok = actual == null ? expected == null : expected != null && Math.Abs(actual.Value - expected.Value) <= tolerance;
                if (!ok) failures++;
                output.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}: {actual?.ToString("0.0", CultureInfo.InvariantCulture) ?? "none"}"
                                 + (ok ? "" : $" (expected {expected?.ToString("0.0", CultureInfo.InvariantCulture) ?? "none"})"));
            }

            // Joined mid-map: the limit from the console, the start from ksf.surf's 70 s old sample; the panel agrees.
            var clock = new MapClock();
            clock.FromConsole(60, t);
            clock.FromKsf(60, 1500, t.AddSeconds(-70));
            Check("joined: ksf.surf sample 70 s old", clock.LeftAt(t), 1430);
            clock.FromPanel(23, null, null, t.AddSeconds(8));
            Check("joined: panel agrees", clock.LeftAt(t.AddSeconds(8)), 1422);
            clock.FromPanel(22, 23, 2, t.AddSeconds(52));
            Check("joined: minute mark agrees", clock.LeftAt(t.AddSeconds(52)), 1378);

            // "The Map has Been extended for 10 minutes", then mp_timelimit confirms.
            var at = t.AddSeconds(1400);
            clock.Extended(10, at);
            Check("extended 10 min (chat)", clock.LeftAt(at), 30 + 600);
            clock.FromConsole(70, at.AddSeconds(1.5));
            Check("extended 10 min, console says 70", clock.LeftAt(at.AddSeconds(1.5)), 628.5);
            clock.FromPanel(10, 0, 3, at.AddSeconds(6));
            Check("extended 10 min, panel jump already counted", clock.LeftAt(at.AddSeconds(6)), 624);

            // "[SM] The current map has been extended": no number until mp_timelimit says 85.
            at = at.AddSeconds(600);
            clock.Extended(null, at);
            Check("extended by ? (vote)", clock.LeftAt(at), null);
            output.WriteLine($"{(clock.Extending ? "ok  " : "FAIL")}  extended by ?: shows 'extended'");
            if (!clock.Extending) failures++;
            clock.FromConsole(85, at.AddSeconds(1.5));
            Check("extended by ?, console says 85", clock.LeftAt(at.AddSeconds(1.5)), 30 - 1.5 + 900);
            Check("extensions counted", clock.Extensions, 2);
            Check("minutes added", clock.ExtendedMinutes, 25);
            output.WriteLine($"{(!clock.SeenFromStart ? "ok  " : "FAIL")}  joined mid-map: extensions are 'since you joined'");
            if (clock.SeenFromStart) failures++;

            // There from the start; one extension said in chat, one only seen as a higher mp_timelimit.
            clock = new MapClock();
            clock.FromKsf(30, 1790, t.AddSeconds(-5));
            clock.FromConsole(30, t);
            clock.Extended(10, t.AddSeconds(600));
            clock.FromConsole(40, t.AddSeconds(602));
            clock.FromConsole(40, t.AddSeconds(700));
            clock.FromConsole(55, t.AddSeconds(900));
            Check("from the start: extensions (one unsaid)", clock.Extensions, 2);
            Check("from the start: minutes added", clock.ExtendedMinutes, 25);
            output.WriteLine($"{(clock.SeenFromStart ? "ok  " : "FAIL")}  from the start: all extensions counted");
            if (!clock.SeenFromStart) failures++;

            // The console was read before the new limit arrived: the panel's jump asks again.
            clock = new MapClock();
            clock.FromConsole(60, t);
            clock.FromKsf(60, 100, t);
            clock.Extended(null, t.AddSeconds(10));
            clock.FromConsole(60, t.AddSeconds(11.5));
            var again = clock.FromPanel(15, 1, 2, t.AddSeconds(16));
            output.WriteLine($"{(again ? "ok  " : "FAIL")}  stale console value: the panel's jump asks to read it again");
            if (!again) failures++;
            clock.FromConsole(75, t.AddSeconds(17));
            Check("stale console value, read again: 75", clock.LeftAt(t.AddSeconds(17)), 100 - 17 + 900);

            // Not in ksf.surf's list: the panel alone, a rough guess until its first minute mark.
            clock = new MapClock();
            clock.FromConsole(30, t);
            clock.FromPanel(12, null, null, t);
            Check("panel only: first look (rough)", clock.LeftAt(t), 750);
            clock.FromPanel(11, 12, 1, t.AddSeconds(40));
            Check("panel only: first minute mark", clock.LeftAt(t.AddSeconds(40)), 719);
            Check("panel only: 30 s later", clock.LeftAt(t.AddSeconds(70)), 689);

            // The timer's countdown in chat is exact; "MAP END" is zero.
            clock = new MapClock();
            clock.FromConsole(60, t);
            clock.FromKsf(60, 200, t.AddSeconds(-90));
            clock.Countdown(120, t);
            Check("'2 minutes remaining' beats a slightly-off sample", clock.LeftAt(t.AddSeconds(10)), 110);
            clock.Countdown(0, t.AddSeconds(120));
            Check("map end", clock.LeftAt(t.AddSeconds(121)), 0);

            // No time limit: no countdown at all.
            clock = new MapClock();
            clock.FromConsole(0, t);
            clock.FromKsf(60, 500, t);
            Check("mp_timelimit 0", clock.LeftAt(t), null);

            // ksf.surf's sample is from before an extension and the console hasn't answered: the panel doesn't fit, ask.
            clock = new MapClock();
            clock.FromKsf(70, 207, t.AddSeconds(-87));
            var ask = clock.FromPanel(12, null, null, t);
            output.WriteLine($"{(ask ? "ok  " : "FAIL")}  old ksf.surf limit at odds with the panel: asks for mp_timelimit");
            if (!ask) failures++;
            clock.FromConsole(80, t.AddSeconds(1));
            Check("old ksf.surf limit, console says 80", clock.LeftAt(t.AddSeconds(1)), 207 - 88 + 600);

            // A new map: forget everything.
            clock.Reset();
            Check("new map", clock.LeftAt(t), null);

            output.WriteLine(failures == 0 ? "all good" : $"{failures} failed");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>The stage and bonus records the app would fetch in the background (off this thread, so waiting on it can't deadlock).</summary>
        static void LoadZoneRecords(KsfApi api, MapReport report, int style)
        {
            var zones = report.RecordZones;
            if (zones.Count == 0) return;
            Task.Run(() => api.FetchZoneWrsAsync(report.Info.Name, zones, report.Game, style,
                (zone, wr) => { if (wr != null) report.ZoneWrs[zone] = wr; }, CancellationToken.None)).GetAwaiter().GetResult();
        }
    }
}
