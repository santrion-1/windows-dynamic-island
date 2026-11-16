using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Models;
using DynamicIsland.Services;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    public partial class MainWindow : Window
    {
        private enum IslandVisualMode
        {
            Compact,
            Expanded,
            BluetoothToast
        }

        // Persistent state & services
        private AppPersistentState _state;
        private readonly MediaAndBluetoothService _mediaService;
        private Forms.NotifyIcon? _trayIcon;

        // Current visual state & page
        private IslandVisualMode _visualMode = IslandVisualMode.Expanded;
        private int _currentPage = 0; // 0:Music, 1:Pomodoro, 2:Water, 3:Shelf, 4:Todo
        private bool _lyricsExpanded = false;
        private bool _todoEditMode = false;

        // Smooth Morph Animation state (0% CPU when not transitioning)
        private double _curW = 450;
        private double _curH = 180;
        private double _targetW = 450;
        private double _targetH = 180;
        private bool _isMorphing = false;

        // Timers
        private readonly DispatcherTimer _mainTickTimer;
        private readonly DispatcherTimer _waveformTimer;
        private readonly DispatcherTimer _collapseDebounceTimer;
        private readonly DispatcherTimer _toastDismissTimer;

        // Pomodoro state
        private bool _pomoRunning = false;
        private bool _pomoIsShortBreak = false;
        private int _pomoTotalSeconds = 25 * 60;
        private int _pomoRemainingSeconds = 25 * 60;

        // Water reminder countdown
        private DateTime _nextWaterReminderTime;

        // Drag out helper (Shelf)
        private System.Windows.Point _shelfDragStartPoint;
        private bool _isDraggingFromShelf = false;

        // Horizontal Window Dragging (Move Island Left/Right)
        private bool _isWindowDragCandidate = false;
        private bool _isWindowDragging = false;
        private double _dragStartScreenX = 0;
        private double _dragStartWindowLeft = 0;

        public MainWindow()
        {
            InitializeComponent();

            _state = AppStorage.Load();
            _currentPage = Math.Clamp(_state.LastPageIndex, 0, 5);
            _nextWaterReminderTime = DateTime.Now.AddMinutes(_state.Water.ReminderIntervalMinutes);

            _mediaService = new MediaAndBluetoothService();
            _mediaService.MediaUpdated += OnMediaUpdated;
            _mediaService.BluetoothConnected += OnBluetoothConnected;
            LyricsService.LyricsLoaded += OnLyricsLoaded;

            // Main 1-second timer for Clock, Battery, Pomodoro, Media timeline interpolation
            _mainTickTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0)
            };
            _mainTickTimer.Tick += MainTickTimer_Tick;

            // Lightweight waveform animation timer (runs only when visible & playing audio)
            _waveformTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _waveformTimer.Tick += WaveformTimer_Tick;

            // Auto-collapse debounce timer when mouse leaves the island
            _collapseDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _collapseDebounceTimer.Tick += CollapseDebounceTimer_Tick;

            // Toast auto-dismiss timer
            _toastDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.6)
            };
            _toastDismissTimer.Tick += ToastDismissTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure accidental previous pin state doesn't lock the window open on launch
            if (_state.IsPinned)
            {
                _state.IsPinned = false;
                AppStorage.Save(_state);
            }

            PositionWindowTopCenter();
            InitSystemTray();

            // Render initial geometries and UI data
            UpdateNotchGeometry(_curW, _curH);
            UpdatePinUI();
            UpdateClockAndBatteryUI();
            UpdateMusicUI(_mediaService.CurrentMedia);
            UpdatePomodoroUI();
            UpdateWaterUI();
            RebuildShelfUI();
            RebuildTodoUI();
            SwitchToPage(_currentPage, animate: false);

            _mediaService.Start();
            _mainTickTimer.Start();
            _waveformTimer.Start();

            var cmdArgs = Environment.GetCommandLineArgs();
            int idx = Array.IndexOf(cmdArgs, "--verify-renders");
            if (idx >= 0 && idx + 1 < cmdArgs.Length)
            {
                _ = ExportVerificationSnapshotsAsync(cmdArgs[idx + 1]);
            }
        }

        private async System.Threading.Tasks.Task ExportVerificationSnapshotsAsync(string folder)
        {
            try
            {
                _mainTickTimer.Stop();
                _collapseDebounceTimer.Stop();
                Directory.CreateDirectory(folder);
                await System.Threading.Tasks.Task.Delay(600);

                // 1. Music Normal
                _lyricsExpanded = false;
                SetVisualMode(IslandVisualMode.Expanded);
                SwitchToPage(0, false);
                _curW = _targetW; _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "1_music.png"));

                // 2. Music Lyrics Expanded
                LyricsToggleBtn_Click(this, new RoutedEventArgs());
                _curW = _targetW; _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "2_music_lyrics.png"));
                LyricsToggleBtn_Click(this, new RoutedEventArgs());

                // 3. Pomodoro
                SwitchToPage(1, false);
                _curW = _targetW; _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "3_pomodoro.png"));

                // 4. Water Ring
                _state.Water.SubViewIndex = 0;
                _state.Water.RemindersEnabled = false;
                UpdateWaterUI();
                SwitchToPage(2, false);
                _curW = _targetW; _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "4_water_ring.png"));

                // 5. Water Chart
                _state.Water.SubViewIndex = 1;
                _state.Water.RemindersEnabled = true;
                UpdateWaterUI();
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "5_water_chart.png"));
                _state.Water.SubViewIndex = 0;
                UpdateWaterUI();

                // 6. Shelf
                SwitchToPage(3, false);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "6_shelf.png"));

                // 7. TodoList
                SwitchToPage(4, false);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "7_todo.png"));

                // 9. Web Search
                SwitchToPage(5, false);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "9_web_search.png"));

                // 8. Bluetooth Toast
                ShowBluetoothToast("Bluetooth Audio", 80);
                _curW = _targetW; _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                await System.Threading.Tasks.Task.Delay(150);
                CaptureWindowToFile(Path.Combine(folder, "8_bt_toast.png"));

                Close();
            }
            catch { Close(); }
        }

        private void CaptureWindowToFile(string filePath)
        {
            UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 148, 214)), null, new Rect(0, 0, Width, Height));
                var vb = new VisualBrush(this);
                dc.DrawRectangle(vb, null, new Rect(0, 0, Width, Height));
            }
            rtb.Render(dv);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(filePath);
            encoder.Save(fs);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _mainTickTimer.Stop();
            _waveformTimer.Stop();
            _mediaService.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            AppStorage.Save(_state);
        }

        private void PositionWindowTopCenter()
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            if (_state.CustomLeft.HasValue)
            {
                Left = Math.Clamp(_state.CustomLeft.Value, -80, screenW - Width + 80);
            }
            else
            {
                Left = (screenW - Width) / 2.0;
            }
            Top = 0;
        }

        private void ResetPositionToCenter()
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            Left = (screenW - Width) / 2.0;
            Top = 0;
            _state.CustomLeft = null;
            AppStorage.Save(_state);
        }

        private void InitSystemTray()
        {
            try
            {
                _trayIcon = new Forms.NotifyIcon
                {
                    Text = "Dynamic Island Desktop",
                    Visible = true,
                    Icon = CreateTrayIcon()
                };

                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Open / Expand Dynamic Island", null, (_, _) => Dispatcher.Invoke(() => SetVisualMode(IslandVisualMode.Expanded)));
                menu.Items.Add("Compact Mode", null, (_, _) => Dispatcher.Invoke(() => SetVisualMode(IslandVisualMode.Compact)));
                menu.Items.Add("Pin / Unpin Dynamic Island", null, (_, _) => Dispatcher.Invoke(TogglePin));
                menu.Items.Add("Reset Position to Center", null, (_, _) => Dispatcher.Invoke(ResetPositionToCenter));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Preview Bluetooth Toast (Bluetooth Audio)", null, (_, _) => Dispatcher.Invoke(() => ShowBluetoothToast("Bluetooth Audio", 80)));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => SetVisualMode(IslandVisualMode.Expanded));
            }
            catch { }
        }

        private static System.Drawing.Icon CreateTrayIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                if (res != null)
                {
                    return new System.Drawing.Icon(res.Stream, new System.Drawing.Size(32, 32));
                }
            }
            catch { }

            using var bmp = new System.Drawing.Bitmap(64, 64);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var blackBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 12, 12, 14));
                using var greenPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 74, 222, 128), 4.5f);
                using var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                g.FillEllipse(blackBrush, 4, 16, 56, 32);
                g.DrawEllipse(greenPen, 12, 23, 18, 18);
                g.FillRectangle(whiteBrush, 40, 27, 3, 10);
                g.FillRectangle(whiteBrush, 45, 24, 3, 16);
                g.FillRectangle(whiteBrush, 50, 28, 3, 8);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        // =====================================================================
        // CUSTOM NOTCH SILHOUETTE GEOMETRY & SMOOTH MORPHING
        // =====================================================================

        private void SetVisualMode(IslandVisualMode mode)
        {
            _visualMode = mode;

            if (mode == IslandVisualMode.Compact)
            {
                CompactStatePanel.Visibility = Visibility.Visible;
                ExpandedStatePanel.Visibility = Visibility.Collapsed;
                BluetoothToastPanel.Visibility = Visibility.Collapsed;
                AmbientGlowPath.Opacity = 0;

                double compactW = (_mediaService.CurrentMedia.IsPlaying || _pomoRunning) ? 268 : 236;
                AnimateIslandSize(compactW, 36);
            }
            else if (mode == IslandVisualMode.BluetoothToast)
            {
                CompactStatePanel.Visibility = Visibility.Collapsed;
                ExpandedStatePanel.Visibility = Visibility.Collapsed;
                BluetoothToastPanel.Visibility = Visibility.Visible;
                AmbientGlowPath.Opacity = 0;

                AnimateIslandSize(376, 82);
            }
            else // Expanded
            {
                CompactStatePanel.Visibility = Visibility.Collapsed;
                BluetoothToastPanel.Visibility = Visibility.Collapsed;
                ExpandedStatePanel.Visibility = Visibility.Visible;

                bool showLyrics = (_currentPage == 0 && _lyricsExpanded);
                AmbientGlowPath.Opacity = showLyrics ? 1.0 : 0.0;

                double expW = showLyrics ? 556 : 454;
                double expH = 180;
                AnimateIslandSize(expW, expH);
            }
        }

        private void AnimateIslandSize(double targetW, double targetH)
        {
            _targetW = targetW;
            _targetH = targetH;
            if (!_isMorphing)
            {
                _isMorphing = true;
                CompositionTarget.Rendering += OnMorphRendering;
            }
        }

        private void OnMorphRendering(object? sender, EventArgs e)
        {
            double dw = _targetW - _curW;
            double dh = _targetH - _curH;

            if (Math.Abs(dw) < 0.6 && Math.Abs(dh) < 0.6)
            {
                _curW = _targetW;
                _curH = _targetH;
                UpdateNotchGeometry(_curW, _curH);
                _isMorphing = false;
                CompositionTarget.Rendering -= OnMorphRendering;
                return;
            }

            // Smooth exponential damping (60/120fps friendly, zero allocation)
            _curW += dw * 0.26;
            _curH += dh * 0.26;
            UpdateNotchGeometry(_curW, _curH);
        }

        private void UpdateNotchGeometry(double bodyWidth, double height)
        {
            double topEar = Math.Min(20, height * 0.42);
            double bottomRad = Math.Min(28, height * 0.46);
            double totalWidth = bodyWidth + (topEar * 2);

            IslandBodyContainer.Width = bodyWidth;
            IslandBodyContainer.Height = height;

            // Keep window height tightly fitted to the current island height so area below never blocks clicks
            Height = Math.Max(50, height + 24);

            // Build Notch Silhouette Path with outward concave top ears & rounded bottom corners:
            // (0,0) -> concave curve to (topEar, topEar) -> down to (topEar, H - bottomRad)
            // -> convex curve to (topEar + bottomRad, H) -> across to (topEar + bodyWidth - bottomRad, H)
            // -> convex curve to (topEar + bodyWidth, H - bottomRad) -> up to (topEar + bodyWidth, topEar)
            // -> concave curve to (totalWidth, 0) -> close
            var geom = new StreamGeometry();
            using (StreamGeometryContext ctx = geom.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(0, 0), isFilled: true, isClosed: true);

                // Top-left concave flare
                ctx.QuadraticBezierTo(
                    new System.Windows.Point(topEar, 0),
                    new System.Windows.Point(topEar, topEar),
                    isStroked: false, isSmoothJoin: true);

                // Left vertical wall
                ctx.LineTo(new System.Windows.Point(topEar, Math.Max(topEar, height - bottomRad)), isStroked: false, isSmoothJoin: true);

                // Bottom-left rounded corner
                ctx.QuadraticBezierTo(
                    new System.Windows.Point(topEar, height),
                    new System.Windows.Point(topEar + bottomRad, height),
                    isStroked: false, isSmoothJoin: true);

                // Bottom flat edge
                ctx.LineTo(new System.Windows.Point(topEar + bodyWidth - bottomRad, height), isStroked: false, isSmoothJoin: true);

                // Bottom-right rounded corner
                ctx.QuadraticBezierTo(
                    new System.Windows.Point(topEar + bodyWidth, height),
                    new System.Windows.Point(topEar + bodyWidth, Math.Max(topEar, height - bottomRad)),
                    isStroked: false, isSmoothJoin: true);

                // Right vertical wall
                ctx.LineTo(new System.Windows.Point(topEar + bodyWidth, topEar), isStroked: false, isSmoothJoin: true);

                // Top-right concave flare
                ctx.QuadraticBezierTo(
                    new System.Windows.Point(topEar + bodyWidth, 0),
                    new System.Windows.Point(totalWidth, 0),
                    isStroked: false, isSmoothJoin: true);
            }
            geom.Freeze();

            NotchShapePath.Data = geom;
            AmbientGlowPath.Data = geom;
        }

        // =====================================================================
        // PAGE NAVIGATION (5 PAGES CAROUSEL)
        // =====================================================================

        private void SwitchToPage(int pageIndex, bool animate = true)
        {
            _currentPage = ((pageIndex % 6) + 6) % 6;
            _state.LastPageIndex = _currentPage;

            Page0Music.Visibility = _currentPage == 0 ? Visibility.Visible : Visibility.Collapsed;
            Page1Pomodoro.Visibility = _currentPage == 1 ? Visibility.Visible : Visibility.Collapsed;
            Page2Water.Visibility = _currentPage == 2 ? Visibility.Visible : Visibility.Collapsed;
            Page3Shelf.Visibility = _currentPage == 3 ? Visibility.Visible : Visibility.Collapsed;
            Page4Todo.Visibility = _currentPage == 4 ? Visibility.Visible : Visibility.Collapsed;
            Page5Search.Visibility = _currentPage == 5 ? Visibility.Visible : Visibility.Collapsed;

            // Update Top Header Title & Right Indicator per page matching reference screenshots
            ShelfClearBtn.Visibility = _currentPage == 3 ? Visibility.Visible : Visibility.Collapsed;

            switch (_currentPage)
            {
                case 0: // Music: No top-left title (Album art occupies top-left), Waveform on right
                    HeaderTitlePrimary.Text = "";
                    HeaderTitleAccent.Text = "";
                    HeaderWaveformPanel.Visibility = Visibility.Visible;
                    HeaderRedBadge.Visibility = Visibility.Collapsed;
                    break;

                case 1: // Pomodoro: "Pomodoro" on top-left, Waveform on right
                    HeaderTitlePrimary.Text = "Pomodoro";
                    HeaderTitleAccent.Text = "";
                    HeaderWaveformPanel.Visibility = Visibility.Visible;
                    HeaderRedBadge.Visibility = Visibility.Collapsed;
                    break;

                case 2: // Water Reminder: "Water " (white) + "Reminder" (sky blue), Red badge on right
                    HeaderTitlePrimary.Text = "Water ";
                    HeaderTitleAccent.Text = "Reminder";
                    HeaderTitleAccent.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 185, 234));
                    HeaderWaveformPanel.Visibility = Visibility.Collapsed;
                    HeaderRedBadge.Visibility = Visibility.Visible;
                    break;

                case 3: // Shelf: "Shelf" + Clear button, Waveform on right
                    HeaderTitlePrimary.Text = "Shelf";
                    HeaderTitleAccent.Text = "";
                    HeaderWaveformPanel.Visibility = Visibility.Visible;
                    HeaderRedBadge.Visibility = Visibility.Collapsed;
                    break;

                case 4: // TodoList: "Todo" (white) + "List" (green), Red badge on right
                    HeaderTitlePrimary.Text = "Todo";
                    HeaderTitleAccent.Text = "List";
                    HeaderTitleAccent.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
                    HeaderWaveformPanel.Visibility = Visibility.Collapsed;
                    HeaderRedBadge.Visibility = Visibility.Visible;
                    break;

                case 5: // Browser Search: "Web " (white) + "Search" (blue), Waveform on right
                    HeaderTitlePrimary.Text = "Web ";
                    HeaderTitleAccent.Text = "Search";
                    HeaderTitleAccent.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 165, 250));
                    HeaderWaveformPanel.Visibility = Visibility.Visible;
                    HeaderRedBadge.Visibility = Visibility.Collapsed;
                    break;
            }

            // When Lyrics mode is open on Page 0, hide < > arrows like in Screenshot 014438
            bool lyricsView = (_currentPage == 0 && _lyricsExpanded);
            PrevPageBtn.Visibility = lyricsView ? Visibility.Collapsed : Visibility.Visible;
            NextPageBtn.Visibility = lyricsView ? Visibility.Collapsed : Visibility.Visible;
            PaginationDotsPanel.Visibility = lyricsView ? Visibility.Collapsed : Visibility.Visible;
            HeaderClockPanel.Visibility = lyricsView ? Visibility.Collapsed : Visibility.Visible;
            HeaderWaveformPanel.Visibility = lyricsView ? Visibility.Collapsed : HeaderWaveformPanel.Visibility;
            HeaderSearchBtn.Visibility = lyricsView ? Visibility.Collapsed : Visibility.Visible;

            // Update Pagination Dots (elongated white pill for active dot)
            Border[] dots = { PageDot0, PageDot1, PageDot2, PageDot3, PageDot4, PageDot5 };
            for (int i = 0; i < dots.Length; i++)
            {
                if (i == _currentPage)
                {
                    dots[i].Width = 18;
                    dots[i].Background = System.Windows.Media.Brushes.White;
                }
                else
                {
                    dots[i].Width = 4;
                    dots[i].Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102));
                }
            }

            if (_visualMode == IslandVisualMode.Expanded)
            {
                SetVisualMode(IslandVisualMode.Expanded);
            }
        }

        private void PrevPageBtn_Click(object sender, RoutedEventArgs e) => SwitchToPage(_currentPage - 1);
        private void NextPageBtn_Click(object sender, RoutedEventArgs e) => SwitchToPage(_currentPage + 1);

        private void PageDot0_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(0);
        private void PageDot1_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(1);
        private void PageDot2_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(2);
        private void PageDot3_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(3);
        private void PageDot4_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(4);
        private void PageDot5_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(5);

        private void HeaderSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            SetVisualMode(IslandVisualMode.Expanded);
            SwitchToPage(5);
            Dispatcher.BeginInvoke(new Action(() => WebSearchInputBox.Focus()), DispatcherPriority.Input);
        }

        private void HeaderWaveform_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(0);
        private void HeaderRedBadge_Click(object sender, MouseButtonEventArgs e) => SwitchToPage(1);

        // =====================================================================
        // TIMERS: CLOCK, BATTERY, WAVEFORM, POMODORO, MEDIA INTERPOLATION
        // =====================================================================

        private void MainTickTimer_Tick(object? sender, EventArgs e)
        {
            UpdateClockAndBatteryUI();

            // Advance demo track if playing locally or interpolate external media smoothly
            if (_mediaService.CurrentMedia.IsExternalSession)
            {
                if (_mediaService.CurrentMedia.IsPlaying &&
                    _mediaService.CurrentMedia.PositionSeconds < _mediaService.CurrentMedia.DurationSeconds)
                {
                    _mediaService.CurrentMedia.PositionSeconds = Math.Min(
                        _mediaService.CurrentMedia.DurationSeconds,
                        _mediaService.CurrentMedia.PositionSeconds + 1.0);
                    UpdateMusicTimelineAndLyricsUI(_mediaService.CurrentMedia);
                }
            }
            else
            {
                _mediaService.TickDemoIfActive(1.0);
            }

            // Advance Pomodoro Timer if running
            if (_pomoRunning)
            {
                if (_pomoRemainingSeconds > 0)
                {
                    _pomoRemainingSeconds--;
                    UpdatePomodoroUI();
                }
                else
                {
                    _pomoRunning = false;
                    UpdatePomodoroUI();
                    System.Media.SystemSounds.Asterisk.Play();
                    ShowBluetoothToast(_pomoIsShortBreak ? "Short Break Complete!" : "Focus Session Complete!", 100);
                }
            }

            // Check Water Reminder notification
            if (_state.Water.RemindersEnabled && DateTime.Now >= _nextWaterReminderTime)
            {
                _nextWaterReminderTime = DateTime.Now.AddMinutes(_state.Water.ReminderIntervalMinutes);
                _state.Water.NextReminderText = _nextWaterReminderTime.ToString("HH:mm");
                UpdateWaterUI();
                ShowBluetoothToast("Time to Drink Water 💧", (int)Math.Round((_state.Water.CurrentLiters / _state.Water.GoalLiters) * 100));
            }

            // Failsafe: if the window is expanded and mouse is no longer hovering (e.g. after fast cursor exit or drag release), schedule collapse
            if (_visualMode == IslandVisualMode.Expanded && !IsMouseOver && !_state.IsPinned &&
                !_isWindowDragging && TodoAddInputPanel.Visibility != Visibility.Visible &&
                !_collapseDebounceTimer.IsEnabled)
            {
                _collapseDebounceTimer.Start();
            }
        }

        private void UpdateClockAndBatteryUI()
        {
            DateTime now = DateTime.Now;
            string datePart = now.ToString("ddd dd MMM yyyy ", CultureInfo.InvariantCulture);
            string timePart = now.ToString("HH:mm", CultureInfo.InvariantCulture);

            HeaderDateText.Text = datePart;
            HeaderTimeText.Text = timePart;

            var (pct, _) = NativeSystemService.GetBatteryInfo();
            double sweepAngle = Math.Clamp(pct / 100.0, 0.08, 0.999) * 359.9;
            var arcGeom = CreateCircularArcGeometry(8, 8, 6.8, 0, sweepAngle);
            HeaderBatteryArc.Data = arcGeom;
            CompactBatteryArc.Data = arcGeom;

            // Update Compact Notch text based on active state
            if (_mediaService.CurrentMedia.IsPlaying)
            {
                CompactAlbumBorder.Visibility = Visibility.Visible;
                CompactAlbumImage.Source = _mediaService.CurrentMedia.AlbumArt;
                CompactCenterText.Text = $"{_mediaService.CurrentMedia.Title} · {_mediaService.CurrentMedia.Artist}";
            }
            else if (_pomoRunning)
            {
                CompactAlbumBorder.Visibility = Visibility.Collapsed;
                int m = _pomoRemainingSeconds / 60;
                int s = _pomoRemainingSeconds % 60;
                CompactCenterText.Text = $"⏱ {m:D2}:{s:D2} · {(_pomoIsShortBreak ? "Short Break" : "Focus")}";
            }
            else
            {
                CompactAlbumBorder.Visibility = Visibility.Collapsed;
                CompactCenterText.Text = $"{datePart} {timePart}";
            }
        }

        private double _wavePhase = 0;

        private void WaveformTimer_Tick(object? sender, EventArgs e)
        {
            float peak = NativeSystemService.GetSystemAudioPeak();
            bool activeAudio = peak > 0.01f || _mediaService.CurrentMedia.IsPlaying;

            if (!activeAudio)
            {
                // Static minimal bars when silent (0 CPU work)
                WaveBar1.Height = 4; WaveBar2.Height = 9; WaveBar3.Height = 6; WaveBar4.Height = 11; WaveBar5.Height = 4;
                MiniWave1.Height = 4; MiniWave2.Height = 8; MiniWave3.Height = 5; MiniWave4.Height = 9; MiniWave5.Height = 4;
                return;
            }

            _wavePhase += 0.45;
            double amp = peak > 0.02f ? Math.Clamp(peak * 1.4, 0.25, 1.0) : 0.65;

            double h1 = 4 + Math.Abs(Math.Sin(_wavePhase)) * 10 * amp;
            double h2 = 5 + Math.Abs(Math.Sin(_wavePhase + 1.1)) * 11 * amp;
            double h3 = 4 + Math.Abs(Math.Sin(_wavePhase + 2.2)) * 12 * amp;
            double h4 = 5 + Math.Abs(Math.Sin(_wavePhase + 3.3)) * 11 * amp;
            double h5 = 4 + Math.Abs(Math.Sin(_wavePhase + 4.4)) * 9 * amp;

            WaveBar1.Height = h1; WaveBar2.Height = h2; WaveBar3.Height = h3; WaveBar4.Height = h4; WaveBar5.Height = h5;
            MiniWave1.Height = h1 * 0.85; MiniWave2.Height = h2 * 0.85; MiniWave3.Height = h3 * 0.85; MiniWave4.Height = h4 * 0.85; MiniWave5.Height = h5 * 0.85;
        }

        // =====================================================================
        // PAGE 0: MUSIC PLAYER & LIVE SYNCED LYRICS
        // =====================================================================

        private void OnMediaUpdated(MediaSessionInfo info)
        {
            Dispatcher.BeginInvoke(() => UpdateMusicUI(info));
        }

        private void OnLyricsLoaded()
        {
            Dispatcher.BeginInvoke(() => UpdateMusicTimelineAndLyricsUI(_mediaService.CurrentMedia));
        }

        private void UpdateMusicUI(MediaSessionInfo info)
        {
            MusicAlbumImage.Source = info.AlbumArt;
            MusicTitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "No Media Playing" : info.Title;

            string sub = info.Artist;
            if (!string.IsNullOrWhiteSpace(info.Album) && !info.Artist.Contains(info.Album, StringComparison.OrdinalIgnoreCase))
                sub = $"{info.Artist} - {info.Album}";

            MusicArtistText.Text = string.IsNullOrWhiteSpace(sub) ? "Play music on Spotify or Browser" : sub;

            MusicPlayIcon.Visibility = info.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            MusicPauseIcon.Visibility = info.IsPlaying ? Visibility.Visible : Visibility.Collapsed;

            // Update Ambient Glow Color from Album Art dominant color
            var dom = info.DominantColor;
            AmbientGlowMidStop.Color = System.Windows.Media.Color.FromArgb(95, dom.R, dom.G, dom.B);
            AmbientGlowBottomStop.Color = System.Windows.Media.Color.FromArgb(215, dom.R, dom.G, dom.B);

            UpdateMusicTimelineAndLyricsUI(info);
            UpdateClockAndBatteryUI();
        }

        private void UpdateMusicTimelineAndLyricsUI(MediaSessionInfo info)
        {
            double dur = Math.Max(0, info.DurationSeconds);
            double pos = dur > 0 ? Math.Clamp(info.PositionSeconds, 0, dur) : 0;
            double rem = Math.Max(0, dur - pos);

            MusicElapsedText.Text = FormatMinSec(pos);
            MusicRemainingText.Text = "-" + FormatMinSec(rem);

            double trackWidth = MusicProgressContainer.ActualWidth > 20 ? MusicProgressContainer.ActualWidth : (_lyricsExpanded ? 145 : 240);
            double ratio = dur > 0 ? pos / dur : 0;
            MusicProgressFill.Width = Math.Clamp(ratio * trackWidth, 4, trackWidth);

            var (curLine, nextLine) = LyricsService.GetCurrentLyrics(info.Title, info.Artist, pos, dur);
            LyricsCurrentLineText.Text = curLine;
            LyricsNextLineText.Text = nextLine;
        }

        private static string FormatMinSec(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }

        private void MusicPlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            _mediaService.TogglePlayPause();
        }

        private void MusicPrevBtn_Click(object sender, RoutedEventArgs e)
        {
            _mediaService.PreviousTrack();
        }

        private void MusicNextBtn_Click(object sender, RoutedEventArgs e)
        {
            _mediaService.NextTrack();
        }

        private void MusicProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (MusicProgressContainer.ActualWidth <= 0) return;
            var pt = e.GetPosition(MusicProgressContainer);
            double ratio = Math.Clamp(pt.X / MusicProgressContainer.ActualWidth, 0, 1);
            double targetSec = ratio * _mediaService.CurrentMedia.DurationSeconds;
            _mediaService.SeekTo(targetSec);
        }

        private void LyricsToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _lyricsExpanded = !_lyricsExpanded;

            if (_lyricsExpanded)
            {
                MusicLeftCol.Width = new GridLength(268);
                MusicRightLyricsCol.Width = new GridLength(1, GridUnitType.Star);
                LyricsRightPanel.Visibility = Visibility.Visible;
                LyricsIconOff.Visibility = Visibility.Collapsed;
                LyricsIconOn.Visibility = Visibility.Visible;
                LyricsQuotesOn.Visibility = Visibility.Visible;
                Page0Music.Margin = new Thickness(24, 14, 24, 16);
            }
            else
            {
                MusicLeftCol.Width = new GridLength(1, GridUnitType.Star);
                MusicRightLyricsCol.Width = new GridLength(0);
                LyricsRightPanel.Visibility = Visibility.Collapsed;
                LyricsIconOff.Visibility = Visibility.Visible;
                LyricsIconOn.Visibility = Visibility.Collapsed;
                LyricsQuotesOn.Visibility = Visibility.Collapsed;
                Page0Music.Margin = new Thickness(38, 12, 38, 22);
            }

            SwitchToPage(0);
            Dispatcher.BeginInvoke(new Action(() => UpdateMusicTimelineAndLyricsUI(_mediaService.CurrentMedia)), DispatcherPriority.Loaded);
        }

        // =====================================================================
        // PAGE 1: POMODORO TIMER (Screenshot 014536)
        // =====================================================================

        private void UpdatePomodoroUI()
        {
            int mins = _pomoRemainingSeconds / 60;
            int secs = _pomoRemainingSeconds % 60;
            PomodoroTimeText.Text = $"{mins:D2}:{secs:D2}";
            PomodoroModeSubtitle.Text = _pomoIsShortBreak ? "Short break" : "Focus time";

            PomoPlayIcon.Visibility = _pomoRunning ? Visibility.Collapsed : Visibility.Visible;
            PomoPauseIcon.Visibility = _pomoRunning ? Visibility.Visible : Visibility.Collapsed;

            PomoShortBreakBtn.Background = _pomoIsShortBreak
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 38, 41))
                : System.Windows.Media.Brushes.Transparent;
            PomoFocusTimeBtn.Background = !_pomoIsShortBreak
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 38, 41))
                : System.Windows.Media.Brushes.Transparent;

            double ratio = _pomoTotalSeconds > 0 ? (double)_pomoRemainingSeconds / _pomoTotalSeconds : 1.0;
            double sweep = Math.Clamp(ratio, 0.01, 0.999) * 359.9;
            PomodoroRingArc.Data = CreateCircularArcGeometry(41, 41, 35.5, 0, sweep);
        }

        private void PomodoroPlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            _pomoRunning = !_pomoRunning;
            UpdatePomodoroUI();
        }

        private void PomodoroResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _pomoRunning = false;
            _pomoRemainingSeconds = _pomoTotalSeconds;
            UpdatePomodoroUI();
        }

        private void PomoShortBreakBtn_Click(object sender, RoutedEventArgs e)
        {
            _pomoRunning = false;
            _pomoIsShortBreak = true;
            _pomoTotalSeconds = 5 * 60;
            _pomoRemainingSeconds = _pomoTotalSeconds;
            UpdatePomodoroUI();
        }

        private void PomoFocusTimeBtn_Click(object sender, RoutedEventArgs e)
        {
            _pomoRunning = false;
            _pomoIsShortBreak = false;
            _pomoTotalSeconds = 25 * 60;
            _pomoRemainingSeconds = _pomoTotalSeconds;
            UpdatePomodoroUI();
        }

        // =====================================================================
        // PAGE 2: WATER REMINDER (Screenshots 014543 & 014548)
        // =====================================================================

        private void UpdateWaterUI()
        {
            var w = _state.Water;
            WaterCurrentText.Text = w.CurrentLiters.ToString("0.0", CultureInfo.InvariantCulture);
            WaterGoalText.Text = $" / {w.GoalLiters.ToString("0.0", CultureInfo.InvariantCulture)} L";

            int pct = w.GoalLiters > 0 ? (int)Math.Round((w.CurrentLiters / w.GoalLiters) * 100) : 0;
            pct = Math.Clamp(pct, 0, 100);

            WaterPercentInlineText.Text = $"{pct}%";

            var skyBlue = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 185, 234));
            var mutedGray = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));

            if (w.RemindersEnabled)
            {
                WaterReminderSwitchTrack.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 127, 249));
                WaterReminderSwitchThumb.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                WaterPercentInlineText.Foreground = skyBlue;
                WaterReminderStatusText.Text = $" · Next {w.NextReminderText}";
                WaterLinearBarFill.Background = skyBlue;
            }
            else
            {
                WaterReminderSwitchTrack.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
                WaterReminderSwitchThumb.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                WaterPercentInlineText.Foreground = mutedGray;
                WaterReminderStatusText.Text = " · Reminders off";
                WaterLinearBarFill.Background = mutedGray;
            }

            WaterLinearBarFill.Width = Math.Clamp((pct / 100.0) * 165.0, 4, 165);

            // Sub-view toggle (0 = 270° Circular Ring Gauge, 1 = Hourly Bar Chart)
            bool showRing = (w.SubViewIndex == 0);
            WaterRingSubView.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;
            WaterChartSubView.Visibility = showRing ? Visibility.Collapsed : Visibility.Visible;

            WaterSubDot0.Width = showRing ? 5 : 4;
            WaterSubDot0.Height = showRing ? 5 : 4;
            WaterSubDot0.Fill = showRing ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102));

            WaterSubDot1.Width = !showRing ? 5 : 4;
            WaterSubDot1.Height = !showRing ? 5 : 4;
            WaterSubDot1.Fill = !showRing ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102));

            // Render 270° Gauge Arc (-135° to +135°)
            WaterGaugeCenterNumber.Text = pct.ToString(CultureInfo.InvariantCulture);
            WaterGaugeBgArc.Data = CreateCircularArcGeometry(46, 46, 39, 225, 270);

            double valSweep = Math.Clamp(pct / 100.0, 0.02, 1.0) * 270.0;
            WaterGaugeValueArc.Data = CreateCircularArcGeometry(46, 46, 39, 225, valSweep);

            // Position the glowing tip dot at the end of the value arc
            double endAngleDeg = 225.0 + valSweep;
            double endRad = (endAngleDeg - 90.0) * Math.PI / 180.0;
            double tipX = 46 + 39 * Math.Cos(endRad) - 4;
            double tipY = 46 + 39 * Math.Sin(endRad) - 4;
            WaterGaugeTipDot.Margin = new Thickness(tipX, tipY, 0, 0);

            // Render Hourly Bar Chart (Screenshot 014548)
            RebuildWaterChartBars();
        }

        private void RebuildWaterChartBars()
        {
            WaterChartBarsPanel.Children.Clear();
            var dict = _state.Water.HourlyIntakeMl;

            // Display hours 14..20 so 15, 17, 18 (and current hour) show up like Screenshot 014548
            int[] hours = { 14, 15, 16, 17, 18, 19, 20 };
            foreach (int h in hours)
            {
                dict.TryGetValue(h, out int ml);
                double barMaxH = 58.0;
                double barH = ml > 0 ? Math.Clamp((ml / 600.0) * barMaxH, 8, barMaxH) : 0;

                var col = new Grid
                {
                    Width = 11,
                    Height = barMaxH,
                    Margin = new Thickness(2.5, 0, 2.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                if (barH > 0)
                {
                    var bar = new Border
                    {
                        Width = 7.5,
                        Height = barH,
                        CornerRadius = new CornerRadius(3.75),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        ToolTip = $"{h:D2}:00 — {ml} ml",
                        Background = new LinearGradientBrush(
                            System.Windows.Media.Color.FromRgb(90, 185, 234),
                            System.Windows.Media.Color.FromRgb(35, 108, 200),
                            new System.Windows.Point(0.5, 0),
                            new System.Windows.Point(0.5, 1))
                    };
                    col.Children.Add(bar);
                }

                WaterChartBarsPanel.Children.Add(col);
            }
        }

        private void WaterDrinkBtn_Click(object sender, RoutedEventArgs e)
        {
            var w = _state.Water;
            w.CurrentLiters = Math.Round(Math.Min(w.GoalLiters * 2, w.CurrentLiters + w.CupSizeLiters), 2);

            int curHour = DateTime.Now.Hour;
            if (curHour < 14 || curHour > 20) curHour = 18; // Ensure visible on chart
            int addMl = (int)Math.Round(w.CupSizeLiters * 1000);
            w.HourlyIntakeMl.TryGetValue(curHour, out int existing);
            w.HourlyIntakeMl[curHour] = existing + addMl;

            AppStorage.Save(_state);
            UpdateWaterUI();
        }

        private void WaterReminderSwitch_Click(object sender, MouseButtonEventArgs e)
        {
            _state.Water.RemindersEnabled = !_state.Water.RemindersEnabled;
            if (_state.Water.RemindersEnabled)
            {
                _nextWaterReminderTime = DateTime.Now.AddMinutes(_state.Water.ReminderIntervalMinutes);
                _state.Water.NextReminderText = _nextWaterReminderTime.ToString("HH:mm");
            }
            AppStorage.Save(_state);
            UpdateWaterUI();
        }

        private void WaterRightPanel_Click(object sender, MouseButtonEventArgs e)
        {
            _state.Water.SubViewIndex = (_state.Water.SubViewIndex == 0) ? 1 : 0;
            AppStorage.Save(_state);
            UpdateWaterUI();
        }

        private void WaterSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Cycle through presets: 1.5L -> 2.0L -> 2.5L -> Reset Today to 0.0L -> 1.5L
            var w = _state.Water;
            if (Math.Abs(w.GoalLiters - 1.5) < 0.05 && WaterSettingsBtnLabel.Text != "Reset 0L")
            {
                w.GoalLiters = 2.0;
                WaterSettingsBtnLabel.Text = "Goal: 2.0L";
            }
            else if (Math.Abs(w.GoalLiters - 2.0) < 0.05)
            {
                w.GoalLiters = 2.5;
                WaterSettingsBtnLabel.Text = "Goal: 2.5L";
            }
            else if (Math.Abs(w.GoalLiters - 2.5) < 0.05)
            {
                w.GoalLiters = 1.5;
                w.CurrentLiters = 0.0;
                w.HourlyIntakeMl.Clear();
                WaterSettingsBtnLabel.Text = "Reset 0L";
            }
            else
            {
                w.GoalLiters = 1.5;
                WaterSettingsBtnLabel.Text = "Settings";
            }

            AppStorage.Save(_state);
            UpdateWaterUI();
        }

        // =====================================================================
        // PAGE 3: SHELF - FILE DROP & DRAG OUT (Screenshot 014642)
        // =====================================================================

        private void RebuildShelfUI()
        {
            ShelfItemsContainer.Children.Clear();

            if (_state.ShelfItems.Count == 0)
            {
                ShelfEmptyHint.Visibility = Visibility.Visible;
                return;
            }

            ShelfEmptyHint.Visibility = Visibility.Collapsed;

            foreach (var item in _state.ShelfItems.ToList())
            {
                var card = CreateShelfCard(item);
                ShelfItemsContainer.Children.Add(card);
            }
        }

        private UIElement CreateShelfCard(ShelfItemModel item)
        {
            var outer = new Border
            {
                Width = 84,
                Height = 104,
                CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.Transparent,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{item.DisplayName} ({item.SizeText})\nDouble-click to open · Drag to move out"
            };

            var grid = new Grid();

            var contentStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(4, 6, 4, 4)
            };

            // Icon / Thumbnail Preview (54x54)
            if (item.IsFolder)
            {
                // Yellow Folder Card matching Screenshot 014642
                var folderCanvas = new Grid { Width = 56, Height = 50, Margin = new Thickness(0, 0, 0, 6) };
                var backTab = new Border
                {
                    Width = 24, Height = 10,
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 168, 42)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2, 2, 0, 0)
                };
                var innerPreview = new Border
                {
                    Width = 48, Height = 26,
                    CornerRadius = new CornerRadius(4),
                    Background = new LinearGradientBrush(
                        System.Windows.Media.Color.FromRgb(40, 75, 165),
                        System.Windows.Media.Color.FromRgb(110, 140, 220),
                        0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                var frontFlap = new Border
                {
                    Width = 54, Height = 30,
                    CornerRadius = new CornerRadius(5),
                    Background = new LinearGradientBrush(
                        System.Windows.Media.Color.FromRgb(255, 204, 77),
                        System.Windows.Media.Color.FromRgb(242, 178, 41),
                        90),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                folderCanvas.Children.Add(backTab);
                folderCanvas.Children.Add(innerPreview);
                folderCanvas.Children.Add(frontFlap);
                contentStack.Children.Add(folderCanvas);
            }
            else if (item.IsImage && File.Exists(item.FilePath))
            {
                var imgBorder = new Border
                {
                    Width = 50, Height = 52,
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 45, 95)),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 100;
                    bmp.UriSource = new Uri(item.FilePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    imgBorder.Child = new System.Windows.Controls.Image
                    {
                        Source = bmp,
                        Stretch = Stretch.UniformToFill
                    };
                }
                catch { }
                contentStack.Children.Add(imgBorder);
            }
            else
            {
                var docBorder = new Border
                {
                    Width = 46, Height = 52,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 46)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 75, 85)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                string ext = Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "FILE";
                docBorder.Child = new TextBlock
                {
                    Text = ext.Length > 4 ? ext.Substring(0, 4) : ext,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                contentStack.Children.Add(docBorder);
            }

            // Title & Size
            contentStack.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 76
            });

            contentStack.Children.Add(new TextBlock
            {
                Text = item.SizeText,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 142, 147)),
                FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });

            grid.Children.Add(contentStack);

            // Top-Right Remove '×' Button (Visible on card hover, matches Screenshot 014642)
            var removeBadge = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 90, 98)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Visibility = Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "×",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -2, 0, 0)
                }
            };

            removeBadge.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _state.ShelfItems.Remove(item);
                AppStorage.Save(_state);
                RebuildShelfUI();
            };

            grid.Children.Add(removeBadge);
            outer.Child = grid;

            outer.MouseEnter += (_, _) =>
            {
                outer.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 38, 38, 46));
                removeBadge.Visibility = Visibility.Visible;
            };

            outer.MouseLeave += (_, _) =>
            {
                outer.Background = System.Windows.Media.Brushes.Transparent;
                removeBadge.Visibility = Visibility.Collapsed;
            };

            // Double click to open file/folder, or drag to drag file out into Windows Explorer
            outer.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    try
                    {
                        if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                        {
                            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                        }
                    }
                    catch { }
                    e.Handled = true;
                }
                else
                {
                    _shelfDragStartPoint = e.GetPosition(this);
                    _isDraggingFromShelf = true;
                }
            };

            outer.MouseMove += (s, e) =>
            {
                if (_isDraggingFromShelf && e.LeftButton == MouseButtonState.Pressed)
                {
                    var cur = e.GetPosition(this);
                    if (Math.Abs(cur.X - _shelfDragStartPoint.X) > 6 || Math.Abs(cur.Y - _shelfDragStartPoint.Y) > 6)
                    {
                        _isDraggingFromShelf = false;
                        if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                        {
                            var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new string[] { item.FilePath });
                            DragDrop.DoDragDrop(outer, data, System.Windows.DragDropEffects.Copy);
                        }
                    }
                }
            };

            outer.MouseLeftButtonUp += (_, _) => _isDraggingFromShelf = false;

            return outer;
        }

        private void ShelfClearBtn_Click(object sender, RoutedEventArgs e)
        {
            _state.ShelfItems.Clear();
            AppStorage.Save(_state);
            RebuildShelfUI();
        }

        private void ShelfEmptyHint_Click(object sender, MouseButtonEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add Files to Dynamic Island Shelf",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                AddFilesToShelf(dlg.FileNames);
            }
        }

        private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                _collapseDebounceTimer.Stop();
                SetVisualMode(IslandVisualMode.Expanded);
                SwitchToPage(3); // Automatically open Shelf page when dragging files to top of screen!
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                e.Effects = System.Windows.DragDropEffects.Copy;
            else
                e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                {
                    AddFilesToShelf(paths);
                    SwitchToPage(3);
                }
            }
        }

        private void AddFilesToShelf(IEnumerable<string> paths)
        {
            foreach (string p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (_state.ShelfItems.Any(x => string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool isDir = Directory.Exists(p);
                bool isFile = File.Exists(p);
                if (!isDir && !isFile) continue;

                string name = Path.GetFileName(p);
                if (string.IsNullOrEmpty(name)) name = p;

                string sizeText = "Folder";
                bool isImg = false;

                if (isFile)
                {
                    try
                    {
                        long len = new FileInfo(p).Length;
                        sizeText = FormatFileSize(len);
                    }
                    catch { sizeText = "File"; }

                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    isImg = ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
                }

                _state.ShelfItems.Add(new ShelfItemModel
                {
                    FilePath = p,
                    DisplayName = name.Length > 12 ? name.Substring(0, 10) + "..." : name,
                    SizeText = sizeText,
                    IsFolder = isDir,
                    IsImage = isImg
                });
            }

            AppStorage.Save(_state);
            RebuildShelfUI();
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{(bytes / (1024.0 * 1024.0)):0.0} MB";
            if (bytes >= 1024)
                return $"{(bytes / 1024.0):0} KB";
            return $"{bytes} B";
        }

        // =====================================================================
        // PAGE 4: TODOLIST (Screenshot 014554)
        // =====================================================================

        private void RebuildTodoUI()
        {
            TodoItemsStack.Children.Clear();

            int total = _state.Todos.Count;
            int done = _state.Todos.Count(t => t.IsCompleted);
            TodoCountText.Text = $"{done}/{total}";

            foreach (var todo in _state.Todos.ToList())
            {
                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 8, 5),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = todo.IsCompleted
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 39, 24))
                        : System.Windows.Media.Brushes.Transparent
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Checkbox Circle (Green filled check when completed, outlined gray circle when pending)
                var checkCircle = new Border
                {
                    Width = 17, Height = 17,
                    CornerRadius = new CornerRadius(8.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0),
                    Background = todo.IsCompleted
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128))
                        : System.Windows.Media.Brushes.Transparent,
                    BorderBrush = todo.IsCompleted
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
                    BorderThickness = new Thickness(1.5)
                };

                if (todo.IsCompleted)
                {
                    checkCircle.Child = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M 3.5,8.5 L 7,12 L 13.5,5"),
                        Stroke = System.Windows.Media.Brushes.Black,
                        StrokeThickness = 2.0,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                }

                Grid.SetColumn(checkCircle, 0);
                rowGrid.Children.Add(checkCircle);

                // Task Title & Optional Subtitle
                var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleBlock = new TextBlock
                {
                    Text = todo.Title,
                    Foreground = todo.IsCompleted
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 212, 216))
                        : System.Windows.Media.Brushes.White,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                if (todo.IsCompleted)
                {
                    titleBlock.TextDecorations = TextDecorations.Strikethrough;
                }
                textStack.Children.Add(titleBlock);

                if (!string.IsNullOrWhiteSpace(todo.Subtitle))
                {
                    textStack.Children.Add(new TextBlock
                    {
                        Text = todo.Subtitle,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 142, 147)),
                        FontSize = 10,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                }

                Grid.SetColumn(textStack, 1);
                rowGrid.Children.Add(textStack);

                // Delete button when in Edit Mode
                if (_todoEditMode)
                {
                    var delBtn = new Border
                    {
                        Width = 18, Height = 18,
                        CornerRadius = new CornerRadius(9),
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 40, 48)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0),
                        Child = new TextBlock
                        {
                            Text = "×",
                            Foreground = System.Windows.Media.Brushes.White,
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, -2, 0, 0)
                        }
                    };
                    delBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        e.Handled = true;
                        _state.Todos.Remove(todo);
                        AppStorage.Save(_state);
                        RebuildTodoUI();
                    };
                    Grid.SetColumn(delBtn, 2);
                    rowGrid.Children.Add(delBtn);
                }

                rowBorder.Child = rowGrid;
                rowBorder.MouseLeftButtonDown += (_, _) =>
                {
                    todo.IsCompleted = !todo.IsCompleted;
                    AppStorage.Save(_state);
                    RebuildTodoUI();
                };

                TodoItemsStack.Children.Add(rowBorder);
            }
        }

        private void TodoAddTaskBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TodoAddInputPanel.Visibility == Visibility.Visible)
            {
                TodoAddInputPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TodoAddInputPanel.Visibility = Visibility.Visible;
                TodoNewTitleBox.Text = "";
                TodoNewTitleBox.Focus();
            }
        }

        private void TodoConfirmAddBtn_Click(object sender, RoutedEventArgs e) => CommitNewTodoTask();

        private void TodoNewTitleBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitNewTodoTask();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TodoAddInputPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        private void CommitNewTodoTask()
        {
            string title = TodoNewTitleBox.Text.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                _state.Todos.Add(new TodoItemModel
                {
                    Title = title,
                    Subtitle = DateTime.Now.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture),
                    IsCompleted = false
                });
                AppStorage.Save(_state);
                RebuildTodoUI();
            }
            TodoNewTitleBox.Text = "";
            TodoAddInputPanel.Visibility = Visibility.Collapsed;
        }

        private void TodoEditTaskBtn_Click(object sender, RoutedEventArgs e)
        {
            _todoEditMode = !_todoEditMode;
            TodoEditBtnLabel.Text = _todoEditMode ? "Done" : "Edit Task";
            TodoEditBtnLabel.Foreground = _todoEditMode
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 212, 216));
            RebuildTodoUI();
        }

        private void TodoScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            TodoScrollViewer.ScrollToVerticalOffset(TodoScrollViewer.VerticalOffset - (e.Delta / 3.0));
            e.Handled = true;
        }

        // =====================================================================
        // BLUETOOTH CONNECTED TOAST (Screenshot 014600)
        // =====================================================================

        private void OnBluetoothConnected(string deviceName, int batteryPercent)
        {
            Dispatcher.BeginInvoke(() => ShowBluetoothToast(deviceName, batteryPercent));
        }

        public void ShowBluetoothToast(string deviceName, int batteryPercent)
        {
            _toastDismissTimer.Stop();

            BtToastDeviceName.Text = deviceName;
            BtToastSubtitle.Text = $"Connected - {batteryPercent}%";

            double sweep = Math.Clamp(batteryPercent / 100.0, 0.08, 0.999) * 359.9;
            BtToastBatteryArc.Data = CreateCircularArcGeometry(23, 23, 19.8, 0, sweep);

            SetVisualMode(IslandVisualMode.BluetoothToast);
            _toastDismissTimer.Start();
        }

        private void ToastDismissTimer_Tick(object? sender, EventArgs e)
        {
            _toastDismissTimer.Stop();
            if (IsMouseOver || _state.IsPinned)
                SetVisualMode(IslandVisualMode.Expanded);
            else
                SetVisualMode(IslandVisualMode.Compact);
        }

        // =====================================================================
        // WINDOW INTERACTION: HOVER EXPAND/COLLAPSE, WHEEL, CONTEXT MENU
        // =====================================================================

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _collapseDebounceTimer.Stop();
            if (_visualMode == IslandVisualMode.Compact)
            {
                SetVisualMode(IslandVisualMode.Expanded);
            }
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_state.IsPinned || TodoAddInputPanel.Visibility == Visibility.Visible)
                return;

            _collapseDebounceTimer.Stop();
            _collapseDebounceTimer.Start();
        }

        private void CollapseDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _collapseDebounceTimer.Stop();
            if (!IsMouseOver && !_state.IsPinned && !_isWindowDragging &&
                _visualMode == IslandVisualMode.Expanded &&
                TodoAddInputPanel.Visibility != Visibility.Visible)
            {
                if (WebSearchInputBox.IsFocused)
                {
                    Keyboard.ClearFocus();
                }
                SetVisualMode(IslandVisualMode.Compact);
            }
        }

        private void CompactPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SetVisualMode(IslandVisualMode.Expanded);
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_visualMode != IslandVisualMode.Expanded) return;
            if (e.Delta < 0)
                SwitchToPage(_currentPage + 1);
            else if (e.Delta > 0)
                SwitchToPage(_currentPage - 1);
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (TodoNewTitleBox.IsFocused || WebSearchInputBox.IsFocused) return;

            if (e.Key == Key.Right) SwitchToPage(_currentPage + 1);
            else if (e.Key == Key.Left) SwitchToPage(_currentPage - 1);
            else if (e.Key == Key.B) ShowBluetoothToast("Bluetooth Audio", 80);
            else if (e.Key == Key.P) TogglePin();
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                HeaderSearchBtn_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void TogglePin()
        {
            _state.IsPinned = !_state.IsPinned;
            AppStorage.Save(_state);
            UpdatePinUI();
            if (_state.IsPinned && _visualMode != IslandVisualMode.Expanded)
                SetVisualMode(IslandVisualMode.Expanded);
        }

        private void UpdatePinUI()
        {
            PinIndicatorIcon.Visibility = _state.IsPinned ? Visibility.Visible : Visibility.Collapsed;
            MenuPinItem.IsChecked = _state.IsPinned;
        }

        // =====================================================================
        // PAGE 5: CLEAN BROWSER SEARCH BAR
        // =====================================================================

        private void WebSearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            WebSearchPlaceholder.Visibility = string.IsNullOrEmpty(WebSearchInputBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void WebSearchInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteWebSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                WebSearchInputBox.Text = "";
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void WebSearchSubmitBtn_Click(object sender, RoutedEventArgs e) => ExecuteWebSearch();

        private void ExecuteWebSearch()
        {
            string raw = WebSearchInputBox.Text.Trim();
            if (string.IsNullOrEmpty(raw))
                return;

            string targetUrl;
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                targetUrl = raw;
            }
            else if (!raw.Contains(' ') && (raw.Contains('.') || raw.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                targetUrl = "https://" + raw;
            }
            else
            {
                targetUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(raw)}";
            }

            OpenBrowserUrl(targetUrl);
            WebSearchInputBox.SelectAll();
        }

        private static void OpenBrowserUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private void RootGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TodoNewTitleBox.IsFocused || WebSearchInputBox.IsMouseOver || MusicProgressContainer.IsMouseOver)
                return;

            _isWindowDragCandidate = true;
            _isWindowDragging = false;
            _dragStartScreenX = PointToScreen(e.GetPosition(this)).X;
            _dragStartWindowLeft = Left;
        }

        private void RootGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isWindowDragCandidate || _isDraggingFromShelf)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isWindowDragCandidate = false;
                if (_isWindowDragging)
                {
                    _isWindowDragging = false;
                    RootIslandGrid.ReleaseMouseCapture();
                    AppStorage.Save(_state);
                }
                return;
            }

            double curScreenX = PointToScreen(e.GetPosition(this)).X;
            double deltaX = curScreenX - _dragStartScreenX;

            if (!_isWindowDragging && Math.Abs(deltaX) > 6)
            {
                _isWindowDragging = true;
                RootIslandGrid.CaptureMouse();
            }

            if (_isWindowDragging)
            {
                double screenW = SystemParameters.PrimaryScreenWidth;
                double newLeft = Math.Clamp(_dragStartWindowLeft + deltaX, -120, screenW - Width + 120);
                Left = newLeft;
                _state.CustomLeft = newLeft;
                e.Handled = true;
            }
        }

        private void RootGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isWindowDragCandidate = false;
            if (_isWindowDragging)
            {
                _isWindowDragging = false;
                RootIslandGrid.ReleaseMouseCapture();
                AppStorage.Save(_state);
                if (!IsMouseOver && !_state.IsPinned && TodoAddInputPanel.Visibility != Visibility.Visible)
                {
                    _collapseDebounceTimer.Stop();
                    _collapseDebounceTimer.Start();
                }
                e.Handled = true;
            }
        }

        private void MenuPin_Click(object sender, RoutedEventArgs e) => TogglePin();
        private void MenuResetPosition_Click(object sender, RoutedEventArgs e) => ResetPositionToCenter();
        private void MenuJumpPage0_Click(object sender, RoutedEventArgs e) { SetVisualMode(IslandVisualMode.Expanded); SwitchToPage(0); }
        private void MenuJumpPage1_Click(object sender, RoutedEventArgs e) { SetVisualMode(IslandVisualMode.Expanded); SwitchToPage(1); }
        private void MenuJumpPage2_Click(object sender, RoutedEventArgs e) { SetVisualMode(IslandVisualMode.Expanded); SwitchToPage(2); }
        private void MenuJumpPage3_Click(object sender, RoutedEventArgs e) { SetVisualMode(IslandVisualMode.Expanded); SwitchToPage(3); }
        private void MenuJumpPage4_Click(object sender, RoutedEventArgs e) { SetVisualMode(IslandVisualMode.Expanded); SwitchToPage(4); }
        private void MenuJumpPage5_Click(object sender, RoutedEventArgs e) { HeaderSearchBtn_Click(sender, e); }
        private void MenuTestBluetooth_Click(object sender, RoutedEventArgs e) => ShowBluetoothToast("Bluetooth Audio", 80);
        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

        // =====================================================================
        // VECTOR CIRCULAR ARC HELPER
        // =====================================================================

        private static Geometry CreateCircularArcGeometry(double cx, double cy, double radius, double startAngleDeg, double sweepAngleDeg)
        {
            sweepAngleDeg = Math.Clamp(sweepAngleDeg, 0.1, 359.9);
            double startRad = (startAngleDeg - 90.0) * Math.PI / 180.0;
            double endRad = (startAngleDeg + sweepAngleDeg - 90.0) * Math.PI / 180.0;

            var startPt = new System.Windows.Point(cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad));
            var endPt = new System.Windows.Point(cx + radius * Math.Cos(endRad), cy + radius * Math.Sin(endRad));
            bool isLargeArc = sweepAngleDeg > 180.0;

            var geom = new StreamGeometry();
            using (StreamGeometryContext ctx = geom.Open())
            {
                ctx.BeginFigure(startPt, isFilled: false, isClosed: false);
                ctx.ArcTo(endPt, new System.Windows.Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();
            return geom;
        }
    }
}