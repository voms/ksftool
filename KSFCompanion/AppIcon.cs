using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace KsfCompanion
{
    /// <summary>
    /// The tray and window icon, made from the PNGs rendered from Ui/Assets/icon.svg (tray hosts scale it to their size).
    /// </summary>
    static class AppIcon
    {
        static WindowIcon icon;

        public static WindowIcon Get() => icon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ksf-companion/Ui/Assets/icon-128.png")));
    }
}
