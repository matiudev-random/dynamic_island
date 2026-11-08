using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IslaDinamica;

/// <summary>Una línea de la letra y el momento de la canción en que se canta.</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Letras sincronizadas desde LRCLIB (lrclib.net): gratis, sin cuenta ni clave.
/// Solo se envían el título, el artista y la duración de la canción que suena.
/// Recuerda la última canción para no volver a pedirla al pausar o reanudar.
/// </summary>
public sealed partial class LyricsService
{
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IslaDinamica/1.0 (+https://github.com/matiudev-random/dynamic_island)");
        return client;
    });

    private string? _lastKey;
    private IReadOnlyList<LyricLine>? _lastLines;

    /// <returns>Las líneas en orden, o null si la canción no tiene letra sincronizada.</returns>
    public async Task<IReadOnlyList<LyricLine>?> GetAsync(string title, string artist, TimeSpan duration)
    {
        string key = $"{artist}\n{title}";
        if (key == _lastKey)
            return _lastLines;

        IReadOnlyList<LyricLine>? lines = null;
        try
        {
            string? lrc = await FindSyncedAsync(title, artist, duration);
            lines = lrc is null ? null : Parse(lrc);
        }
        catch (Exception)
        {
            // Sin internet o LRCLIB caído: la isla sigue sin letra
            return null;
        }

        _lastKey = key;
        _lastLines = lines is { Count: > 0 } ? lines : null;
        return _lastLines;
    }

    private static async Task<string?> FindSyncedAsync(string title, string artist, TimeSpan duration)
    {
        // Primero la coincidencia exacta; si no, una búsqueda con el título sin "- Remix", "(feat. …)", etc.
        string exact = $"get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (duration > TimeSpan.Zero)
            exact += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        using (var response = await Http.Value.GetAsync(exact))
        {
            if (response.IsSuccessStatusCode &&
                (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["syncedLyrics"] is { Length: > 0 } synced)
                return synced;
        }

        string search = $"search?track_name={Uri.EscapeDataString(SimplifyTitle(title))}&artist_name={Uri.EscapeDataString(artist)}";
        if (JsonNode.Parse(await Http.Value.GetStringAsync(search)) is not JsonArray results)
            return null;

        // Entre los que tienen letra sincronizada, el de duración más parecida
        return results
            .Where(r => (string?)r?["syncedLyrics"] is { Length: > 0 })
            .OrderBy(r => Math.Abs(((double?)r?["duration"] ?? 0) - duration.TotalSeconds))
            .Select(r => (string?)r?["syncedLyrics"])
            .FirstOrDefault();
    }

    private static string SimplifyTitle(string title)
    {
        string simple = TitleExtras().Replace(title, "").Trim();
        return simple.Length > 0 ? simple : title;
    }

    /// <summary>Formato LRC: "[01:23.45] texto" (una línea puede tener varias marcas de tiempo).</summary>
    private static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            var stamps = TimeStamp().Matches(raw);
            if (stamps.Count == 0)
                continue;
            string text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                double seconds = int.Parse(m.Groups[1].Value) * 60 +
                                 double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>La línea que se está cantando en este momento ("" en los silencios).</summary>
    public static string LineAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int low = 0, high = lines.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found < 0 ? "" : lines[found].Text;
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex TimeStamp();

    [GeneratedRegex(@"\s*(-\s.*|\(.*?\)|\[.*?\])")]
    private static partial Regex TitleExtras();
}
