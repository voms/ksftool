{
  config,
  lib,
  pkgs,
  ...
}:

let
  cfg = config.programs.ksf-companion;

  autostartEntry = pkgs.makeDesktopItem {
    name = "ksf-companion";
    desktopName = "KSF Companion";
    comment = "KSF surf dashboard for Counter-Strike: Source, started in the tray";
    exec = "${lib.getExe cfg.package} --background";
    icon = "ksf-companion";
    extraConfig."X-GNOME-Autostart-enabled" = "true";
  };
in
{
  options.programs.ksf-companion = {
    enable = lib.mkEnableOption "KSF Companion, the KSF surf dashboard and in-game helper for Counter-Strike: Source";

    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.ksf-companion or (pkgs.callPackage ./package.nix { });
      defaultText = lib.literalExpression "ksf-companion.packages.\${pkgs.stdenv.hostPlatform.system}.default";
      description = "The KSF Companion package to use.";
    };

    autostart = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Start KSF Companion in the tray when anyone logs in to a desktop session (an XDG autostart entry).
        Each user can still turn it off for themselves with "Start when I log in" in its tray menu.
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];

    environment.etc."xdg/autostart/ksf-companion.desktop" = lib.mkIf cfg.autostart {
      source = "${autostartEntry}/share/applications/ksf-companion.desktop";
    };
  };
}
