using System.IO;
using System.Runtime.InteropServices;

namespace IslaDinamica;

/// <summary>
/// "Campanita" propia de la isla: dos notas tipo campana sintetizadas al vuelo (unos 30 KB en memoria),
/// sin archivos de audio ni reproductores pesados. Se reproduce con PlaySound en segundo plano.
/// </summary>
public static class NotificationSound
{
    private const int SampleRate = 22050;
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    private static GCHandle _wave;

    public static void Play()
    {
        if (!Settings.NotificationSound)
            return;
        if (!_wave.IsAllocated)
            _wave = GCHandle.Alloc(BuildChime(), GCHandleType.Pinned); // PlaySound asíncrono lee el búfer después
        PlaySound(_wave.AddrOfPinnedObject(), 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    private static byte[] BuildChime()
    {
        const double length = 0.75;
        int samples = (int)(SampleRate * length);
        var pcm = new short[samples];

        // Dos golpes (Sol6 y luego Do7) con parciales de campana que se apagan rápido
        AddBell(pcm, frequency: 1568, start: 0.00, volume: 0.22);
        AddBell(pcm, frequency: 2093, start: 0.11, volume: 0.20);

        using var stream = new MemoryStream(44 + samples * 2);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        foreach (short s in pcm)
            w.Write(s);
        return stream.ToArray();
    }

    private static void AddBell(short[] pcm, double frequency, double start, double volume)
    {
        (double ratio, double gain, double decay)[] partials = [(1, 1, 7), (2.76, 0.35, 12), (5.4, 0.12, 20)];
        int first = (int)(start * SampleRate);
        for (int i = first; i < pcm.Length; i++)
        {
            double t = (double)(i - first) / SampleRate;
            double attack = Math.Min(1, t / 0.004); // evita el "clic" inicial
            double value = 0;
            foreach (var (ratio, gain, decay) in partials)
                value += gain * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * frequency * ratio * t);
            int mixed = pcm[i] + (int)(value * attack * volume * short.MaxValue);
            pcm[i] = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
        }
    }

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(nint sound, nint module, uint flags);
}
