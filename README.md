# KSF Companion for Linux

A second-monitor dashboard for **KSF surf** in **Counter-Strike: Source** - on Linux, packaged for **NixOS**.

This is a Linux port of [KSF Companion](https://github.com/Shamshoo/ksf-companion) (Windows): the same dashboard,
nominate page, binds page and in-game keys, plus a Records tab, running in your system tray.

## Install

### NixOS

Two small edits to your NixOS config, then a rebuild. First see which kind of config you have:

```sh
ls /etc/nixos
```

There's a `flake.nix`: follow **A**. Only `configuration.nix` (and `hardware-configuration.nix`): follow **B**. (If
you keep your config in another folder and rebuild with `--flake`, that's **A**, with your folder instead of
`/etc/nixos`.)

#### A. Your config is a flake

1. Open `/etc/nixos/flake.nix` (for example `sudo nano /etc/nixos/flake.nix`) and add the three lines marked
   `# add`. Leave the rest of your file as it is:

   ```nix
   {
     inputs = {
       nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
       ksf-companion.url = "github:voms/ksftool";                  # add
     };

     outputs = { self, nixpkgs, ksf-companion, ... }: {           # add ksf-companion,
       nixosConfigurations.nixos = nixpkgs.lib.nixosSystem {
         modules = [
           ./configuration.nix
           ksf-companion.nixosModules.default                     # add
         ];
       };
     };
   }
   ```

   If your `outputs` starts with `inputs@{ ... }:` or `{ ... }@inputs:`, you can write
   `inputs.ksf-companion.nixosModules.default` in `modules` instead.

2. Open `/etc/nixos/configuration.nix` and add these two lines inside its outer `{ ... }`, next to your other
   settings:

   ```nix
   programs.ksf-companion.enable = true;
   programs.ksf-companion.autostart = true;   # start it in the tray when you log in
   ```

3. Rebuild:

   ```sh
   sudo nixos-rebuild switch --flake /etc/nixos
   ```

   That picks the configuration named after your computer's hostname. If the name after `nixosConfigurations.` in
   your `flake.nix` is a different one, put it after a `#`: `sudo nixos-rebuild switch --flake /etc/nixos#nixos`.
   The first rebuild builds KSF Companion, which takes a few minutes.

#### B. A plain `configuration.nix` (no flake)

1. Open `/etc/nixos/configuration.nix` (for example `sudo nano /etc/nixos/configuration.nix`). Add the line marked
   `# add` to the `imports` list at the top, and the two settings below it:

   ```nix
   imports = [
     ./hardware-configuration.nix
     "${builtins.fetchTarball "https://github.com/voms/ksftool/archive/HEAD.tar.gz"}/nix/nixos-module.nix"   # add
   ];

   programs.ksf-companion.enable = true;
   programs.ksf-companion.autostart = true;   # start it in the tray when you log in
   ```

   Put the line into the `imports` you already have: a second `imports = [ ... ];` is an error.

2. Rebuild:

   ```sh
   sudo nixos-rebuild switch
   ```

   The first rebuild builds KSF Companion, which takes a few minutes.

#### After the rebuild

1. If you tried it with `nix run` before, quit that copy: tray icon → **Exit**.
2. Log out and back in (it starts in the tray), or start **KSF Companion** from your app menu.
3. Set up CS:S once (next section). Your settings and play-later list stay in `~/.config/ksf-companion`, however
   you start it.

### Home Manager

To install it for your user instead: add the flake input as in **A**, then add
`ksf-companion.homeManagerModules.default` where your Home Manager modules are listed (`home-manager.sharedModules`
when Home Manager runs inside NixOS, or `modules` of `homeManagerConfiguration`), and in your home config:

```nix
programs.ksf-companion = {
  enable = true;
  autostart = true; # optional: a systemd user service in your graphical session
};
```

### Just try it

```sh
nix run github:voms/ksftool
```

It runs until you close it, nothing is installed. (If Nix says flakes are experimental, add
`--extra-experimental-features 'nix-command flakes'` after `nix run`.) There's also `overlays.default`, which adds
`pkgs.ksf-companion`.

