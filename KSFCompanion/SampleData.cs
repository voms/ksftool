using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using KsfCompanion.Ui;
using SkiaSharp;

namespace KsfCompanion
{
    /// <summary>
    /// Made-up data for "--preview sample": the dashboard, the nominate page and the binds page filled in without
    /// ksf.surf or a game - for screenshots and the UI self-test. None of the players, times or maps are real; the
    /// pictures are drawn here. The same KSFC_PREVIEW_* switches as the live preview pick the page and the view.
    /// </summary>
    static class SampleData
    {
        const string You = "STEAM_0:1:1000";
        static readonly string[] Players = { "nova", "drift", "kestrel", "atlas", "ember", "vortex", "lumen", "zephyr", "orbit", "quill", "sable", "tundra" };
        static readonly string[] Maps =
        {
            "surf_aurora", "surf_cascade", "surf_ember", "surf_glacier", "surf_harbor", "surf_lantern", "surf_meridian", "surf_nimbus",
            "surf_quartz", "surf_solace", "surf_tidal", "surf_umbra", "surf_vesper", "surf_willow", "surf_zenith", "surf_drifter",
            "surf_halcyon", "surf_monolith", "surf_paragon", "surf_reverie", "surf_sundial", "surf_tempest", "surf_obsidian", "surf_kaleido",
        };

