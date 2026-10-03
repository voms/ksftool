using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using KsfCompanion.Ui;

namespace KsfCompanion
{
    enum LinkState { NoGame, Waiting, Testing, Ready, Unavailable }

    /// <summary>
    /// The brain of the app: follows the game's console log, fetches KSF data when the map changes, keeps the
    /// in-game card and the dashboard up to date and, on KSF servers, asks the server to print map info and rank.
    /// </summary>
    sealed class Companion
    {
        const long MaxLogBytes = 32L * 1024 * 1024;
        const int MaxCatchUpBytes = 8 * 1024 * 1024;
        const string Tick66 = "css", Tick100 = "css100t";
        static readonly Regex HostnameLine = new Regex(@"^hostname\s*:\s*(.*)$", RegexOptions.IgnoreCase);
        // Also from "status": the server's address, which is how KSF's server list names it too.
        static readonly Regex AddressLine = new Regex(@"^udp/ip\s*:\s*(?<address>\d{1,3}(?:\.\d{1,3}){3}:\d+)", RegexOptions.IgnoreCase);
        // Joining a server, before its map even starts loading: "Connected to 137.74.205.6:27018"
        static readonly Regex ConnectedLine = new Regex(@"^Connected to (?<address>\d{1,3}(?:\.\d{1,3}){3}:\d+)", RegexOptions.Compiled);
        // KSF's timer announces runs in chat: [Surf Timer] - SomePlayer finished in 08:23:89 (WR +07:16:94)
        static readonly Regex FinishLine = new Regex(@"^\[Surf Timer\] - (?<name>.+?) finished (?<zone>.*?)in (?<time>\d+(?:[:.]\d{1,3}){1,3})", RegexOptions.Compiled);
        // Comes just before your own finish line: "... You finished the map for the first time . You have received [47] points"
        static readonly Regex PointsLine = new Regex(@"^\[Surf Timer\] - Congratulations! .*received \[(?<points>\d+)\] points", RegexOptions.Compiled);
        // "[Surf Timer] - Nextmap: surf_x" or "[SM] Map voting has finished. The next map will be surf_x. (Received ...)"
        static readonly Regex NextMapLine = new Regex(@"^\[(?:Surf Timer\] - Nextmap: |SM\] .*?The next map will be )(?<map>[\w\-]+)", RegexOptions.Compiled);
        // The timer's warnings near the end of a map: "[Surf Timer] - 2 minutes remaining", "... 30 seconds remaining"
        static readonly Regex RemainingLine = new Regex(@"^\[Surf Timer\] - (?<n>\d+) (?<unit>minute|second)s? remaining", RegexOptions.Compiled);
        // The game starting a demo: "Recording to ksfc_live.dem..."
        static readonly Regex RecordingLine = new Regex(@"^Recording to (?<file>[^\s\\/]+?\.dem)\.\.\.", RegexOptions.Compiled);
        // After a respawn: "[Surf Timer] - Your timer has been resumed at 'Stage 2 - Hyttekos'"
        static readonly Regex ResumedLine = new Regex(@"^\[Surf Timer\] - Your timer has been resumed at 'Stage (?<n>\d+)", RegexOptions.Compiled);
        // The ways KSF extends a map: "The Map has Been extended for 10 minutes" (!cvote), "Extending map by 20 mins due to
        // players vote." (votemap), and the end-of-map vote's "[SM] The current map has been extended. (Received 80% ...)",
        // which doesn't say by how much.
        static readonly Regex ExtendedLine = new Regex(
            @"^(?:The Map has Been extended for (?<n>\d+) min|Extending map by (?<n>\d+) min|\[SM\] The current map has been extended)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Off a server, older builds of the game say so for a command meant for it: Can't "status", not connected
        static readonly Regex NotConnectedLine = new Regex(@"^Can't ""[^""]+"", not connected", RegexOptions.Compiled);
        // The server dropped you (kicked, shut down, timed out): "Disconnect: Server shutting down."
        static readonly Regex DisconnectLine = new Regex(@"^Disconnect: ", RegexOptions.Compiled);
        // A demo stopped recording: our live one does when you leave the server - and when you switch servers, which
        // "Connecting to ..." follows straight away.
        static readonly Regex DemoCompletedLine = new Regex(@"^Completed demo\b", RegexOptions.Compiled);
        // KSF's servers advertise each other in chat: "[Surf Timer] - Expert - surf_boreas (7/60) IP: 167.114.158.6:27016"
        static readonly Regex KsfServerAdLine = new Regex(@"^\[Surf Timer\] - .+? - [\w\-.]+ \(\d+/\d+\) IP: (?<address>\d{1,3}(?:\.\d{1,3}){3}:\d+)", RegexOptions.Compiled);
        // Typing mp_timelimit in the console prints (only there): "mp_timelimit" = "80" ( def. "0" )
        static readonly Regex TimeLimitLine = new Regex(@"^""mp_timelimit"" = ""(?<minutes>\d+(?:\.\d+)?)""", RegexOptions.Compiled);

        readonly Settings settings;
        KeyNames keys;
        // The binds page: your keys for KSF commands, the keys KSF Companion has taken over in the game (to give back
        // what they did when they're taken off), and the turn speed.
        BindSet binds;
        HashSet<string> boundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool bindsToApply;
        int turnSpeed;
        int? turnSpeedToSave;
        readonly PlayLaterList later;
        readonly KsfApi api = new KsfApi();
        readonly ImageCache images;
        readonly DashboardViewModel vm = new DashboardViewModel();
        readonly TrayIcon tray;
        readonly NativeMenuItem statusItem, keepOnTopItem;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, MapReport> cache = new Dictionary<string, MapReport>(StringComparer.OrdinalIgnoreCase);
        readonly LogParser parser = new LogParser();

        GameConfig config;
        LogWatcher watcher;
        string setupError;
        DashboardWindow window;
        bool placementDirty;

        int gamePid;
        DateTime gameSeenAt, gameStartedAt, nextGameCheck, nextCheckpoint;
        // The game's remote console on this PC, which is how commands reach it (see GameLink).
        GameLink gameLink;
        LinkState link = LinkState.NoGame;
        LinkProblem linkProblem;
        DateTime nextLinkTest;
        int linkAttempts;
        string awaitedNonce;
        TaskCompletionSource<bool> nonceSeen;
        TaskCompletionSource<string> hostnameSeen;
        // CS:S's launch options in Steam (null: unknown), read now and then to see whether -usercon is there.
        string launchOptions;
        DateTime nextLaunchOptionsCheck;

        string currentMap;
        DateTime mapSeenAt;
        DateTime? inGameAt;
        MapReport report;
        int fetchId;
        bool fetching;
        bool announcePending, cardEchoPending, announcing;
        DateTime nextAnnounceTry, lastKsfChatAt = DateTime.MinValue;

        List<KsfServer> servers = new List<KsfServer>();
        // Servers that are KSF's though ksf.surf's list doesn't have them (private ones), with their tick rate (css /
        // css100t): from settings.ini, and added by themselves when they show KSF's servers in chat. What they last
        // answered when asked themselves (for the server list), and maps looked up for them.
        readonly Dictionary<string, string> extraKsfServers;
        readonly Dictionary<string, KsfServer> privateServers = new Dictionary<string, KsfServer>();
        readonly HashSet<string> mapsLookedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The server you're on as the game's "status" last showed it.
        StatusAnswer lastStatus;
        KsfServer yourServer;
        int? lastRank, lastPoints;
        DateTime nextServerPoll, nextRecentPoll;
        bool pollingServers, pollingRecent;
        string avatarFor;

        // Which KSF records to show (66 or 100 tick) and who "you" are in the game's chat.
        string detectedGame, manualGame, ourName, configName;
        readonly List<DateTime> refreshAt = new List<DateTime>();

        // Live bits: the server you're on, what's next, and how this session is going.
        bool onKsfServer;
        string connectedAddress, nextMapName;
        int? pointsJustEarned;
        // The stage or bonus you're on, when we last had it live (timer text or chat), and when moving on last made us
        // re-check your times on ksf.surf.
        int? currentZone;
        DateTime lastLiveZoneAt, lastZoneRefresh;
        // Stage and bonus records still coming in for this map (game|map).
        CancellationTokenSource zoneFetch;
        string zoneFetchKey;
        // The timer's on-screen text, read from a demo the game records (see LiveHud), and which map we asked it to record.
        LiveHud hud;
        string hudRequestedFor;
        bool demoBusyNoted;
        DateTime nextDemoCheck;
        TimeSpan demoRetry = TimeSpan.FromSeconds(30);
        int failedFetches;
        // ksf.surf's server list has you as a spectator (zone -1): used until the demo itself shows it.
        bool listedAsSpectating;
        // Where you stand on each tick rate (css / css100t), when to read your ksf.surf profile again, and what it takes
        // to reach a rank (for the next rank title): game|style|rank -> when looked up, points of whoever is there.
        readonly Dictionary<string, PlayerStanding> standings = new Dictionary<string, PlayerStanding>();
        DateTime nextStandingsCheck = DateTime.MinValue;
        readonly Dictionary<string, (DateTime At, int? Points)> pointsAtRank = new Dictionary<string, (DateTime, int?)>();
        bool levelLoading;
        double? tileSizeToSave;
        bool layoutToSave;
        // The sliders change on every step of a drag: what they changed is saved once they've stood still a moment.
        DateTime lastSliderMove;
        // The map's time left, and when to read mp_timelimit in the console next (it only prints there).
        readonly MapClock clock = new MapClock();
        DateTime? timeLimitCheckAt;
        DateTime lastTimeLimitCheck = DateTime.MinValue;
        // Times you set in game that ksf.surf may not have yet: game|map -> zone -> best time.
        readonly Dictionary<string, Dictionary<int, double>> localBests = new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);
        // Map finishes seen live that ksf.surf may not count yet: game|map -> (its count when we started counting, finishes since).
        readonly Dictionary<string, (int OnRecord, int Since)> liveFinishes = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        // The leaderboard on show follows where you are - the map (its stages are part of the run) or the bonus you're
        // on - unless you picked one, which lasts until you move on to another map or bonus.
        int? pinnedLeaderZone;
        int followedLeaderZone;
        // The nominate page: every KSF map (read once, kept on disk), and what's loading for it.
        readonly MapCatalog catalog;
        bool catalogLoading;
        int mapSearchId;
        readonly HashSet<string> thumbsLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The maps you've finished (per player, tick and style) that the nominate page marks, which of those lists is
        // on show, and which is being read from ksf.surf right now.
        readonly FinishedMaps finishedMaps;
        string finishedShown, finishedReading;
        // Which tick rate's finished maps the nominate page marks: picked there (and kept), or else the one you play.
        string nominateGame;
        // A read of the map list that stopped (ksf.surf busy or away) carries on from here, a little later.
        int catalogResumeAt = 1;
        DateTime nextCatalogTry, nextFinishedTry;
        readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        readonly Dictionary<string, (DateTime At, List<WorldRecord> Top)> zoneTops = new Dictionary<string, (DateTime, List<WorldRecord>)>(StringComparer.OrdinalIgnoreCase);
        string loadingZoneTop;
        // The group tile: whoever is at the end of a group on a map (by game|style|map|rank), and the leaderboard sizes
        // of maps you haven't finished (by game|style|map) - your own record has it otherwise.
        readonly Dictionary<string, (DateTime At, WorldRecord Row)> groupCutoffs = new Dictionary<string, (DateTime, WorldRecord)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (DateTime At, int Total)> leaderboardSizes = new Dictionary<string, (DateTime, int)>(StringComparer.OrdinalIgnoreCase);
        string loadingGroupCutoff;
        // KSF's own group cutoffs on a map (game|style|map), from its leaderboard page on ksf.surf (null: not there).
        readonly Dictionary<string, (DateTime At, int[] Ends)> groupEnds = new Dictionary<string, (DateTime, int[])>(StringComparer.OrdinalIgnoreCase);
        string loadingGroupEnds;
        // Your records on the servers' maps (by game|map), for the progress the server list shows.
        readonly Dictionary<string, (DateTime At, List<ZoneRecord> Zones)> serverMapRecords = new Dictionary<string, (DateTime, List<ZoneRecord>)>(StringComparer.OrdinalIgnoreCase);
        bool loadingProgress;
        // The group the tile shows (the arrows step from it).
        int shownGroupGoal = KsfGroups.Count;
        string lastLocalFinish;
        DateTime lastLocalFinishAt;
        readonly DateTime companionStartedAt = DateTime.Now;
        // The session: time on servers this run of the game (the clock waits in the menu and between servers), and
        // since when it's counting again (null: waiting).
        TimeSpan sessionPlayed;
        DateTime? sessionRunningSince;
        int sessionMaps, sessionFinishes, sessionPbs;
        // Leaving a server: a hint (our demo stopped, ksf.surf's list lost you, a long quiet) is checked by asking the game
        // for "status" (see LeaveCheck); and when the console last said anything.
        readonly LeaveCheck leaveCheck = new LeaveCheck();
        DateTime lastLineAt = DateTime.Now, lastQuietCheck = DateTime.MinValue;
        bool listedLastPoll;
        int unlistedPolls;
        // You left the server and haven't joined one since (ksf.surf's list still has you there for a minute).
        bool offServer;

