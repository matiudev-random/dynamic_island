using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslaDinamica;

public enum ClipKind { Text, Image, Files }

/// <summary>Algo copiado. Text o Files permiten volver a copiarlo; de las imágenes solo guardamos la miniatura.</summary>
public sealed record ClipItem(ClipKind Kind, string Preview, string? Text, StringCollection? Files, BitmapSource? Thumbnail);

/// <summary>
/// Escucha el portapapeles con AddClipboardFormatListener (Windows avisa, sin sondeo).
/// Respeta a los gestores de contraseñas: lo que marcan como privado se ignora.
/// El historial vive solo en memoria.
/// </summary>
public sealed partial class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int ThumbnailSize = 56; // 28px a 200% de escala

    private readonly HwndSource _source;

    /// <summary>Se dispara en el hilo de la interfaz.</summary>
    public event Action<ClipItem>? Copied;

    public ClipboardWatcher(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            _ = ReadWithRetryAsync();
        return IntPtr.Zero;
    }

    /// <summary>La app que copió puede tener el portapapeles abierto un instante: reintentamos.</summary>
    private async Task ReadWithRetryAsync()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Read() is { } item)
                    Copied?.Invoke(item);
                return;
            }
            catch (ExternalException)
            {
                await Task.Delay(60);
            }
        }
    }

    private static ClipItem? Read()
    {
        var data = Clipboard.GetDataObject();
        if (data is null || IsPrivate(data))
            return null;

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count == 0)
                return null;
            string preview = files.Count == 1 ? Path.GetFileName(files[0]!.TrimEnd('\\')) : $"{files.Count} archivos";
            return new ClipItem(ClipKind.Files, preview, null, files, null);
        }

        if (Clipboard.ContainsImage())
        {
            var image = ReadImage(data);
            if (image is null)
                return null;
            return new ClipItem(ClipKind.Image, $"Imagen {image.PixelWidth}×{image.PixelHeight}", null, null, Thumbnail(image));
        }

        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || IsSecretCalendarLink(text))
                return null;
            string preview = Whitespace().Replace(text.Trim(), " ");
            if (preview.Length > 120)
                preview = preview[..120];
            return new ClipItem(ClipKind.Text, preview, text, null, null);
        }

        return null;
    }

    /// <summary>Formatos estándar que usan los gestores de contraseñas para pedir privacidad.</summary>
    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"))
            return true;
        foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream flag &&
                flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
                return true;
        }
        return false;
    }

    /// <summary>El enlace iCal secreto de Google da acceso a todo el calendario: no se muestra ni se guarda.</summary>
    public static bool IsSecretCalendarLink(string text) =>
        text.Contains("/calendar/ical/", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("/private", StringComparison.OrdinalIgnoreCase);

    private static BitmapSource? ReadImage(IDataObject data)
    {
        // El formato PNG conserva bien la transparencia (Recortes y la mayoría de apps lo ponen)
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            try
            {
                var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            catch (Exception)
            {
                // Si el PNG viene mal, probamos con el formato estándar
            }
        }
        return Clipboard.GetImage();
    }

    /// <summary>Copia reducida: no retenemos la imagen completa en memoria.</summary>
    private static BitmapSource Thumbnail(BitmapSource image)
    {
        double scale = Math.Min(1, ThumbnailSize / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        var copy = new WriteableBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)));
        copy.Freeze();
        return copy;
    }

    public static void Recopy(ClipItem item)
    {
        try
        {
            if (item.Text is not null)
                Clipboard.SetText(item.Text);
            else if (item.Files is not null)
                Clipboard.SetFileDropList(item.Files);
        }
        catch (ExternalException)
        {
            // Portapapeles ocupado por otra app; el usuario puede volver a intentarlo
        }
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
