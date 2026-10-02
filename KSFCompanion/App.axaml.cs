using System;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace KsfCompanion
{
    /// <summary>The Avalonia application: the dark theme and the app's styles (Ui/Theme.axaml).</summary>
    public partial class App : Application
    {
        /// <summary>Raised once the app is up (not for --preview renders, which only borrow the theme).</summary>
        public static event Action Started;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Started?.Invoke();
        }
    }
}