        public Companion(Settings settings, bool startHidden)
        {
            this.settings = settings;
            keys = KeyNames.From(settings);
            later = new PlayLaterList(Path.Combine(Program.DataDir, "play-later.txt"));
            extraKsfServers = ParseKsfServers(settings.Get("ksf_servers"));
            images = new ImageCache(api.Http);
            if (int.TryParse(settings.Get("last_rank"), out var rank)) lastRank = rank;
            if (int.TryParse(settings.Get("last_points"), out var points)) lastPoints = points;

            vm.SaveCommand = new RelayCommand(_ => SaveCurrentMap());
            vm.OpenMapCommand = new RelayCommand(_ => OpenMapPage(currentMap));
            vm.RefreshCommand = new RelayCommand(_ => RefreshEverything());
            vm.OpenLaterCommand = new RelayCommand(p => OpenMapPage(p as string));
            vm.RemoveLaterCommand = new RelayCommand(p => { if (p is string map && later.Remove(map)) { ListChanged(); vm.Toast = "Removed " + map; } });
            vm.NominateCommand = new RelayCommand(p => Nominate(p as string));
            vm.TeleportCommand = new RelayCommand(Teleport);
            vm.RtvCommand = new RelayCommand(_ => RockTheVote());
            vm.ToggleSavedCommand = new RelayCommand(p => ToggleSaved(p as string));
            catalog = new MapCatalog(Path.Combine(Program.CacheDir, "maps.txt"));
            var pickedTick = settings.Get("nominate_tick");
            nominateGame = pickedTick == Tick66 || pickedTick == Tick100 ? pickedTick : null;
            vm.NominateTickCommand = new RelayCommand(p =>
            {
                nominateGame = p as string == Tick100 ? Tick100 : Tick66;
                settings.Set("nominate_tick", nominateGame);
                nextFinishedTry = DateTime.MinValue;
                EnsureFinishedMaps();
            });
            vm.SetMapCatalog(catalog.Maps, loading: false);
            finishedMaps = new FinishedMaps(Path.Combine(Program.CacheDir, "finished-maps.txt"));
            ShowFinishedMaps();
            vm.ThumbsNeeded += rows => _ = LoadMapThumbsAsync(rows);
            vm.MapSearchChanged += text => _ = SearchKsfAsync(text);
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(DashboardViewModel.Page) || !vm.IsNominatePage) return;
                EnsureMapCatalog();
                EnsureFinishedMaps();
            };
            vm.SelectLeaderboardCommand = new RelayCommand(SelectLeaderboard);
            vm.FollowLeaderboardCommand = new RelayCommand(_ =>
            {
                pinnedLeaderZone = null;
                UpdateLeaderboard();
            });
            vm.JoinCommand = new RelayCommand(p => Join(p as string));
            vm.StepGroupGoalCommand = new RelayCommand(StepGroupGoal);
            vm.OpenFolderCommand = new RelayCommand(_ => OpenDataFolder());
            vm.NoticeActionCommand = new RelayCommand(_ => CopyText("-usercon", "Copied  -usercon  - paste it into CS:S's launch options in Steam"));
            vm.TickCommand = new RelayCommand(p =>
            {
                manualGame = p as string == Tick100 ? Tick100 : Tick66;
                if (currentMap != null) _ = FetchAsync(currentMap);
            });
            vm.SetPlayer(settings.Get("last_name"), settings.Get("last_country"), lastRank, lastPoints, settings.Get("last_rank_tick"));
            // The parts you've hidden and the size: kept in settings.ini.
            vm.Layout.Load(settings.Get("hidden").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries), settings.GetInt("size", 80, 150) / 100.0);
            // Saved once the Size slider stands still (it changes on every step of a drag).
            vm.Layout.Changed += () =>
            {
                layoutToSave = true;
                lastSliderMove = DateTime.Now;
            };
            // The nominate page's tile size (saved a moment after the slider stops, not on every step of a drag).
            if (double.TryParse(settings.Get("tile_size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tileSize)) vm.TileSize = tileSize;
            vm.TileSizeChanged += size =>
            {
                tileSizeToSave = size;
                lastSliderMove = DateTime.Now;
            };
            binds = BindSet.Parse(settings.Get("binds"));
            turnSpeed = settings.GetInt("turn_speed", 50, 600);
            vm.Binds.Message += text => vm.Toast = text;
            vm.Binds.Changed += OnBindChanged;
            vm.Binds.TurnSpeedChanged += speed =>
            {
                turnSpeedToSave = speed;
                lastSliderMove = DateTime.Now;
            };
            vm.PropertyChanged += (s, e) =>
            {
                // What the keys do in the game may have changed since (it saves config.cfg when it closes).
                if (e.PropertyName == nameof(DashboardViewModel.Page) && vm.IsBindsPage) vm.Binds.SetGameBinds(config?.CurrentBinds());
            };
            vm.Binds.GameKeyRemoved += RemoveGameBind;

            // The tray icon (StatusNotifierItem: KDE, Xfce, Cinnamon, waybar...; GNOME with the AppIndicator extension).
            var menu = new NativeMenu();
            statusItem = new NativeMenuItem("Starting...") { IsEnabled = false };
            var announce = Check($"Run {GameConfig.ServerCommandsInChat(settings)} on map load (KSF answers in chat)", settings.GetBool("run_server_commands"),
                on => settings.Set("run_server_commands", on ? "1" : "0"));
            var autoOpen = Check("Open the dashboard when CS:S starts", settings.GetBool("dashboard_on_game_start"),
                on => settings.Set("dashboard_on_game_start", on ? "1" : "0"));
            var startAtLogin = Check(Autostart.IsManaged ? "Start when I log in (set in your Nix config)" : "Start when I log in", Autostart.IsEnabled, on =>
            {
                try { Autostart.Set(on); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Program.Trace("autostart: " + ex.Message); }
            });
            startAtLogin.IsEnabled = !Autostart.IsManaged;
            keepOnTopItem = Check("Keep the dashboard on top", settings.GetBool("window_topmost"), on =>
            {
                EnsureWindow();
                window.SetTopmost(on);
            });
            menu.Items.Add(statusItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Open dashboard", () => ShowDashboard(activate: true)));
            menu.Items.Add(Item("Open this map on ksf.surf", () => OpenMapPage(currentMap)));
            menu.Items.Add(Item("Refresh now", RefreshEverything));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(keepOnTopItem);
            menu.Items.Add(announce);
            menu.Items.Add(autoOpen);
            menu.Items.Add(startAtLogin);
            menu.Items.Add(Item("Open settings folder", OpenDataFolder));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Remove from CS:S...", Uninstall));
            menu.Items.Add(Item("Exit", ExitApp));

            tray = new TrayIcon { Icon = AppIcon.Get(), ToolTipText = Program.AppName, Menu = menu, IsVisible = true };
            tray.Clicked += (s, e) => ShowDashboard(activate: true);
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });

            parser.MapChanged += map => OnMapChanged(map, justJoined: true);
            parser.InGame += () =>
            {
                if (currentMap == null || inGameAt != null) return;
                inGameAt = DateTime.Now;
                CancelLeaveCheck();
                // Still on a KSF server: start the live timer recording now, not once "status" has answered - a stage
                // finished in the first seconds of the map would be missed otherwise.
                if (onKsfServer) EnsureLiveDemo();
                // The new map's time limit has arrived by now.
                CheckTimeLimitSoon(2);
            };
            vm.Clock = clock;
            parser.SaveRequested += SaveCurrentMap;
            parser.GameStarted += OnGameStarted;
            parser.LineParsed += line =>
            {
                lastLineAt = DateTime.Now;
                OnLine(line);
            };

            SetUpGame();
            ListChanged();
            UpdateStatus();

            timer.Tick += (s, e) => OnTick();
            timer.Start();
            if (!startHidden) ShowDashboard(activate: true);
        }

        static NativeMenuItem Item(string text, Action click)
        {
            var item = new NativeMenuItem(text);
            item.Click += (s, e) => click();
            return item;
        }

        /// <summary>A menu item with a tick that flips when clicked.</summary>
        static NativeMenuItem Check(string text, bool on, Action<bool> changed)
        {
            var item = new NativeMenuItem(text) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = on };
            item.Click += (s, e) =>
            {
                item.IsChecked = !item.IsChecked;
                changed(item.IsChecked);
            };
            return item;
        }

        /// <summary>
        /// css (66 tick) or css100t. Follows the server you're on unless you picked one on the dashboard or in settings.
        /// </summary>
        string Game => manualGame ?? settings.FixedGame ?? detectedGame ?? (settings.Get("last_game") == Tick100 ? Tick100 : Tick66);
        int KsfStyle => settings.GetInt("ksf_style", 0, 3);

        void SetDetectedGame(string game)
        {
            if (game == detectedGame) return;
            Program.Trace($"server tick detected: {game}");
            var before = Game;
            detectedGame = game;
            if (settings.Get("last_game") != game) settings.Set("last_game", game);
            if (Game != before && currentMap != null) _ = FetchAsync(currentMap);
        }

        /// <summary>
        /// Just connected to a KSF server that's in KSF's list: use its tick rate from the start. The map line comes
        /// right after and loads the right records; a map already on screen is reloaded if the tick changed.
        /// </summary>
        void ChooseTickEarly(KsfServer server)
        {
            onKsfServer = true;
            if (server.Game == detectedGame) return;
            Program.Trace($"joining {server.Name}: {server.Game}");
            detectedGame = server.Game;
            if (settings.Get("last_game") != server.Game) settings.Set("last_game", server.Game);
        }

        /// <summary>A server we haven't seen in KSF's list yet: look it up before the map finishes loading.</summary>
        async Task LookUpServerAsync(string address)
        {
            var lists = await Task.WhenAll(ServersOrEmpty(Tick66), ServersOrEmpty(Tick100));
            var all = lists[0].Concat(lists[1]).ToList();
            // (The private servers stay till the next poll asks them again.)
            if (all.Count > 0) servers = all.Concat(servers.Where(s => !s.FromKsf && all.All(k => k.Address != s.Address))).ToList();
            var server = all.FirstOrDefault(s => s.Address == address);
            if (server == null || address != connectedAddress) return;
            var before = Game;
            ChooseTickEarly(server);
            // The map may already be loading with the old tick rate's records.
            if (Game != before && currentMap != null) _ = FetchAsync(currentMap);
        }

        static bool Is100Tick(string hostname) =>
            hostname.IndexOf("100 tick", StringComparison.OrdinalIgnoreCase) >= 0 || hostname.IndexOf("100t", StringComparison.OrdinalIgnoreCase) >= 0;
        bool DashboardVisible => window != null && window.IsVisible && window.WindowState != WindowState.Minimized;

        void SetUpGame()
        {
            var dir = SteamLocator.FindCstrikeDir(settings.Get("game_dir"));
            if (dir == null) return;

            config = new GameConfig(dir);
            configName = config.PlayerName();
            var firstTime = !config.IsInstalled;
            try
            {
                config.Install(settings);
                config.RememberOriginals(settings, binds.Keys.Values);
                config.WriteBinds(BoundActions(), turnSpeed);
                boundKeys = WantedKeys();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                setupError = ex.Message;
            }
            LoadBindsPage();

            var gameRunning = GameBridge.FindGameProcessId() != 0;
            hud = new LiveHud(config.CstrikeDir);
            hud.ZoneChanged += OnHudZone;
            hud.ZoneFinished += OnHudFinished;
            hud.TimeLeftShown += (minutes, previous, changedAgo) =>
            {
                if (currentMap == null) return;
                if (clock.FromPanel(minutes, previous, changedAgo, DateTime.Now)) CheckTimeLimitSoon(0.5);
                if (previous != minutes) TraceClock($"panel {minutes} min" + (previous is int p ? $", was {p}" : ""));
                vm.Tick(DateTime.Now);
            };
            // Left over from a game that has closed since (it's only needed while playing).
            if (!gameRunning) hud.Delete();
            var log = config.LogCandidates.First();
            var start = CatchUpOnMissedSaves(log, out var lastMap);
            if (!gameRunning && start > MaxLogBytes && TryDelete(log)) start = 0;

            watcher = new LogWatcher(config.LogCandidates, new Dictionary<string, long> { [log] = start });
            watcher.LineRead += parser.Feed;
            SaveCheckpoint();

            if (firstTime && setupError == null)
            {
                Desktop.Notify("KSF Companion is set up",
                    $"In-game: {keys.Save} saves the map for later, hold {keys.Card} for the KSF card, hold {keys.List} for your list.");
            }

            if (gameRunning)
            {
                // Already on a map: get everything ready, but don't announce anything mid-map.
                var map = lastMap ?? LogWatcher.FindCurrentMap(config.LogCandidates);
                if (map != null) OnMapChanged(map, justJoined: false);
            }
            _ = FillInMissingTiersAsync();
        }

        void OnTick()
        {
            try { watcher?.Poll(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            // The live timer text (stage you're on, stage/bonus finishes) from the demo the game is recording.
            if (gamePid != 0) hud?.Poll();
            CheckLiveDemo(DateTime.Now);

            var now = DateTime.Now;
            if (now >= nextGameCheck)
            {
                nextGameCheck = now.AddSeconds(2);
                CheckGame(now);
                RefreshIfStale(now);
                UpdateStatus();
                if (placementDirty) SavePlacement();
            }
            if (now >= nextCheckpoint)
            {
                nextCheckpoint = now.AddSeconds(30);
                SaveCheckpoint();
            }
            if (bindsToApply) ApplyBinds();
            if (now - lastSliderMove > TimeSpan.FromSeconds(0.6))
            {
                if (turnSpeedToSave is int speed)
                {
                    turnSpeedToSave = null;
                    turnSpeed = speed;
                    settings.Set("turn_speed", speed.ToString(CultureInfo.InvariantCulture));
                    ApplyBinds(onlySpeed: true);
                }
                if (tileSizeToSave is double size)
                {
                    tileSizeToSave = null;
                    settings.Set("tile_size", Math.Round(size).ToString(CultureInfo.InvariantCulture));
                }
                if (layoutToSave)
                {
                    layoutToSave = false;
                    settings.Set("hidden", string.Join(", ", vm.Layout.Hidden));
                    settings.Set("size", Math.Round(vm.Layout.Scale * 100).ToString(CultureInfo.InvariantCulture));
                }
            }
            if (refreshAt.Count > 0 && refreshAt.Min() <= now)
            {
                refreshAt.RemoveAll(t => t <= now);
                ReloadFromKsf();
            }
            if (DashboardVisible)
            {
                vm.Tick(now);
                // The nominate page's lists, if a read of them stopped part way (each waits a while before trying again).
                if (vm.IsNominatePage)
                {
                    EnsureMapCatalog();
                    EnsureFinishedMaps();
                }
                // After a "too many requests" from ksf.surf the regular polls sit out for a bit.
                var ksfBusy = now < api.BusyUntil;
                if (now >= nextServerPoll && !pollingServers && !ksfBusy) _ = PollServersAsync();
                if (now >= nextRecentPoll && !pollingRecent && !ksfBusy) _ = PollRecentAsync();
                if (now >= nextStandingsCheck && !ksfBusy) _ = RefreshStandingsAsync();
            }
            // Not listening yet can be a game that's still starting: it's asked again now and then.
            if ((link == LinkState.Waiting || (link == LinkState.Unavailable && linkProblem == LinkProblem.NotListening)) && now >= nextLinkTest)
                _ = TestLinkAsync();
            // Now and then anyway, in case the map was extended in a way that said nothing we recognise.
            if (onKsfServer && currentMap != null && timeLimitCheckAt == null && now - lastTimeLimitCheck > TimeSpan.FromMinutes(5)) CheckTimeLimitSoon(0);
            if (timeLimitCheckAt is DateTime due && now >= due && link == LinkState.Ready && currentMap != null && now - lastTimeLimitCheck >= TimeSpan.FromSeconds(3))
            {
                timeLimitCheckAt = null;
                lastTimeLimitCheck = now;
                // Prints only in the console: "mp_timelimit" = "80" ( def. "0" )
                _ = PushAsync("mp_timelimit");
            }
            // Still on a server? "status" goes to it, and it answers with its address; off a server nothing does.
            var (askStatus, leftAt) = leaveCheck.Tick(now);
            if (leftAt is DateTime left) LeftServer(left);
            else if (askStatus)
            {
                if (link == LinkState.Ready && !offServer && gamePid != 0) _ = PushAsync("status");
                else leaveCheck.Cancel();
            }
            // A long quiet (no chat, no timer messages) may be the main menu without a hint: asked now and then.
            if (sessionRunningSince != null && !offServer && link == LinkState.Ready && !leaveCheck.Pending
                && now - lastLineAt > TimeSpan.FromMinutes(3) && now - lastQuietCheck > TimeSpan.FromMinutes(3))
            {
                lastQuietCheck = now;
                CheckStillOnServer(0, lastLineAt);
            }
            if ((announcePending || cardEchoPending) && !announcing && link == LinkState.Ready && now >= nextAnnounceTry)
            {
                if (now > mapSeenAt.AddMinutes(2))
                {
                    announcePending = cardEchoPending = false;
                }
                else if (now >= (inGameAt?.AddSeconds(2) ?? mapSeenAt.AddSeconds(30)))
                {
                    _ = AnnounceAsync();
                }
            }
        }

        void CheckGame(DateTime now)
        {
            if (now >= nextLaunchOptionsCheck) CheckLaunchOptions(now);
            var pid = GameBridge.FindGameProcessId();
            if (pid == 0)
            {
                if (gamePid != 0)
                {
                    gamePid = 0;
                    if (hud != null) hud.GamePid = 0;
                    gameLink?.Dispose();
                    gameLink = null;
                    link = LinkState.NoGame;
                    linkProblem = LinkProblem.None;
                    yourServer = null;
                    onKsfServer = false;
                    connectedAddress = nextMapName = hudRequestedFor = null;
                    currentZone = null;
                    vm.SetCurrentZone(null);
                    // The game let go of its demo when it closed; it was only there for the live timer text.
                    hud?.Delete();
                    vm.SetServerLine(null);
                    vm.SetLive(false);
                    vm.SetLiveServer(null, null, null);
                    vm.SetNextMap(null, null);
                    CancelLeaveCheck();
                    listedLastPoll = offServer = false;
                    // The session ends with the game.
                    PauseSession();
                    vm.EndSession(sessionPlayed);
                }
                return;
            }

            if (pid != gamePid)
            {
                gamePid = pid;
                gameSeenAt = now;
                gameStartedAt = ProcessStartTime(pid) ?? now;
                if (hud != null) hud.GamePid = pid;
                gameLink?.Dispose();
                gameLink = new GameLink(GameConfig.RconPort(settings), GameConfig.RconPassword(settings));
                linkAttempts = 0;
                linkProblem = LinkProblem.None;
                nextLinkTest = now.AddSeconds(5);
                link = LinkState.Waiting;
                CheckLaunchOptions(now);
                vm.SetLive(currentMap != null);
                StartSession(gameStartedAt);
                OpenDashboardForGame();
            }
        }

        /// <summary>
        /// Whether CS:S's launch options in Steam have -usercon, which the game needs to take KSF Companion's commands.
        /// Read again every half minute until the game is taking them (Steam saves the options a moment after you edit them).
        /// </summary>
        void CheckLaunchOptions(DateTime now)
        {
            nextLaunchOptionsCheck = now.AddSeconds(30);
            if (link == LinkState.Ready && launchOptions != null) return;
            var steamId = CurrentSteamId();
            launchOptions = steamId == null ? null : SteamLocator.LaunchOptions(steamId);
        }

        bool UserconMissing => launchOptions != null && !SteamLocator.HasLaunchOption(launchOptions, "-usercon");

        /// <summary>
        /// When CS:S starts, pop the dashboard up on the second monitor without taking focus from the game.
        /// </summary>
        void OpenDashboardForGame()
        {
            if (!settings.GetBool("dashboard_on_game_start") || DashboardVisible) return;
            EnsureWindow();
            if (window.Screens.ScreenCount >= 2 && window.IsOnSecondaryScreen) ShowDashboard(activate: false);
        }

        /// <summary>
        /// Opens the game's remote console (once per game launch, and again if it's lost) and checks that commands get
        /// through: an echo with a random tag, which comes back in the answer or shows up in the console log.
        /// </summary>
        async Task TestLinkAsync()
        {
            if (link != LinkState.Waiting && link != LinkState.Unavailable) return;
            var target = gameLink;
            if (target == null) return;
            link = LinkState.Testing;

            var problem = await target.OpenAsync();
            if (target != gameLink || link != LinkState.Testing) return;
            if (problem != LinkProblem.None)
            {
                linkProblem = problem;
                // A wrong password stays wrong until the game restarts (and asking again would get this PC banned from it);
                // nothing listening may be a game that's still starting, so that's asked again, less often after a minute.
                link = problem == LinkProblem.BadPassword || ++linkAttempts > 15 ? LinkState.Unavailable : LinkState.Waiting;
                nextLinkTest = DateTime.Now.AddSeconds(link == LinkState.Unavailable ? 30 : 4);
                if (link == LinkState.Unavailable) Program.Trace($"game link: {problem}");
                UpdateStatus();
                return;
            }

            var nonce = "#" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var seen = new TaskCompletionSource<bool>();
            awaitedNonce = nonce;
            nonceSeen = seen;
            // Turning con_logfile on here also covers a game that was started before we were installed.
            var output = await SendAsync($"con_logfile {GameConfig.LogFileName}; echo \"{GameConfig.LinkMarker} {nonce}\"");
            if (output != null) FeedGameOutput(output);
            if (output != null && !seen.Task.IsCompleted) await Task.WhenAny(seen.Task, Task.Delay(4000));
            awaitedNonce = null;
            if (target != gameLink || link != LinkState.Testing) return;
            if (output == null)
            {
                // Busy loading - try again in a moment.
                link = LinkState.Waiting;
                nextLinkTest = DateTime.Now.AddSeconds(4);
                return;
            }

            // Logged in and the command went through (its echo may only be in the reply, or only in the log).
            Program.Trace("game link: ready" + (seen.Task.IsCompleted ? "" : " (the echo didn't come back)"));
            link = LinkState.Ready;
            linkProblem = LinkProblem.None;
            linkAttempts = 0;
            UpdateStatus();
            // Makes the keys work right away, even in a game started before KSF Companion was installed.
            await PushAsync("exec ksf_companion");
            // Already on a map: "status" tells us the server (66 or 100 tick) and your in-game name.
            if (currentMap != null) await PushAsync("status");
        }

        /// <summary>
        /// What the game printed in answer to a command over its remote console: read like the console log's lines
        /// (the game may print it in the log too - each of these lines means the same seen twice).
        /// </summary>
        void FeedGameOutput(string output)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r', ' ');
                if (line.Length > 0) OnLine(line);
            }
        }

        void OnLine(string line)
        {
            if (awaitedNonce != null && line.StartsWith(GameConfig.LinkMarker, StringComparison.Ordinal) && line.EndsWith(awaitedNonce, StringComparison.Ordinal))
                nonceSeen?.TrySetResult(true);

            var host = HostnameLine.Match(line);
            if (host.Success)
            {
                var hostname = host.Groups[1].Value.Trim();
                hostnameSeen?.TrySetResult(hostname);
                // The first line of "status": its address, player count and players follow.
                lastStatus = new StatusAnswer { Name = hostname };
                onKsfServer = hostname.IndexOf("ksf", StringComparison.OrdinalIgnoreCase) >= 0 || IsKsfAddress(connectedAddress);
                if (onKsfServer)
                {
                    SetDetectedGame(Is100Tick(hostname) ? Tick100 : Tick66);
                    EnsureLiveDemo();
                }
                return;
            }

            // The game says which file it's recording to (it picks ksfc_live_2.dem etc. if the name is taken).
            var recordingTo = RecordingLine.Match(line);
            if (recordingTo.Success)
            {
                var file = recordingTo.Groups["file"].Value;
                if (hud != null && config != null && file.StartsWith(LiveHud.DemoName, StringComparison.OrdinalIgnoreCase))
                {
                    Program.Trace("live hud: reading " + file);
                    hud.Watch(Path.Combine(config.CstrikeDir, file), alreadyRunning: false);
                    demoRetry = TimeSpan.FromSeconds(30);
                }
                return;
            }

            // The game's answer to "record" while you're already recording a demo of your own.
            if (line.StartsWith("Already recording", StringComparison.Ordinal))
            {
                // Most likely our own demo of this map, from before KSF Companion restarted: follow that one.
                if (FollowOwnDemo()) return;
                if (!demoBusyNoted)
                {
                    demoBusyNoted = true;
                    vm.Toast = "You're recording a demo, so live stage times wait until it's stopped";
                }
                return;
            }

            // A new server: its address says which KSF server it is - and so 66 or 100 tick - before the map loads,
            // so the first thing shown for the new map is already the right records.
            // Switching servers: the demo that just stopped was the last server's.
            if (line.StartsWith("Connecting to ", StringComparison.Ordinal))
            {
                CancelLeaveCheck();
                return;
            }
            var connected = ConnectedLine.Match(line);
            if (connected.Success)
            {
                connectedAddress = connected.Groups["address"].Value;
                yourServer = null;
                offServer = false;
                CancelLeaveCheck();
                ResumeSession();
                // A KSF server if it's in KSF's list (or its name says so, once "status" has it) - not just because the
                // last one was.
                var known = servers.FirstOrDefault(s => s.Address == connectedAddress);
                onKsfServer = known != null || extraKsfServers.ContainsKey(connectedAddress);
                if (known != null) ChooseTickEarly(known);
                else if (extraKsfServers.TryGetValue(connectedAddress, out var privateGame))
                    ChooseTickEarly(new KsfServer { Name = connectedAddress, Address = connectedAddress, Game = privateGame });
                else _ = LookUpServerAsync(connectedAddress);
                nextServerPoll = DateTime.MinValue;
                return;
            }

            if (NotConnectedLine.IsMatch(line) || DisconnectLine.IsMatch(line))
            {
                LeftServer(DateTime.Now);
                return;
            }
            if (DemoCompletedLine.IsMatch(line))
            {
                // Left the server, or switching to another (then "Connecting to" comes next): "status" tells.
                CheckStillOnServer(3, DateTime.Now);
                return;
            }

            var extended = ExtendedLine.Match(line);
            if (extended.Success)
            {
                int? minutes = extended.Groups["n"].Success ? int.Parse(extended.Groups["n"].Value, CultureInfo.InvariantCulture) : (int?)null;
                clock.Extended(minutes, DateTime.Now);
                TraceClock($"extended by {minutes?.ToString(CultureInfo.InvariantCulture) ?? "?"} min");
                // Extending raises mp_timelimit: read it again once the new value has reached the game.
                CheckTimeLimitSoon(1.5);
                vm.Tick(DateTime.Now);
                vm.Toast = minutes is int m ? $"{currentMap ?? "The map"} was extended by {m} minutes" : $"{currentMap ?? "The map"} was extended";
                return;
            }

            var timeLimit = TimeLimitLine.Match(line);
            if (timeLimit.Success)
            {
                var minutes = double.Parse(timeLimit.Groups["minutes"].Value, CultureInfo.InvariantCulture);
                clock.FromConsole(minutes, DateTime.Now);
                TraceClock("mp_timelimit " + minutes.ToString(CultureInfo.InvariantCulture));
                vm.Tick(DateTime.Now);
                return;
            }

            var address = AddressLine.Match(line);
            if (address.Success)
            {
                var value = address.Groups["address"].Value;
                // A server's answer to "status" (the game's own, off a server, would be this PC's).
                if (!value.StartsWith("0.0.0.0", StringComparison.Ordinal) && !value.StartsWith("127.", StringComparison.Ordinal)) leaveCheck.Answered(DateTime.Now);
                if (lastStatus != null && lastStatus.Address == null) lastStatus.Address = value;
                if (value != connectedAddress)
                {
                    connectedAddress = value;
                    Program.Trace($"server address: {value}");
                    // Look the server up in KSF's list right away rather than waiting for the next poll.
                    nextServerPoll = DateTime.MinValue;
                }
                return;
            }

            if (StatusAnswer.Player(line) is { } player)
            {
                if (player.Account == SteamLocator.AccountId(CurrentSteamId()))
                {
                    ourName = player.Name;
                    Program.Trace($"in-game name: {ourName}");
                }
                lastStatus?.Add(player.Name, SteamLocator.FromAccountId(player.Account), player.Connected);
                return;
            }
            if (StatusAnswer.HumansIn(line) is int humans)
            {
                if (lastStatus != null) lastStatus.Humans = humans;
                return;
            }

            if (!line.StartsWith("[", StringComparison.Ordinal)) return;

            // KSF servers advertise in chat every few minutes; a fallback if "status" gets no answer.
            if (line.StartsWith("[KSF Clan]", StringComparison.Ordinal)) lastKsfChatAt = DateTime.Now;

            // A server that advertises KSF's servers is one of them, even if ksf.surf doesn't list it (a private one).
            if (AdvertisedServer(line) is string advertised)
            {
                lastKsfChatAt = DateTime.Now;
                if (connectedAddress != null && !IsKsfAddress(connectedAddress) && servers.Any(s => s.Address == advertised)) CountAsKsf(connectedAddress);
                return;
            }

            var next = NextMapLine.Match(line);
            if (next.Success)
            {
                _ = ShowNextMapAsync(next.Groups["map"].Value.ToLowerInvariant());
                return;
            }

            var resumed = ResumedLine.Match(line);
            if (resumed.Success)
            {
                SetCurrentZone(int.Parse(resumed.Groups["n"].Value, CultureInfo.InvariantCulture), ZoneSource.Live);
                return;
            }

            // The timer's own countdown is exact.
            var remaining = RemainingLine.Match(line);
            if (remaining.Success)
            {
                var n = int.Parse(remaining.Groups["n"].Value, CultureInfo.InvariantCulture);
                var seconds = remaining.Groups["unit"].Value == "minute" ? n * 60 : n;
                clock.Countdown(seconds, DateTime.Now);
                TraceClock($"timer says {seconds}s");
                vm.Tick(DateTime.Now);
                return;
            }
            if (line.StartsWith("[Surf Timer] - ---- MAP END", StringComparison.Ordinal))
            {
                clock.Countdown(0, DateTime.Now);
                vm.Tick(DateTime.Now);
                return;
            }

            var zoneFinish = ChatZoneFinishLine.Match(line);
            if (zoneFinish.Success)
            {
                OnOwnFinish(zoneFinish.Groups["zone"].Value, zoneFinish.Groups["time"].Value);
                return;
            }

            var finish = FinishLine.Match(line);
            if (finish.Success && IsMe(finish.Groups["name"].Value))
            {
                OnOwnFinish(finish.Groups["zone"].Value.Trim(), finish.Groups["time"].Value);
            }
            else if (line.StartsWith("[Surf Timer] - Congratulations! You finished", StringComparison.Ordinal))
            {
                var points = PointsLine.Match(line);
                pointsJustEarned = points.Success ? int.Parse(points.Groups["points"].Value, CultureInfo.InvariantCulture) : (int?)null;
                OnOwnFinish(null, null);
            }
        }

        bool IsMe(string name) => name == "You" ||
            new[] { ourName, configName, report?.PlayerName, settings.Get("last_name") }
                .Any(n => !string.IsNullOrEmpty(n) && string.Equals(n, name, StringComparison.Ordinal));

        // "bonus [Bonus 4 - Watti] " in "... finished bonus [Bonus 4 - Watti] in 00:18:26" (the same for stages, when the
        // timer's chat messages print them)
        static readonly Regex ZoneText = new Regex(@"\b(?<kind>Stage|Bonus) (?<n>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The timer's own-finish chat line in the style of its on-screen text: "[Surf Timer] - Finished [Stage 3]: 00:17:06 ..."
        static readonly Regex ChatZoneFinishLine = new Regex(
            @"^\[Surf Timer\] - (?:You )?[Ff]inished (?<zone>\[?(?:Stage|Bonus) \d+[^\]:]*\]?)\s*:?\s*(?:in )?(?<time>\d+(?::\d{2}){1,2}(?:[:.]\d{1,3})?)",
            RegexOptions.Compiled);

        /// <summary>
        /// You just finished (the timer said so in chat). Show the time right away, then get the new rank,
        /// group and leaderboard from KSF once it has the run.
        /// </summary>
        void OnOwnFinish(string zone, string timeText)
        {
            if (currentMap == null) return;
            Program.Trace($"own finish on {currentMap}: zone='{zone}' time='{timeText}'");
            // A stage or bonus finish in chat: that's instant (the on-screen text only reaches us with the demo's next write).
            var zoneRef = zone == null ? null : ZoneText.Match(zone);
            if (timeText != null && zoneRef?.Success == true && TryParseTimerTime(timeText, out var zoneTime))
            {
                var n = int.Parse(zoneRef.Groups["n"].Value, CultureInfo.InvariantCulture);
                var bonus = zoneRef.Groups["kind"].Value.StartsWith("b", StringComparison.OrdinalIgnoreCase);
                var id = bonus ? MapReport.FirstBonusZone - 1 + n : n;
                RecordLocalBest(id, zoneTime, announce: true);
                // Finishing a stage puts you at the start of the next one.
                if (!bonus && report?.IsStaged == true && n < report.Info.StageCount) SetCurrentZone(n + 1, ZoneSource.Live);
                return;
            }

            var mainMap = string.IsNullOrEmpty(zone) || zone.Equals("the map", StringComparison.OrdinalIgnoreCase);
            if (timeText != null && mainMap && TryParseTimerTime(timeText, out var time))
            {
                sessionFinishes++;
                var points = pointsJustEarned;
                pointsJustEarned = null;
                if (report != null && report.Game == Game)
                {
                    var previous = report.Main?.Time;
                    // Chat only shows hundredths, so compare at that precision.
                    var improved = previous == null || time < Math.Floor(previous.Value * 100) / 100 - 0.0001;
                    vm.ShowFreshFinish(time, report.Wr, improved);
                    Program.Trace($"showing finish right away: {Format.Time(time)} (improved={improved})");
                    if (improved)
                    {
                        sessionPbs++;
                        var beatWr = report.Wr != null && time < report.Wr.Time;
                        var detail = previous == null ? Format.Time(time) : $"{Format.Time(time)}   {Format.Gap(time, previous.Value)}";
                        if (points > 0) detail += $"   +{points} pts";
                        var title = beatWr ? "WORLD RECORD" : previous == null ? "FIRST FINISH" : "NEW PERSONAL BEST";
                        Program.Trace($"celebrate: {title} / {detail}");
                        vm.Celebrate(title, detail);
                    }
                    else
                    {
                        vm.Toast = $"Finished in {Format.Time(time)}  -  your PB is {Format.Time(previous.Value)}";
                    }
                }
                else
                {
                    vm.Toast = $"Finished {currentMap} in {Format.Time(time)}";
                }
                // The finish counts right away; ksf.surf's own count takes over once it has caught up.
                var key = CacheKey(Game, currentMap);
                liveFinishes[key] = liveFinishes.TryGetValue(key, out var counted) ? (counted.OnRecord, counted.Since + 1)
                    : (report?.Game == Game ? report.Main?.Completions ?? 0 : 0, 1);
                // After the PB check above: this makes it the time on record until ksf.surf has it.
                RecordLocalBest(0, time, announce: true);
                if (report != null && ApplyLocalBests(report)) WriteCard();
                MarkFinished(currentMap, time, Game);
                UpdateSession();
                // New points (and maybe a new title): look at your profile again once ksf.surf has the run.
                if (nextStandingsCheck > DateTime.Now.AddSeconds(90)) nextStandingsCheck = DateTime.Now.AddSeconds(90);
            }
            refreshAt.Add(DateTime.Now.AddSeconds(1.5));
            refreshAt.Add(DateTime.Now.AddSeconds(12));
        }

        /// <summary>KSF announces the next map in chat near the end of the current one.</summary>
        async Task ShowNextMapAsync(string map)
        {
            if (map == nextMapName) return;
            nextMapName = map;
            vm.SetNextMap(map, null);
            var tier = await api.GetTierAsync(map);
            Program.Trace($"next map: {map} (tier {tier?.ToString(CultureInfo.InvariantCulture) ?? "?"})");
            if (nextMapName == map) vm.SetNextMap(map, tier);
            await PrefetchMapAsync(map);
        }

        /// <summary>
        /// The next map, as soon as KSF names it: its stage and bonus records are fetched in the background (and kept
        /// for a day), so your times on it - with the gaps to the records - are there the moment it starts.
        /// </summary>
        async Task PrefetchMapAsync(string map)
        {
            var game = Game;
            var info = catalog.Find(map);
            // Its picture too, so the map's picture is there the moment it starts.
            _ = images.MapAsync(map, 40);
            try
            {
                if (info == null)
                {
                    var next = await api.GetReportAsync(map, CurrentSteamId(), game, KsfStyle);
                    info = next.Info;
                    if (next.Error == null && next.PersonalError == null) cache[CacheKey(game, map)] = next;
                }
                var zones = MapReport.ZonesOf(info);
                if (info == null || zones.Count == 0 || api.FreshZoneCount(info.Name, zones, game, KsfStyle) == zones.Count) return;
                Program.Trace($"getting {info.Name}'s stage/bonus records ahead of time ({zones.Count})");
                await api.FetchZoneWrsAsync(info.Name, zones, game, KsfStyle, (zone, wr) => { }, shutdown.Token, background: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace($"next map records: {ex.GetBaseException().Message}");
            }
        }

        enum ZoneSource { Live, ServerList }

        /// <summary>
        /// The stage or bonus you're on. The timer's own text and chat are live; ksf.surf's server list is a minute or
        /// so behind, so it only counts when nothing live has been heard for a while - and then moving on to the next
        /// stage is the hint that one was finished, so your times get checked on ksf.surf again.
        /// </summary>
        void SetCurrentZone(int? zone, ZoneSource source)
        {
            if (source == ZoneSource.Live) lastLiveZoneAt = DateTime.Now;
            // The server list is a minute or so behind: it must never override what the timer's text shows now -
            // including when you've simply stayed on the same stage/bonus for a while.
            else if (DateTime.Now - lastLiveZoneAt < TimeSpan.FromSeconds(90)
                     || (hud != null && DateTime.Now - hud.LastZoneSeenAt < TimeSpan.FromSeconds(60))
                     || hud?.Spectating == false && hud.FindRecordingDemo() != null) return;
            if (zone == currentZone) return;
            var previous = currentZone;
            currentZone = zone;
            vm.SetCurrentZone(zone);
            UpdateLeaderboard();
            Program.Trace($"on: {(previous is int p ? MapReport.ZoneName(p) : "-")} -> {(zone is int z ? MapReport.ZoneName(z) : "-")} ({source})");
            if (source == ZoneSource.ServerList && previous >= 1 && zone > previous && report?.IsStaged == true && DateTime.Now - lastZoneRefresh > TimeSpan.FromSeconds(20))
            {
                lastZoneRefresh = DateTime.Now;
                refreshAt.Add(DateTime.Now.AddSeconds(1));
            }
        }

        /// <summary>The timer's text says you finished a stage or bonus (read live from the demo).</summary>
        void OnHudFinished(int zone, double time, bool justNow)
        {
            if (MaybeSpectating) return;
            Program.Trace($"hud: finished {MapReport.ZoneName(zone)} in {Format.Short(time)}{(justNow ? "" : " (from before)")}");
            lastLiveZoneAt = DateTime.Now;
            RecordLocalBest(zone, time, announce: justNow);
        }

        void OnHudZone(int zone)
        {
            if (!MaybeSpectating) SetCurrentZone(zone, ZoneSource.Live);
        }

        /// <summary>The demo hasn't shown yet whether you're spectating, but ksf.surf's list says you are.</summary>
        bool MaybeSpectating => hud?.Spectating == null && listedAsSpectating;

        /// <summary>Puts the right leaderboard on show: your pick, or else where you are (a bonus, or the map for everything else).</summary>
        void UpdateLeaderboard()
        {
            var follow = currentZone is int z && MapReport.IsBonus(z) ? z : 0;
            if (follow != followedLeaderZone)
            {
                // You moved on to another bonus or back to the map: follow again.
                followedLeaderZone = follow;
                pinnedLeaderZone = null;
            }
            if (report == null || !report.IsOnKsf) return;
            var zone = pinnedLeaderZone ?? followedLeaderZone;
            if (zone != 0 && !report.RecordZones.Contains(zone)) zone = 0;
            vm.SetLeaderZone(zone, pinnedLeaderZone != null);
            if (zone != 0) _ = LoadZoneTopAsync(report, zone);
        }

        /// <summary>Clicking a leaderboard chip or a row in "your times": show that leaderboard (the one you're on = follow again).</summary>
        void SelectLeaderboard(object parameter)
        {
            if (!(parameter is int zone)) return;
            pinnedLeaderZone = zone == followedLeaderZone ? (int?)null : zone;
            UpdateLeaderboard();
        }

        /// <summary>A stage's or bonus's top 10, fetched when it's first shown and kept a few minutes.</summary>
        async Task LoadZoneTopAsync(MapReport r, int zone)
        {
            var key = $"{r.Game}|{r.Map}|{zone}";
            if (zoneTops.TryGetValue(key, out var known))
            {
                vm.SetZoneTop(zone, known.Top);
                if (DateTime.Now - known.At < TimeSpan.FromMinutes(3)) return;
            }
            if (loadingZoneTop == key || DateTime.Now < api.BusyUntil) return;
            loadingZoneTop = key;
            try
            {
                var top = await api.GetZoneTopAsync(r.Info.Name, zone, r.Game, KsfStyle);
                zoneTops[key] = (DateTime.Now, top);
                if (report == null || report.Game != r.Game || report.Map != r.Map) return;
                // Its first row is the record: keep the gaps in "your times" up to date too.
                if (top.Count > 0)
                {
                    report.ZoneWrs[zone] = top[0];
                    vm.ShowTimes(report);
                }
                vm.SetZoneTop(zone, top);
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace($"top of {MapReport.ZoneName(zone)}: {ex.GetBaseException().Message}");
            }
            finally
            {
                if (loadingZoneTop == key) loadingZoneTop = null;
            }
        }

        /// <summary>
        /// The nominate page needs every KSF map: read ksf.surf's whole list once (about 90 small requests, paced),
        /// then only top it up with the newest maps now and then. Shown as it comes in.
        /// </summary>
        async void EnsureMapCatalog()
        {
            if (catalogLoading || DateTime.Now < nextCatalogTry) return;
            var full = catalog.Count == 0 || DateTime.Now - catalog.CompleteAt > TimeSpan.FromDays(7);
            if (!full && DateTime.Now - catalog.ToppedUpAt < TimeSpan.FromHours(12)) return;
            catalogLoading = true;
            vm.SetMapCatalog(catalog.Maps, loading: true);
            var finished = false;
            // A full read that stopped part way carries on where it stopped.
            var start = full ? catalogResumeAt : 1;
            try
            {
                for (var pages = 0; start < 3000; start += 10, pages++)
                {
                    var page = await api.GetMapsPageAsync(start, shutdown.Token);
                    if (page.Count == 0) break;
                    var anythingNew = page.Any(m => !catalog.Has(m.Name));
                    catalog.Add(page);
                    // Topping up: the list is newest first, so a page of known maps means we're done.
                    if (!full && !anythingNew) break;
                    if (pages % 5 == 4) vm.SetMapCatalog(catalog.Maps, loading: true);
                    if (pages % 20 == 19) catalog.Save();
                }
                finished = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace("map list: " + ex.GetBaseException().Message);
            }
            finally
            {
                if (finished)
                {
                    if (full) catalog.MarkComplete();
                    else catalog.MarkToppedUp();
                    catalogResumeAt = 1;
                }
                else
                {
                    // ksf.surf was busy or away: the rest comes a little later (while the nominate page is open).
                    if (full) catalogResumeAt = start;
                    nextCatalogTry = DateTime.Now.AddSeconds(45);
                }
                catalog.Save();
                catalogLoading = false;
                vm.SetMapCatalog(catalog.Maps, loading: false);
                Program.Trace($"map list: {catalog.Count} maps (finished: {finished})");
            }
        }

        /// <summary>The tick rate the nominate page marks finished maps for.</summary>
        string NominateGame => nominateGame ?? Game;
        string FinishedKey() => FinishedMaps.Key(CurrentSteamId(), NominateGame, KsfStyle);

        /// <summary>The nominate page marks the maps you've finished on its tick rate.</summary>
        void ShowFinishedMaps()
        {
            var key = FinishedKey();
            finishedShown = key;
            vm.NominateTick = NominateGame;
            vm.SetFinishedMaps(finishedMaps.Of(key), loading: key != null && key == finishedReading);
        }

        /// <summary>
        /// Which maps you've finished, for the nominate page: the "best records" on your ksf.surf profile read to the
        /// end - 5 maps a request, paced, so about 40 requests for 200 maps - at most about once a day. Maps you finish
        /// in between are added as they happen (MarkFinished).
        /// </summary>
        async void EnsureFinishedMaps()
        {
            var steamId = CurrentSteamId();
            var game = NominateGame;
            var style = KsfStyle;
            var key = FinishedMaps.Key(steamId, game, style);
            if (key == null || finishedReading != null || DateTime.Now < nextFinishedTry || DateTime.Now - finishedMaps.ReadAt(key) < TimeSpan.FromHours(20))
            {
                if (key != finishedShown) ShowFinishedMaps();
                return;
            }
            finishedReading = key;
            ShowFinishedMaps();
            int read = 0;
            var complete = false;
            try
            {
                await api.GetFinishedMapsAsync(steamId, game, style, page =>
                {
                    finishedMaps.Merge(key, page);
                    read += page.Count;
                    // Shown as it comes in.
                    if (page.Count > 0 && read % 25 == 0 && key == finishedShown) vm.SetFinishedMaps(finishedMaps.Of(key), loading: true);
                }, shutdown.Token);
                complete = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace("finished maps: " + ex.GetBaseException().Message);
            }
            finally
            {
                if (complete) finishedMaps.MarkRead(key);
                // Stopped part way (ksf.surf busy): read it again in a while.
                else nextFinishedTry = DateTime.Now.AddMinutes(1);
                finishedMaps.Save();
                finishedReading = null;
                ShowFinishedMaps();
                Program.Trace($"finished maps ({game}): {read} read, complete: {complete}");
            }
            // You moved to the other tick rate while this was reading: that one's list next.
            if (complete && vm.IsNominatePage && FinishedKey() != key) EnsureFinishedMaps();
        }

        /// <summary>A map finish of yours (seen in game, or your time on the dashboard): marked done straight away.</summary>
        void MarkFinished(string map, double time, string game)
        {
            var key = FinishedMaps.Key(CurrentSteamId(), game, KsfStyle);
            if (!finishedMaps.Add(key, map, time)) return;
            finishedMaps.Save();
            if (key == FinishedKey()) ShowFinishedMaps();
        }

        /// <summary>While the full map list is still coming in, ksf.surf's own search fills in what you look for (up to 5 maps).</summary>
        async Task SearchKsfAsync(string text)
        {
            if (catalog.CompleteAt != DateTime.MinValue || text.Length < 2) return;
            var id = ++mapSearchId;
            await Task.Delay(400);
            if (id != mapSearchId) return;
            try
            {
                var found = await api.SearchMapsAsync(text);
                if (found.Count == 0 || id != mapSearchId) return;
                catalog.Add(found);
                vm.SetMapCatalog(catalog.Maps, loading: catalogLoading);
            }
            catch (Exception ex) when (IsNetworkError(ex)) { }
        }

        /// <summary>Pictures for the maps on show (downloaded once, then read from disk).</summary>
        async Task LoadMapThumbsAsync(List<MapResultRow> rows)
        {
            await Task.WhenAll(rows.Where(row => thumbsLoading.Add(row.Map)).Select(async row =>
            {
                try { vm.SetMapThumb(row, await images.MapAsync(row.Map, 240)); }
                finally { thumbsLoading.Remove(row.Map); }
            }));
        }

        // ----- binds page -----

        void LoadBindsPage()
        {
            var appKeys = new Dictionary<string, string> { ["app_save"] = keys.Save, ["app_card"] = keys.Card, ["app_list"] = keys.List };
            vm.Binds.Load(binds, appKeys, turnSpeed, key => config?.OriginalBind(settings, key));
            vm.Binds.SetGameBinds(config?.CurrentBinds());
        }

        /// <summary>One of your own binds taken off on the binds page: the key does nothing now.</summary>
        async void RemoveGameBind(string key)
        {
            if (config == null) return;
            string command;
            try { command = config.RemoveBind(key); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                vm.Toast = "Couldn't change CS:S's config.cfg: " + ex.Message;
                return;
            }
            Program.Trace("binds: took your bind off " + key);
            if (link == LinkState.Ready) await PushAsync(command);
        }

        /// <summary>The binds page's keys with what they do (KSF Companion's own keys are in ksf_companion.cfg instead).</summary>
        List<(BindAction Action, string Key)> BoundActions() => binds.Keys
            .Select(b => (Action: b.Key.StartsWith(BindCatalog.CustomPrefix, StringComparison.Ordinal)
                ? BindCatalog.OwnAction(b.Key.Substring(BindCatalog.CustomPrefix.Length)) : BindCatalog.Find(b.Key), Key: b.Value))
            .Where(b => b.Action != null)
            .ToList();

        HashSet<string> WantedKeys() =>
            new HashSet<string>(binds.Keys.Values.Concat(new[] { keys.Save, keys.Card, keys.List }), StringComparer.OrdinalIgnoreCase);

        /// <summary>A key set, moved or taken off on the binds page: noted now, put in the game on the next tick (a move is two changes).</summary>
        void OnBindChanged(BindChange change)
        {
            if (change.Action.AppSetting != null)
            {
                if (change.NewKey == null) return;
                settings.Set(change.Action.AppSetting, change.NewKey);
                keys = KeyNames.From(settings);
                window?.SetKeys(keys);
            }
            else if (change.NewKey == null) binds.Keys.Remove(change.Action.Id);
            else binds.Keys[change.Action.Id] = change.NewKey;
            settings.Set("binds", binds.ToString());
            bindsToApply = true;
        }

        /// <summary>
        /// Writes the binds into the game's cfg and, while it runs, loads them there. Keys that aren't ours any more
        /// get back what they did before. All of it from the console - nothing is typed in chat.
        /// </summary>
        async void ApplyBinds(bool onlySpeed = false)
        {
            bindsToApply = false;
            if (config == null) return;
            var commands = new List<string>();
            try
            {
                if (!onlySpeed)
                {
                    var wanted = WantedKeys();
                    foreach (var key in boundKeys.Where(k => !wanted.Contains(k)).ToList()) commands.Add(config.GiveBack(settings, key));
                    config.RememberOriginals(settings, wanted);
                    // Rewrites ksf_companion.cfg too (KSF Companion's own keys), which runs ksf_binds.
                    config.Install(settings);
                    boundKeys = wanted;
                }
                config.WriteBinds(BoundActions(), turnSpeed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                vm.Toast = "Couldn't write the binds into CS:S's cfg folder: " + ex.Message;
                return;
            }
            Program.Trace($"binds: {binds} (turn speed {turnSpeed})" + (commands.Count > 0 ? " - given back: " + string.Join("; ", commands) : ""));
            vm.Binds.RefreshReplaces();
            WriteCard();
            if (link != LinkState.Ready) return;
            foreach (var command in commands) await PushAsync(command);
            if (!onlySpeed) await PushAsync("exec ksf_companion");
            else if (BoundActions().Any(b => b.Action.IsTurn)) await PushAsync("cl_yawspeed " + turnSpeed.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Rock the vote on the KSF server you're on - sent from the console (sm_rtv), not typed in chat.</summary>
        async void RockTheVote()
        {
            if (link != LinkState.Ready || !(onKsfServer || yourServer != null))
            {
                vm.Toast = "Join a KSF server to rock the vote";
                return;
            }
            if (await PushAsync("sm_rtv")) vm.Toast = "Rock the vote sent - the server shows the votes in chat";
        }

        void ToggleSaved(string map)
        {
            if (string.IsNullOrEmpty(map)) return;
            if (later.Contains(map))
            {
                later.Remove(map);
                vm.Toast = "Took " + map + " off your play-later list";
            }
            else
            {
                later.Add(map, catalog.Find(map)?.Tier);
                vm.Toast = "Saved " + map + " for later";
            }
            ListChanged();
        }

        /// <summary>Clicking a row's arrow: teleport to that stage or bonus (KSF's !stage / !bonus), or back to the start for the map.</summary>
        async void Teleport(object parameter)
        {
            if (!(parameter is int zone)) return;
            if (link != LinkState.Ready || !(onKsfServer || yourServer != null) || currentMap == null)
            {
                vm.Toast = "Join a KSF server to teleport";
                return;
            }
            var command = zone == 0 ? "sm_restart"
                : MapReport.IsBonus(zone) ? "sm_bonus " + (zone - MapReport.FirstBonusZone + 1).ToString(CultureInfo.InvariantCulture)
                : "sm_stage " + zone.ToString(CultureInfo.InvariantCulture);
            if (await PushAsync(command))
                vm.Toast = zone == 0 ? "Back to the start" : "Teleporting to " + MapReport.ZoneName(zone);
        }

        /// <summary>
        /// A time you just set in game on the map, a stage or a bonus. It shows right away - as your new best if it
        /// beats the one on record - long before ksf.surf has it (and your new rank).
        /// </summary>
        void RecordLocalBest(int zone, double time, bool announce)
        {
            if (currentMap == null) return;
            // The timer text and chat can both report the same finish.
            var finishKey = $"{currentMap}|{zone}|{time:0.00}";
            if (finishKey == lastLocalFinish && DateTime.Now - lastLocalFinishAt < TimeSpan.FromMinutes(1)) return;
            lastLocalFinish = finishKey;
            lastLocalFinishAt = DateTime.Now;

            var key = CacheKey(Game, currentMap);
            if (!localBests.TryGetValue(key, out var bests)) localBests[key] = bests = new Dictionary<int, double>();
            var onRecord = report != null && report.Game == Game ? report.Zone(zone)?.Time : null;
            var best = bests.TryGetValue(zone, out var mine) && (onRecord == null || mine < onRecord) ? mine : onRecord;
            // The timer shows hundredths, so compare at that precision.
            var improved = best == null || time < Math.Floor(best.Value * 100) / 100 - 0.0001;
            if (improved)
            {
                bests[zone] = time;
                if (report != null && ApplyLocalBests(report)) WriteCard();
            }
            if (!announce) return;

            vm.FlashZones(new[] { zone });
            if (zone == 0) return; // the map itself gets the big PB banner instead
            var name = MapReport.ZoneName(zone);
            vm.Toast = best == null ? $"{name} done  {Format.Short(time)}"
                : improved ? $"{name} new best  {Format.Short(time)}  ({Format.Gap(time, best.Value)})"
                : $"{name}  {Format.Short(time)}   best {Format.Short(best.Value)}  ({Format.Gap(time, best.Value)})";
        }

        /// <summary>Puts the times (and finishes) you set in game into a report from ksf.surf that doesn't have them yet. True if it changed.</summary>
        bool ApplyLocalBests(MapReport r)
        {
            var changed = false;
            var key = CacheKey(r.Game, r.Map);
            if (liveFinishes.TryGetValue(key, out var counted))
            {
                var main = r.Zone(0);
                if (main == null) r.Zones.Add(main = new ZoneRecord { ZoneId = 0 });
                var shown = counted.OnRecord + counted.Since;
                // ksf.surf's count has caught up: from now on it's the one to show.
                if ((main.Completions ?? 0) >= shown) liveFinishes.Remove(key);
                else if (main.Completions != shown)
                {
                    main.Completions = shown;
                    changed = true;
                }
            }
            if (!localBests.TryGetValue(key, out var bests)) return changed;
            foreach (var pair in bests)
            {
                var zone = r.Zone(pair.Key);
                if (zone == null) r.Zones.Add(zone = new ZoneRecord { ZoneId = pair.Key });
                // ksf.surf has it now (its time has more decimals than the timer's hundredths).
                if (zone.Time is double onRecord && onRecord <= pair.Value + 0.005) continue;
                if (zone.Unsynced && zone.Time == pair.Value) continue;
                zone.Time = pair.Value;
                zone.Rank = null;
                zone.Unsynced = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Has the game record a demo of this map with its own "record" command, so the timer's on-screen text
        /// (the stage you're on, stage and bonus finishes) can be read live. Once per map, only on KSF, and it
        /// never touches a demo you're recording yourself.
        /// </summary>
        async void EnsureLiveDemo()
        {
            if (hud == null || !settings.GetBool("live_hud") || currentMap == null || hudRequestedFor == currentMap || link != LinkState.Ready) return;
            var map = hudRequestedFor = currentMap;

            var recording = hud.FindRecordingDemo();
            if (recording != null)
            {
                // Already recording (e.g. KSF Companion was restarted): keep reading it if it's this map's. A header
                // that isn't on disk yet means the recording has only just started - also this map's.
                hud.Watch(recording, alreadyRunning: true);
                var of = LiveHud.MapOf(recording);
                if (of == null || string.Equals(of, map, StringComparison.OrdinalIgnoreCase)) return;
                // Still the last map's recording: finish it before starting this map's.
                Program.Trace($"live hud: stop ({of})");
                await PushAsync("stop");
                await Task.Delay(700);
                if (map != currentMap) return;
            }
            // The game won't overwrite a demo (it would pick ksfc_live_2.dem), so clear out old ones first.
            hud.DeleteFinishedDemos();
            Program.Trace("live hud: record " + LiveHud.DemoName);
            await PushAsync("record " + LiveHud.DemoName);
        }

        /// <summary>Our demo of the map you're on, if the game is still writing one: read that. True if there is one.</summary>
        bool FollowOwnDemo()
        {
            var recording = hud?.FindRecordingDemo();
            if (recording == null) return false;
            var of = LiveHud.MapOf(recording);
            if (of != null && !string.Equals(of, currentMap, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(recording, hud.DemoPath, StringComparison.OrdinalIgnoreCase))
            {
                Program.Trace("live hud: following " + Path.GetFileName(recording));
                hud.Watch(recording, alreadyRunning: true);
            }
            return true;
        }

        /// <summary>
        /// Keeps the live timer text coming: if nothing new has come from the demo for a while on a KSF map, find the
        /// one the game is writing (it may have a new name) - or have it record one again if it isn't recording.
        /// </summary>
        void CheckLiveDemo(DateTime now)
        {
            if (hud == null || gamePid == 0 || !onKsfServer || currentMap == null || !settings.GetBool("live_hud") || link != LinkState.Ready) return;
            if (now - hud.LastDataAt < TimeSpan.FromSeconds(40) || now - mapSeenAt < TimeSpan.FromSeconds(40) || now < nextDemoCheck) return;
            if (FollowOwnDemo())
            {
                nextDemoCheck = now.AddSeconds(30);
                return;
            }
            // Asking again when that doesn't start one (your own demo is recording, you're in the menu) waits longer
            // each time, so the console gets at most a "record" every few minutes.
            nextDemoCheck = now + demoRetry;
            demoRetry = TimeSpan.FromSeconds(Math.Min(demoRetry.TotalSeconds * 2, 300));
            Program.Trace("live hud: nothing recording - asking again");
            hudRequestedFor = null;
            EnsureLiveDemo();
        }

        void UpdateSession()
        {
            if (gamePid == 0) return;
            Program.Trace($"session: {sessionMaps} maps, {sessionFinishes} finishes, {sessionPbs} PBs, "
                          + Format.Duration(SessionPlayed(DateTime.Now).TotalSeconds) + " on servers" + (sessionRunningSince == null ? " (paused)" : ""));
            vm.SetSession(sessionPlayed, sessionRunningSince, sessionMaps, sessionFinishes, sessionPbs);
        }

        TimeSpan SessionPlayed(DateTime now) => sessionPlayed + (sessionRunningSince is DateTime since && now > since ? now - since : TimeSpan.Zero);

        /// <summary>A session is one run of the game; its clock runs while you're on a server and waits in between.</summary>
        void StartSession(DateTime start)
        {
            // Started after the game and it's already on a map: that one counts, from when the game started. A fresh
            // game is in its menu: the clock waits for a server.
            var onMap = currentMap != null && start < companionStartedAt;
            sessionPlayed = TimeSpan.Zero;
            sessionRunningSince = onMap ? start : (DateTime?)null;
            sessionMaps = onMap ? 1 : 0;
            sessionFinishes = sessionPbs = 0;
            UpdateSession();
        }

        /// <summary>On a server (again): the session's clock runs.</summary>
        void ResumeSession()
        {
            if (gamePid == 0 || sessionRunningSince != null) return;
            sessionRunningSince = DateTime.Now;
            UpdateSession();
        }

        /// <summary>Off a server (or the game closed): the session's clock stops at <paramref name="at"/> (now) and waits.</summary>
        void PauseSession(DateTime? at = null)
        {
            if (!(sessionRunningSince is DateTime since)) return;
            var now = DateTime.Now;
            var until = at ?? now;
            sessionPlayed = SessionPlayed(until < since ? since : until > now ? now : until);
            sessionRunningSince = null;
            UpdateSession();
        }

        /// <summary>The server a KSF server-list ad in chat names ("... IP: 167.114.158.6:27016"), or null for any other line.</summary>
        internal static string AdvertisedServer(string line)
        {
            var ad = KsfServerAdLine.Match(line);
            return ad.Success ? ad.Groups["address"].Value : null;
        }

        /// <summary>A server address that's KSF's: on ksf.surf's list, or one of the private ones (ksf_servers in settings.ini).</summary>
        bool IsKsfAddress(string address) => address != null && (servers.Any(s => s.Address == address) || extraKsfServers.ContainsKey(address));

        /// <summary>
        /// ksf_servers in settings.ini: "ip:port" for a 66 tick server, "ip:port@100" for a 100 tick one, separated by
        /// spaces - to the tick rate (css / css100t) by address.
        /// </summary>
        internal static Dictionary<string, string> ParseKsfServers(string setting)
        {
            var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (setting ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var at = entry.IndexOf('@');
                var address = at < 0 ? entry : entry.Substring(0, at);
                if (!IPEndPoint.TryParse(address, out var ip) || ip.Port == 0) continue;
                list[address] = at >= 0 && entry.Substring(at + 1).StartsWith("100", StringComparison.Ordinal) ? Tick100 : Tick66;
            }
            return list;
        }

        /// <summary>The server you're on is KSF's though ksf.surf doesn't list it: from now on (and next time) it counts as one.</summary>
        void CountAsKsf(string address)
        {
            // 66 tick, like most of KSF's servers ("ip:port@100" in settings.ini for a 100 tick one).
            if (!extraKsfServers.TryAdd(address, Tick66)) return;
            settings.Set("ksf_servers", string.Join(" ", extraKsfServers.OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => a.Value == Tick100 ? a.Key + "@100" : a.Key)));
            Program.Trace($"{address} counts as a KSF server (it shows KSF's servers in chat)");
            onKsfServer = true;
            EnsureLiveDemo();
        }

        /// <summary>
        /// Asks the game in a moment whether it's still on a server (see OnTick): if not, you left at <paramref name="hintAt"/>.
        /// </summary>
        void CheckStillOnServer(double seconds, DateTime hintAt)
        {
            if (offServer || gamePid == 0) return;
            leaveCheck.Hint(hintAt, TimeSpan.FromSeconds(seconds), DateTime.Now);
        }

        /// <summary>Joining a server (or on one): nothing to check.</summary>
        void CancelLeaveCheck() => leaveCheck.Cancel();

        /// <summary>
        /// You left the server - the game is in its menu. The map stays on show (as the last map), the session's clock
        /// waits until you join a server again, and what was live about the server goes.
        /// </summary>
        void LeftServer(DateTime since)
        {
            CancelLeaveCheck();
            listedLastPoll = false;
            if (gamePid == 0 || offServer) return;
            Program.Trace($"left the server (at {since:HH:mm:ss})");
            offServer = true;
            PauseSession(since);
            yourServer = null;
            onKsfServer = listedAsSpectating = false;
            connectedAddress = nextMapName = hudRequestedFor = null;
            announcePending = cardEchoPending = false;
            currentZone = null;
            timeLimitCheckAt = null;
            clock.Reset();
            vm.SetCurrentZone(null);
            vm.SetServerLine(null);
            vm.SetLiveServer(null, null, null);
            vm.SetNextMap(null, null);
            vm.SetLive(false);
            vm.Tick(DateTime.Now);
            if (servers.Count > 0) vm.SetServers(servers, null, SavedMaps());
            UpdateStatus();
        }

        /// <summary>The timer's "08:23:89" is minutes:seconds:hundredths (hours come first on very long runs).</summary>
        static bool TryParseTimerTime(string text, out double seconds)
        {
            seconds = 0;
            var parts = text.Split(':', '.');
            if (parts.Length < 2) return false;
            var fraction = parts[parts.Length - 1];
            if (!int.TryParse(fraction, out var hundredths)) return false;
            seconds = hundredths / Math.Pow(10, fraction.Length);
            var unit = 1;
            for (var i = parts.Length - 2; i >= 0; i--, unit *= 60)
            {
                if (!int.TryParse(parts[i], out var value)) return false;
                seconds += value * unit;
            }
            return true;
        }

        /// <summary>Pulls fresh numbers for the current map and your recent records, ignoring the cache.</summary>
        void ReloadFromKsf()
        {
            if (currentMap != null)
            {
                cache.Remove(CacheKey(Game, currentMap));
                _ = FetchAsync(currentMap);
            }
            nextRecentPoll = DateTime.MinValue;
        }

        static string CacheKey(string game, string map) => game + "|" + map;

        /// <summary>The game just (re)started and is sitting in the main menu.</summary>
        void OnGameStarted()
        {
            PauseSession();
            currentMap = null;
            report = null;
            inGameAt = null;
            announcePending = cardEchoPending = false;
            yourServer = null;
            onKsfServer = false;
            connectedAddress = nextMapName = null;
            currentZone = null;
            vm.SetCurrentZone(null);
            zoneFetch?.Cancel();
            vm.ShowNoMap();
            vm.SetNextMap(null, null);
            vm.SetLiveServer(null, null, null);
            clock.Reset();
            timeLimitCheckAt = null;
            vm.AmbientImage = null;
            ListChanged();
            UpdateStatus();
        }

        /// <summary>
        /// Your title, rank and points on 66 and 100 tick, from your ksf.surf profile (read every quarter of an hour
        /// while the dashboard is up; the server list keeps the tick rate you're playing current in between).
        /// </summary>
        async Task RefreshStandingsAsync()
        {
            var steamId = CurrentSteamId();
            if (steamId == null || levelLoading || DateTime.Now < api.BusyUntil) return;
            levelLoading = true;
            nextStandingsCheck = DateTime.Now.AddMinutes(15);
            try
            {
                foreach (var game in new[] { Tick66, Tick100 })
                {
                    try
                    {
                        var standing = await api.GetStandingAsync(steamId, game);
                        if (standing != null) standings[game] = standing;
                    }
                    catch (Exception ex) when (IsNetworkError(ex))
                    {
                        Program.Trace($"standing ({game}): {ex.GetBaseException().Message}");
                    }
                }
                Program.Trace("standings: " + string.Join(", ", standings.Values.Select(s => $"{s.Tick} {s.Title} #{s.Rank} {s.Points} pts")));
            }
            finally
            {
                levelLoading = false;
            }
            await ShowLevelsAsync();
        }

        /// <summary>The level card: each tick rate's title and what the next one takes. Rank titles take passing whoever holds the last spot.</summary>
        async Task ShowLevelsAsync()
        {
            var rows = new List<(PlayerStanding, int?)>();
            foreach (var standing in standings.Values.OrderBy(s => s.Game).ToList())
            {
                int? needed = null;
                var index = KsfLevels.IndexOf(standing);
                if (index > 0 && KsfLevels.Ladder[index - 1].TopRank is int top)
                {
                    var key = $"{standing.Game}|{KsfStyle}|{top}";
                    if (pointsAtRank.TryGetValue(key, out var known) && DateTime.Now - known.At < TimeSpan.FromMinutes(15)) needed = known.Points;
                    else if (DateTime.Now >= api.BusyUntil)
                    {
                        try
                        {
                            needed = await api.GetPointsAtRankAsync(top, standing.Game, KsfStyle);
                            pointsAtRank[key] = (DateTime.Now, needed);
                        }
                        catch (Exception ex) when (IsNetworkError(ex)) { }
                    }
                }
                rows.Add((standing, needed));
            }
            if (rows.Count > 0) vm.SetLevels(rows, Game);
        }

        /// <summary>
        /// The server list has your rank and points on the tick rate you're playing - but as the game server had them,
        /// which can be from when you joined. Only used while your profile hasn't been read lately.
        /// </summary>
        void UpdateStanding(string game, int? rank, int? points)
        {
            if (!(points is int p)) return;
            if (!standings.TryGetValue(game, out var standing))
                standings[game] = standing = new PlayerStanding { Game = game, Tick = game == Tick100 ? "100T" : "66T" };
            if (standing.FromProfile && DateTime.Now - standing.At < TimeSpan.FromMinutes(20)) return;
            if (standing.Rank == rank && standing.Points == p) return;
            standing.Rank = rank;
            standing.Points = p;
            _ = ShowLevelsAsync();
        }

        void TraceClock(string why)
        {
            var left = clock.LeftAt(DateTime.Now);
            Program.Trace($"time left ({why}): " + (clock.Extending ? "extended, reading mp_timelimit"
                : left is double seconds ? DashboardViewModel.Countdown(seconds) : "unknown")
                + (clock.LimitMinutes is double limit ? $", limit {limit.ToString(CultureInfo.InvariantCulture)} min" : ""));
        }

        /// <summary>Reads mp_timelimit in the console in a moment (at most every few seconds, and only while on a map).</summary>
        void CheckTimeLimitSoon(double seconds)
        {
            var at = DateTime.Now.AddSeconds(seconds);
            if (timeLimitCheckAt == null || at < timeLimitCheckAt) timeLimitCheckAt = at;
        }

        async void OnMapChanged(string map, bool justJoined)
        {
            currentMap = map;
            mapSeenAt = DateTime.Now;
            demoRetry = TimeSpan.FromSeconds(30);
            failedFetches = 0;
            inGameAt =justJoined ? (DateTime?)null : DateTime.Now;
            announcePending = justJoined && settings.GetBool("run_server_commands");
            cardEchoPending = justJoined;
            nextAnnounceTry = DateTime.MinValue;
            manualGame = null;
            refreshAt.Clear();
            report = null;
            nextMapName = null;
            pointsJustEarned = null;
            currentZone = null;
            lastLiveZoneAt = DateTime.MinValue;
            vm.SetCurrentZone(null);
            zoneFetch?.Cancel();
            pinnedLeaderZone = null;
            followedLeaderZone = 0;
            if (justJoined)
            {
                sessionMaps++;
                offServer = false;
                CancelLeaveCheck();
                ResumeSession();
            }
            UpdateSession();
            vm.ShowLoading(map, live: gamePid != 0 || justJoined);
            vm.SetNextMap(null, null);
            clock.Reset();
            // Joining: read it once in the game (see InGame); already on the map when the app started: now.
            CheckTimeLimitSoon(justJoined ? 30 : 1);
            if (yourServer != null) vm.SetLiveServer(yourServer, CurrentSteamId(), map);
            vm.SetServerLine(yourServer != null && yourServer.Map == map ? yourServer : null);
            ListChanged();
            UpdateStatus();
            // The server list lags a little behind map changes; look again shortly.
            nextServerPoll = DateTime.Now.AddSeconds(justJoined ? 12 : 0);
            try
            {
                var image = LoadHeroImageAsync(map);
                var ambient = LoadAmbientAsync(map);
                await FetchAsync(map);
                await image;
                await ambient;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        async Task LoadHeroImageAsync(string map)
        {
            var image = await images.MapAsync(map, 1600, urgent: true);
            if (map == currentMap) vm.MapImage = image;
        }

        /// <summary>A tiny blurred copy of the map picture; stretched behind the whole dashboard it tints it with the map's colours.</summary>
        async Task LoadAmbientAsync(string map)
        {
            var image = await images.AmbientAsync(map);
            if (map == currentMap) vm.AmbientImage = image;
        }

        async Task FetchAsync(string map)
        {
            var id = ++fetchId;
            var game = Game;
            var key = CacheKey(game, map);
            fetching = true;
            vm.Is100t = game == Tick100;
            MapReport fresh;
            try
            {
                if (cache.TryGetValue(key, out var cached) && DateTime.Now - cached.FetchedAt < TimeSpan.FromMinutes(2))
                {
                    fresh = cached;
                }
                else
                {
                    fresh = await api.GetReportAsync(map, CurrentSteamId(), game, KsfStyle);
                    // Kept only when complete: a missing part is asked for again soon.
                    if (fresh.Error == null && fresh.PersonalError == null) cache[key] = fresh;
                }
            }
            finally
            {
                if (id == fetchId) fetching = false;
            }

            if (id != fetchId || map != currentMap) return;
            if (game != Game)
            {
                // Found out we're on the other tick rate while this was loading.
                _ = FetchAsync(map);
                return;
            }
            var before = report;
            report = fresh;
            // Times you've set in game that ksf.surf doesn't have yet stay on show.
            ApplyLocalBests(fresh);
            if (fresh.Main?.Time is double best) MarkFinished(fresh.Info?.Name ?? map, best, game);
            // The nominate page follows the tick rate you're on.
            if (FinishedKey() != finishedShown)
            {
                ShowFinishedMaps();
                if (vm.IsNominatePage) EnsureFinishedMaps();
            }
            var zones = fresh.RecordZones;
            Program.Trace($"loaded {map} ({game}): pb={(fresh.Main?.Time is double pb ? Format.Time(pb) : "none")} rank={fresh.Main?.Rank}/{fresh.Main?.TotalRanks} error={fresh.Error ?? "-"}" +
                (zones.Count > 0 ? $" zones done={zones.Count(z => fresh.Zone(z)?.Time != null)}/{zones.Count} records={fresh.ZoneWrs.Count}" : ""));
            if (report.PlayerName != null)
            {
                if (settings.Get("last_name") != report.PlayerName) settings.Set("last_name", report.PlayerName);
                if (report.PlayerCountry != null && settings.Get("last_country") != report.PlayerCountry) settings.Set("last_country", report.PlayerCountry);
                vm.SetPlayer(report.PlayerName, report.PlayerCountry, lastRank, lastPoints, settings.Get("last_rank_tick"));
            }
            WriteCard();
            ShowNewZoneBests(before, fresh);
            LoadZoneRecords(fresh);
            UpdateLeaderboard();
            UpdateStatus();
        }

        /// <summary>
        /// The record on each stage and bonus, for "your times": whatever wasn't saved from an earlier visit comes in
        /// one at a time after everything else (ksf.surf limits how fast it can be asked).
        /// </summary>
        async void LoadZoneRecords(MapReport r)
        {
            var key = r.Game + "|" + r.Map;
            // The zone you're on and the ones you've done come first: they get a gap and a bar, the rest only the record.
            var zones = r.RecordZones.Where(z => !r.ZoneWrs.ContainsKey(z))
                .OrderBy(z => z == currentZone ? 0 : r.Zone(z)?.Time != null ? 1 : 2).ThenBy(z => z).ToList();
            if (zones.Count == 0 || key == zoneFetchKey) return;
            zoneFetch?.Cancel();
            var cancel = new CancellationTokenSource();
            zoneFetch = cancel;
            zoneFetchKey = key;
            try
            {
                // A timeout or a hiccup shouldn't leave gaps until the next refresh: try again a little later.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await api.FetchZoneWrsAsync(r.Info.Name, zones, r.Game, KsfStyle, (zone, wr) =>
                        {
                            if (wr == null || report == null || report.Game + "|" + report.Map != key) return;
                            report.ZoneWrs[zone] = wr;
                            vm.ShowTimes(report);
                        }, cancel.Token);
                        break;
                    }
                    catch (Exception ex) when (!cancel.IsCancellationRequested && IsNetworkError(ex) && attempt < 4)
                    {
                        Program.Trace($"zone records for {r.Map} (try {attempt}): {ex.GetBaseException().Message}");
                        await Task.Delay(TimeSpan.FromSeconds(15 * attempt), cancel.Token);
                    }
                }
                Program.Trace($"zone records for {r.Map}: {report?.ZoneWrs.Count}/{r.RecordZones.Count}");
                // The in-game card shows the gaps too.
                if (report != null && report.Game + "|" + report.Map == key) WriteCard();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace("zone records: " + ex.GetBaseException().Message);
            }
            finally
            {
                if (zoneFetch == cancel)
                {
                    zoneFetch = null;
                    zoneFetchKey = null;
                }
                cancel.Dispose();
            }
        }

        /// <summary>
        /// Compares your stage and bonus times with the previous load of the same map and points out the ones you
        /// improved (for times the live timer text didn't already show).
        /// </summary>
        void ShowNewZoneBests(MapReport before, MapReport after)
        {
            if (before == null || !after.IsOnKsf || before.Map != after.Map || before.Game != after.Game || before.PersonalError != null || after.PersonalError != null
                || before.Zones.Count == 0) return;
            var better = new List<(int Zone, double Time, double? Old)>();
            foreach (var zone in after.RecordZones)
            {
                if (!(after.Zone(zone)?.Time is double time)) continue;
                var old = before.Zone(zone)?.Time;
                if (old == null || time < old.Value - 0.0005) better.Add((zone, time, old));
            }
            if (better.Count == 0) return;

            Program.Trace("new bests: " + string.Join(", ", better.Select(b => $"{MapReport.ZoneLabel(b.Zone)} {Format.Short(b.Time)}")));
            vm.FlashZones(better.Select(b => b.Zone).ToList());
            var one = better[0];
            vm.Toast = better.Count > 1 ? "New bests on " + string.Join(", ", better.Select(b => MapReport.ZoneLabel(b.Zone)))
                : one.Old is double was ? $"{MapReport.ZoneName(one.Zone)} new best  {Format.Short(one.Time)}  ({Format.Gap(one.Time, was)})"
                : $"{MapReport.ZoneName(one.Zone)} done  {Format.Short(one.Time)}";
        }

        /// <summary>
        /// Once the player is in the game: print the card into the console and, on a KSF server, run the
        /// server_commands so KSF itself shows the map info and the player's rank in chat.
        /// </summary>
        async Task AnnounceAsync()
        {
            announcing = true;
            try
            {
                var map = currentMap;
                var announce = announcePending;
                var echoCard = cardEchoPending && report != null;
                var commands = new List<string>();
                if (echoCard) commands.Add("exec ksf_card");
                // "status" only prints to your console: the server (66/100 tick) and its address for the live panel.
                if (echoCard || announce) commands.Add("status");
                if (commands.Count == 0) return;

                var hostname = new TaskCompletionSource<string>();
                if (announce) hostnameSeen = hostname;

                if (!await PushAsync(string.Join("; ", commands)))
                {
                    hostnameSeen = null;
                    nextAnnounceTry = DateTime.Now.AddSeconds(2);
                    return;
                }
                // A newer map may have started meanwhile; it gets its own announcement.
                if (map != currentMap) return;
                if (echoCard) cardEchoPending = false;
                if (!announce) return;

                announcePending = false;
                var winner = await Task.WhenAny(hostname.Task, Task.Delay(3000));
                hostnameSeen = null;
                var onKsf = winner == hostname.Task
                    ? hostname.Task.Result.IndexOf("ksf", StringComparison.OrdinalIgnoreCase) >= 0 || IsKsfAddress(connectedAddress)
                    : DateTime.Now - lastKsfChatAt < TimeSpan.FromMinutes(15);
                // One at a time: KSF answers a second command within a second with "You must wait 1.0 seconds".
                var serverCommands = GameConfig.ServerCommands(settings).Split(';').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                for (var i = 0; i < serverCommands.Count && onKsf && map == currentMap; i++)
                {
                    if (i > 0) await Task.Delay(1300);
                    await PushAsync(serverCommands[i]);
                }
            }
            finally
            {
                announcing = false;
            }
        }

        /// <summary>Keeps your times current while you grind the map.</summary>
        void RefreshIfStale(DateTime now)
        {
            if (gamePid == 0 || currentMap == null || report == null || fetching) return;
            var age = now - report.FetchedAt;
            // Your times or the map's records didn't come (ksf.surf timed out or was busy): ask again in seconds, not
            // minutes - a little later each time it keeps failing.
            var failed = report.Error != null || report.PersonalError != null;
            if (!failed) failedFetches = 0;
            var retry = TimeSpan.FromSeconds(Math.Min(180, 8 << Math.Min(failedFetches, 5)));
            if (age > TimeSpan.FromMinutes(3) || (failed && age > retry))
            {
                if (failed)
                {
                    failedFetches++;
                    Program.Trace($"asking ksf.surf again for {currentMap} (try {failedFetches + 1}): {report.Error ?? report.PersonalError}");
                }
                _ = FetchAsync(currentMap);
            }
        }

        void RefreshEverything()
        {
            ReloadFromKsf();
            nextServerPoll = DateTime.MinValue;
            vm.Toast = "Refreshing from ksf.surf...";
        }

        async Task<List<KsfServer>> ServersOrEmpty(string game)
        {
            try { return await api.GetServersAsync(game); }
            catch (Exception ex) when (IsNetworkError(ex)) { return new List<KsfServer>(); }
        }

        async Task<List<RecentRecord>> RecentOrEmpty(string steamId, string game)
        {
            try { return await api.GetRecentAsync(steamId, game, KsfStyle); }
            catch (Exception ex) when (IsNetworkError(ex)) { return new List<RecentRecord>(); }
        }

        async Task PollServersAsync()
        {
            pollingServers = true;
            try
            {
                // 66 and 100 tick servers together, so you see every KSF server and where you are - and KSF's private
                // ones, which ksf.surf doesn't list: those are asked themselves.
                var lists = await Task.WhenAll(ServersOrEmpty(Tick66), ServersOrEmpty(Tick100), PrivateServersAsync());
                var list = lists[0].Concat(lists[1]).ToList();
                if (list.Count == 0) return;
                list.AddRange(lists[2].Where(p => list.All(s => s.Address != p.Address)));
                servers = list;
                var steamId = CurrentSteamId();
                KsfServerPlayer you = null;
                KsfServer listedOn = null;
                if (gamePid != 0 && steamId != null)
                {
                    foreach (var server in list)
                    {
                        you = server.Players.FirstOrDefault(p => string.Equals(p.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
                        if (you == null) continue;
                        listedOn = server;
                        break;
                    }
                }
                // ksf.surf's list stopped listing you: maybe you left the server. It lags a minute behind, so "status" tells
                // for sure (once); without the game's console, two polls in a row do.
                if (gamePid != 0 && steamId != null && !offServer)
                {
                    if (listedOn != null)
                    {
                        listedLastPoll = true;
                        unlistedPolls = 0;
                    }
                    else if (listedLastPoll && link == LinkState.Ready)
                    {
                        listedLastPoll = false;
                        CheckStillOnServer(0, DateTime.Now);
                    }
                    else if (listedLastPoll && ++unlistedPolls >= 2) LeftServer(DateTime.Now);
                }
                // The address from "status" is exact; ksf.surf's player lists can lag a minute behind a server switch.
                yourServer = gamePid == 0 || offServer ? null
                    : connectedAddress != null ? list.FirstOrDefault(s => s.Address == connectedAddress)
                    : listedOn;
                var rankTick = listedOn?.Game == Tick100 ? "100T" : "66T";
                // Your rank and points: ksf.surf's list has them (a private server only has your name).
                if (listedOn?.FromKsf == false) you = null;
                if (you != null && (you.Rank != lastRank || you.Points != lastPoints || settings.Get("last_rank_tick") != rankTick))
                {
                    lastRank = you.Rank;
                    lastPoints = you.Points;
                    settings.Set("last_rank", lastRank?.ToString(CultureInfo.InvariantCulture) ?? "");
                    settings.Set("last_points", lastPoints?.ToString(CultureInfo.InvariantCulture) ?? "");
                    settings.Set("last_rank_tick", rankTick);
                    vm.SetPlayer(report?.PlayerName ?? settings.Get("last_name"), report?.PlayerCountry ?? settings.Get("last_country"), lastRank, lastPoints, rankTick);
                }
                if (you != null && listedOn != null) UpdateStanding(listedOn.Game, you.Rank, you.Points);
                // Only trust it for the tick rate when it's clearly up to date (the hostname from "status" usually settles this first).
                if (yourServer != null && (connectedAddress != null || string.Equals(yourServer.Map, currentMap, StringComparison.OrdinalIgnoreCase)))
                    SetDetectedGame(yourServer.Game);
                vm.YourSteamId = steamId;
                vm.SetServers(list, yourServer?.Address, SavedMaps());
                _ = LoadServerProgressAsync();
                vm.SetServerLine(yourServer != null && string.Equals(yourServer.Map, currentMap, StringComparison.OrdinalIgnoreCase) ? yourServer : null);
                // ksf.surf's sample says when the map started (even from before an extension) and its time limit then.
                if (yourServer != null && yourServer.FromKsf && string.Equals(yourServer.Map, currentMap, StringComparison.OrdinalIgnoreCase))
                    clock.FromKsf(yourServer.TimeLimitMinutes, yourServer.TimeLeftSeconds, yourServer.FetchedAt);
                vm.SetLiveServer(yourServer, steamId, currentMap);
                // Your stage or bonus, when KSF's list is up to date with the map you're on (1-30 stages, 31+ bonuses;
                // 0 is the start zone, -1 spectating). Only used while the live timer text isn't coming in.
                listedAsSpectating = yourServer != null && listedOn == yourServer && you?.Zone == -1;
                if (you != null && yourServer != null && listedOn == yourServer && string.Equals(yourServer.Map, currentMap, StringComparison.OrdinalIgnoreCase))
                    SetCurrentZone(you.Zone >= 1 ? you.Zone : null, ZoneSource.ServerList);
                RefreshLaterView();
            }
            finally
            {
                pollingServers = false;
                // Faster while you're on a KSF server, so the players and their stages stay current.
                nextServerPoll = DateTime.Now.AddSeconds(yourServer != null ? 15 : 30);
            }
        }

        /// <summary>
        /// KSF's private servers (ksf_servers), which ksf.surf doesn't list: asked themselves, as a server browser does -
        /// their name, map and players (without the players' stages and ranks, or the time left: only ksf.surf has those).
        /// One that doesn't answer is shown as it last was, for a couple of minutes - or, while you're on it, as the
        /// game's "status" last showed it.
        /// </summary>
        async Task<List<KsfServer>> PrivateServersAsync()
        {
            var steamId = CurrentSteamId();
            var answers = await Task.WhenAll(extraKsfServers.ToList().Select(async pair =>
            {
                A2sInfo info = null;
                List<A2sPlayer> players = null;
                try
                {
                    info = await A2s.InfoAsync(pair.Key, TimeSpan.FromSeconds(2.5), shutdown.Token);
                    if (info != null) players = await A2s.PlayersAsync(pair.Key, TimeSpan.FromSeconds(2.5), shutdown.Token);
                }
                catch (OperationCanceledException) { }
                return (Address: pair.Key, Game: pair.Value, Info: info, Players: players);
            }));
            var now = DateTime.Now;
            var list = new List<KsfServer>();
            foreach (var answer in answers)
            {
                var status = !offServer && currentMap != null && answer.Address == connectedAddress && lastStatus?.Address == answer.Address ? lastStatus : null;
                if (answer.Info == null || string.IsNullOrEmpty(answer.Info.Map))
                {
                    if (privateServers.TryGetValue(answer.Address, out var last) && now - last.FetchedAt < TimeSpan.FromMinutes(2)) list.Add(last);
                    else if (status != null)
                    {
                        var fromStatus = PrivateServer(answer.Address, answer.Game, status.Name, currentMap, status.Humans ?? status.Players.Count);
                        fromStatus.Players.AddRange(status.PlayersAt(now));
                        list.Add(fromStatus);
                    }
                    continue;
                }
                var server = PrivateServer(answer.Address, answer.Game, answer.Info.Name, answer.Info.Map, answer.Info.Players - answer.Info.Bots);
                if (answer.Players != null)
                    foreach (var p in A2s.People(answer.Info, answer.Players))
                        server.Players.Add(new KsfServerPlayer { Name = p.Name, SteamId = IsMe(p.Name) ? steamId : null, ConnectedSeconds = (int)p.Seconds });
                // It doesn't say who's on it: as "status" showed them, while you're there.
                else if (status != null) server.Players.AddRange(status.PlayersAt(now));
                privateServers[answer.Address] = server;
                list.Add(server);
            }
            return list;
        }

        /// <summary>A private server's row: its map's tier, stages and bonuses from KSF's map list (looked up once if it isn't there).</summary>
        KsfServer PrivateServer(string address, string game, string name, string map, int players)
        {
            map = map.ToLowerInvariant();
            var mapInfo = catalog.Find(map);
            if (mapInfo == null) _ = LookUpMapAsync(map);
            return new KsfServer
            {
                Game = game,
                Name = string.IsNullOrWhiteSpace(name) ? address : name.Trim(),
                Address = address,
                Map = map,
                Tier = mapInfo?.Tier ?? 0,
                IsLinear = mapInfo?.IsLinear ?? false,
                StageCount = mapInfo?.StageCount ?? 0,
                BonusCount = mapInfo?.BonusCount ?? 0,
                PlayerCount = Math.Max(0, players),
                FromKsf = false,
            };
        }

        /// <summary>A private server's map that isn't in KSF's map list (yet): ksf.surf's search has its tier, stages and bonuses.</summary>
        async Task LookUpMapAsync(string map)
        {
            if (!mapsLookedUp.Add(map)) return;
            try
            {
                catalog.Add((await api.SearchMapsAsync(map)).Where(m => string.Equals(m.Name, map, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                mapsLookedUp.Remove(map);
            }
        }

        async Task PollRecentAsync()
        {
            pollingRecent = true;
            try
            {
                var steamId = CurrentSteamId();
                if (steamId == null) return;
                if (avatarFor != steamId)
                {
                    avatarFor = steamId;
                    var url = await api.GetAvatarUrlAsync(steamId);
                    if (url != null) vm.Avatar = await images.AvatarAsync(url);
                }
                var both = await Task.WhenAll(RecentOrEmpty(steamId, Tick66), RecentOrEmpty(steamId, Tick100));
                vm.SetRecent(both[0].Concat(both[1]).OrderByDescending(r => r.Date ?? DateTime.MinValue));
            }
            catch (Exception ex) when (IsNetworkError(ex)) { }
            finally
            {
                pollingRecent = false;
                nextRecentPoll = DateTime.Now.AddMinutes(3);
            }
        }

        static bool IsNetworkError(Exception ex) =>
            ex is HttpRequestException || ex is TaskCanceledException || ex is ArgumentException || ex is InvalidOperationException;

        string CurrentSteamId()
        {
            var id = SteamLocator.FindSteamId(settings.Get("steamid"));
            if (id != null)
            {
                if (settings.Get("last_steamid") != id) settings.Set("last_steamid", id);
                return id;
            }
            var last = settings.Get("last_steamid");
            return last.Length > 0 ? last : null;
        }

        void SaveCurrentMap()
        {
            string message;
            if (currentMap == null)
                message = "no map detected yet - join a map first";
            else if (later.Contains(currentMap))
                message = $"{currentMap} is already in your play-later list ({later.Items.Count} maps)";
            else
            {
                later.Add(currentMap, report?.Info?.Tier);
                var count = later.Items.Count;
                message = $"saved {currentMap} for later - {count} map{(count == 1 ? "" : "s")} in your list";
                if (report?.Info == null) _ = FillInMissingTiersAsync();
            }
            ListChanged();
            vm.Toast = char.ToUpperInvariant(message[0]) + message.Substring(1);

            try { config?.WriteMessage(new[] { message }); }
            catch (IOException) { }
            _ = PushAsync("exec ksf_msg");
        }

        /// <summary>
        /// On a KSF server with the game connected, nominates straight away (the server answers in chat).
        /// Otherwise copies "!nominate map" to paste in chat.
        /// </summary>
        async void Nominate(string map)
        {
            if (string.IsNullOrEmpty(map)) return;
            if (link == LinkState.Ready && (yourServer != null || onKsfServer) && await PushAsync("sm_nominate " + map))
            {
                vm.Toast = $"Nominated {map} - the server replies in chat";
                return;
            }
            CopyText("!nominate " + map, $"Copied  !nominate {map}  - paste it in KSF chat");
        }

        /// <summary>Puts text on the clipboard (through the dashboard window, which has it) and says so.</summary>
        async void CopyText(string text, string done)
        {
            EnsureWindow();
            var clipboard = window.Clipboard;
            try
            {
                if (clipboard == null) throw new InvalidOperationException("no clipboard");
                await clipboard.SetTextAsync(text);
                vm.Toast = done;
            }
            catch (InvalidOperationException)
            {
                vm.Toast = "Couldn't reach the clipboard - try again";
            }
        }

        void Join(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            // Steam hands it to the running game (or starts it).
            if (Desktop.Open("steam://connect/" + address)) vm.Toast = "Joining " + address + "...";
            else CopyText("connect " + address, $"Copied  connect {address}  - paste it in the CS:S console");
        }

        void ListChanged()
        {
            var items = later.Items;
            try { config?.WriteList(CardBuilder.BuildList(items, currentMap, keys)); }
            catch (IOException) { }
            RefreshLaterView();
            if (servers.Count > 0) vm.SetServers(servers, yourServer?.Address, SavedMaps());
            vm.SetMapsContext(SavedMaps(), currentMap);
            WriteCard();
        }

        void RefreshLaterView()
        {
            var needThumbs = vm.SetPlayLater(later.Items, currentMap, servers);
            _ = LoadThumbsAsync(needThumbs);
        }

        async Task LoadThumbsAsync(List<LaterRow> rows)
        {
            foreach (var row in rows)
            {
                var thumb = await images.MapAsync(row.Map, 240);
                if (thumb != null) row.Thumb = thumb;
            }
        }

        HashSet<string> SavedMaps() => new HashSet<string>(later.Items.Select(e => e.Map), StringComparer.OrdinalIgnoreCase);

        void WriteCard()
        {
            if (report == null) return;
            var saved = later.Contains(report.Map);
            try { config?.WriteCard(CardBuilder.Build(report, saved, keys)); }
            catch (IOException) { }
            vm.ShowReport(report, saved);
            UpdateGroupGoal();
            ShowMapProgress(report);
        }

        /// <summary>Your progress on the map on show, for its server in the list (with the times you've just set, too).</summary>
        void ShowMapProgress(MapReport r)
        {
            if (r?.Info == null || r.SteamId == null || r.PersonalError != null) return;
            vm.SetMapProgress(r.Game, r.Info.Name, MapProgress.From(r.Zones, r.Info.IsLinear, r.Info.StageCount, r.Info.BonusCount));
        }

        /// <summary>
        /// Your progress on each server's map - your time, and which stages and bonuses you've done - for the server
        /// list: one lookup per map (in the background, kept 20 minutes), and the map on show from the dashboard.
        /// </summary>
        async Task LoadServerProgressAsync()
        {
            var steamId = CurrentSteamId();
            if (steamId == null || loadingProgress) return;
            loadingProgress = true;
            try
            {
                foreach (var server in servers.ToList())
                {
                    if (report != null && report.Game == server.Game && string.Equals(report.Info?.Name, server.Map, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowMapProgress(report);
                        if (report.PersonalError == null) continue;
                    }
                    // A private server's map that ksf.surf doesn't know (or not yet): there's nothing to look up.
                    if (!server.FromKsf && server.Tier == 0) continue;
                    var key = CacheKey(server.Game, server.Map);
                    if (!serverMapRecords.TryGetValue(key, out var known) || DateTime.Now - known.At > TimeSpan.FromMinutes(20))
                    {
                        if (DateTime.Now < api.BusyUntil) break;
                        known = (DateTime.Now, await api.GetPlayerZonesAsync(server.Map, steamId, server.Game, KsfStyle, shutdown.Token));
                        serverMapRecords[key] = known;
                    }
                    vm.SetMapProgress(server.Game, server.Map, MapProgress.From(known.Zones, server.IsLinear, server.StageCount, server.BonusCount));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace("server list progress: " + ex.GetBaseException().Message);
            }
            finally
            {
                loadingProgress = false;
            }
        }

        /// <summary>The arrows on the group tile: -1 = a better group (down to the top 10), 1 = an easier one.</summary>
        void StepGroupGoal(object parameter)
        {
            if (!int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)) return;
            var goal = Math.Max(0, Math.Min(KsfGroups.Count, shownGroupGoal + Math.Sign(step)));
            settings.Set("group_goal", goal == 0 ? "top10" : goal.ToString(CultureInfo.InvariantCulture));
            UpdateGroupGoal();
        }

        /// <summary>
        /// The group tile for the map on show: where that group ends on the map's leaderboard (KSF's own cutoffs from
        /// ksf.surf), and the time there - looked up on ksf.surf (one request, kept a few minutes) unless your best is
        /// in the group already.
        /// </summary>
        void UpdateGroupGoal()
        {
            var r = report;
            if (r?.Info == null) return;
            var me = r.Main;
            var sizeKey = $"{r.Game}|{KsfStyle}|{r.Info.Name}";
            var total = me?.TotalRanks ?? (leaderboardSizes.TryGetValue(sizeKey, out var size) && DateTime.Now - size.At < TimeSpan.FromMinutes(30) ? size.Total : 0);
            // KSF's cutoffs, read off the map's leaderboard page once in a while; until then (or without them) the same rule worked out.
            int[] ends = null;
            var endsPending = false;
            if (groupEnds.TryGetValue(sizeKey, out var published) && DateTime.Now - published.At < TimeSpan.FromMinutes(30)) ends = published.Ends;
            else if (DateTime.Now >= api.BusyUntil)
            {
                endsPending = true;
                if (loadingGroupEnds != sizeKey) _ = LoadGroupEndsAsync(r, sizeKey);
            }
            var goal = GroupGoal.For(r, GroupGoal.Picked(settings.Get("group_goal")), total, ends);
            shownGroupGoal = goal.Group;
            if (goal.LastRank == null && endsPending) goal.Loading = true;
            else if (total == 0 && ends == null && r.Wr != null)
            {
                // No cutoffs from ksf.surf and you haven't finished it: the record holder's own record says how many have.
                goal.Loading = true;
                _ = LoadGroupCutoffAsync(r, sizeKey, null);
            }
            else if (goal.NeedsCutoff && goal.LastRank is int last)
            {
                var key = $"{sizeKey}|{last}";
                // The top 10 is on show already.
                if (goal.Group == 0 && r.Top.Count >= last) goal.Cutoff = r.Top[last - 1].Time;
                else if (groupCutoffs.TryGetValue(key, out var known) && DateTime.Now - known.At < TimeSpan.FromMinutes(5)) goal.Cutoff = known.Row?.Time;
                else
                {
                    goal.Loading = true;
                    _ = LoadGroupCutoffAsync(r, sizeKey, last);
                }
            }
            vm.ShowGroupGoal(goal);
        }

        /// <summary>KSF's group cutoffs on the map, from its leaderboard page on ksf.surf (kept half an hour; without them, the rule).</summary>
        async Task LoadGroupEndsAsync(MapReport r, string key)
        {
            loadingGroupEnds = key;
            int[] ends = null;
            try
            {
                ends = await api.GetGroupEndsAsync(r.Info.Name, r.Game, KsfStyle);
                Program.Trace($"group cutoffs on {r.Info.Name} ({r.Game}): " + (ends != null ? string.Join(", ", ends) : "not on ksf.surf's page - working them out"));
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace($"group cutoffs ({key}): {ex.GetBaseException().Message}");
            }
            finally
            {
                if (loadingGroupEnds == key) loadingGroupEnds = null;
            }
            groupEnds[key] = (DateTime.Now, ends);
            if (report == r) UpdateGroupGoal();
        }

        /// <summary>Looks up the time at <paramref name="rank"/> on the map (or, with no rank, how many have finished it) for the group tile.</summary>
        async Task LoadGroupCutoffAsync(MapReport r, string sizeKey, int? rank)
        {
            var key = rank is int at ? $"{sizeKey}|{at}" : sizeKey;
            if (loadingGroupCutoff == key || DateTime.Now < api.BusyUntil) return;
            loadingGroupCutoff = key;
            try
            {
                if (rank is int cutoffRank)
                    groupCutoffs[key] = (DateTime.Now, await api.GetRecordAtRankAsync(r.Info.Name, 0, cutoffRank, r.Game, KsfStyle));
                else if (await api.GetTotalRanksAsync(r.Info.Name, r.Wr.SteamId, r.Game, KsfStyle) is int total)
                    leaderboardSizes[key] = (DateTime.Now, total);
                else return;
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                Program.Trace($"group tile ({key}): {ex.GetBaseException().Message}");
                return;
            }
            finally
            {
                if (loadingGroupCutoff == key) loadingGroupCutoff = null;
            }
            if (report == r) UpdateGroupGoal();
        }

        /// <summary>
        /// The save key keeps working while KSF Companion is closed, because the game still logs the press.
        /// Read what was logged since we last looked and save those maps now.
        /// </summary>
        long CatchUpOnMissedSaves(string path, out string lastMap)
        {
            lastMap = null;
            long length;
            try { length = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (IOException) { return 0; }

            if (!long.TryParse(settings.Get("log_offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) return length;
            if (offset > length) offset = 0;
            offset = Math.Max(offset, length - MaxCatchUpBytes);
            if (offset >= length) return length;

            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[length - offset];
                var total = 0;
                while (total < buffer.Length)
                {
                    var n = stream.Read(buffer, total, buffer.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
                // Stop at the last whole line; the watcher picks up from there.
                var end = total > 0 ? Array.LastIndexOf(buffer, (byte)'\n', total - 1) : -1;
                if (end < 0) return offset;
                text = Encoding.UTF8.GetString(buffer, 0, end + 1);
                length = offset + end + 1;
            }
            catch (IOException) { return length; }

            var map = settings.Get("log_map");
            if (map.Length == 0) map = null;
            var added = new List<string>();
            var catchUp = new LogParser();
            catchUp.MapChanged += m => map = m;
            catchUp.GameStarted += () => map = null;
            catchUp.SaveRequested += () => { if (map != null && later.Add(map, null)) added.Add(map); };
            foreach (var line in text.Split('\n')) catchUp.Feed(line);

            lastMap = map;
            if (added.Count > 0)
                Desktop.Notify(Program.AppName, $"Saved {string.Join(", ", added)} - you pressed {keys.Save} while KSF Companion was closed.");
            return length;
        }

        void SaveCheckpoint()
        {
            if (config == null || watcher == null || !watcher.TryGetCheckpoint(config.LogCandidates.First(), out var position)) return;
            var offset = position.ToString(CultureInfo.InvariantCulture);
            if (settings.Get("log_offset") != offset) settings.Set("log_offset", offset);
            if (settings.Get("log_map") != (currentMap ?? "")) settings.Set("log_map", currentMap ?? "");
        }

        async Task FillInMissingTiersAsync()
        {
            var changed = false;
            foreach (var entry in later.Items.Where(e => e.Tier == null).Take(25).ToList())
            {
                var tier = await api.GetTierAsync(entry.Map);
                if (tier == null) continue;
                later.SetTier(entry.Map, tier.Value);
                changed = true;
            }
            if (changed) ListChanged();
        }

        async Task<bool> PushAsync(string command)
        {
            if (link != LinkState.Ready) return false;
            var output = await SendAsync(command);
            if (output == null)
            {
                // The connection was lost (not just a game busy loading): open it again in a moment.
                if (link == LinkState.Ready && gameLink?.Lost == true)
                {
                    link = LinkState.Waiting;
                    nextLinkTest = DateTime.Now.AddSeconds(3);
                    UpdateStatus();
                }
                return false;
            }
            FeedGameOutput(output);
            return true;
        }

        /// <summary>One command at a time over the game's remote console: what it printed, or null if it didn't get there.</summary>
        async Task<string> SendAsync(string command)
        {
            var target = gameLink;
            if (target == null) return null;
            await sendLock.WaitAsync();
            try
            {
                return await target.SendAsync(command, TimeSpan.FromSeconds(3));
            }
            finally
            {
                sendLock.Release();
            }
        }

        void UpdateStatus()
        {
            string text;
            var connection = Connection.Waiting;
            if (config == null)
            {
                text = "Couldn't find Counter-Strike: Source - set game_dir in settings.ini";
                connection = Connection.Limited;
            }
            else if (setupError != null)
            {
                text = "Couldn't write to the CS:S cfg folder: " + setupError;
                connection = Connection.Limited;
            }
            else if (gamePid == 0)
                text = "Waiting for CS:S";
            else if (link == LinkState.Ready)
            {
                text = "Connected to CS:S";
                connection = Connection.Connected;
            }
            else if (!LogIsActive())
            {
                text = "CS:S is running - restart it once to finish setup";
                connection = Connection.Limited;
            }
            else if (link == LinkState.Unavailable)
            {
                text = $"Following your maps (hold {keys.Card} in-game for the card)";
                connection = Connection.Limited;
            }
            else
                text = "Connecting to CS:S...";

            vm.SetStatus(text, connection);
            // Only the states you can do something about get a notice under the title bar.
            var keysWork = $"The dashboard and {GameKeys.Label(keys.Save)} / {GameKeys.Label(keys.Card)} / {GameKeys.Label(keys.List)} work without it.";
            var userconMissing = config != null && setupError == null && UserconMissing && link != LinkState.Ready;
            vm.SetNotice(config == null ? "Couldn't find Counter-Strike: Source. Put your .../Counter-Strike Source/cstrike folder in settings.ini (game_dir), then restart KSF Companion."
                : setupError != null ? "Couldn't write to the CS:S cfg folder: " + setupError
                : connection == Connection.Limited && gamePid != 0 && !LogIsActive() ? "CS:S is running but KSF Companion can't follow it yet. Restart the game once to finish setup."
                : userconMissing ? "Add -usercon to Counter-Strike: Source's launch options in Steam (right-click it > Properties > Launch Options), then restart the game: "
                                   + "KSF Companion sends the game its console commands through it (server and time left, live stage times, teleports, nominate). " + keysWork
                : link == LinkState.Unavailable && linkProblem == LinkProblem.BadPassword
                    ? "CS:S didn't accept KSF Companion's console password. Restart the game once, so it reads the current one from autoexec.cfg."
                : link == LinkState.Unavailable
                    ? $"The game isn't taking commands from KSF Companion. Check that -usercon is in CS:S's launch options and that nothing else uses port {GameConfig.RconPort(settings)} (rcon_port in settings.ini). " + keysWork
                : null, userconMissing ? "Copy -usercon" : null);
            statusItem.Header = text.Length > 90 ? text.Substring(0, 87) + "..." : text;
            tray.ToolTipText = Program.AppName + (currentMap != null ? " - " + currentMap : "");
        }

        bool LogIsActive()
        {
            if (watcher != null && watcher.LastLineAt >= gameSeenAt) return true;
            foreach (var path in config.LogCandidates)
            {
                try
                {
                    if (File.Exists(path) && File.GetLastWriteTime(path) >= gameStartedAt) return true;
                }
                catch (IOException) { }
            }
            return false;
        }

        static DateTime? ProcessStartTime(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.StartTime;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                return null;
            }
        }

        static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        void EnsureWindow()
        {
            if (window != null) return;
            window = new DashboardWindow(vm, keys, systemFrame: settings.Get("window_frame") == "system");
            window.ApplyPlacement(settings.Get("window"));
            window.SetTopmost(settings.GetBool("window_topmost"));
            window.TopmostChanged += on =>
            {
                settings.Set("window_topmost", on ? "1" : "0");
                if (keepOnTopItem.IsChecked != on) keepOnTopItem.IsChecked = on;
            };
            window.PlacementChanged += () => placementDirty = true;
            window.PropertyChanged += (s, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty) return;
                if (window.IsVisible)
                {
                    nextServerPoll = nextRecentPoll = DateTime.MinValue;
                }
                else if (!window.AllowClose && settings.Get("hint_closed") != "1")
                {
                    // The first time the dashboard is closed: say where it went.
                    settings.Set("hint_closed", "1");
                    Desktop.Notify(Program.AppName, "Still running in the tray. Click the tray icon - or start KSF Companion again - to bring the dashboard back.");
                }
            };
        }

        public void ShowDashboard(bool activate)
        {
            EnsureWindow();
            if (!activate)
            {
                // Next to a running game: never take focus.
                if (!window.IsVisible) window.ShowWithoutFocus();
                return;
            }
            window.ShowActivated = true;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        void SavePlacement()
        {
            placementDirty = false;
            var placement = window?.Placement;
            if (placement != null && settings.Get("window") != placement) settings.Set("window", placement);
        }

        void OpenMapPage(string map)
        {
            if (!string.IsNullOrEmpty(map) && !Desktop.Open(KsfApi.MapPage(map))) vm.Toast = "Couldn't open a browser (xdg-open)";
        }

        void OpenDataFolder()
        {
            if (!Desktop.Open(Program.DataDir)) vm.Toast = "Settings and your play-later list are in " + Program.DataDir;
        }

        async void Uninstall()
        {
            if (config == null) return;
            if (GameBridge.FindGameProcessId() != 0)
            {
                await MessageDialog.ShowAsync(window, "Close Counter-Strike: Source first, then choose Remove again.");
                return;
            }
            var question =
                "Remove KSF Companion from Counter-Strike: Source?\n\n" +
                $"This deletes its ksf_*.cfg files and its block in autoexec.cfg, and puts your old {keys.Save} / {keys.Card} / {keys.List} binds back.\n\n" +
                $"Your play-later list stays in {Program.DataDir}.";
            if (!await MessageDialog.ShowAsync(window, question, "Remove", "Cancel")) return;

            try
            {
                config.Uninstall(settings);
                if (!Autostart.IsManaged) Autostart.Set(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                await MessageDialog.ShowAsync(window, "Couldn't remove everything: " + ex.Message);
                return;
            }
            timer.Stop();
            config = null;
            await MessageDialog.ShowAsync(window, "Removed from CS:S. KSF Companion will close now.");
            ExitApp();
        }

        void ExitApp()
        {
            shutdown.Cancel();
            timer.Stop();
            SavePlacement();
            if (config != null)
            {
                SaveCheckpoint();
                // Without the companion the card would go stale, so say so instead of showing an old map.
                try { config.WriteCard(new[] { "KSF Companion isn't running - start it to see KSF info for your map" }); }
                catch (IOException) { }
            }
            gameLink?.Dispose();
            tray.IsVisible = false;
            tray.Dispose();
            if (window != null)
            {
                window.AllowClose = true;
                window.Close();
            }
            api.Dispose();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
}
