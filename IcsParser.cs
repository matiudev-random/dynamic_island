using System.Globalization;
using System.IO;
using System.Xml;

namespace IslaDinamica;

/// <param name="Calendar">Nombre del calendario de origen (X-WR-CALNAME).</param>
/// <param name="ColorIndex">Posición del calendario en la configuración, para elegir su color.</param>
public sealed record CalendarEvent(string Uid, string Title, string? Location, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
    string Calendar = "", int ColorIndex = 0);

/// <summary>
/// Lector mínimo de iCalendar (RFC 5545) para lo que exporta Google Calendar:
/// eventos simples, repeticiones (RRULE diaria, semanal, mensual, anual),
/// excepciones (EXDATE), ocurrencias modificadas (RECURRENCE-ID), cancelados y zonas horarias.
/// Solo devuelve lo que cae en la ventana pedida, así que el resultado es pequeño.
/// </summary>
internal static class IcsParser
{
    private const int MaxPeriods = 20000; // tope de seguridad para reglas raras

    private sealed class RawEvent
    {
        public string Uid = "";
        public string Title = "";
        public string? Location;
        public DateTimeOffset Start;
        public DateTime WallStart;          // hora "de reloj" en su zona, para repetir respetando el horario de verano
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public bool AllDay;
        public DateTimeOffset? End;
        public TimeSpan? Duration;
        public string? RRule;
        public readonly HashSet<DateTimeOffset> ExDates = new();
        public DateTimeOffset? RecurrenceId;
        public bool Cancelled;

        public TimeSpan Length => End is { } end ? end - Start : Duration ?? (AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
    }

    private readonly record struct ParsedDate(DateTimeOffset Instant, DateTime Wall, TimeZoneInfo Zone, bool AllDay);

    /// <summary>Nombre del calendario; el principal de Google se llama como el correo, lo mostramos como "Principal".</summary>
    public static string CalendarName(string ics)
    {
        foreach (string line in Unfold(ics))
        {
            if (line.StartsWith("X-WR-CALNAME:", StringComparison.OrdinalIgnoreCase))
            {
                string name = Unescape(line["X-WR-CALNAME:".Length..]);
                return name.Contains('@') ? "Principal" : name;
            }
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                break; // el nombre va en la cabecera
        }
        return "Calendario";
    }