### Other distros

Download **ksf-companion-linux-x64.tar.gz** from the [latest release](../../releases/latest) (.NET comes inside it), then:

```sh
mkdir -p ~/.local/opt && tar -xzf ksf-companion-linux-x64.tar.gz -C ~/.local/opt
~/.local/opt/ksf-companion/install.sh   # adds it to your app menu and to ~/.local/bin
```

Or install [Nix](https://nixos.org/download) and use `nix run` as above.

## Set up CS:S (once)

1. In Steam, right-click **Counter-Strike: Source → Properties → Launch Options** and add **`-usercon`**.
2. Start KSF Companion, then (re)start CS:S. That's it.
3. To check, run `ksf-companion --status` while you're on a server: `-usercon` should say `yes` and
   `game console (rcon)` should say `connected (port 27015)`.

- It finds CS:S and your Steam account by itself: Steam from your distro or NixOS (`programs.steam`), the Flatpak or
  the Snap, and every Steam library folder. It's made for the native Linux CS:S (the Windows one under Proton should
  work too).
- No sign-in, no account. It never asks for your Steam password.
- **Why `-usercon`:** on Windows the app hands its console commands (`status`, `mp_timelimit`, teleports, nominate,
  ...) to the game's window; Linux has nothing like that, so it sends them over the game's own remote console, which
  CS:S only opens with `-usercon`. Until it's there the dashboard shows a reminder with a button that copies it for
  you. Everything that only reads (the dashboard, F5 / F6 / F7) works without it.

## The tray icon