        public static DashboardViewModel Dashboard()
        {
            var vm = new DashboardViewModel();
            // The buttons do nothing here, but they look as they do in the app (a button with no command looks disabled).
            var nothing = new RelayCommand(_ => { });
            vm.SaveCommand = vm.OpenMapCommand = vm.RefreshCommand = vm.OpenLaterCommand = vm.RemoveLaterCommand = vm.NominateCommand = nothing;
            vm.TeleportCommand = vm.SelectLeaderboardCommand = vm.FollowLeaderboardCommand = vm.JoinCommand = vm.OpenFolderCommand = nothing;
            vm.NoticeActionCommand = vm.TickCommand = vm.RtvCommand = vm.ToggleSavedCommand = nothing;
            var now = DateTime.Now;
            var info = new MapInfo
            {
                Name = "surf_sample", Tier = 4, IsLinear = false, StageCount = 6, BonusCount = 2, Mappers = "Someone & Someone Else",
                Rating = 4.3, RatingCount = 212, Popularity = 90, Added = now.AddDays(-400),
            };
            var top = Players.Take(10).Select((name, i) => new WorldRecord
            {
                Rank = i + 1, Name = name, SteamId = "STEAM_0:0:" + (2000 + i), Time = 214.48 + i * 0.85 + i * i * 0.07, Date = now.AddDays(-30 - i * 17),
            }).ToList();
            var report = new MapReport
            {
                Game = "css", Map = info.Name, SteamId = You, PlayerName = "you", PlayerCountry = "Germany", Info = info, Wr = top[0], Top = top, FetchedAt = now,
            };
            report.Zones.Add(new ZoneRecord { ZoneId = 0, Time = 231.917, Rank = 412, TotalRanks = 3118, Completions = 14, Attempts = 162, PlaytimeSeconds = 5 * 3600 + 12 * 60, Group = 3 });
            double[] stageRecord = { 31.2, 38.75, 29.4, 41.05, 35.6, 38.1 };
            double[] stageYou = { 32.85, 41.9, 30.02, 44.6, 37.11 };
            int[] stageRank = { 388, 701, 156, 902, 410 };
            for (var i = 0; i < stageRecord.Length; i++)
            {
                report.ZoneWrs[i + 1] = new WorldRecord { Rank = 1, Name = Players[i * 3 % Players.Length], Time = stageRecord[i] };
                if (i < stageYou.Length)
                    report.Zones.Add(new ZoneRecord { ZoneId = i + 1, Time = stageYou[i], Rank = stageRank[i], TotalRanks = 2950 - i * 140, Completions = 20 - i, Unsynced = i == 2 });
            }
            report.ZoneWrs[31] = new WorldRecord { Rank = 1, Name = "ember", Time = 11.92 };
            report.ZoneWrs[32] = new WorldRecord { Rank = 1, Name = "atlas", Time = 24.4 };
            report.Zones.Add(new ZoneRecord { ZoneId = 31, Time = 12.84, Rank = 95, TotalRanks = 1840, Completions = 3 });

            vm.ShowLoading(info.Name, live: true);
            vm.ShowReport(report, saved: false);
            // The group tile: the next group up from yours, and the time at the end of it.
            vm.ShowGroupGoal(new GroupGoal
            {
                Group = 2, Total = 3118, FirstRank = KsfGroups.FirstRank(2, 3118), LastRank = KsfGroups.LastRank(2, 3118),
                Cutoff = 228.604, YourTime = 231.917, YourRank = 412, YourGroup = 3,
            });
            vm.SetCurrentZone(4);
            vm.MapImage = Picture(1600, 900, 0);
            vm.AmbientImage = Picture(40, 22, 0);
            vm.Avatar = Picture(96, 96, 7);
            vm.SetPlayer("you", "Germany", 4936, 7884, "66T");
            vm.SetStatus("Connected to CS:S", Connection.Connected);

            var servers = Servers(now);
            var yours = servers[0];
            var clock = new MapClock();
            // 12:34 left of an 80 minute limit, extended twice by 10 minutes.
            clock.FromConsole(60, now.AddSeconds(-(80 * 60 - 754) + 30));
            for (var i = 0; i < 2; i++)
            {
                clock.Extended(10, now);
                clock.FromConsole(70 + i * 10, now);
            }
            clock.Countdown(754, now);
            vm.Clock = clock;
            vm.SetServerLine(yours);
            vm.SetLiveServer(yours, You, info.Name);
            vm.SetSession(TimeSpan.FromMinutes(71), now.AddMinutes(-12), 4, 3, 1);
            vm.SetNextMap("surf_cascade", 3);

            var later = new List<PlayLaterEntry>
            {
                new PlayLaterEntry { Map = "surf_lantern", Tier = 5, Saved = now.AddHours(-14) },
                new PlayLaterEntry { Map = "surf_harbor", Tier = 3, Saved = now.AddHours(-17) },
            };
            foreach (var row in vm.SetPlayLater(later, info.Name, servers)) row.Thumb = Picture(240, 135, row.Map.Length);
            var saved = new HashSet<string>(later.Select(e => e.Map), StringComparer.OrdinalIgnoreCase);
            vm.YourSteamId = You;
            vm.SetServers(servers, yours.Address, saved);
            // Your progress on each server's map, and another server clicked open to see who's on it.
            // (The last one's isn't in yet.)
            for (var i = 0; i < servers.Count - 1; i++)
            {
                var server = servers[i];
                var zones = new List<ZoneRecord>();
                if (i % 3 != 2) zones.Add(new ZoneRecord { ZoneId = 0, Time = 95.317 + i * 17.29 });
                for (var z = 1; z <= server.StageCount; z++)
                    if ((z + i) % 3 != 0) zones.Add(new ZoneRecord { ZoneId = z, Time = 12 + z });
                for (var b = 0; b < server.BonusCount; b++)
                    if ((b + i) % 2 == 0) zones.Add(new ZoneRecord { ZoneId = MapReport.FirstBonusZone + b, Time = 20 });
                vm.SetMapProgress(server.Game, server.Map, MapProgress.From(zones, server.IsLinear, server.StageCount, server.BonusCount));
            }
            if (Environment.GetEnvironmentVariable("KSFC_PREVIEW_OPEN") != "0") vm.ToggleServerCommand.Execute(servers[1].Address);
            vm.SetRecent(Recent(now));
            vm.SetLevels(new List<(PlayerStanding, int?)>
            {
                (new PlayerStanding { Game = "css", Tick = "66T", Title = "PROFICIENT", Rank = 4936, CountryRank = 240, Country = "Germany", Points = 7884, MapsDone = 212, FromProfile = true }, null),
                (new PlayerStanding { Game = "css100t", Tick = "100T", Title = "SEASONED", Rank = 636, CountryRank = 33, Country = "Germany", Points = 19257, MapsDone = 237, FromProfile = true }, 25503),
            }, "css");
            vm.Tick(now);

            vm.Layout.Load((Environment.GetEnvironmentVariable("KSFC_PREVIEW_HIDE") ?? "").Split(','));
            if (Environment.GetEnvironmentVariable("KSFC_PREVIEW_CELEBRATE") == "1")
            {
                vm.CelebrationTitle = "NEW PERSONAL BEST";
                vm.CelebrationDetail = "3:51.917   -1.207   +47 pts";
            }

            switch (Environment.GetEnvironmentVariable("KSFC_PREVIEW_PAGE"))
            {
                case "nominate":
                    vm.ThumbsNeeded += rows =>
                    {
                        foreach (var row in rows) vm.SetMapThumb(row, Picture(240, 135, row.Map.Length * 7 + row.Tier));
                    };
                    vm.SetMapsContext(saved, info.Name);
                    vm.SetFinishedMaps(Maps.Where((m, i) => i % 3 == 0).ToDictionary(m => m, m => new FinishedMap { Map = m, Time = 120 + m.Length * 9.37, Group = 4, Points = 61 }),
                        loading: false);
                    vm.SetMapCatalog(Catalog(now, info), loading: false);
                    vm.MapView = Environment.GetEnvironmentVariable("KSFC_PREVIEW_VIEW") == "list" ? "list" : "tiles";
                    vm.MapSearch = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SEARCH") ?? "";
                    vm.Page = "nominate";
                    break;
                case "records":
                    vm.Records.ThumbsNeeded += rows =>
                    {
                        foreach (var row in rows) vm.Records.SetThumb(row, Picture(240, 135, row.Map.Length * 7 + row.Map[6]));
                    };
                    vm.Records.RefreshCommand = nothing;
                    vm.SetMapsContext(saved, info.Name);
                    vm.Records.SetRecords(Records(now, info), "you  ·  66T", now.AddMinutes(-3), loading: false);
                    vm.Records.View = Environment.GetEnvironmentVariable("KSFC_PREVIEW_VIEW") == "list" ? "list" : "tiles";
                    vm.Records.Show = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SHOW") ?? "all";
                    vm.Records.Search = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SEARCH") ?? "";
                    vm.NominateTick = "css";
                    vm.NominateTickCommand = nothing;
                    vm.Page = "records";
                    break;
                case "binds":
                    vm.Binds.Load(BindSet.Parse("restart=r|restart_stage=t|saveloc=MOUSE4|loadloc=MOUSE5|hide=h|turn_left=q|turn_right=e|custom:sm_stage 3=KP_END"),
                        new Dictionary<string, string> { ["app_save"] = "F5", ["app_card"] = "F6", ["app_list"] = "F7" }, 230,
                        key => key == "r" ? "+reload" : key == "e" ? "+use" : null);
                    vm.Binds.SetGameBinds(new Dictionary<string, string> { ["k"] = "sm_saveloc", ["v"] = "sm_stuck", ["5"] = "sm_noclip", ["n"] = "sm_nominate" });
                    vm.Binds.Search = Environment.GetEnvironmentVariable("KSFC_PREVIEW_SEARCH") ?? "";
                    vm.Page = "binds";
                    break;
            }
            return vm;
        }

