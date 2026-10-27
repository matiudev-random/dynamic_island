using System.Runtime.InteropServices;

namespace IslaDinamica;

/// <summary>
/// Escucha los cambios de volumen del dispositivo de salida predeterminado
/// (Core Audio). Sin sondeo: Windows nos avisa por callback.
/// </summary>
public sealed class VolumeService : IDisposable
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 23;

    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private SynchronizationContext? _context;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;

    /// <summary>(nivel 0..1, silenciado). Se dispara desde un hilo de audio.</summary>
    public event Action<float, bool>? Changed;

    public VolumeService()
    {
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
    }

    public void Start()
    {
        _context = SynchronizationContext.Current;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            BindDefaultDevice();
        }
        catch (COMException)
        {
            // Sin dispositivo de audio: la isla funciona igual, solo sin popup de volumen.
        }
    }

    private void BindDefaultDevice()
    {
        Unbind();
        if (_enumerator is null)
            return;
        if (_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device is null)
            return;

        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var activated) != 0 || activated is null)
            return;

        _endpoint = (IAudioEndpointVolume)activated;
        _endpoint.RegisterControlChangeNotify(_volumeCallback);
    }

    private void Unbind()
    {
        if (_endpoint is null)
            return;
        try { _endpoint.UnregisterControlChangeNotify(_volumeCallback); }
        catch (COMException) { }
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private void OnDefaultDeviceChanged() => _context?.Post(_ => BindDefaultDevice(), null);

    private void Raise(float level, bool muted) => Changed?.Invoke(level, muted);

    public void Dispose()
    {
        Unbind();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); }
            catch (COMException) { }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    private sealed class VolumeCallback(VolumeService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: Guid (16 bytes), BOOL bMuted, float fMasterVolume, ...
            bool muted = Marshal.ReadInt32(data, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner.Raise(level, muted);
            return 0;
        }
    }

    private sealed class DeviceCallback(VolumeService owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(int dataFlow, int role, string? deviceId)
        {
            if (dataFlow == ERender && role == EMultimedia)
                owner.OnDefaultDeviceChanged();
            return 0;
        }
    }
}

#region Interop Core Audio

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    // Declarados en el orden de la vtable hasta GetMute, que es lo último que usamos
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

#endregion
