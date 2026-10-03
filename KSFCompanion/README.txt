KSF Companion
=============
A black KSF dashboard for your second monitor, plus KSF map info and a play-later list inside
Counter-Strike: Source. It runs in the system tray (the orange icon).
For Linux: it sends the game its console commands over the game's remote console, so add -usercon
to CS:S's launch options in Steam (the dashboard reminds you until it's there).

The dashboard
  Customize (top right): switch any part of the dashboard off or on - or hover a part and click
  the x on its corner. The rest moves up to fill the space; hide everything on one side and the
  other side gets the whole width. The Size slider in there makes everything smaller or bigger
  (80% to 150%, the Nominate tab too). All of it is remembered (hidden and size in settings.ini).

  - The map you're on: KSF's preview image, tier, stages/bonuses, mapper, rating, which KSF
    server you're on and the next map as soon as KSF announces it.
  - Time left, big across from the map's name (orange in the last 2 minutes, red in the last 30
    seconds), with the map's time limit and how many times it's been extended and by how much underneath
    ("80 min limit  ·  extended 2x (+20 min)"; "since you joined" when you came in after it
    started). How many extensions KSF allows isn't something players can see - it isn't in any
    message or setting the game gets - so that can't be shown.
  - Your level: your KSF title (the tag in front of your name in chat) on 66 and 100 tick, with
    your rank, country rank, points and maps done, and how far off the next title is. The top
    titles go by rank - MASTER top 10, ELITE 25, VETERAN 50, PRO 100, EXPERT 200, HOTSHOT 300,
    EXCEPTIONAL 500, SEASONED 750, EXPERIENCED 1,500 - the rest by points: ACCOMPLISHED 13,000,
    ADEPT 9,000, PROFICIENT 6,000, SKILLED 4,000, CASUAL 2,500, BEGINNER 1,000, then ROOKIE.
    (KSF doesn't publish these; they were worked out from ksf.surf's rankings.) For a rank title
    it says how many points the player in its last spot has right now.
  - Your numbers: world record, your best time and rank (top x%), gap to the WR, group,
    finishes, attempts and time played, plus your bonus times. Times are cut off at the
    millisecond like the game shows them (10.199, never rounded up to 10.200).
  - Group tile: how much faster than your best you have to be to get into a KSF group - the next
    one up from yours, or the one you pick with its arrows (the top 10, or group 1 to 6; it's
    remembered). Before you've finished the map it shows the time to beat. The cutoffs are KSF's
    own, from the map's leaderboard page on ksf.surf (the "group" lines there). Without them it
    works them out the way KSF does: everyone below the top 10 is in a group by how far down the
    leaderboard they are - group 6 reaches two thirds of the way down, group 5 a third, then 1/6,
    1/12, 1/24 and 1/48 for groups 4 to 1 - but groups 1 to 5 reach at least the 20th, 35th,
    60th, 100th and 150th place (unless that's past the end of group 6).
  - Leaderboard: the top 10, with you highlighted (and your own row if you're outside the top 10).
    It follows what you're doing: the map's while you're on the map or its stages, a bonus's while
    you're on that bonus. Click a chip above it (MAP, S1, B2, ...) or a row in "your times" to see
    that stage's or bonus's top 10 instead; "follow me" goes back to following you.
  - Your times: the map, every stage and every bonus - your best time, how far off that record it
    is, and where you are on that leaderboard (#370 / 736). The bar shows how close you are to the
    record (full = record pace, empty = twice the record's time) and fills up smoothly as times
    come in.
    The stage you lose the most time on is orange, the stage/bonus you're on is highlighted, and
    stages show your sum of best next to the stage records added up.
    The arrow at the end of a row teleports you there in game: a stage, a bonus, or the map (back
    to the start). It's sent as a console command (sm_stage N, sm_bonus N, sm_restart), so nothing
    appears in chat for anyone. Stage and bonus records load one by one in the background the
    first time you play a map (the one you're on and the ones you've done first), then are kept
    for a day. The next map's are fetched as soon as KSF names it, so on a new map your times and
    the gaps are there straight away.
  - Live stage times: the moment you finish a stage or bonus, its row lights up with your time
    (a new best says "new best" until ksf.surf has it and your new rank - ksf.surf itself can
    take a while to catch up), and a message says how it compares to your best.
    How: the timer only shows that text on screen, never in the console, so on KSF servers KSF
    Companion has the game record a demo (its own "record" command) to cstrike/ksfc_live.dem and
    reads the timer text from that file as it grows. It's replaced every map and deleted when the
    game closes. Nothing touches the game's memory. If KSF Companion is restarted mid-map, it
    picks up the demo the game is still recording, and if that demo ever stops growing it's
    started again, so the times keep coming. If you record a demo yourself, live stage
    times wait until you stop it. While you spectate, the timer shows someone else's run, so
    nothing on screen counts as yours until you're back. Turn it off with live_hud = 0 in
    settings.ini.
    Speed: the game writes its demo to disk every ~5 seconds, so this way a finish shows up within
    about 5 s. If the timer prints your stage/bonus times in chat (see the !surftimer options),
    those lines reach KSF Companion instantly and it uses them straight away.
  - LIVE: the KSF server you're on - everyone surfing there, which stage/checkpoint/bonus they're
    on, their rank and how long they've been on (refreshed every 15 seconds), then who's
    spectating. It lists 12; "+ 28 more surfing - show everyone" lists them all.
  - This session: how long you've been on servers, maps, finishes and new PBs since CS:S started.
    Leave a server and the clock waits (the last map stays on show) until you join one again;
    closing the game ends the session.
  - Play later: your saved maps with thumbnails. When a KSF server is running one of them it
    says LIVE and gives you a Join button. Hover a map to nominate it (sent straight to the KSF
    server you're on; otherwise "!nominate <map>" is copied for chat), open it on ksf.surf, or
    remove it.
  - KSF servers: every server, its map (linear or staged, with its stages and bonuses), players
    and time left - and your progress on its map: your time (or "not done") over a bar for each
    stage (one for the whole map on a linear map) and one for each bonus, green for the ones
    you've done and red for the rest, like on ksf.surf. Click a server to see everyone on it
    (in the game or not); hover one to join it.
  - Your recent KSF records (new PRs, groups, map finishes).
  The whole window takes on a faint tint of the map you're on.
  It opens on your second monitor by itself when CS:S starts (without taking focus from the
  game) and remembers where you put it. Right-click the title bar for "Keep on top"; closing it
  only hides it.

  Private KSF servers aren't on ksf.surf's list: one counts as KSF's (live stage times, the
  session noticing when you leave it) from the first time it shows KSF's servers in chat
  ("[Surf Timer] - Expert - surf_boreas (7/60) IP: ..."), and is remembered in settings.ini
  (ksf_servers - you can put one there yourself, as ip:port, or ip:port@100 for a 100 tick one).
  It's in the KSF servers list too, marked "private server": KSF Companion asks it directly, the
  way the game's server browser does - its name, map and who's on it, and your progress on its
  map (once ksf.surf has the map). Only ksf.surf has the time left and where everyone is on the
  map, so those stay empty. If it doesn't answer, it's still listed while you're on it, as the
  game's own "status" showed it. ksf-companion --server ip:port prints what a server answers.

  66 tick / 100 tick: KSF keeps separate records for its 100 tick servers (US 100T, EU 100T).
  The dashboard follows the server you're on automatically (it knows which one the moment you
  connect, so your times show straight away); the 66T / 100T switch on the map card lets you look
  at the other one.

The Binds tab (top of the window)
  - Put KSF's commands on keys: restart (sm_restart), restart stage (sm_teleport), previous stage,
    repeat stage, save location / load location / previous and next saved location, hide players,
    show zones, map info, rock the vote and more - and turn left/right (+left/+right) with a turn
    speed slider (cl_yawspeed). Your own command (like sm_stage 2 or !b 1) can go on a key too.
  - They run from the console, never from chat, so nobody sees anything. KSF's answer (like
    "saved location") only shows in your own chat.
  - Click a key button, then press the key - or a mouse button (not the left one) or turn the wheel.
    Esc cancels. Search finds an action by its name, its command or the key it's on.
  - A key you take over gets back what it did before when you take the bind off (shown on the row
    as "replaces ..."). They're written to cfg/ksf_binds.cfg and loaded straight away while the
    game runs. KSF Companion's own keys (F5/F6/F7) can be moved here too.

The Nominate tab (top of the window)
  - Rock the vote: votes to change the map now (sm_rtv).
  - Every KSF map with its picture, tier, stages/bonuses, mappers and rating. Search by map or
    mapper - no need to type surf_ or the _, and a typo or two swapped letters still finds it
    ("lieden" finds surf_leidenfrost, "No exact match" says when that's what you're seeing); filter by tier, by type (linear or staged) and by whether you've done it; sort by
    popular / newest / tier / A-Z / rating; switch between tiles and a list, and make the tiles
    bigger or smaller with the slider next to them (remembered). Nominate puts a map on the next vote (sm_nominate <map>; when you're not on
    a KSF server, "!nominate <map>" is copied for you to paste in chat instead); Save puts it on
    your play-later list (it then says Saved - click again to take it off). Your play-later maps
    come first when you're not searching.
  - Maps you've finished have a green tick with your time on them (hover it for your group and
    points). All / Not done / Done shows every map, only the ones you haven't finished yet, or
    only the ones you have - on the tick rate picked with 66T / 100T (it follows the server
    you're on until you pick one there). Which maps you've finished comes from
    your ksf.surf profile, read about once a day (it takes a minute or two the first time); maps
    you finish in between get their tick the moment you finish them.
  - Both commands go through the console, so nothing is typed in chat; the server announces the
    votes as usual. The map list is loaded from ksf.surf once and kept for a week (new maps are
    added twice a day); if ksf.surf is busy part way through, the rest comes a little later.

  Live: when you finish a map, your time shows the moment the timer announces it in chat - a new
  PB, first finish or WR gets a big banner with the time you took off and the points you got -
  and your new rank, group and the leaderboard load from KSF a second later.
  Time left on your map = its time limit minus how long it has been running. The limit is read
  from the game's console (mp_timelimit - it only prints there), again whenever the map is
  extended, however it's extended; when the map started comes from ksf.surf and the timer itself
  (its "Timeleft" panel and its "2 minutes remaining" messages). So an extension shows within a
  couple of seconds. Other servers' countdowns come from ksf.surf (as of when KSF last checked).

In the game
  F5          save the current map to your play-later list (you hear a blip)
  hold F6     open the console with the KSF card for this map (also your time, the gap to the
              record and your rank on every stage and bonus) - printed once, however long you
              hold the key
  hold F7     open the console with your play-later list
  KSF Companion never types in chat, and on its own it only runs console commands that answer in
  your console ("status" to see which server you're on - or that you've left it -, "mp_timelimit"
  for the map's time limit,
  and the demo it reads the timer from). It sends them over the game's remote console (CS:S needs
  -usercon in its launch options for that), on port 27015 of this PC with a password it makes up
  (rcon_port and rcon_password in settings.ini).
  Tray icon > "Run /m and /pr on map load" makes it ask KSF for the map info and your time each
  map - KSF answers those in chat, so that's off unless you turn it on (server_commands in
  settings.ini picks the commands).