    public static List<CalendarEvent> Parse(string ics, DateTimeOffset from, DateTimeOffset to)
    {
        var events = ReadEvents(ics);

        // Ocurrencias modificadas o canceladas de una serie, por (UID, inicio original)
        var overrides = new Dictionary<(string, DateTimeOffset), RawEvent>();
        foreach (var e in events)
        {
            if (e.RecurrenceId is { } id)
                overrides[(e.Uid, id)] = e;
        }

        var result = new List<CalendarEvent>();
        foreach (var e in events)
        {
            if (e.RecurrenceId is not null || e.Cancelled)
                continue;
            var length = e.Length;
            foreach (var start in Occurrences(e, to))
            {
                if (e.ExDates.Contains(start) || overrides.ContainsKey((e.Uid, start)))
                    continue;
                AddIfInWindow(result, e, start, start + length, from, to);
            }
        }
        foreach (var o in overrides.Values)
        {
            if (!o.Cancelled)
                AddIfInWindow(result, o, o.Start, o.Start + o.Length, from, to);
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static void AddIfInWindow(List<CalendarEvent> result, RawEvent e, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset from, DateTimeOffset to)
    {
        if (end > from && start < to)
            result.Add(new CalendarEvent(e.Uid, e.Title, e.Location, start, end, e.AllDay));
    }

    #region Lectura del archivo

    private static List<RawEvent> ReadEvents(string ics)
    {
        var events = new List<RawEvent>();
        RawEvent? current = null;
        int nested = 0; // VALARM y similares dentro del evento: sus propiedades no son del evento

        foreach (string line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new RawEvent();
                nested = 0;
                continue;
            }
            if (current is null)
                continue;
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(current);
                current = null;
                continue;
            }
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { nested++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { nested--; continue; }
            if (nested > 0)
                continue;

            var (name, parameters, value) = SplitProperty(line);
            try
            {
                ApplyProperty(current, name, parameters, value);
            }
            catch (FormatException)
            {
                // Una propiedad mal formada no debe tirar todo el calendario
            }
        }
        return events;
    }

    private static void ApplyProperty(RawEvent e, string name, Dictionary<string, string> parameters, string value)
    {
        parameters.TryGetValue("TZID", out var tzid);
        switch (name)
        {
            case "UID":
                e.Uid = value;
                break;
            case "SUMMARY":
                e.Title = Unescape(value);
                break;
            case "LOCATION":
                e.Location = string.IsNullOrWhiteSpace(value) ? null : Unescape(value);
                break;
            case "DTSTART":
                var start = ParseDate(value, tzid);
                e.Start = start.Instant;
                e.WallStart = start.Wall;
                e.Zone = start.Zone;
                e.AllDay = start.AllDay;
                break;
            case "DTEND":
                e.End = ParseDate(value, tzid).Instant;
                break;
            case "DURATION":
                e.Duration = XmlConvert.ToTimeSpan(value);
                break;
            case "RRULE":
                e.RRule = value;
                break;
            case "EXDATE":
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    e.ExDates.Add(ParseDate(part, tzid).Instant);
                break;
            case "RECURRENCE-ID":
                e.RecurrenceId = ParseDate(value, tzid).Instant;
                break;
            case "STATUS":
                e.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    /// <summary>Las líneas largas se parten y continúan con un espacio o tab al inicio.</summary>
    private static IEnumerable<string> Unfold(string ics)
    {
        using var reader = new StringReader(ics);
        string? pending = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                pending += line[1..];
                continue;
            }
            if (pending is not null)
                yield return pending;
            pending = line;
        }
        if (pending is not null)
            yield return pending;
    }

    private static (string Name, Dictionary<string, string> Parameters, string Value) SplitProperty(string line)
    {
        // El valor empieza en el primer ':' que no esté dentro de comillas
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon < 0)
            return (line.ToUpperInvariant(), new(), "");

        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0)
                parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return (head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    private static string Unescape(string text) =>
        text.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static ParsedDate ParseDate(string value, string? tzid)
    {
        value = value.Trim();
        if (value.Length == 8) // VALUE=DATE: evento de todo el día, en la hora local de este PC
        {
            var date = DateTime.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);
            return new ParsedDate(new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)), date, TimeZoneInfo.Local, true);
        }

