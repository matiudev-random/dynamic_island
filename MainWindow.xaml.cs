using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IslaDinamica;

public partial class MainWindow : Window
{
    private enum IslandState { Idle, Compact, Expanded, Volume, Notification, Clipboard }

    /// <summary>Páginas de la vista expandida; se cambian con la rueda del mouse.</summary>
    private enum Page { Music, Agenda, System, Clipboard }

    private sealed class Bar(ScaleTransform transform, DoubleAnimation animation)
    {
        public ScaleTransform Transform { get; } = transform;
        public DoubleAnimation Animation { get; } = animation;
        public bool Running { get; set; }
    }

    private const int MaxPendingNotifications = 5;
    private const int MaxClipboardHistory = 4;
    private const int MaxAgendaRows = 5;
    private static readonly TimeSpan EventHeadsUp = TimeSpan.FromMinutes(10);
    private static readonly Brush CalendarBlue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>Un color por calendario, según su orden en la configuración.</summary>
    private static readonly Color[] CalendarColors =
    [
        Color.FromRgb(0x0A, 0x84, 0xFF), // azul
        Color.FromRgb(0x30, 0xD1, 0x58), // verde
        Color.FromRgb(0xFF, 0x9F, 0x0A), // naranja
        Color.FromRgb(0xBF, 0x5A, 0xF2), // morado
        Color.FromRgb(0xFF, 0x37, 0x5F), // rosa
        Color.FromRgb(0x64, 0xD2, 0xFF), // celeste
        Color.FromRgb(0xFF, 0xD6, 0x0A), // amarillo
        Color.FromRgb(0xFF, 0x45, 0x3A), // rojo
        Color.FromRgb(0x5E, 0x5C, 0xE6), // índigo
        Color.FromRgb(0xAC, 0x8E, 0x68), // café
    ];

    private static Color CalendarColor(CalendarEvent e) => CalendarColors[e.ColorIndex % CalendarColors.Length];
    private static readonly Brush PlaceholderGray = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private const int GraphSamples = 30;
    private const double GraphWidth = 100;
    private const double GraphHeight = 28;

    private static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(420);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(160);
    private static readonly Brush DotActive = Brushes.White;
    private static readonly Brush DotInactive = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHover = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly NotificationService _notifications = new();
    private readonly CalendarService _calendar = new();
    private readonly HashSet<string> _alertedEvents = new();
    private ClipboardWatcher? _clipboard;
    private SystemMonitor? _monitor;

    private readonly Queue<NotificationInfo> _pendingNotifications = new();
    private readonly List<ClipItem> _clipHistory = new();
    private readonly Queue<double> _cpuHistory = new(), _ramHistory = new(), _gpuHistory = new();
    private readonly SolidColorBrush _barBrush = new(Colors.White);
    private readonly List<Bar> _compactBars = new();
    private readonly List<Bar> _expandedBars = new();

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _systemTimer;
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _clipToastTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly DispatcherTimer _trimTimer;
    private readonly DispatcherTimer _calendarTimer;

    private IslandState _state = IslandState.Idle;
    private Page _page = Page.System;
    private FrameworkElement _currentView;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private bool _hovering;