Where things are
  ~/.config/ksf-companion:
    play-later.txt    your saved maps - you can edit it in any text editor
    settings.ini      keys, binds, tick (auto/66/100), style, game_dir, rcon_port, ...
    errors.log        anything that went wrong
  ~/.cache/ksf-companion: what's kept from ksf.surf (map pictures, records, the map list)
  ksf-companion --status shows what it finds: Steam, CS:S, your account, -usercon, the game.
  Updating: update it the way you installed it (nix flake update and rebuild, or the new download
  over the old folder) - your settings stay.
  Removing: close CS:S, then tray icon > "Remove from CS:S..."; then uninstall it the way you
  installed it.

Good to know
  - Keep it running while you play. Tray icon > "Start when I log in" starts it quietly in the tray with
    your desktop (or set programs.ksf-companion.autostart in your NixOS or Home Manager config).
  - If you press F5 while it isn't running, the map is still saved the next time you open it.
  - F5 used to be the in-game screenshot key. Steam's F12 screenshot still works.
  - It only reads files the game writes (the console log and, on KSF, its own demo recording) and
    sends normal console commands (like a keybind would). It never reads or writes game memory
    and injects nothing, so it is not a cheat and is VAC safe.
  - Data comes from ksf.surf (the same data their website uses; not an official API).
  - To remove it from CS:S: close the game, then tray icon > "Remove from CS:S...".
    That also puts your old F5/F6/F7 binds back.