        bool utc = value.EndsWith('Z');
        var wall = DateTime.ParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var zone = utc ? TimeZoneInfo.Utc : FindZone(tzid);
        return new ParsedDate(ToInstant(wall, zone), wall, zone, false);
    }

    private static TimeZoneInfo FindZone(string? tzid)
    {
        if (string.IsNullOrEmpty(tzid))
            return TimeZoneInfo.Local; // hora "flotante": la del PC
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzid); // .NET acepta nombres IANA (America/Santiago)
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
            wall = wall.AddHours(1); // cae en el salto del horario de verano
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }

    #endregion

    #region Repeticiones

    private static IEnumerable<DateTimeOffset> Occurrences(RawEvent e, DateTimeOffset until)
    {
        if (e.RRule is null)
        {
            yield return e.Start;
            yield break;
        }

        var rule = e.RRule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);

        string freq = rule.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out int i) && i > 0 ? i : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out int c) ? c : null;
        DateTimeOffset? ruleUntil = rule.TryGetValue("UNTIL", out var u) ? ParseDate(u, null).Instant : null;
        if (ruleUntil is { } ru && u!.Length == 8)
            ruleUntil = ru.AddDays(1); // UNTIL con solo fecha incluye ese día completo
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd)
            ? bmd.Split(',').Select(s => int.TryParse(s, out int d) ? d : 0).Where(d => d != 0).ToArray()
            : [];

        var firstDate = e.WallStart.Date;
        var time = e.WallStart.TimeOfDay;
        int produced = 0;

        for (int period = 0; period < MaxPeriods; period++)
        {
            IEnumerable<DateTime> dates = freq switch
            {
                "DAILY" => DailyDates(firstDate.AddDays((double)period * interval), byDay),
                "WEEKLY" => WeekDates(firstDate, period * interval, byDay),
                "MONTHLY" => MonthDates(firstDate, period * interval, byDay, byMonthDay),
                "YEARLY" => YearDates(firstDate, period * interval),
                _ => [],
            };

            foreach (var date in dates.OrderBy(d => d))
            {
                if (date < firstDate)
                    continue;
                var occurrence = e.AllDay
                    ? new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))
                    : ToInstant(date + time, e.Zone);
                if (ruleUntil is { } limit && occurrence >= limit && occurrence != e.Start)
                    yield break;
                if (count is { } n && produced >= n)
                    yield break;
                produced++;
                if (occurrence > until)
                    yield break;
                yield return occurrence;
            }

            if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
            {
                yield return e.Start; // regla desconocida: al menos la primera ocurrencia
                yield break;
            }
        }
    }

    private static IEnumerable<DateTime> DailyDates(DateTime date, string[] byDay)
    {
        if (byDay.Length == 0 || byDay.Any(d => ParseWeekday(d).Day == date.DayOfWeek))
            yield return date;
    }

    private static IEnumerable<DateTime> WeekDates(DateTime first, int weekOffset, string[] byDay)
    {
        var monday = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)).AddDays(7.0 * weekOffset);
        if (byDay.Length == 0)
        {
            yield return monday.AddDays(((int)first.DayOfWeek + 6) % 7);
            yield break;
        }
        foreach (var code in byDay)
            yield return monday.AddDays(((int)ParseWeekday(code).Day + 6) % 7);
    }

    private static IEnumerable<DateTime> MonthDates(DateTime first, int monthOffset, string[] byDay, int[] byMonthDay)
    {
        var month = new DateTime(first.Year, first.Month, 1).AddMonths(monthOffset);
        int days = DateTime.DaysInMonth(month.Year, month.Month);

        if (byMonthDay.Length > 0)
        {
            foreach (int d in byMonthDay)
            {
                int day = d > 0 ? d : days + d + 1; // -1 = último día del mes
                if (day >= 1 && day <= days)
                    yield return month.AddDays(day - 1);
            }
            yield break;
        }

        if (byDay.Length > 0)
        {
            foreach (var code in byDay)
            {
                var (weekday, ordinal) = ParseWeekday(code);
                var matches = Enumerable.Range(0, days).Select(k => month.AddDays(k)).Where(d => d.DayOfWeek == weekday).ToList();
                if (ordinal == 0)
                {
                    foreach (var m in matches)
                        yield return m;
                }
                else
                {
                    int index = ordinal > 0 ? ordinal - 1 : matches.Count + ordinal; // -1 = el último
                    if (index >= 0 && index < matches.Count)
                        yield return matches[index];
                }
            }
            yield break;
        }

        if (first.Day <= days)
            yield return month.AddDays(first.Day - 1);
    }

    private static IEnumerable<DateTime> YearDates(DateTime first, int yearOffset)
    {
        int year = first.Year + yearOffset;
        if (first.Day <= DateTime.DaysInMonth(year, first.Month))
            yield return new DateTime(year, first.Month, first.Day);
    }

    /// <summary>"MO", "2TU", "-1FR" → día y número de orden (0 = todos).</summary>
    private static (DayOfWeek Day, int Ordinal) ParseWeekday(string code)
    {
        code = code.Trim().ToUpperInvariant();
        string dayCode = code[^2..];
        int ordinal = code.Length > 2 && int.TryParse(code[..^2], out int o) ? o : 0;
        var day = dayCode switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday,
        };
        return (day, ordinal);
    }

    #endregion
}
