# KSF Companion for Linux

A second-monitor dashboard for **KSF surf** in **Counter-Strike: Source** - on Linux, packaged for **NixOS**.

This is a Linux port of [KSF Companion](https://github.com/Shamshoo/ksf-companion) (Windows): the same dashboard,
nominate page, binds page and in-game keys, running in your system tray.

## Install

### NixOS

Add the flake and turn it on:

```nix
# flake.nix
{
  inputs.ksf-companion.url = "github:voms/ksftool";

  outputs = { nixpkgs, ksf-companion, ... }: {
    nixosConfigurations.my-pc = nixpkgs.lib.nixosSystem {
      modules = [
        ./configuration.nix
        ksf-companion.nixosModules.default
        {
          programs.ksf-companion.enable = true;
          # Optional: start it in the tray when you log in (anyone can still turn that off in its tray menu).
          programs.ksf-companion.autostart = true;
        }
      ];
    };
  };
}
```

### Home Manager

```nix
imports = [ ksf-companion.homeManagerModules.default ];

programs.ksf-companion = {
  enable = true;
  autostart = true; # optional: a systemd user service in your graphical session
};
```

### Just try it

```sh
nix run github:voms/ksftool
```

`nix profile install github:voms/ksftool` keeps it. There's also `overlays.default`, which adds `pkgs.ksf-companion`.

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

- It finds CS:S and your Steam account by itself: Steam from your distro or NixOS (`programs.steam`), the Flatpak or
  the Snap, and every Steam library folder. It's made for the native Linux CS:S (the Windows one under Proton should
  work too).
- No sign-in, no account. It never asks for your Steam password.
- **Why `-usercon`:** on Windows the app hands its console commands (`status`, `mp_timelimit`, teleports, nominate,
  ...) to the game's window; Linux has nothing like that, so it sends them over the game's own remote console, which
  CS:S only opens with `-usercon`. Until it's there the dashboard shows a reminder with a button that copies it for you. Everything that only
  reads (the dashboard, F5 / F6 / F7) works without it.

## The tray icon

It lives in your system tray. KDE Plasma, Cinnamon, XFCE, MATE, LXQt and Budgie show it out of the box. On **GNOME**
add the AppIndicator extension (`pkgs.gnomeExtensions.appindicator` on NixOS) and turn it on in Extensions; on Sway,
Hyprland and friends you need a bar with a tray (Waybar's `tray` module). Without a tray, start KSF Companion again from
your app menu to bring the dashboard back.

It runs on X11 and on Wayland desktops (through XWayland).

## What it does

### Dashboard
![Dashboard](docs/dashboard-boreas.png)

- **Live times:** your stage and bonus times appear as soon as you finish, with the gap to the record.
- **Map info:** see the map you're on, its tier, the top 10, and the time left (including extends).
- **Your rank:** see your KSF title, rank and points on 66 and 100 tick.
- **Servers:** see every KSF server, who's on, and the map, and join in one click.
- **Play later:** press F5 in game to save a map for later.

### Nominate
![Nominate](docs/nominate-boreas.png)

- Search every KSF map (typos are OK), filter by tier, type, or done / not done.
- Nominate or rock the vote in one click.

### Binds
![Binds](docs/binds-boreas.png)

- Put restart, restart stage, save/load location and turn binds on any key.
- Your binds already in the game show up here. Nothing shows in chat.

<sub>Screenshots from the Windows version; the Linux one looks the same.</sub>

Everything in detail: [KSFCompanion/README.txt](KSFCompanion/README.txt).

## Is it safe?

**VAC:** ✅ Safe. It never touches the game's memory; it only uses config files and console commands, like a normal bind.

**Your Steam account:** 🔒 Never touched. It never asks for, reads or stores your Steam password or login. It only looks
up your public Steam ID (the one on your profile page) so it can show your own KSF times.

**The remote console:** `-usercon` makes CS:S accept console commands on TCP port 27015 while it runs, from anyone who
has the password. KSF Companion makes up a random 24-character one, keeps it in its settings file (readable only by
you) and puts it in your `autoexec.cfg`. NixOS's firewall keeps that port closed to other computers (unless you've
opened it, e.g. with `programs.steam.dedicatedServer.openFirewall`); on other distros, don't open or forward TCP 27015
unless you're hosting a server. Another port: `rcon_port` in settings.ini.

## Where things are

| | |
|---|---|
| `~/.config/ksf-companion/settings.ini` | keys, binds, tick, `game_dir`, `rcon_port`, `window_frame`, ... (each one explained in the file) |
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

- **Update:** `nix flake update ksf-companion` and rebuild (or `nix profile upgrade`). With the download, extract the
  new one over the old folder. Your settings stay.
- **Remove:** close CS:S, then tray icon → **Remove from CS:S...** - that takes its files out of the game and puts
  your old key binds back. Then take it out of your Nix config (or run `install.sh --remove` and delete the folder).

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
