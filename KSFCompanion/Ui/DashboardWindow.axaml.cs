using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KsfCompanion.Ui
{
    public partial class DashboardWindow : Window
    {
        readonly DashboardViewModel vm;
        readonly bool systemFrame;
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        // Where the window was while it wasn't maximized (what's saved, so it comes back the same).
        PixelPoint normalPosition;
        Size normalSize;

        /// <summary>For the XAML designer: an empty dashboard.</summary>
        public DashboardWindow() : this(new DashboardViewModel(), new KeyNames { Save = "F5", Card = "F6", List = "F7" })
        {
        }

        /// <param name="systemFrame">Use the desktop's own title bar and borders (window_frame = system) instead of the dashboard's.</param>
        internal DashboardWindow(DashboardViewModel vm, KeyNames keys, bool systemFrame = false)
        {
            this.vm = vm;
            this.systemFrame = systemFrame;
            InitializeComponent();
            DataContext = vm;
            SetKeys(keys);
            if (systemFrame)
            {
                SystemDecorations = SystemDecorations.Full;
                WindowButtons.IsVisible = false;
                Root.BorderThickness = new Thickness(0);
            }
            Grips.IsVisible = !systemFrame;

            // The binds page waits for the key you want: the next key, mouse button or wheel turn is it.
            AddHandler(KeyDownEvent, OnCaptureKey, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, OnCaptureMouse, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, OnCaptureWheel, RoutingStrategies.Tunnel);
            Deactivated += (s, e) => vm.Binds.CancelCapture();

            // Drawn without the desktop's frame: the title bar moves the window, its edges resize it.
            TitleBar.PointerPressed += OnTitleBarPressed;
            foreach (var grip in Grips.Children.OfType<Border>()) grip.PointerPressed += OnGripPressed;
            KeepOnTopMenu.Click += (s, e) => SetTopmost(!Topmost);

            // The window can be opened after the map (and its colours) arrived.
            Ambient.Opacity = vm.AmbientImage != null ? 1 : 0;
            HeroContent.RenderTransform = new TranslateTransform();
            CelebrationContent.RenderTransform = new ScaleTransform();
            vm.PropertyChanged += OnViewModelChanged;
            SizeChanged += (s, e) =>
            {
                Relayout(e.NewSize.Width);
                RememberNormal();
            };
            // Showing, hiding or resizing parts lays the dashboard out again.
            vm.Layout.PropertyChanged += (s, e) => Relayout(Bounds.Width);
            PositionChanged += (s, e) =>
            {
                RememberNormal();
                PlacementChanged?.Invoke();
            };
            PropertyChanged += (s, e) =>
            {
                if (e.Property == WindowStateProperty) OnStateChanged();
            };
            toastTimer.Tick += (s, e) =>
            {
                toastTimer.Stop();
                ToastHost.Opacity = 0;
            };
        }

        /// <summary>Raised when the window is closed or moved so the owner can remember where it was.</summary>
        internal event Action PlacementChanged;
        internal event Action<bool> TopmostChanged;

        internal bool AllowClose { get; set; }

        void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DashboardViewModel.MapName):
                    Run(HeroContent, 380, (OpacityProperty, 0.0, 1.0));
                    Run(HeroContent, 420, (TranslateTransform.YProperty, 12.0, 0.0));
                    break;
                case nameof(DashboardViewModel.MapImage):
                    if (vm.MapImage != null) Run(HeroImage, 650, (OpacityProperty, 0.0, 1.0));
                    break;
                case nameof(DashboardViewModel.AmbientImage):
                    Ambient.Opacity = vm.AmbientImage != null ? 1 : 0;
                    Run(Ambient, 1400, (OpacityProperty, 0.0, Ambient.Opacity));
                    break;
                case nameof(DashboardViewModel.CelebrationId):
                    Celebrate();
                    break;
                case nameof(DashboardViewModel.HasStagesB):
                case nameof(DashboardViewModel.HasBonusesB):
                    Relayout(Bounds.Width);
                    break;
                case nameof(DashboardViewModel.Toast):
                    if (string.IsNullOrEmpty(vm.Toast)) break;
                    ToastText.Text = vm.Toast;
                    ToastHost.Opacity = 1;
                    toastTimer.Stop();
                    toastTimer.Start();
                    break;
            }
        }

        /// <summary>
        /// Animates properties from one value to another (eased out). The property keeps whatever value it had set when
        /// the animation is over, so it ends where it should be set to.
        /// </summary>
        static void Run(Animatable target, int ms, params (AvaloniaProperty Property, double From, double To)[] values)
        {
            var from = new KeyFrame { Cue = new Cue(0) };
            var to = new KeyFrame { Cue = new Cue(1) };
            foreach (var (property, start, end) in values)
            {
                from.Setters.Add(new Setter(property, start));
                to.Setters.Add(new Setter(property, end));
            }
            _ = new Animation { Duration = TimeSpan.FromMilliseconds(ms), Easing = new CubicEaseOut(), Children = { from, to } }.RunAsync(target);
        }

        /// <summary>Pops the PB card over the map picture: in with a little overshoot, holds, then fades away.</summary>
        void Celebrate()
        {
            Celebration.IsVisible = true;
            Celebration.Opacity = 0;
            // The map name and buttons step aside meanwhile so the two texts don't overlap.
            _ = InHoldOut(HeroContent, 1, 0);
            var shown = InHoldOut(Celebration, 0, 1);
            _ = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(520),
                Easing = new BackEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.82), new Setter(ScaleTransform.ScaleYProperty, 0.82) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
                },
            }.RunAsync(CelebrationContent);
            shown.ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (Celebration.Opacity < 0.01) Celebration.IsVisible = false; }));
        }

        /// <summary>Goes from one opacity to the other in a quarter second, stays there about four seconds, then eases back.</summary>
        static System.Threading.Tasks.Task InHoldOut(Visual target, double from, double to)
        {
            KeyFrame At(double ms, double value) => new KeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Setters = { new Setter(OpacityProperty, value) } };
            return new Animation { Duration = TimeSpan.FromMilliseconds(4900), Children = { At(0, from), At(260, to), At(4200, to), At(4900, from) } }.RunAsync(target);
        }

        /// <summary>For --preview: the PB card fully shown, no animation.</summary>
        internal void ShowCelebrationStill()
        {
            Celebration.IsVisible = true;
            Celebration.Opacity = 1;
            HeroContent.Opacity = 0;
        }

        /// <summary>
        /// Two columns when there is room and both sides have something on show, one scrolling column otherwise. On a
        /// big screen everything is scaled up (it's laid out for about 1500 px across).
        /// </summary>
        internal void Relayout(double width)
        {
            if (!(width > 0)) return;
            // The title bar isn't scaled: it has the window's own width.
            PlayerChip.IsVisible = width >= 1200;
            // Size (Customize): everything on the page drawn bigger or smaller.
            Zoom(NominateZoom, vm.Layout.Scale);
            var zoom = vm.Layout.Scale;
            Zoom(ColumnsZoom, zoom);
            width /= zoom;

            bool leftShown = vm.Layout.AnyShown(left: true), rightShown = vm.Layout.AnyShown(left: false);
            NothingShown.IsVisible = !leftShown && !rightShown;
            var wide = width >= 1060 && leftShown && rightShown;
            Columns.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(RightPanel, wide ? 1 : 0);
            Grid.SetRow(RightPanel, wide ? 0 : 1);
            RightPanel.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0);
            // A single column on a very wide window stays a readable width, in the middle.
            const double readable = 1560;
            Columns.MaxWidth = wide ? double.PositiveInfinity : readable;
            if (!wide) width = Math.Min(width, readable + 40);

            var leftWidth = wide ? (width - 56) * 0.6 : width - 40;
            // Five number tiles: all in a row, or three and two, or two to a row.
            Tiles.Columns = leftWidth >= 900 ? 5 : leftWidth >= 560 ? 3 : 2;
            // Stages and bonuses side by side (S1-S8 | S9-S16) when there's room, otherwise one list each.
            var room = leftWidth >= 700;
            SideBySide(StagesGrid, StagesRight, room && vm.HasStagesB);
            SideBySide(BonusesGrid, BonusesRight, room && vm.HasBonusesB);
            Hero.Height = leftWidth >= 680 ? 300 : 230;
            MapTitle.FontSize = leftWidth >= 800 ? 46 : leftWidth >= 600 ? 36 : 28;
        }

        static void Zoom(LayoutTransformControl element, double zoom) =>
            element.LayoutTransform = Math.Abs(zoom - 1) > 0.01 ? new ScaleTransform(zoom, zoom) : null;

        /// <summary>The second half of a list next to the first (in its own column) or under it.</summary>
        static void SideBySide(Grid grid, Control secondHalf, bool sideBySide)
        {
            grid.ColumnDefinitions[1].Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(secondHalf, sideBySide ? 1 : 0);
            Grid.SetRow(secondHalf, sideBySide ? 0 : 1);
            secondHalf.Margin = sideBySide ? new Thickness(28, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        }

        void OnStateChanged()
        {
            var maximized = WindowState == WindowState.Maximized;
            MaxIcon.Data = (Geometry)this.FindResource(maximized ? "IconRestore" : "IconMaximize");
            // A maximized window has no edges to drag, and no border of its own around the screen.
            Grips.IsVisible = !systemFrame && WindowState == WindowState.Normal;
            Root.BorderThickness = new Thickness(maximized || systemFrame ? 0 : 1);
            PlacementChanged?.Invoke();
        }

        /// <summary>KSF Companion's own keys, shown at the bottom of the dashboard.</summary>
        internal void SetKeys(KeyNames keys)
        {
            SaveKey.Text = GameKeys.Label(keys.Save);
            CardKey.Text = GameKeys.Label(keys.Card);
            ListKey.Text = GameKeys.Label(keys.List);
        }

        void OnTitleBarPressed(object sender, PointerPressedEventArgs e)
        {
            if (systemFrame || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount == 2) OnMaximize(sender, e);
            else BeginMoveDrag(e);
        }

        void OnGripPressed(object sender, PointerPressedEventArgs e)
        {
            if (!(sender is Border grip) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            BeginResizeDrag((grip.Tag as string) switch
            {
                "N" => WindowEdge.North,
                "S" => WindowEdge.South,
                "W" => WindowEdge.West,
                "E" => WindowEdge.East,
                "NW" => WindowEdge.NorthWest,
                "NE" => WindowEdge.NorthEast,
                "SW" => WindowEdge.SouthWest,
                _ => WindowEdge.SouthEast,
            }, e);
        }

        void OnCaptureKey(object sender, KeyEventArgs e)
        {
            if (!vm.Binds.IsCapturing) return;
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                vm.Binds.CancelCapture();
                return;
            }
            var name = GameKeyOf(e.Key, e.PhysicalKey);
            if (name == null)
            {
                vm.Toast = e.Key == Key.OemTilde || e.PhysicalKey == PhysicalKey.Backquote ? "That's the console key - pick another" : "That key can't be bound - pick another";
                vm.Binds.CancelCapture();
                return;
            }
            vm.Binds.Capture(name);
        }

        void OnCaptureMouse(object sender, PointerPressedEventArgs e)
        {
            if (!vm.Binds.IsCapturing) return;
            string name = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.RightButtonPressed => "MOUSE2",
                PointerUpdateKind.MiddleButtonPressed => "MOUSE3",
                PointerUpdateKind.XButton1Pressed => "MOUSE4",
                PointerUpdateKind.XButton2Pressed => "MOUSE5",
                _ => null,
            };
            if (name != null)
            {
                e.Handled = true;
                vm.Binds.Capture(name);
                return;
            }
            // A left click stops waiting; on the waiting row's own key button that's all it does.
            var source = e.Source as Visual;
            var row = (source as StyledElement)?.DataContext as BindRow;
            var onWaitingButton = row != null && row.IsCapturing && source.GetSelfAndVisualAncestors().OfType<Button>().Any();
            vm.Binds.CancelCapture();
            if (onWaitingButton) e.Handled = true;
        }

        void OnCaptureWheel(object sender, PointerWheelEventArgs e)
        {
            if (!vm.Binds.IsCapturing || e.Delta.Y == 0) return;
            e.Handled = true;
            vm.Binds.Capture(e.Delta.Y > 0 ? "MWHEELUP" : "MWHEELDOWN");
        }

        static readonly string[] NumPad = { "KP_INS", "KP_END", "KP_DOWNARROW", "KP_PGDN", "KP_LEFTARROW", "KP_5", "KP_RIGHTARROW", "KP_HOME", "KP_UPARROW", "KP_PGUP" };

        /// <summary>
        /// The Source engine's name for a key ("r", "SHIFT", "KP_END"), or null for keys that can't be bound here. The
        /// number pad goes by where the key is (with Num Lock off it sends the same keys as the arrows, Home, End...).
        /// </summary>
        static string GameKeyOf(Key key, PhysicalKey physical)
        {
            if (physical >= PhysicalKey.NumPad0 && physical <= PhysicalKey.NumPad9) return NumPad[physical - PhysicalKey.NumPad0];
            switch (physical)
            {
                case PhysicalKey.NumPadDecimal: return "KP_DEL";
                case PhysicalKey.NumPadEnter: return "KP_ENTER";
                case PhysicalKey.NumPadDivide: return "KP_SLASH";
                case PhysicalKey.NumPadMultiply: return "KP_MULTIPLY";
                case PhysicalKey.NumPadSubtract: return "KP_MINUS";
                case PhysicalKey.NumPadAdd: return "KP_PLUS";
            }
            if (key >= Key.A && key <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.F1 && key <= Key.F12) return "F" + (key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture);
            switch (key)
            {
                case Key.Space: return "SPACE";
                case Key.Tab: return "TAB";
                case Key.Enter: return "ENTER";
                case Key.Back: return "BACKSPACE";
                case Key.CapsLock: return "CAPSLOCK";
                case Key.Insert: return "INS";
                case Key.Delete: return "DEL";
                case Key.Home: return "HOME";
                case Key.End: return "END";
                case Key.PageUp: return "PGUP";
                case Key.PageDown: return "PGDN";
                case Key.Up: return "UPARROW";
                case Key.Down: return "DOWNARROW";
                case Key.Left: return "LEFTARROW";
                case Key.Right: return "RIGHTARROW";
                case Key.Clear: return "KP_5";
                case Key.LeftShift: return "SHIFT";
                case Key.RightShift: return "RSHIFT";
                case Key.LeftCtrl: return "CTRL";
                case Key.RightCtrl: return "RCTRL";
                case Key.LeftAlt: return "ALT";
                case Key.RightAlt: return "RALT";
                case Key.Pause: return "PAUSE";
                case Key.Scroll: return "SCROLLLOCK";
                case Key.NumLock: return "NUMLOCK";
                case Key.OemSemicolon: return "SEMICOLON";
                case Key.OemQuotes: return "'";
                case Key.OemComma: return ",";
                case Key.OemMinus: return "-";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.OemPlus: return "=";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
            }
            // Punctuation another keyboard layout puts its own letters on: by where the key is.
            switch (physical)
            {
                case PhysicalKey.Semicolon: return "SEMICOLON";
                case PhysicalKey.Quote: return "'";
                case PhysicalKey.Comma: return ",";
                case PhysicalKey.Minus: return "-";
                case PhysicalKey.Period: return ".";
                case PhysicalKey.Slash: return "/";
                case PhysicalKey.Equal: return "=";
                case PhysicalKey.BracketLeft: return "[";
                case PhysicalKey.BracketRight: return "]";
            }
            return null;
        }

        internal void SetTopmost(bool on)
        {
            if (Topmost == on && KeepOnTopMenu.IsChecked == on) return;
            Topmost = on;
            KeepOnTopMenu.IsChecked = on;
            TopmostChanged?.Invoke(on);
        }

        void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        void OnClose(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            PlacementChanged?.Invoke();
            if (!AllowClose && e.CloseReason != WindowCloseReason.ApplicationShutdown && e.CloseReason != WindowCloseReason.OSShutdown)
            {
                // Closing only hides it; KSF Companion keeps running in the tray.
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        /// <summary>Shows the dashboard without taking focus from the game.</summary>
        internal void ShowWithoutFocus()
        {
            ShowActivated = false;
            Show();
        }

        void RememberNormal()
        {
            if (WindowState != WindowState.Normal || !IsVisible) return;
            normalPosition = Position;
            normalSize = ClientSize;
        }

        /// <summary>"left,top,width,height,maximized": the position in pixels, the size in the window's own units.</summary>
        internal string Placement => normalSize.Width <= 0 ? null
            : string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:0},{3:0},{4}", normalPosition.X, normalPosition.Y, normalSize.Width, normalSize.Height,
                WindowState == WindowState.Maximized ? 1 : 0);

        /// <summary>Puts the window where it was last time, or on the second monitor the first time.</summary>
        internal void ApplyPlacement(string saved)
        {
            var parts = (saved ?? "").Split(',');
            if (parts.Length == 5 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) &&
                Screens.All.Any(s => s.WorkingArea.Intersects(new PixelRect(left + 40, top + 10, 120, 40))))
            {
                Position = new PixelPoint(left, top);
                Width = Math.Max(MinWidth, width);
                Height = Math.Max(MinHeight, height);
                normalPosition = Position;
                normalSize = new Size(Width, Height);
                if (parts[4] == "1") WindowState = WindowState.Maximized;
                return;
            }

            // First run: the biggest monitor that isn't the one the game runs on.
            var screen = Screens.All.Where(s => !s.IsPrimary).OrderByDescending(s => s.Bounds.Width * s.Bounds.Height).FirstOrDefault()
                         ?? Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Width = Math.Min(1440, area.Width / scale * 0.9);
            Height = Math.Min(940, area.Height / scale * 0.9);
            Position = new PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
            normalPosition = Position;
            normalSize = new Size(Width, Height);
        }

        /// <summary>True when the window sits on a monitor other than the main one (where the game runs).</summary>
        internal bool IsOnSecondaryScreen
        {
            get
            {
                var scale = Screens.ScreenFromPoint(Position)?.Scaling ?? 1;
                var center = new PixelPoint(Position.X + (int)(Width * scale / 2), Position.Y + (int)(Height * scale / 2));
                var screen = Screens.ScreenFromPoint(center);
                return screen != null && !screen.IsPrimary;
            }
        }
    }
}
