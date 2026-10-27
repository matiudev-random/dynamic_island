using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IslaDinamica;

/// <summary>Ventanita para pegar (o quitar) los enlaces iCal secretos de cada calendario, uno por línea.</summary>
public sealed class CalendarSetupWindow : Window
{
    private static readonly Brush Background1 = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    private readonly CalendarService _calendar;
    private readonly TextBox _urls;
    private readonly TextBlock _status;
    private readonly Button _save;

    public CalendarSetupWindow(CalendarService calendar)
    {
        _calendar = calendar;
        Title = "Calendarios · Isla Dinámica";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Background1;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _urls = new TextBox
        {
            Text = InitialText(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 170,
            Background = Field,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        _save = CreateButton("Guardar", Accent);
        _save.Click += async (_, _) => await SaveAsync();
        var cancel = CreateButton("Cancelar", Field);
        cancel.Click += (_, _) => Close();
        var disconnect = CreateButton("Desconectar todo", Field);
        disconnect.Visibility = calendar.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        disconnect.Click += async (_, _) =>
        {
            Settings.CalendarUrls = [];
            await _calendar.RefreshAsync();
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { disconnect, cancel, _save },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                new TextBlock { Text = "Calendarios de Google", FontSize = 17, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "Para cada calendario que quieras ver en la isla:\n" +
                           "1. En Google Calendar (web) abre Configuración (⚙).\n" +
                           "2. A la izquierda, en «Configuración de mis calendarios», elige el calendario → «Integrar calendario».\n" +
                           "3. Copia la «Dirección secreta en formato iCal» y pégala abajo, un enlace por línea.",
                    Foreground = Dim,
                    FontSize = 12,
                    LineHeight = 20,
                    Margin = new Thickness(0, 10, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                },
                _urls,
                _status,
                new TextBlock
                {
                    Text = "Los enlaces se guardan solo en este PC. No los compartas: dan acceso de lectura a tus calendarios.",
                    Foreground = Dim,
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                },
                buttons,
            },
        };

        Loaded += (_, _) =>
        {
            _urls.Focus();
            _urls.CaretIndex = _urls.Text.Length;
        };
    }

    /// <summary>Los enlaces ya guardados y, si hay uno nuevo en el portapapeles, también ese al final.</summary>
    private static string InitialText()
    {
        var urls = Settings.CalendarUrls.ToList();
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains(".ics", StringComparison.OrdinalIgnoreCase) && !urls.Contains(text))
                    urls.Add(text);
            }
        }
        catch (Exception)
        {
            // Portapapeles ocupado: no pasa nada
        }
        return urls.Count == 0 ? "" : string.Join(Environment.NewLine, urls) + Environment.NewLine;
    }

    private async Task SaveAsync()
    {
        var lines = _urls.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        if (lines.Count == 0)
        {
            ShowStatus("Pega al menos un enlace (o usa «Desconectar todo»).", Error);
            return;
        }

        int bad = lines.FindIndex(l => !File.Exists(l) &&
            !(Uri.TryCreate(l, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps));
        if (bad >= 0)
        {
            ShowStatus($"La línea {bad + 1} no es un enlace https:// válido.", Error);
            return;
        }

        var previous = Settings.CalendarUrls;
        Settings.CalendarUrls = lines.ToArray();
        _save.IsEnabled = false;
        ShowStatus($"Leyendo {lines.Count} calendario(s)…", Dim);

        var result = await _calendar.RefreshAsync();
        if (result.Loaded == 0)
        {
            Settings.CalendarUrls = previous;
            await _calendar.RefreshAsync();
            _save.IsEnabled = true;
            ShowStatus(result.FirstError ?? "No pude leer ningún calendario.", Error);
            return;
        }

        int upcoming = _calendar.Events.Count(e => e.End > DateTimeOffset.Now && e.Start < DateTimeOffset.Now.AddHours(24));
        string summary = $"{result.Loaded} de {result.Total} calendario(s) conectados · {upcoming} evento(s) en las próximas 24 horas.";
        if (result.FirstError is not null)
        {
            // Se guarda igual: los que funcionan ya se ven; el usuario puede corregir el resto
            _save.IsEnabled = true;
            ShowStatus($"{summary}\n{result.FirstError}", Warning);
            return;
        }

        ShowStatus($"¡Listo! {summary}", Ok);
        await Task.Delay(1600);
        Close();
    }

    private void ShowStatus(string text, Brush color)
    {
        _status.Text = text;
        _status.Foreground = color;
    }

    private static Button CreateButton(string text, Brush background)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, background);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 7));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);
        template.VisualTree = border;

        return new Button
        {
            Content = text,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
    }
}
