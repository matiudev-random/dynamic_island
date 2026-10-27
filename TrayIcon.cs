using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IslaDinamica;

/// <summary>Elemento del menú de la bandeja. Sin acción = separador.</summary>
public sealed record TrayMenuItem(string Text, Action? Action = null, bool Checked = false)
{
    public static readonly TrayMenuItem Separator = new("");
}

/// <summary>
/// Ícono de bandeja con Shell_NotifyIcon directo, para no cargar Windows Forms.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    private const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

    private readonly HwndSource _window;
    private IntPtr _icon;
    private string _tooltip;
    private readonly int _taskbarCreatedMessage;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _buildMenu;
    private bool _added;

    /// <summary>Clic izquierdo sobre el ícono.</summary>
    public event Action? Clicked;

    public TrayIcon(string iconPath, string tooltip, Func<IReadOnlyList<TrayMenuItem>> buildMenu)
    {
        _tooltip = tooltip;
        _buildMenu = buildMenu;

        // Ventana invisible que recibe los mensajes del ícono
        _window = new HwndSource(new HwndSourceParameters("IslaDinamicaTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);

        _icon = LoadIcon(iconPath);

        // Si el Explorador se reinicia, hay que volver a agregar el ícono
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    private void Add()
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static IntPtr LoadIcon(string path)
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        return LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
    }

    /// <summary>Cambia el ícono y el texto al pasar el mouse.</summary>
    public void Update(string iconPath, string tooltip)
    {
        var previous = _icon;
        _icon = LoadIcon(iconPath);
        _tooltip = tooltip;

        var data = CreateData();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);

        if (previous != IntPtr.Zero)
            DestroyIcon(previous);
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                    Clicked?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMessage)
        {
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var items = _buildMenu();
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == TrayMenuItem.Separator)
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                else
                    AppendMenu(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (nuint)(i + 1), item.Text);
            }

            GetCursorPos(out var point);
            // Necesario para que el menú se cierre al hacer clic fuera
            SetForegroundWindow(_window.Handle);
            int command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
                point.X, point.Y, 0, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0)
                items[command - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
        _window.Dispose();
    }

    #region Win32

    private const int SM_CXSMICON = 49;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInstance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    #endregion
}