        static List<KsfServer> Servers(DateTime now)
        {
            string[] names = { "Beginner EU", "Beginner US Central", "Intermediate EU", "Public", "AU", "Euro", "Top500", "Veteran", "Expert", "EU 100T" };
            var servers = new List<KsfServer>();
            for (var i = 0; i < names.Length; i++)
            {
                var server = new KsfServer
                {
                    Game = i == names.Length - 1 ? "css100t" : "css",
                    Name = names[i],
                    Address = $"192.0.2.{10 + i}:27015",
                    Map = i == 0 ? "surf_sample" : Maps[i * 2 % Maps.Length],
                    Tier = i == 0 ? 4 : 1 + i * 3 % 7,
                    IsLinear = i % 3 == 1,
                    StageCount = i % 3 == 1 ? 3 : 6,
                    BonusCount = i % 4,
                    PlayerCount = Math.Max(0, 14 - i * 2 + i % 3),
                    TimeLeftSeconds = 300 + i * 197 % 1900,
                    TimeLimitMinutes = 60,
                    FetchedAt = now.AddSeconds(-20),
                };
                if (i == 0)
                {
                    int[] zones = { 4, 5, 2, 6, 31, 0, 3, -1, 1 };
                    for (var p = 0; p < zones.Length; p++)
                        server.Players.Add(new KsfServerPlayer
                        {
                            SteamId = p == 2 ? You : "STEAM_0:0:" + (3000 + p),
                            Name = p == 2 ? "you" : Players[(p + 3) % Players.Length],
                            Rank = 120 + p * 389,
                            Points = 40000 - p * 3100,
                            Zone = zones[p],
                            ConnectedSeconds = 600 + p * 913,
                        });
                    server.PlayerCount = server.Players.Count;
                }
                else if (i == 1)
                {
                    // A busy one: more than its list shows at first.
                    for (var p = 0; p < 17; p++)
                        server.Players.Add(new KsfServerPlayer
                        {
                            SteamId = "STEAM_0:1:" + (5000 + p),
                            Name = Players[(p + 1) % Players.Length] + (p >= Players.Length ? " " + p : ""),
                            Rank = 300 + p * 517,
                            Zone = p == 16 ? -1 : p % 4,
                            ConnectedSeconds = 300 + p * 411,
                        });
                    server.PlayerCount = server.Players.Count;
                }
                servers.Add(server);
            }
            // Your play-later map is on somewhere right now.
            servers[5].Map = "surf_lantern";
            servers[5].Tier = 5;
            // A private KSF server (ksf_servers): it was asked itself, so nobody's stage or rank, and no time left.
            var privateServer = new KsfServer
            {
                Game = "css",
                Name = "Private surf",
                Address = "192.0.2.40:27068",
                Map = "surf_harbor",
                Tier = 3,
                StageCount = 5,
                BonusCount = 1,
                PlayerCount = 5,
                FetchedAt = now.AddSeconds(-4),
                FromKsf = false,
            };
            for (var p = 0; p < privateServer.PlayerCount; p++)
                privateServer.Players.Add(new KsfServerPlayer { Name = Players[(p + 5) % Players.Length], ConnectedSeconds = 240 + p * 1290 });
            servers.Add(privateServer);
            return servers;
        }

