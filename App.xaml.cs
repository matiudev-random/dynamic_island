using System.IO;
using System.Windows;

namespace IslaDinamica;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _island;
    private VolumeKeyHook? _volumeKeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Evita abrir dos islas a la vez
        _singleInstance = new Mutex(true, @"Local\IslaDinamica.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        AutoStart.Initialize();

        _island = new MainWindow();
        _island.IsVisibleChanged += (_, _) => UpdateVolumeKeys();
        _island.Show();

        _volumeKeys = new VolumeKeyHook();
        _volumeKeys.Start();
        UpdateVolumeKeys();

        _tray = new TrayIcon(IconPath(visible: true), "Isla Dinámica", BuildTrayMenu);
        _tray.Clicked += ToggleIsland;
    }

    /// <summary>Verde = isla visible, rojo = oculta a mano.</summary>
    private static string IconPath(bool visible) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", visible ? "isla.ico" : "isla-off.ico");

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu() =>
    [
        new(_island?.UserHidden == true ? "Mostrar isla" : "Ocultar isla", ToggleIsland),
        new("Mostrar notificaciones", () => Settings.NotificationsEnabled = !Settings.NotificationsEnabled,
            Settings.NotificationsEnabled),
        new("Mostrar portapapeles", () => Settings.ClipboardEnabled = !Settings.ClipboardEnabled,
            Settings.ClipboardEnabled),
        new("Ocultar volumen de Windows", () =>
        {
            Settings.HideNativeVolume = !Settings.HideNativeVolume;
            UpdateVolumeKeys();
        }, Settings.HideNativeVolume),
        new(Settings.CalendarUrls.Length is var n && n > 0 ? $"Calendarios ({n})…" : "Conectar calendarios…", OpenCalendarSetup),
        new("Iniciar con Windows", () => AutoStart.SetEnabled(!AutoStart.IsEnabled), AutoStart.IsEnabled),
        TrayMenuItem.Separator,
        new("Salir", Shutdown),
    ];

    private void OpenCalendarSetup()
    {
        if (_island is null)
            return;
        var existing = Windows.OfType<CalendarSetupWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        new CalendarSetupWindow(_island.Calendar).Show();
    }

    private void ToggleIsland()
    {
        if (_island is null)
            return;
        bool hidden = !_island.UserHidden;
        _island.SetUserHidden(hidden);
        _tray?.Update(IconPath(!hidden), hidden ? "Isla Dinámica (oculta)" : "Isla Dinámica");
    }

    /// <summary>
    /// Solo interceptamos las teclas si la isla está a la vista para mostrar el cambio;
    /// oculta (pantalla completa o a mano), Windows vuelve a mostrar su popup.
    /// </summary>
    private void UpdateVolumeKeys()
    {
        if (_volumeKeys is not null)
            _volumeKeys.Enabled = Settings.HideNativeVolume && _island?.IsVisible == true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _volumeKeys?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
