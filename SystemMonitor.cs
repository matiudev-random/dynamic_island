using System.Runtime.InteropServices;

namespace IslaDinamica;

public readonly record struct SystemSample(double Cpu, double Ram, double Gpu, double RamUsedGb, double RamTotalGb);

/// <summary>
/// Uso de CPU, RAM y GPU con llamadas nativas baratas. Solo se consulta mientras
/// la página de sistema está visible, así que fuera de eso no consume nada.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private const double BytesPerGb = 1024d * 1024 * 1024;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private static readonly int PDH_MORE_DATA = unchecked((int)0x800007D2);

    private long _lastIdle, _lastKernel, _lastUser;
    private readonly IntPtr _query;
    private readonly IntPtr _gpuCounter;
    private readonly bool _gpuAvailable;

    public SystemMonitor()
    {
        ReadCpu(); // primera muestra de referencia

        // Mismo contador que usa el Administrador de tareas para el motor 3D
        _gpuAvailable =
            PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 &&
            PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0 &&
            PdhCollectQueryData(_query) == 0;
    }

    public SystemSample Read()
    {
        double cpu = ReadCpu();

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref memory);
        double total = memory.TotalPhys / BytesPerGb;
        double used = (memory.TotalPhys - memory.AvailPhys) / BytesPerGb;

        return new SystemSample(cpu, total > 0 ? used / total * 100 : 0, ReadGpu(), used, total);
    }

    private double ReadCpu()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        long idleDelta = idle - _lastIdle;
        long totalDelta = kernel - _lastKernel + (user - _lastUser); // kernel ya incluye idle
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        return totalDelta <= 0 ? 0 : Math.Clamp((1 - idleDelta / (double)totalDelta) * 100, 0, 100);
    }

    /// <summary>Suma el uso de cada proceso por motor y se queda con el motor más ocupado.</summary>
    private double ReadGpu()
    {
        if (!_gpuAvailable || PdhCollectQueryData(_query) != 0)
            return double.NaN;

        uint size = 0;
        int status = PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA)
            return status == 0 ? 0 : double.NaN;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0)
                return double.NaN;

            // PDH_FMT_COUNTERVALUE_ITEM_W en x64: nombre (8) + CStatus (4 + relleno 4) + double (8)
            const int itemSize = 24;
            var perEngine = new Dictionary<string, double>();
            for (int i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                if (Marshal.ReadInt32(item, 8) > 1) // solo PDH_CSTATUS_VALID_DATA / NEW_DATA
                    continue;

                // "pid_123_luid_0x..._phys_0_eng_0_engtype_3D" -> agrupamos por motor (desde "luid_")
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = luid >= 0 ? name[luid..] : name;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
            }
            return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
            PdhCloseQuery(_query);
    }

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    #endregion
}