        static IEnumerable<RecentRecord> Recent(DateTime now)
        {
            (string Type, string Map, int Zone, double Time, int Rank, string Server, double Hours)[] rows =
            {
                ("Group 3", "surf_sample", 0, 231.917, 412, "Beginner EU", 2),
                ("Map finish", "surf_glacier", 0, 289.011, 2784, "Intermediate EU", 23),
                ("Top 100", "surf_quartz", 2, 41.274, 87, "Euro", 52),
                ("Group 4", "surf_ember", 0, 179.274, 202, "Public", 98),
                ("WR", "surf_tidal", 31, 9.982, 1, "Veteran", 130),
                ("Group 6", "surf_nimbus", 0, 141.391, 3695, "Beginner EU", 160),
            };
            return rows.Select(r => new RecentRecord
            {
                Game = "css", Map = r.Map, ZoneId = r.Zone, Time = r.Time, Type = r.Type, Server = r.Server, Rank = r.Rank, Date = now.AddHours(-r.Hours),
            });
        }

        static List<MapInfo> Catalog(DateTime now, MapInfo current)
        {
            var maps = Maps.Select((name, i) => new MapInfo
            {
                Name = name,
                Tier = 1 + i * 5 % 8,
                IsLinear = i % 3 == 1,
                StageCount = i % 3 == 1 ? 0 : 3 + i % 7,
                BonusCount = i % 4,
                Mappers = Players[i % Players.Length] + (i % 2 == 0 ? ", " + Players[(i + 5) % Players.Length] : ""),
                Rating = i % 5 == 4 ? (double?)null : 3.1 + i * 0.37 % 1.8,
                RatingCount = i % 5 == 4 ? 0 : 12 + i * 31 % 300,
                Popularity = 100 - i * 3,
                Added = now.AddDays(-i * 41),
            }).ToList();
            maps.Insert(0, current);
            return maps;
        }

