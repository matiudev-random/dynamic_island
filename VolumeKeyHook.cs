using System.Runtime.InteropServices;

namespace IslaDinamica;

/// <summary>
/// En Windows 11 el popup de volumen lo dibuja el Explorador sin una ventana que se pueda ocultar.
/// Para que no aparezca, interceptamos las teclas de volumen y cambiamos el volumen nosotros
/// (pasos de 2 %, como Windows). La isla muestra el cambio porque escucha el volumen del sistema.
///
/// Corre en un hilo propio con su bucle de mensajes: el teclado nunca espera a la interfaz.
/// Si la app se cierra o falla, Windows quita el hook y todo vuelve a la normalidad.
/// </summary>
public sealed class VolumeKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;
    private const uint WM_VOLUME_KEY = 0x8000 + 10; // WM_APP + 10
    private const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const float Step = 0.02f;
    private const int ERender = 0, EMultimedia = 1, ClsctxAll = 23;

    private readonly Thread _thread;
    private readonly LowLevelKeyboardProc _callback; // referencia fija para que el GC no la recolecte
    private uint _threadId;
    private IntPtr _hook;
    private volatile bool _enabled;
    private bool _muteHeld;
    private IMMDeviceEnumerator? _enumerator;

    /// <summary>Si es false, las teclas pasan a Windows y se ve su popup normal.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public VolumeKeyHook()
    {
        _callback = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "VolumeKeyHook" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WM_VOLUME_KEY)
                ApplyKey((int)msg.WParam);
        }

        UnhookWindowsHookEx(_hook);
        if (_enumerator is not null)
            Marshal.ReleaseComObject(_enumerator);
    }

    /// <summary>Debe ser rapidísimo: solo decide y encola; el cambio de volumen va después.</summary>
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && _enabled)
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                int message = (int)wParam;
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;

                if (vk == VK_VOLUME_MUTE)
                {
                    // Mantener apretado "silenciar" no debe alternar una y otra vez
                    if (down && !_muteHeld)
                        PostThreadMessage(_threadId, WM_VOLUME_KEY, vk, IntPtr.Zero);
                    _muteHeld = down || (_muteHeld && !up);
                }
                else if (down)
                {
                    PostThreadMessage(_threadId, WM_VOLUME_KEY, vk, IntPtr.Zero);
                }
                return 1; // tecla consumida: Windows no muestra su popup
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void ApplyKey(int vk)
    {
        IAudioEndpointVolume? volume = null;
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            // Se pide el dispositivo en cada tecla: así sigue al predeterminado si cambia
            if (_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device is null)
                return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            int hr = device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var activated);
            Marshal.ReleaseComObject(device);
            if (hr != 0 || activated is null)
                return;
            volume = (IAudioEndpointVolume)activated;

            var context = Guid.Empty;
            volume.GetMute(out bool muted);
            if (vk == VK_VOLUME_MUTE)
            {
                volume.SetMute(!muted, ref context);
                return;
            }

            // Como Windows: subir o bajar quita el silencio
            if (muted)
                volume.SetMute(false, ref context);

            volume.GetMasterVolumeLevelScalar(out float level);
            float next = MathF.Round((level + (vk == VK_VOLUME_UP ? Step : -Step)) / Step) * Step;
            volume.SetMasterVolumeLevelScalar(Math.Clamp(next, 0f, 1f), ref context);
        }
        catch (COMException)
        {
            // Dispositivo desconectado justo ahora; se ignora
        }
        finally
        {
            if (volume is not null)
                Marshal.ReleaseComObject(volume);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, 0, IntPtr.Zero);
    }

    #region Win32

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    #endregion
}
