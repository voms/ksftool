using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace KsfCompanion.Ui
{
    /// <summary>A small message in the app's own style (Avalonia has no message box): OK, or OK / Cancel.</summary>
    static class MessageDialog
    {
        /// <summary>Shows the message over the dashboard when it's open. True when the first button was chosen.</summary>
        public static Task<bool> ShowAsync(Window owner, string message, string ok = "OK", string cancel = null)
        {
            var done = new TaskCompletionSource<bool>();
            var result = false;
            var resources = Application.Current;
            T Resource<T>(string key) where T : class => resources?.FindResource(key) as T;

            var dialog = new Window
            {
                Title = Program.AppName,
                Icon = AppIcon.Get(),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                ShowInTaskbar = owner == null || !owner.IsVisible,
                WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = Resource<IBrush>("Bg1Brush"),
                Foreground = Resource<IBrush>("Text0Brush"),
                FontSize = 13,
            };

            var okButton = new Button { Content = ok, Theme = Resource<ControlTheme>("AccentButton"), Padding = new Thickness(18, 7), IsDefault = true };
            okButton.Click += (s, e) =>
            {
                result = true;
                dialog.Close();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
            if (cancel != null)
            {
                var cancelButton = new Button { Content = cancel, Theme = Resource<ControlTheme>("GhostButton"), Padding = new Thickness(16, 7), IsCancel = true };
                cancelButton.Click += (s, e) => dialog.Close();
                buttons.Children.Add(cancelButton);
            }
            buttons.Children.Add(okButton);

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(22, 20, 22, 18),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19 },
                    buttons,
                },
            };
            dialog.Closed += (s, e) => done.TrySetResult(result);
            if (owner != null && owner.IsVisible) _ = dialog.ShowDialog(owner);
            else dialog.Show();
            return done.Task;
        }
    }
}
