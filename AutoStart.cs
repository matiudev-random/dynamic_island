using Microsoft.Win32;

namespace IslaDinamica;

/// <summary>
/// Inicio con Windows mediante HKCU\...\Run (solo el usuario actual, sin permisos de administrador).
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\IslaDinamica";
    private const string ValueName = "IslaDinamica";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// La primera vez activa el inicio automático; después respeta lo que elija el usuario
    /// y corrige la ruta si el .exe se movió.
    /// </summary>
    public static void Initialize()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (settings.GetValue("Configured") is null)
        {
            SetEnabled(true);
            settings.SetValue("Configured", 1);
        }
        else if (IsEnabled)
        {
            SetEnabled(true);
        }
    }
}
