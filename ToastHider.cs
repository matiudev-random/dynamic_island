using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace IslaDinamica;

/// <summary>
/// Esconde el aviso de notificación de Windows (esquina inferior derecha) para que solo se vea en la isla.
/// Windows no deja desactivar solo el aviso sin perder la notificación, así que la ventana del aviso
/// (que vive siempre dentro de ShellExperienceHost) se saca de la pantalla cada vez que Windows la
/// vuelve a colocar. La notificación sigue llegando al centro de notificaciones y a la isla.
/// Debe crearse en el hilo de la interfaz: los eventos llegan por su cola de mensajes.
/// </summary>
public sealed class ToastHider : IDisposable
{
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int OffScreen = -32000;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    // Título de la ventana del aviso según el idioma de Windows
    private static readonly HashSet<string> ToastTitles =
        ["New notification", "Notificación nueva", "Nueva notificación"];

    private readonly WinEventProc _callback; // referencia viva para que el GC no la recoja
    private readonly DispatcherTimer _watchdog;
    private nint _hook;
    private nint _toast;
    private bool _enabled;

    public ToastHider()
    {
        _callback = OnWinEvent;
        // Si Windows reinicia ShellExperienceHost, la ventana cambia: la buscamos de nuevo
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _watchdog.Tick += (_, _) =>
        {
            if (!IsWindow(_toast))
                Attach();
        };
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
            {
                Attach();
                _watchdog.Start();
            }
            else
            {
                _watchdog.Stop();
                Detach();
            }
        }
    }

    private void Attach()
    {
        Detach();
        _toast = FindToastWindow();
        if (_toast == 0)
            return;

        GetWindowThreadProcessId(_toast, out uint pid);
        _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _callback,
            pid, 0, WINEVENT_OUTOFCONTEXT);
        MoveAway();
    }

    private void Detach()
    {
        if (_hook != 0)
            UnhookWinEvent(_hook);
        _hook = 0;
        _toast = 0;
    }

    private void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == _toast && idObject == OBJID_WINDOW)
            MoveAway();
    }

    private void MoveAway()
    {
        if (GetWindowRect(_toast, out var rect) && rect.Left > OffScreen / 2)
            SetWindowPos(_toast, 0, OffScreen, OffScreen, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// La ventana del aviso vive en una capa especial del escritorio que EnumWindows no recorre,
    /// pero FindWindowEx sí la encuentra.
    /// </summary>
    private static nint FindToastWindow()
    {
        var title = new StringBuilder(128);
        nint hwnd = 0;
        while ((hwnd = FindWindowEx(0, hwnd, "Windows.UI.Core.CoreWindow", null)) != 0)
        {
            title.Clear();
            GetWindowText(hwnd, title, title.Capacity);
            if (ToastTitles.Contains(title.ToString()) && IsShellProcess(hwnd))
                return hwnd;
        }
        return 0;
    }

    private static bool IsShellProcess(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    public void Dispose()
    {
        _watchdog.Stop();
        Detach();
    }

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