        /// <summary>Your records on the sample maps: some world records and top 10s, groups, maps with bonuses left, maps not done.</summary>
        static List<MapRecord> Records(DateTime now, MapInfo current)
        {
            var list = new List<MapRecord>();
            foreach (var map in Catalog(now, current))
            {
                var i = list.Count;
                var staged = !map.IsLinear;
                var record = new MapRecord
                {
                    Map = map.Name,
                    Tier = map.Tier,
                    IsLinear = map.IsLinear,
                    StageCount = staged ? map.StageCount : 4,
                    BonusCount = map.BonusCount,
                    Stages = staged ? Enumerable.Range(0, map.StageCount).Select(z => (z + i) % 4 != 3 || i % 5 == 0).ToArray() : new bool[0],
                    Bonuses = Enumerable.Range(0, map.BonusCount).Select(b => (b + i) % 3 != 2).ToArray(),
                };
                if (i % 4 != 3)
                {
                    record.Time = 41.3 + i * 13.917;
                    record.WrDiff = i % 7 == 0 ? 0 : 0.412 + i * 0.731;
                    record.Rank = i % 7 == 0 ? 1 : i % 5 == 1 ? 3 + i % 7 : (int?)null;
                    record.Group = record.Rank == null ? 1 + i % 6 : (int?)null;
                    record.Points = record.Rank == 1 ? 1580.4 : record.Rank != null ? 640 - i * 3 : 220 - i * 6.5;
                    record.Completions = 1 + i * 7 % 40;
                    record.Date = now.AddDays(-(i * 9 % 70)).AddHours(-3);
                    if (staged && i % 5 == 0) record.Stages = record.Stages.Select(_ => true).ToArray();
                }
                list.Add(record);
            }
            return list;
        }

        /// <summary>A made-up map picture: a dusky sky and two surf ramps, in a colour of its own for each seed.</summary>
        static Bitmap Picture(int width, int height, int seed)
        {
            var hue = seed * 47 % 360;
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            using (var sky = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height),
                    new[] { SKColor.FromHsl(hue, 32, 46), SKColor.FromHsl(hue, 28, 22), SKColor.FromHsl(hue, 30, 9) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp),
            })
                canvas.DrawRect(0, 0, width, height, sky);

            void Ramp(float x0, float y0, float x1, float y1, float thickness, SKColor top, SKColor bottom)
            {
                using var path = new SKPath();
                path.MoveTo(x0 * width, y0 * height);
                path.LineTo(x1 * width, y1 * height);
                path.LineTo(x1 * width, (y1 + thickness) * height);
                path.LineTo(x0 * width, (y0 + thickness) * height);
                path.Close();
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, y1 * height), new SKPoint(0, (y1 + thickness) * height), new[] { top, bottom }, SKShaderTileMode.Clamp),
                };
                canvas.DrawPath(path, paint);
            }
            Ramp(-0.05f, 0.92f, 0.62f, 0.44f, 0.16f, SKColor.FromHsl(hue, 8, 78), SKColor.FromHsl(hue, 10, 30));
            Ramp(0.48f, 0.66f, 1.05f, 0.30f, 0.12f, SKColor.FromHsl((hue + 25) % 360, 10, 64), SKColor.FromHsl(hue, 12, 24));
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new Bitmap(new MemoryStream(png.ToArray()));
        }
    }
}
