using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IslaDinamica;

/// <summary>
/// Agrega (o quita) los hooks HTTP de la isla en la configuración de usuario de Claude Code
/// (%USERPROFILE%\.claude\settings.json), sin tocar el resto del archivo. La primera vez guarda una copia
/// del archivo original como settings.json.isla-backup.
/// </summary>
public static class ClaudeHooks
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Evento → (matcher, ruta de la isla, segundos de espera máxima).</summary>
    private static readonly (string Event, string? Matcher, string Route, int Timeout)[] Entries =
    [
        ("SessionStart", null, "event", 5),
        ("SessionEnd", null, "event", 5),
        ("UserPromptSubmit", null, "event", 5),
        ("PreToolUse", "*", "event", 5),
        ("PreToolUse", "AskUserQuestion", "ask", 90),
        ("PermissionRequest", "*", "permission", 90),
        ("Notification", null, "event", 5),
        ("Stop", null, "event", 5),
    ];

    /// <summary>True si faltan los hooks o el status line de la isla (por ejemplo, tras una actualización).</summary>
    public static bool NeedsInstall()
    {
        try
        {
            if (Load() is not { } root)
                return true;
            bool hooks = root["hooks"] is JsonObject list && list.Any(e => e.Value is JsonArray groups && groups.Any(IsOurs));
            bool statusLine = root["statusLine"] is not null; // el de la isla o uno propio del usuario
            return !hooks || !statusLine;
        }
        catch (Exception)
        {
            return false; // archivo que no entendemos: no lo tocamos
        }
    }

    public static void Install(string token)
    {
        var root = Load() ?? new JsonObject();
        if (File.Exists(SettingsPath) && !File.Exists(SettingsPath + ".isla-backup"))
            File.Copy(SettingsPath, SettingsPath + ".isla-backup");

        RemoveOurs(root);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var (name, matcher, route, timeout) in Entries)
        {
            var group = new JsonObject();
            if (matcher is not null)
                group["matcher"] = matcher;
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "http",
                ["url"] = ClaudeBridge.BaseUrl + route,
                ["timeout"] = timeout,
                ["headers"] = new JsonObject { [ClaudeBridge.TokenHeader] = token },
            });
            if (hooks[name] is not JsonArray list)
                hooks[name] = list = new JsonArray();
            list.Add(group);
        }

        // El status line es lo único que recibe los límites de uso; si el usuario ya tiene uno, se respeta
        if (root["statusLine"] is null)
        {
            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"curl.exe -s -m 2 -X POST {ClaudeBridge.BaseUrl}status -H \"{ClaudeBridge.TokenHeader}: {token}\" --data-binary @-",
            };
        }
        Save(root);
    }

    public static void Uninstall()
    {
        if (Load() is not { } root)
            return;
        RemoveOurs(root);
        if (((string?)root["statusLine"]?["command"])?.Contains(ClaudeBridge.BaseUrl, StringComparison.OrdinalIgnoreCase) == true)
            root.Remove("statusLine");
        Save(root);
    }

    /// <summary>Quita solo los grupos que apuntan a la isla y limpia lo que quede vacío.</summary>
    private static void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
            return;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray groups)
                continue;
            foreach (var group in groups.Where(IsOurs).ToList())
                groups.Remove(group);
            if (groups.Count == 0)
                hooks.Remove(name);
        }
        if (hooks.Count == 0)
            root.Remove("hooks");
    }

    private static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray list &&
        list.Any(h => ((string?)h?["url"])?.StartsWith(ClaudeBridge.BaseUrl, StringComparison.OrdinalIgnoreCase) == true);

    private static JsonObject? Load()
    {
        if (!File.Exists(SettingsPath))
            return null;
        return JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject;
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        // Se escribe a un temporal y se reemplaza: si algo falla, el archivo original queda intacto
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
