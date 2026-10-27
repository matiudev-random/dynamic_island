using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace IslaDinamica;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Artist,
    BitmapSource? Art,
    Color Accent,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt)
{
    public static readonly MediaSnapshot Empty =
        new(false, "", "", null, Colors.White, false, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);

    /// <summary>Windows actualiza la posición pocas veces; la interpolamos localmente.</summary>
    public TimeSpan EstimatedPosition
    {
        get
        {
            if (!IsPlaying || Duration <= TimeSpan.Zero)
                return Position;
            var estimated = Position + (DateTimeOffset.Now - PositionUpdatedAt);
            return estimated > Duration ? Duration : estimated;
        }
    }
}

/// <summary>
/// Lee lo que suena en el sistema (Spotify, navegador, etc.) mediante los
/// controles de transporte de medios de Windows (SMTC). Sin claves de API.
/// </summary>
public sealed class MediaService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artKey = "";
    private BitmapSource? _art;
    private Color _accent = Colors.White;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;

    /// <summary>Se dispara desde un hilo de fondo.</summary>
    public event Action<MediaSnapshot>? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => AttachCurrentSession();
        AttachCurrentSession();
    }

    private void AttachCurrentSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync(includeProperties: true);
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ = RefreshAsync(includeProperties: true);

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ = RefreshAsync(includeProperties: false);

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ = RefreshAsync(includeProperties: false);

    private async Task RefreshAsync(bool includeProperties)
    {
        await _gate.WaitAsync();
        try
        {
            var session = _session;
            if (session is null)
            {
                _artKey = "";
                Publish(MediaSnapshot.Empty);
                return;
            }

            string title = Current.Title;
            string artist = Current.Artist;

            if (includeProperties || !Current.HasSession)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";

                // Spotify a veces manda la carátula en un segundo evento: reintentamos si aún no hay
                string key = title + "\u0001" + artist;
                if (key != _artKey || _art is null)
                {
                    (_art, _accent) = await LoadArtAsync(props?.Thumbnail);
                    _artKey = _art is null ? "" : key;
                }
            }

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;

            Publish(new MediaSnapshot(
                HasSession: true,
                Title: title,
                Artist: artist,
                Art: _art,
                Accent: _accent,
                IsPlaying: playing,
                Position: timeline?.Position ?? TimeSpan.Zero,
                Duration: duration,
                PositionUpdatedAt: timeline?.LastUpdatedTime ?? DateTimeOffset.Now));
        }
        catch (Exception)
        {
            // La sesión puede desaparecer mientras la leemos (app cerrada); se ignora.
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static async Task<(BitmapSource?, Color)> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        // 120px basta para 56px a 200% de escala
        var image = await ImageLoader.LoadAsync(thumbnail, 120);
        return image is null ? (null, Colors.White) : (image, AccentFrom(image));
    }

    /// <summary>Color promedio de la carátula, aclarado para que se lea sobre negro.</summary>
    private static Color AccentFrom(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < pixels.Length; i += 16)
        {
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
            n++;
        }
        if (n == 0)
            return Colors.White;

        double rr = r / (double)n, gg = g / (double)n, bb = b / (double)n;
        double max = Math.Max(rr, Math.Max(gg, bb));
        double scale = max < 1 ? 1 : 235 / max;

        static byte Mix(double channel, double scale) => (byte)Math.Clamp(channel * scale * 0.8 + 255 * 0.2, 0, 255);
        return Color.FromRgb(Mix(rr, scale), Mix(gg, scale), Mix(bb, scale));
    }

    public async Task PlayPauseAsync()
    {
        if (_session is { } s)
            await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_session is { } s)
            await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session is { } s)
            await s.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(double fraction)
    {
        if (_session is not { } s)
            return;
        var timeline = s.GetTimelineProperties();
        var target = timeline.StartTime + (timeline.EndTime - timeline.StartTime) * Math.Clamp(fraction, 0, 1);
        await s.TryChangePlaybackPositionAsync(target.Ticks);
    }
}
