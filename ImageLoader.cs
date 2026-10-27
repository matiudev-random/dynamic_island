using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace IslaDinamica;

internal static class ImageLoader
{
    /// <summary>
    /// Decodifica una imagen WinRT a un BitmapSource congelado (usable desde cualquier hilo).
    /// Devuelve null si no hay imagen o falla la lectura.
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(IRandomAccessStreamReference? reference, int decodePixelWidth)
    {
        if (reference is null)
            return null;

        try
        {
            using var source = await reference.OpenReadAsync();
            using var input = source.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth; // tamaño justo para mostrar: ahorra memoria
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
