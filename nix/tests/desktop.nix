# A NixOS desktop (Xfce) that logs in by itself: KSF Companion installed by the NixOS module and started by the Home
# Manager service, Steam's files for one account, and a stand-in for CS:S started with -usercon. Checks that it starts
# with the session, puts its icon in the tray, takes the game's console, and opens its dashboard when it's started
# again. Part of `nix flake check` (it needs KVM).
{ self }:
let
  # Home Manager, for its NixOS module (only this test uses it).
  home-manager = builtins.fetchTarball {
    url = "https://github.com/nix-community/home-manager/archive/0560d64401a6d5370732a69d08bc4a6b135962d9.tar.gz";
    sha256 = "0kbhig23f1c7fnjs7ak14vxd1hf2vf55zib09s8kpws3559hli0h";
  };
  steam = ".local/share/Steam";
  cstrike = "${steam}/steamapps/common/Counter-Strike Source/cstrike";
in
{
  name = "ksf-companion-desktop";

  nodes.machine =
    { pkgs, ... }:
    {
      imports = [
        self.nixosModules.default
        "${home-manager}/nixos"
      ];

      users.users.alice = {
        isNormalUser = true;
        uid = 1000;
        password = "alice";
      };

      services.xserver.enable = true;
      services.xserver.displayManager.lightdm.enable = true;
      services.xserver.desktopManager.xfce.enable = true;
      services.displayManager.autoLogin = {
        enable = true;
        user = "alice";
      };
      virtualisation.memorySize = 2048;

      programs.ksf-companion.enable = true;
      environment.systemPackages = [
        (pkgs.writeScriptBin "cstrike_linux64" ("#!${pkgs.python3}/bin/python3\n" + builtins.readFile ./fake-cstrike.py))
      ];

      home-manager.useGlobalPkgs = true;
      home-manager.users.alice = {
        imports = [ self.homeManagerModules.default ];
        programs.ksf-companion = {
          enable = true;
          autostart = true;
        };
        home.stateVersion = "25.11";

        # Steam with CS:S, an account (STEAM_0:0:1000) and its launch options.
        home.file."${steam}/steamapps/libraryfolders.vdf".text = ''
          "libraryfolders"
          {
            "0"
            {
              "path"    "/home/alice/${steam}"
              "apps"    { "240"    "4520000000" }
            }
          }
        '';
        home.file."${steam}/config/loginusers.vdf".text = ''
          "users"
          {
            "76561197960267728"
            {
              "AccountName"    "alice"
              "PersonaName"    "alice"
              "MostRecent"    "1"
            }
          }
        '';
        home.file."${steam}/userdata/2000/config/localconfig.vdf".text = ''
          "UserLocalConfigStore"
          {
            "Software" { "Valve" { "Steam" { "apps" { "240" { "LaunchOptions"    "-usercon -novid" } } } } }
          }
        '';
        home.file."${cstrike}/cfg/config.cfg".text = "// the game's own config\n";
      };
    };

  testScript = ''
    import re
    import shlex

    cstrike = "/home/alice/${cstrike}"

    def as_alice(command):
        env = "DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus"
        return "su - alice -c " + shlex.quote(f"{env} {command}")

    machine.wait_for_x()
    machine.wait_for_file("/home/alice/.Xauthority")
    machine.succeed("xauth merge /home/alice/.Xauthority")
    machine.wait_for_window("xfce4-panel")

    with subtest("starts with the session, from the Home Manager service"):
        machine.wait_until_succeeds(as_alice("systemctl --user is-active ksf-companion.service"))

    with subtest("finds CS:S through Steam and sets itself up in it"):
        machine.wait_for_file(shlex.quote(f"{cstrike}/cfg/autoexec.cfg"))
        machine.succeed(f"grep -q 'exec ksf_companion' {shlex.quote(cstrike)}/cfg/autoexec.cfg")
        status = machine.succeed(as_alice("ksf-companion --status"))
        print(status)
        assert re.search(r"^-usercon\s+yes", status, re.M), "CS:S's launch options weren't read"

    with subtest("puts its icon in the tray"):
        machine.wait_until_succeeds(as_alice(
            "busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher "
            "org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems | grep -q StatusNotifierItem"))

    with subtest("takes the game's console"):
        machine.succeed(as_alice(f"systemd-run --user --unit=fake-cstrike cstrike_linux64 {shlex.quote(cstrike)}"))
        machine.wait_until_succeeds(f"grep -q '^exec ksf_companion$' {shlex.quote(cstrike)}/rcon-commands.txt")
        machine.wait_until_succeeds(f"grep -q '^status$' {shlex.quote(cstrike)}/rcon-commands.txt")
        machine.wait_until_succeeds(as_alice("ksf-companion --status | grep -q 'connected (port 27015)'"))

    with subtest("opens the dashboard when it's started again"):
        machine.succeed(as_alice("ksf-companion"))
        machine.wait_for_window("KSF Companion")
        machine.sleep(3)
        machine.screenshot("dashboard")

    with subtest("no errors"):
        machine.fail("test -s /home/alice/.config/ksf-companion/errors.log")
        machine.succeed(as_alice("systemctl --user is-active ksf-companion.service"))
  '';
}
