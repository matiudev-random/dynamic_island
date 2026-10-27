using System.Windows.Media.Imaging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace IslaDinamica;

/// <param name="Glyph">Ícono de Segoe Fluent para avisos propios (p. ej. calendario) en lugar de la imagen de la app.</param>
/// <param name="GlyphColor">Fondo del ícono (p. ej. el color del calendario).</param>
public sealed record NotificationInfo(string App, string Title, string Body, BitmapSource? Icon,
    string? Glyph = null, System.Windows.Media.Color? GlyphColor = null);

/// <summary>
/// Lee las notificaciones de Windows (WhatsApp, Discord, correo...).
/// El evento NotificationChanged solo funciona en apps empaquetadas (MSIX),
/// así que consultamos la lista cada segundo y comparamos IDs: es una llamada barata.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    private readonly HashSet<uint> _seen = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private readonly CancellationTokenSource _cts = new();
    private UserNotificationListener? _listener;

    /// <summary>Se dispara desde un hilo de fondo.</summary>
    public event Action<NotificationInfo>? Received;

    /// <returns>False si el usuario no dio permiso de acceso a notificaciones.</returns>
    public async Task<bool> StartAsync()
    {
        _listener = UserNotificationListener.Current;
        if (await _listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
            return false;

        // Las que ya estaban en el centro de notificaciones no se muestran
        foreach (var existing in await _listener.GetNotificationsAsync(NotificationKinds.Toast))
            _seen.Add(existing.Id);

        _ = Task.Run(PollLoopAsync);
        return true;
    }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    await PollAsync();
                }
                catch (Exception)
                {
                    // Un fallo puntual de la API no debe detener el sondeo
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollAsync()
    {
        var current = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);

        var currentIds = new HashSet<uint>();
        var fresh = new List<UserNotification>();
        foreach (var notification in current)
        {
            currentIds.Add(notification.Id);
            if (_seen.Add(notification.Id))
                fresh.Add(notification);
        }
        _seen.IntersectWith(currentIds); // olvida las que el usuario ya descartó

        foreach (var notification in fresh.OrderBy(n => n.CreationTime))
        {
            if (DateTimeOffset.Now - notification.CreationTime > MaxAge)
                continue;
            if (await ToInfoAsync(notification) is { } info)
                Received?.Invoke(info);
        }
    }

    private async Task<NotificationInfo?> ToInfoAsync(UserNotification notification)
    {
        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (texts is null || texts.Count == 0)
            return null;

        string app = "";
        BitmapSource? icon = null;
        try
        {
            var appInfo = notification.AppInfo;
            app = appInfo?.DisplayInfo?.DisplayName ?? "";
            icon = await GetIconAsync(appInfo);
        }
        catch (Exception)
        {
            // Algunas apps Win32 no exponen información de app
        }

        return new NotificationInfo(app, texts[0], string.Join(" ", texts.Skip(1)), icon);
    }

    private async Task<BitmapSource?> GetIconAsync(Windows.ApplicationModel.AppInfo? appInfo)
    {
        if (appInfo is null)
            return null;

        string key = appInfo.AppUserModelId;
        if (_iconCache.TryGetValue(key, out var cached))
            return cached;

        var logo = appInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
        var icon = await ImageLoader.LoadAsync(logo, 80);
        _iconCache[key] = icon;
        return icon;
    }

    public void Dispose() => _cts.Cancel();
}
