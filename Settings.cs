using Microsoft.Win32;

namespace IslaDinamica;

/// <summary>Preferencias del usuario guardadas en HKCU\Software\IslaDinamica.</summary>
public static class Settings
{
    private const string Key = @"Software\IslaDinamica";

    private static bool? _notificationsEnabled;
    private static bool? _clipboardEnabled;

    /// <summary>Mostrar notificaciones en la isla (activado por defecto).</summary>
    public static bool NotificationsEnabled
    {
        get => _notificationsEnabled ??= ReadBool("Notifications", true);
        set => WriteBool("Notifications", (_notificationsEnabled = value).Value);
    }

    /// <summary>Mostrar lo copiado y guardar el historial en memoria (activado por defecto).</summary>
    public static bool ClipboardEnabled
    {
        get => _clipboardEnabled ??= ReadBool("Clipboard", true);
        set => WriteBool("Clipboard", (_clipboardEnabled = value).Value);
    }

    private static bool? _hideNativeVolume;

    /// <summary>Ocultar el popup de volumen de Windows (activado por defecto).</summary>
    public static bool HideNativeVolume
    {
        get => _hideNativeVolume ??= ReadBool("HideNativeVolume", true);
        set => WriteBool("HideNativeVolume", (_hideNativeVolume = value).Value);
    }

    private static bool? _hideNativeNotifications;

    /// <summary>Ocultar el aviso de notificación de Windows y verlo solo en la isla (activado por defecto).</summary>
    public static bool HideNativeNotifications
    {
        get => _hideNativeNotifications ??= ReadBool("HideNativeNotifications", true);
        set => WriteBool("HideNativeNotifications", (_hideNativeNotifications = value).Value);
    }

    private static bool? _notificationSound;

    /// <summary>Sonar una campanita cuando la isla muestra un aviso (activado por defecto).</summary>
    public static bool NotificationSound
    {
        get => _notificationSound ??= ReadBool("NotificationSound", true);
        set => WriteBool("NotificationSound", (_notificationSound = value).Value);
    }

    private static bool? _claudeEnabled;

    /// <summary>Mostrar las sesiones de Claude Code en la isla (desactivado hasta que el usuario lo active).</summary>
    public static bool ClaudeEnabled
    {
        get => _claudeEnabled ??= ReadBool("ClaudeCode", false);
        set => WriteBool("ClaudeCode", (_claudeEnabled = value).Value);
    }

    /// <summary>Clave que los hooks de Claude Code mandan a la isla; se crea la primera vez.</summary>
    public static string ClaudeToken
    {
        get
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (key.GetValue("ClaudeToken") is string token && token.Length >= 32)
                return token;
            token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            key.SetValue("ClaudeToken", token);
            return token;
        }
    }

    /// <summary>
    /// Enlaces iCal secretos de cada calendario (o rutas a .ics). Vacío = sin calendario.
    /// Migra solo el valor "CalendarUrl" de cuando se admitía un único enlace.
    /// </summary>
    public static string[] CalendarUrls
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("CalendarUrls") is string[] urls)
                return urls;
            if (key?.GetValue("CalendarUrl") is string single && !string.IsNullOrWhiteSpace(single))
            {
                CalendarUrls = [single];
                return [single];
            }
            return [];
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.DeleteValue("CalendarUrl", throwOnMissingValue: false);
            var clean = value.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().ToArray();
            if (clean.Length == 0)
                key.DeleteValue("CalendarUrls", throwOnMissingValue: false);
            else
                key.SetValue("CalendarUrls", clean, RegistryValueKind.MultiString);
        }
    }

    private static bool ReadBool(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }

    private static void WriteBool(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value ? 1 : 0);
    }
}
