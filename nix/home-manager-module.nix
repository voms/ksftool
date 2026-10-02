{
  config,
  lib,
  pkgs,
  ...
}:

let
  cfg = config.programs.ksf-companion;
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
        Start KSF Companion in the tray with your desktop session (a systemd user service that is part of
        graphical-session.target). Its "Start when I log in" tray item then shows that this is set here.
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      (lib.hm.assertions.assertPlatform "programs.ksf-companion" pkgs lib.platforms.linux)
    ];

    home.packages = [ cfg.package ];

    systemd.user.services.ksf-companion = lib.mkIf cfg.autostart {
      Unit = {
        Description = "KSF Companion";
        Documentation = [ "https://github.com/voms/ksftool" ];
        PartOf = [ "graphical-session.target" ];
        # Not Requires: tray.target only exists when Home Manager runs the session, and the icon shows up
        # whenever a tray does.
        After = [
          "graphical-session.target"
          "tray.target"
        ];
      };
      Service = {
        ExecStart = "${lib.getExe cfg.package} --background";
        Environment = [ "KSFC_AUTOSTART=systemd" ];
        Restart = "on-failure";
        RestartSec = 5;
      };
      Install.WantedBy = [ "graphical-session.target" ];
    };
  };
}