    public MainWindow()
    {
        InitializeComponent();
        _currentView = IdleView;

        CreateBars(CompactBars, _compactBars, count: 4, height: 14);
        CreateBars(ExpandedBars, _expandedBars, count: 5, height: 22);
        CpuSub.Text = $"{Environment.ProcessorCount} hilos";

        _clockTimer = CreateTimer(1000, UpdateClock);
        _progressTimer = CreateTimer(500, UpdateProgress);
        _systemTimer = CreateTimer(1000, SampleSystem);
        _volumeTimer = CreateTimer(1600, ReturnToRest, oneShot: true);
        _notificationTimer = CreateTimer(4500, ReturnToRest, oneShot: true);
        _clipToastTimer = CreateTimer(1600, ReturnToRest, oneShot: true);
        _leaveTimer = CreateTimer(350, () => { if (!_hovering && _state == IslandState.Expanded) GoTo(RestingState()); }, oneShot: true);
        _fullscreenTimer = CreateTimer(2000, CheckFullscreen);
        _trimTimer = CreateTimer(30000, TrimMemory);
        _calendarTimer = CreateTimer(20000, OnCalendarTick);

        UpdateClock();
        _clockTimer.Start();
        _fullscreenTimer.Start();
        _trimTimer.Start();
        _calendarTimer.Start();

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _volume.Dispose();
            _notifications.Dispose();
            _calendar.Dispose();
            _clipboard?.Dispose();
            _monitor?.Dispose();
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow);
    }

    private DispatcherTimer CreateTimer(int milliseconds, Action tick, bool oneShot = false)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            if (oneShot)
                timer.Stop();
            tick();
        };
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Fuera de Alt+Tab y sin robar el foco al hacer clic
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        _clipboard = new ClipboardWatcher(HwndSource.FromHwnd(hwnd));
        _clipboard.Copied += OnClipboardCopied;

        PositionWindow();
    }

    private void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _volume.Changed += (level, muted) => Dispatcher.InvokeAsync(() => OnVolumeChanged(level, muted));
        _volume.Start();

        _media.Changed += snapshot => Dispatcher.InvokeAsync(() => OnMediaChanged(snapshot));
        try
        {
            await _media.StartAsync();
        }
        catch (Exception)
        {
            // Sin acceso a los controles de medios: la isla queda como reloj.
        }

        _calendar.Changed += () => Dispatcher.InvokeAsync(OnCalendarChanged);
        _calendar.Start();

        _notifications.Received += info => Dispatcher.InvokeAsync(() => OnNotification(info));
        try
        {
            await _notifications.StartAsync();
        }
        catch (Exception)
        {
            // Sin permiso de notificaciones: el resto de la isla sigue funcionando.
        }
    }

    #region Estados y animación

    private IslandState RestingState() =>
        _hovering ? IslandState.Expanded
        : _snapshot.IsPlaying ? IslandState.Compact
        : IslandState.Idle;

    /// <summary>Al terminar un aviso temporal: muestra la siguiente notificación pendiente o vuelve al reposo.</summary>
    private void ReturnToRest()
    {
        _notificationTimer.Stop();
        if (_pendingNotifications.TryDequeue(out var next))
            ShowNotification(next);
        else
            GoTo(RestingState());
    }

    private List<Page> AvailablePages()
    {
        var pages = new List<Page>(3);
        if (_snapshot.HasSession)
            pages.Add(Page.Music);
        if (_calendar.IsConfigured)
            pages.Add(Page.Agenda);
        pages.Add(Page.System);
        if (Settings.ClipboardEnabled)
            pages.Add(Page.Clipboard);
        return pages;
    }

    /// <param name="force">Vuelve a aplicar el diseño aunque el estado no cambie (p. ej. al cambiar de página).</param>
    private void GoTo(IslandState state, bool force = false)
    {
        if (_state == state && !force)
            return;
        bool opening = state == IslandState.Expanded && _state != IslandState.Expanded;
        _state = state;

        // Al expandirse (también al terminar un aviso con el mouse encima) empieza por la primera página
        var pages = AvailablePages();
        if (state == IslandState.Expanded && (opening || !pages.Contains(_page)))
            _page = pages[0];

        (FrameworkElement view, double width, double height, double radius) layout = state switch
        {
            IslandState.Compact => (CompactView, 250, 36, 16),
            IslandState.Expanded => _page switch
            {
                Page.Music => (MusicView, 400, 162, 28),
                Page.Agenda => (AgendaView, 400, AgendaPageHeight(), 28),
                Page.System => (SystemView, 400, 138, 28),
                _ => (ClipboardView, 400, ClipboardPageHeight(), 28),
            },
            IslandState.Volume => (VolumeView, 280, 36, 16),
            IslandState.Notification => (NotificationView, 390, 80, 24),
            IslandState.Clipboard => (ClipToastView, 320, 40, 18),
            _ => (IdleView, 130, 34, 16),
        };
        Morph(layout.width, layout.height, layout.radius);
        ShowOnly(layout.view);
        UpdatePageDots(state == IslandState.Expanded ? pages : null);

        bool expanded = state == IslandState.Expanded;
        _progressTimer.IsEnabled = expanded && _page == Page.Music;
        if (_progressTimer.IsEnabled)
            UpdateProgress();
        _systemTimer.IsEnabled = expanded && _page == Page.System;
        if (_systemTimer.IsEnabled)
            SampleSystem();
        if (expanded && _page == Page.Clipboard)
            RebuildClipboardList();
        if (expanded && _page == Page.Agenda)
            RebuildAgenda();
        UpdateVisualizer();
    }

    /// <summary>Radio de las esquinas inferiores; las superiores van rectas, pegadas al borde.</summary>
    private static readonly DependencyProperty BottomRadiusProperty = DependencyProperty.Register(
        nameof(BottomRadius), typeof(double), typeof(MainWindow),
        new PropertyMetadata(16d, (d, e) =>
        {
            double r = Math.Max(0, (double)e.NewValue);
            ((MainWindow)d).Pill.CornerRadius = new CornerRadius(0, 0, r, r);
        }));

    private double BottomRadius
    {
        get => (double)GetValue(BottomRadiusProperty);
        set => SetValue(BottomRadiusProperty, value);
    }

    private void Morph(double width, double height, double radius)
    {
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(width, MorphDuration) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(height, MorphDuration) { EasingFunction = ease });
        BeginAnimation(BottomRadiusProperty, new DoubleAnimation(radius, MorphDuration) { EasingFunction = ease });
    }

    private void ShowOnly(FrameworkElement target)
    {
        var previous = _currentView;
        _currentView = target;
        if (previous == target)
            return;

        var fadeOut = new DoubleAnimation(0, FadeDuration);
        fadeOut.Completed += (_, _) =>
        {
            if (_currentView != previous)
                previous.Visibility = Visibility.Collapsed;
        };
        previous.BeginAnimation(OpacityProperty, fadeOut);

        // El contenido nuevo aparece cuando la isla ya va creciendo
        target.Visibility = Visibility.Visible;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration) { BeginTime = TimeSpan.FromMilliseconds(90) });
    }

    private void UpdatePageDots(List<Page>? pages)
    {
        PageDots.Children.Clear();
        if (pages is null || pages.Count < 2)
        {
            PageDots.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var page in pages)
        {
            PageDots.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(2.5, 0, 2.5, 0),
                Fill = page == _page ? DotActive : DotInactive,
            });
        }
        PageDots.Visibility = Visibility.Visible;
    }

    private void CreateBars(Panel host, List<Bar> bars, int count, double height)
    {
        int[] periods = [420, 330, 510, 380, 460];
        for (int i = 0; i < count; i++)
        {
            var transform = new ScaleTransform(1, 0.3);
            host.Children.Add(new Rectangle
            {
                Width = 3,
                Height = height,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Fill = _barBrush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform,
            });

            var animation = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(periods[i % periods.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(animation, 30); // la mitad de cuadros: menos CPU, se ve igual
            bars.Add(new Bar(transform, animation));
        }
    }

    /// <summary>Solo anima las barras visibles y mientras suena algo.</summary>
    private void UpdateVisualizer()
    {
        bool playing = _snapshot.IsPlaying && IsVisible;
        SetBars(_compactBars, playing && _state == IslandState.Compact);
        SetBars(_expandedBars, playing && _state == IslandState.Expanded && _page == Page.Music);
    }

    private static void SetBars(List<Bar> bars, bool run)
    {
        foreach (var bar in bars)
        {
            if (bar.Running == run)
                continue;
            bar.Running = run;
            bar.Transform.BeginAnimation(ScaleTransform.ScaleYProperty, run ? bar.Animation : null);
            if (!run)
                bar.Transform.ScaleY = 0.3;
        }
    }

    #endregion

    #region Música

    private void UpdateClock() => IdleView.Text = DateTime.Now.ToString("HH:mm");

    private void OnMediaChanged(MediaSnapshot snapshot)
    {
        bool hadSession = _snapshot.HasSession;
        _snapshot = snapshot;

        TitleText.Text = snapshot.Title;
        ArtistText.Text = snapshot.Artist;
        CompactArt.ImageSource = snapshot.Art;
        ExpandedArt.ImageSource = snapshot.Art;
        _barBrush.Color = snapshot.Art is null ? Colors.White : snapshot.Accent;
        PlayPauseButton.Content = snapshot.IsPlaying ? "" : "";

        if (_state == IslandState.Expanded && hadSession != snapshot.HasSession)
            GoTo(IslandState.Expanded, force: true); // apareció o desapareció la página de música
        else if (_state is IslandState.Idle or IslandState.Compact)
            GoTo(RestingState());
        UpdateVisualizer();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var duration = _snapshot.Duration;
        if (duration <= TimeSpan.Zero)
        {
            ProgressScale.ScaleX = 0;
            PositionText.Text = DurationText.Text = "";
            return;
        }

        var position = _snapshot.EstimatedPosition;
        ProgressScale.ScaleX = Math.Clamp(position / duration, 0, 1);
        PositionText.Text = Format(position);
        DurationText.Text = Format(duration);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    #endregion

    #region Monitor del sistema

    private void SampleSystem()
    {
        _monitor ??= new SystemMonitor();
        var sample = _monitor.Read();

        CpuText.Text = $"{sample.Cpu:0}%";
        RamText.Text = $"{sample.Ram:0}%";
        RamSub.Text = $"{sample.RamUsedGb:0.0} / {sample.RamTotalGb:0} GB";
        GpuText.Text = double.IsNaN(sample.Gpu) ? "—" : $"{sample.Gpu:0}%";

        DrawGraph(CpuGraph, _cpuHistory, sample.Cpu);
        DrawGraph(RamGraph, _ramHistory, sample.Ram);
        DrawGraph(GpuGraph, _gpuHistory, double.IsNaN(sample.Gpu) ? 0 : sample.Gpu);
    }

    private static void DrawGraph(Polyline graph, Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > GraphSamples)
            history.Dequeue();

        double step = GraphWidth / (GraphSamples - 1);
        double x = GraphWidth - step * (history.Count - 1); // los datos nuevos entran por la derecha
        var points = new PointCollection(history.Count);
        foreach (double v in history)
        {
            points.Add(new Point(x, GraphHeight - 1 - v / 100 * (GraphHeight - 2)));
            x += step;
        }
        points.Freeze();
        graph.Points = points;
    }

    #endregion

    #region Portapapeles

    private double ClipboardPageHeight() => 66 + 30 * Math.Max(1, _clipHistory.Count);

    private void OnClipboardCopied(ClipItem item)
    {
        if (!Settings.ClipboardEnabled)
            return;

        // Sin duplicados: lo que se vuelve a copiar sube al primer lugar
        _clipHistory.RemoveAll(existing =>
            (item.Text is not null && existing.Text == item.Text) ||
            (item.Files is not null && existing.Files is not null &&
             string.Join('|', existing.Files.Cast<string>()) == string.Join('|', item.Files.Cast<string>())));
        _clipHistory.Insert(0, item);
        if (_clipHistory.Count > MaxClipboardHistory)
            _clipHistory.RemoveAt(_clipHistory.Count - 1);

        if (_state == IslandState.Expanded)
        {
            if (_page == Page.Clipboard)
                GoTo(IslandState.Expanded, force: true); // la altura depende de la cantidad de elementos
            return;
        }
        if (!IsVisible || _state is IslandState.Notification or IslandState.Volume)
            return;

        bool isImage = item.Thumbnail is not null;
        ClipToastImage.ImageSource = item.Thumbnail;
        ClipToastThumb.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        ClipToastCheck.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        ClipToastText.Text = item.Preview;

        GoTo(IslandState.Clipboard);
        Restart(_clipToastTimer);
    }

    private void RebuildClipboardList()
    {
        ClipboardList.Children.Clear();
        ClearClipboardButton.Visibility = _clipHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_clipHistory.Count == 0)
        {
            ClipboardList.Children.Add(new TextBlock
            {
                Text = "Copia algo y aparecerá aquí",
                Foreground = (Brush)FindResource("Dim"),
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(6, 7, 0, 0),
            });
            return;
        }

        foreach (var item in _clipHistory)
            ClipboardList.Children.Add(CreateClipRow(item));
    }

    private Border CreateClipRow(ClipItem item)
    {
        FrameworkElement icon = item.Thumbnail is not null
            ? new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new ImageBrush(item.Thumbnail) { Stretch = Stretch.UniformToFill },
            }
            : new TextBlock
            {
                Width = 22,
                Text = item.Kind == ClipKind.Files ? "" : "",
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 13,
                Foreground = (Brush)FindResource("Dim"),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var text = new TextBlock
        {
            Text = item.Preview,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);

        bool canRecopy = item.Text is not null || item.Files is not null;
        var row = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 0),
            Background = Brushes.Transparent,
            Cursor = canRecopy ? Cursors.Hand : Cursors.Arrow,
            ToolTip = canRecopy ? "Clic para volver a copiar" : null,
            Child = new DockPanel { Children = { icon, text } },
        };
        row.MouseEnter += (_, _) => row.Background = RowHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (canRecopy)
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ClipboardWatcher.Recopy(item);
            };
        return row;
    }

    private void ClearClipboardHistory_Click(object sender, RoutedEventArgs e)
    {
        _clipHistory.Clear();
        GoTo(IslandState.Expanded, force: true);
    }

    #endregion

    #region Calendario

    /// <summary>Para abrir la ventana de configuración desde la bandeja.</summary>
    public CalendarService Calendar => _calendar;

    /// <summary>Eventos que todavía no terminan, dentro de las próximas 24 horas.</summary>
    private List<CalendarEvent> UpcomingEvents()
    {
        var now = DateTimeOffset.Now;
        return _calendar.Events.Where(e => e.End > now && e.Start < now.AddHours(24)).ToList();
    }

    private double AgendaPageHeight() => 66 + 30 * Math.Clamp(UpcomingEvents().Count, 1, MaxAgendaRows);

    private void OnCalendarChanged()
    {
        if (_state == IslandState.Expanded && (_page == Page.Agenda || !_calendar.IsConfigured))
            GoTo(IslandState.Expanded, force: true); // cambió la lista o desapareció la página
        OnCalendarTick();
    }

    /// <summary>Cada 20 s: avisa 10 min antes y al empezar; refresca los "en N min" de la agenda.</summary>
    private void OnCalendarTick()
    {
        var now = DateTimeOffset.Now;
        foreach (var e in UpcomingEvents())
        {
            if (e.AllDay)
                continue;
            var until = e.Start - now;
            string key = $"{e.Uid}|{e.Start.UtcTicks}";

            if (until <= EventHeadsUp && until > TimeSpan.FromMinutes(1) && _alertedEvents.Add(key + "|10"))
                EnqueueAlert(EventAlert(e, $"En {Math.Ceiling(until.TotalMinutes)} min"));
            else if (until <= TimeSpan.FromMinutes(1) && until > TimeSpan.FromMinutes(-2) && _alertedEvents.Add(key + "|0"))
                EnqueueAlert(EventAlert(e, "Empieza ahora"));
        }

        if (_state == IslandState.Expanded && _page == Page.Agenda)
            RebuildAgenda();
    }

    private static NotificationInfo EventAlert(CalendarEvent e, string when)
    {
        string body = $"{when} · {e.Start.LocalDateTime:HH:mm}–{e.End.LocalDateTime:HH:mm}";
        if (e.Location is not null)
            body += $" · {e.Location}";
        return new NotificationInfo(e.Calendar.Length > 0 ? e.Calendar : "Calendario", e.Title, body, null,
            Glyph: "", GlyphColor: CalendarColor(e));
    }

    private void RebuildAgenda()
    {
        AgendaDate.Text = DateTime.Now.ToString("dddd d 'de' MMMM");
        AgendaList.Children.Clear();

        var events = UpcomingEvents();
        if (events.Count == 0)
        {
            AgendaList.Children.Add(new TextBlock
            {
                Text = "Sin eventos en las próximas 24 horas",
                Foreground = (Brush)FindResource("Dim"),
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(6, 7, 0, 0),
            });
            return;
        }

        foreach (var e in events.Take(MaxAgendaRows))
            AgendaList.Children.Add(CreateAgendaRow(e));
    }

    private Grid CreateAgendaRow(CalendarEvent e)
    {
        var now = DateTimeOffset.Now;
        var start = e.Start.LocalDateTime;
        bool tomorrow = start.Date > DateTime.Today;
        string time = e.AllDay ? "Todo el día" : (tomorrow ? "Mañ. " : "") + start.ToString("HH:mm");
        string relative = e.AllDay ? ""
            : e.Start <= now ? "ahora"
            : e.Start - now < TimeSpan.FromHours(1) ? $"en {Math.Ceiling((e.Start - now).TotalMinutes)} min"
            : "";
        bool happening = !e.AllDay && e.Start <= now;

        var dim = (Brush)FindResource("Dim");
        var row = new Grid { Height = 30, Margin = new Thickness(6, 0, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var timeText = new TextBlock { Text = time, Foreground = happening ? CalendarBlue : dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        // Punto con el color del calendario; el nombre aparece al pasar el mouse
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(CalendarColor(e)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(dot, Dock.Left);
        var titleText = new TextBlock
        {
            Text = e.Title.Length > 0 ? e.Title : "(sin título)",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = happening ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new DockPanel { Children = { dot, titleText }, ToolTip = e.Calendar, Background = Brushes.Transparent };

        var relativeText = new TextBlock { Text = relative, Foreground = happening ? CalendarBlue : dim, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(relativeText, 2);
        row.Children.Add(timeText);
        row.Children.Add(title);
        row.Children.Add(relativeText);
        return row;
    }

    #endregion

    #region Volumen y notificaciones

    private void OnVolumeChanged(float level, bool muted)
    {
        if (!IsVisible)
            return;

        VolumeIcon.Text = muted || level <= 0.001f ? ""
            : level < 0.34f ? ""
            : level < 0.67f ? ""
            : "";
        VolumeText.Text = muted ? "" : Math.Round(level * 100).ToString();
        VolumeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(muted ? 0 : level, TimeSpan.FromMilliseconds(120)));

        GoTo(IslandState.Volume);
        Restart(_volumeTimer);
    }

    private void OnNotification(NotificationInfo info)
    {
        if (Settings.NotificationsEnabled)
            EnqueueAlert(info);
    }

    /// <summary>Notificaciones de Windows y avisos propios (calendario) comparten la misma cola.</summary>
    private void EnqueueAlert(NotificationInfo info)
    {
        if (!IsVisible)
            return;

        NotificationSound.Play();
        if (_pendingNotifications.Count >= MaxPendingNotifications)
            _pendingNotifications.Dequeue();
        _pendingNotifications.Enqueue(info);

        if (_state is not (IslandState.Notification or IslandState.Volume))
            ReturnToRest();
    }

    private void ShowNotification(NotificationInfo info)
    {
        NotificationApp.Text = info.App;
        NotificationTitle.Text = info.Title;
        NotificationBody.Text = info.Body;
        NotificationBody.Visibility = info.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationIcon.ImageSource = info.Icon;
        NotificationPlaceholder.Visibility = info.Icon is null ? Visibility.Visible : Visibility.Collapsed;
        NotificationPlaceholder.Background = info.Glyph is null ? PlaceholderGray
            : info.GlyphColor is { } color ? new SolidColorBrush(color) : CalendarBlue;
        NotificationInitial.FontFamily = info.Glyph is null ? FontFamily : (FontFamily)FindResource("Icons");
        NotificationInitial.Text = info.Glyph ?? (info.App.Length > 0 ? info.App[..1].ToUpperInvariant() : "•");

        if (_state == IslandState.Notification)
        {
            // Ya se ve una: un pequeño parpadeo marca el cambio de contenido
            NotificationView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.2, 1, FadeDuration));
        }
        GoTo(IslandState.Notification);
        _notificationTimer.Start();
    }

    #endregion

    #region Visibilidad y memoria

    /// <summary>
    /// En reposo, compacta el heap y devuelve a Windows la RAM sin usar.
    /// Lo que se usa al animar vuelve solo; lo que quedó del arranque no.
    /// </summary>
    private void TrimMemory()
    {
        if (_state is not (IslandState.Idle or IslandState.Compact))
            return;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        NativeMethods.TrimWorkingSet();
    }

    /// <summary>Ocultada a mano desde la bandeja (tiene prioridad sobre la detección de pantalla completa).</summary>
    public bool UserHidden { get; private set; }

    public void SetUserHidden(bool hidden)
    {
        UserHidden = hidden;
        CheckFullscreen();
    }

    private void CheckFullscreen()
    {
        bool shouldShow = !UserHidden && !NativeMethods.IsFullscreenAppActive();
        if (shouldShow == IsVisible)
            return;
        if (shouldShow)
        {
            Show();
        }
        else
        {
            Hide();
            // Lo que llegó mientras estaba oculta ya no es relevante
            _pendingNotifications.Clear();
            if (_state is not (IslandState.Idle or IslandState.Compact))
            {
                _hovering = false;
                GoTo(RestingState());
            }
        }
        UpdateVisualizer();
    }

    #endregion

    #region Interacción

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _leaveTimer.Stop();
        if (_state is IslandState.Idle or IslandState.Compact)
            GoTo(IslandState.Expanded);
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        Restart(_leaveTimer);
    }

    /// <summary>Rueda del mouse: siguiente o anterior página.</summary>
    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state != IslandState.Expanded)
            return;
        var pages = AvailablePages();
        if (pages.Count < 2)
            return;

        int index = Math.Max(0, pages.IndexOf(_page));
        index = (index + (e.Delta < 0 ? 1 : -1) + pages.Count) % pages.Count;
        _page = pages[index];
        GoTo(IslandState.Expanded, force: true);
        e.Handled = true;
    }

    /// <summary>Clic sobre un aviso (notificación o "Copiado"): lo descarta.</summary>
    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state is IslandState.Notification or IslandState.Clipboard)
            ReturnToRest();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ProgressTrack.ActualWidth > 0)
            await _media.SeekAsync(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    #endregion
}