It lives in your system tray. KDE Plasma, Cinnamon, XFCE, MATE, LXQt and Budgie show it out of the box. On **GNOME**
add the AppIndicator extension (`pkgs.gnomeExtensions.appindicator` on NixOS) and turn it on in Extensions; on Sway,
Hyprland and friends you need a bar with a tray (Waybar's `tray` module). Without a tray, start KSF Companion again from
your app menu to bring the dashboard back.

It runs on X11 and on Wayland desktops (through XWayland).

## What it does

### Dashboard
![Dashboard](docs/dashboard-boreas.png)

- **Live times:** your stage and bonus times appear as soon as you finish, with the gap to the record. Times are cut off
  at the millisecond like the game shows them.
- **Groups:** how much faster than your best you have to be to get into the next KSF group, or the one you pick, with
  KSF's own group cutoffs from ksf.surf.
- **Map info:** see the map you're on, its tier, the top 10, and the time left (including extends).
- **Your rank:** see your KSF title, rank and points on 66 and 100 tick.
- **Servers:** see every KSF server, its map (linear or staged) and your progress on it (your time, and the stages and
  bonuses you've done). Click a server to see everyone on it, and join in one click. Private KSF servers that ksf.surf
  doesn't list are in it too, once they're in `ksf_servers` in settings.ini (see below).
- **Session:** time on servers, maps, finishes and PBs. It waits while you're off a server and ends when CS:S closes.
- **Play later:** press F5 in game to save a map for later.

### Nominate
![Nominate](docs/nominate-boreas.png)

- Search every KSF map (typos are OK), filter by tier, type, or done / not done on 66 or 100 tick.
- Nominate or rock the vote in one click.

### Records
- Every KSF map with its picture and your record on it, like your records page on ksf.surf: time, WR diff, your rank
  on the map (with your group: `#523 · G2`), points, completions, date, and which stages and bonuses you've done.
- ksf.surf's records page only shows your group below the top 10, so each map's rank is read from its own leaderboard
  in the background (the maps on show first) and kept: it's read again when your time on the map changes, or after a
  few days.
- Sort and filter like the site (every order best-to-worst or worst-to-best), plus "Zones left": maps you've finished
  with stages or bonuses still to do.

### Binds
![Binds](docs/binds-boreas.png)

- Put restart, restart stage, save/load location and turn binds on any key.
- Your binds already in the game show up here. Nothing shows in chat.

<sub>Screenshots from the Windows version. The Linux one looks the same, plus the Records tab and a few new tiles.</sub>

Everything in detail: [KSFCompanion/README.txt](KSFCompanion/README.txt).

## Is it safe?

**VAC:** ✅ Safe. It never touches the game's memory; it only uses config files and console commands, like a normal bind.

**Your Steam account:** 🔒 Never touched. It never asks for, reads or stores your Steam password or login. It only looks
up your public Steam ID (the one on your profile page) so it can show your own KSF times.

**The remote console:** `-usercon` makes CS:S accept console commands on TCP port 27015 while it runs, from anyone who
has the password. KSF Companion makes up a random 24-character one and keeps it in its settings file and your
`autoexec.cfg`, both readable only by you. NixOS's firewall keeps that port closed to other computers (unless you've
opened it, e.g. with `programs.steam.dedicatedServer.openFirewall`); on other distros, don't open or forward TCP 27015
unless you're hosting a server. Another port: `rcon_port` in settings.ini.

**What comes in from outside:** ksf.surf's data, game servers' answers and other players' chat are checked before
they're used. Map names and server addresses are checked before they go into a console command, a link or a file name,
text before it's printed in your console, and pictures before they're opened. Chat can't pass for the timer's messages.

## Where things are

| | |
|---|---|
| `~/.config/ksf-companion/settings.ini` | keys, binds, tick, `ksf_servers` (private KSF servers, `ip:port` each), `game_dir`, `rcon_port`, `window_frame`, ... (each one explained in the file) |
| `~/.config/ksf-companion/play-later.txt` | your saved maps - edit it in any text editor |
| `~/.cache/ksf-companion/` | map pictures, records and the map list from ksf.surf |
| in `cstrike/` | a block in `cfg/autoexec.cfg`, `cfg/ksf_*.cfg`, `ksf_console.log` and, on KSF servers, `ksfc_live.dem` |

## Something's not working?

- `ksf-companion --status` shows what it finds: Steam, CS:S, your account, whether `-usercon` is set, and whether the
  running game answers.
- CS:S in an unusual place: set `game_dir` in settings.ini to your `.../Counter-Strike Source/cstrike` folder.
- Something else uses port 27015: change `rcon_port` in settings.ini, then restart CS:S.
- Rather have your desktop's title bar on the dashboard: `window_frame = system`.
- Errors go to `~/.config/ksf-companion/errors.log`.

## Update or remove

**Update** (your settings stay):

- Flake config (**A**): `cd /etc/nixos && sudo nix flake update ksf-companion`, then
  `sudo nixos-rebuild switch --flake /etc/nixos`.
- Plain config (**B**): every `sudo nixos-rebuild switch` gets the newest version once the last download is an hour
  old; `sudo nixos-rebuild switch --option tarball-ttl 0` gets it right away.
- The download: extract the new one over the old folder.

**Remove:** close CS:S, then tray icon → **Remove from CS:S...** - that takes its files out of the game and puts your
old key binds back. Then take the lines you added out of your NixOS config and rebuild (or run `install.sh --remove`
and delete the folder).

## Build it yourself

```sh
nix develop                         # .NET 10 SDK and the libraries it loads
cd KSFCompanion
dotnet build
./bin/Debug/net10.0/ksf-companion --selftest
./bin/Debug/net10.0/ksf-companion --preview sample dashboard.png 1600 1080   # made-up data, no game or network needed
nix flake check                     # the package, its tests, the NixOS module, and a desktop in a VM (needs KVM)
```

After changing NuGet packages, regenerate `nix/deps.json`: `nix build .#ksf-companion.fetch-deps && ./result nix/deps.json`.
The **Screenshots** workflow (Actions tab) draws the dashboard from ksf.surf's live data on a fresh machine and keeps
the pictures - a quick check that it still reads ksf.surf right.

---

<sub>Not affiliated with KSF. Map data and pictures come from ksf.surf. · Fonts: Barlow and JetBrains Mono (SIL OFL),
Inter (SIL OFL) · Icons: Lucide (ISC).</sub>
