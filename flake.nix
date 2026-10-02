{
  description = "KSF Companion for Linux: a KSF surf dashboard for your second monitor, plus in-game map info and a play-later list for Counter-Strike: Source";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      inherit (nixpkgs) lib;
      # CS:S only runs on x86-64, and KSF Companion runs next to it.
      systems = [ "x86_64-linux" ];
      forAllSystems = f: lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});

      # The modules install this flake's build unless you set programs.ksf-companion.package.
      withPackage =
        module:
        { pkgs, ... }:
        {
          imports = [ module ];
          programs.ksf-companion.package = lib.mkDefault self.packages.${pkgs.stdenv.hostPlatform.system}.default;
        };
    in
    {
      packages = forAllSystems (pkgs: rec {
        ksf-companion = pkgs.callPackage ./nix/package.nix { };
        default = ksf-companion;
      });

      overlays.default = final: prev: { ksf-companion = final.callPackage ./nix/package.nix { }; };

      nixosModules.default = withPackage ./nix/nixos-module.nix;
      homeManagerModules.default = withPackage ./nix/home-manager-module.nix;

      checks = forAllSystems (
        pkgs:
        let
          package = self.packages.${pkgs.stdenv.hostPlatform.system}.default;
          nixos =
            (lib.nixosSystem {
              modules = [
                self.nixosModules.default
                {
                  nixpkgs.hostPlatform = pkgs.stdenv.hostPlatform.system;
                  programs.ksf-companion = {
                    enable = true;
                    autostart = true;
                  };
                  boot.loader.grub.enable = false;
                  fileSystems."/" = {
                    device = "none";
                    fsType = "tmpfs";
                  };
                  system.stateVersion = lib.trivial.release;
                }
              ];
            }).config;
        in
        {
          inherit package;

          # The app's own tests: the map clock, the binds page, and the Linux parts (Steam's files, the game's
          # remote console, finding the game and its demo, autostart, one copy at a time, drawing the dashboard).
          tests = pkgs.runCommand "ksf-companion-tests" { FONTCONFIG_FILE = pkgs.makeFontsConf { fontDirectories = [ ]; }; } ''
            export HOME=$TMPDIR KSFC_DATA_DIR=$TMPDIR/data
            ${lib.getExe package} --clock-test
            ${lib.getExe package} --binds-test
            ${lib.getExe package} --selftest
            touch $out
          '';

          # A whole desktop in a VM: the Home Manager service starts it, the tray shows it, a stand-in CS:S takes its
          # commands. Needs KVM.
          desktop = pkgs.testers.runNixOSTest (import ./nix/tests/desktop.nix { inherit self; });

          # The NixOS module installs it and adds the autostart entry.
          nixos-module =
            assert lib.elem package nixos.environment.systemPackages;
            pkgs.runCommand "ksf-companion-nixos-module" { } ''
              grep -q '^Exec=${lib.getExe package} --background$' ${
                nixos.environment.etc."xdg/autostart/ksf-companion.desktop".source
              }
              touch $out
            '';
        }
      );

      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          packages = [ pkgs.dotnetCorePackages.sdk_10_0 ];
          # So the built ksf-companion finds .NET.
          DOTNET_ROOT = "${pkgs.dotnetCorePackages.sdk_10_0}/share/dotnet";
          # What the app loads at run time (dotnet run, the preview and the self-test).
          LD_LIBRARY_PATH = lib.makeLibraryPath (
            with pkgs;
            [
              fontconfig
              libGL
              libice
              libsm
              libx11
              libxcursor
              libxext
              libxi
              libxrandr
            ]
          );
          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          AVALONIA_TELEMETRY_OPTOUT = "1";
        };
      });
    };
}
