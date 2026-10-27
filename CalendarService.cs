using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;

namespace IslaDinamica;

/// <param name="Loaded">Calendarios leídos bien en esta actualización.</param>
/// <param name="Total">Calendarios configurados.</param>
/// <param name="FirstError">Motivo del primer fallo, para mostrarlo al usuario.</param>
public sealed record CalendarRefreshResult(int Loaded, int Total, string? FirstError);

/// <summary>
/// Descarga todos los calendarios (enlaces iCal secretos de Google) en paralelo cada 10 minutos
/// y une sus eventos de las próximas horas. Si uno falla, se conservan sus últimos eventos conocidos.
/// El cliente HTTP se crea recién cuando hay enlaces.
/// </summary>
public sealed class CalendarService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, List<CalendarEvent>> _lastGood = new();

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];

    public bool IsConfigured => Settings.CalendarUrls.Length > 0;

    /// <summary>Se dispara desde un hilo de fondo.</summary>
    public event Action? Changed;

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            await RefreshAsync();
            using var timer = new PeriodicTimer(RefreshInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                    await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task<CalendarRefreshResult> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var urls = Settings.CalendarUrls;
            var now = DateTimeOffset.Now;
            var errors = new string?[urls.Length];

            await Task.WhenAll(urls.Select(async (url, index) =>
            {
                try
                {
                    _lastGood[url] = await LoadAsync(url, index, now);
                }
                catch (Exception ex)
                {
                    errors[index] = Describe(ex, index + 1);
                }
            }));

            // Los enlaces quitados de la configuración dejan de aportar eventos
            foreach (var stale in _lastGood.Keys.Except(urls).ToList())
                _lastGood.TryRemove(stale, out _);

            // Un mismo evento puede estar en varios calendarios (invitaciones): se muestra una vez
            Events = urls
                .SelectMany(u => _lastGood.TryGetValue(u, out var list) ? list : [])
                .DistinctBy(e => (e.Uid, e.Start))
                .OrderBy(e => e.Start)
                .ToList();
            Changed?.Invoke();

            int failed = errors.Count(e => e is not null);
            return new CalendarRefreshResult(urls.Length - failed, urls.Length, errors.FirstOrDefault(e => e is not null));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<CalendarEvent>> LoadAsync(string url, int index, DateTimeOffset now)
    {
        // También acepta una ruta local a un .ics (útil para probar)
        string ics = File.Exists(url)
            ? await File.ReadAllTextAsync(url, _cts.Token)
            : await Http.Value.GetStringAsync(url, _cts.Token);
        if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("no es un calendario iCal");

        return await Task.Run(() =>
        {
            string name = IcsParser.CalendarName(ics);
            return IcsParser.Parse(ics, now.AddHours(-2), now.AddHours(36))
                .Select(e => e with { Calendar = name, ColorIndex = index })
                .ToList();
        });
    }

    private static string Describe(Exception ex, int line) => ex switch
    {
        HttpRequestException http => $"Línea {line}: no pude descargarlo ({(int?)http.StatusCode ?? 0}). Revisa el enlace.",
        TaskCanceledException => $"Línea {line}: la descarga tardó demasiado.",
        FormatException => $"Línea {line}: el enlace no devolvió un calendario (¿es la dirección secreta en formato iCal?).",
        _ => $"Línea {line}: {ex.Message}",
    };

    public void Dispose() => _cts.Cancel();
}
