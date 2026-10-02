{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  copyDesktopItems,
  makeDesktopItem,

  libGL,
  libxcursor,
  libxext,
  libxi,
  libxrandr,

  libnotify,
  xdg-utils,
}:

buildDotnetModule (finalAttrs: {
  pname = "ksf-companion";
  # The one in the project file, so there is only one place to bump it.
  version = lib.head (
    builtins.match ".*<Version>([^<]+)</Version>.*" (builtins.readFile ../KSFCompanion/KSFCompanion.csproj)
  );

  src = lib.fileset.toSource {
    root = ../KSFCompanion;
    fileset = lib.fileset.difference ../KSFCompanion (
      lib.fileset.unions [
        (lib.fileset.maybeMissing ../KSFCompanion/bin)
        (lib.fileset.maybeMissing ../KSFCompanion/obj)
      ]
    );
  };

  projectFile = "KSFCompanion.csproj";
  # Regenerate after changing the project's packages: nix build .#ksf-companion.fetch-deps && ./result nix/deps.json
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;

  executables = [ "ksf-companion" ];

  nativeBuildInputs = [ copyDesktopItems ];

  # Loaded at run time: GL for drawing with the GPU (without it Avalonia draws in software), the rest for the
  # pointer, input and screens. (libX11, libICE, libSM and fontconfig come with the Avalonia and Skia packages.)
  runtimeDeps = [
    libGL
    libxcursor
    libxext
    libxi
    libxrandr
  ];

  makeWrapperArgs = [
    # notify-send for its pop-ups and xdg-open for links, folders and steam:// - the ones on your PATH come first.
    "--suffix"
    "PATH"
    ":"
    (lib.makeBinPath [
      libnotify
      xdg-utils
    ])
    # What "Start when I log in" runs, when ksf-companion isn't on the PATH (nix run).
    "--set-default"
    "KSFC_LAUNCHER"
    "${placeholder "out"}/bin/ksf-companion"
  ];

  desktopItems = [
    (makeDesktopItem {
      name = "ksf-companion";
      desktopName = "KSF Companion";
      genericName = "Surf dashboard";
      comment = finalAttrs.meta.description;
      exec = "ksf-companion";
      icon = "ksf-companion";
      startupWMClass = "ksf-companion";
      categories = [ "Game" ];
      keywords = [
        "ksf"
        "surf"
        "counter-strike"
        "css"
      ];
    })
  ];

  postInstall = ''
    for size in 16 20 24 32 40 48 64 128 256; do
      install -Dm644 Ui/Assets/icon-$size.png $out/share/icons/hicolor/''${size}x$size/apps/ksf-companion.png
    done
    install -Dm644 Ui/Assets/icon.svg $out/share/icons/hicolor/scalable/apps/ksf-companion.svg
  '';

  meta = {
    description = "KSF surf dashboard for your second monitor, plus in-game map info and a play-later list for Counter-Strike: Source";
    homepage = "https://github.com/voms/ksftool";
    license = with lib.licenses; [
      mit
      # the Barlow and JetBrains Mono fonts it draws with
      ofl
      # the Lucide icons
      isc
    ];
    mainProgram = "ksf-companion";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
})
