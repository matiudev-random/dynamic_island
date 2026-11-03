using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace IslaDinamica;

/// <summary>Una pregunta de Claude que espera respuesta en la isla (permiso o AskUserQuestion).</summary>
public sealed class ClaudeRequest
{
    private readonly TaskCompletionSource<string?> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClaudeRequest(bool isQuestion, JsonObject payload)
    {
        IsQuestion = isQuestion;
        Payload = payload;
    }

    public bool IsQuestion { get; }
    public JsonObject Payload { get; }
    public string SessionId => (string?)Payload["session_id"] ?? "";
    public bool IsDone => _reply.Task.IsCompleted;
    internal Task<string?> Reply => _reply.Task;

    /// <summary>Permitir o denegar la herramienta que pidió permiso.</summary>
    public void Decide(bool allow) => _reply.TrySetResult(new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PermissionRequest",
            ["decision"] = allow
                ? new JsonObject { ["behavior"] = "allow" }
                : new JsonObject { ["behavior"] = "deny", ["message"] = "El usuario lo denegó desde la isla." },
        },
    }.ToJsonString());

    /// <summary>Responde la pregunta de AskUserQuestion con la opción elegida.</summary>
    public void Answer(string question, string label)
    {
        var input = Payload["tool_input"]?.DeepClone() as JsonObject ?? new JsonObject();
        input["answers"] = new JsonObject { [question] = label };
        _reply.TrySetResult(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["updatedInput"] = input,
            },
        }.ToJsonString());
    }

    /// <summary>Sin decisión: Claude Code muestra su propio diálogo, como si la isla no existiera.</summary>
    public void Release() => _reply.TrySetResult(null);
}

/// <summary>
/// Recibe los hooks HTTP de Claude Code en http://localhost:47861/isla/ (solo desde este PC y con la clave
/// guardada en la configuración). Los eventos normales se responden al instante; los permisos y las
/// preguntas esperan a que el usuario conteste en la isla o, pasado un rato, se liberan para que Claude
/// pregunte como siempre. Si la isla está cerrada, Claude Code simplemente sigue sin ella.
/// </summary>
public sealed class ClaudeBridge : IDisposable
{
    public const int Port = 47861;
    public const string BaseUrl = "http://localhost:47861/isla/";
    public const string TokenHeader = "X-Isla-Token";
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(60);

    private readonly string _token;
    private HttpListener? _listener;

    public ClaudeBridge(string token) => _token = token;

    /// <summary>Evento de una sesión (inicio, prompt, herramienta, aviso, fin). Se dispara desde un hilo de fondo.</summary>
    public event Action<JsonObject>? EventReceived;

    /// <summary>Permiso o pregunta que espera respuesta. Se dispara desde un hilo de fondo.</summary>
    public event Action<ClaudeRequest>? RequestReceived;

    /// <summary>La isla decide si puede mostrar preguntas ahora (p. ej. no en pantalla completa).</summary>
    public Func<bool> CanAsk { get; set; } = () => true;

    /// <returns>False si el puerto está ocupado.</returns>
    public bool Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener = null;
            return false;
        }
        _ = Task.Run(AcceptLoopAsync);
        return true;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // detenido
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string? reply = null;
        try
        {
            var request = context.Request;
            if (request.HttpMethod != "POST" || request.Headers[TokenHeader] != _token)
            {
                context.Response.StatusCode = 403;
                return;
            }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            if (JsonNode.Parse(await reader.ReadToEndAsync()) is not JsonObject payload)
                return;

            switch (request.Url?.AbsolutePath.TrimEnd('/'))
            {
                case "/isla/event":
                    EventReceived?.Invoke(payload);
                    break;
                case "/isla/permission":
                    reply = await AskAsync(new ClaudeRequest(isQuestion: false, payload));
                    break;
                case "/isla/ask":
                    reply = await AskAsync(new ClaudeRequest(isQuestion: true, payload));
                    break;
            }
        }
        catch (Exception)
        {
            // Una petición rara no debe tumbar el servidor; Claude sigue sin respuesta de la isla
        }
        finally
        {
            try
            {
                if (reply is not null)
                {
                    byte[] body = Encoding.UTF8.GetBytes(reply);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                }
                context.Response.Close();
            }
            catch (Exception)
            {
                // Claude ya cortó la conexión (p. ej. el usuario respondió en la terminal)
            }
        }
    }

    private async Task<string?> AskAsync(ClaudeRequest request)
    {
        if (RequestReceived is null || !CanAsk())
            return null;

        RequestReceived(request);
        await Task.WhenAny(request.Reply, Task.Delay(AnswerWindow));
        request.Release(); // sin respuesta a tiempo: que pregunte Claude
        return await request.Reply;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Close();
        }
        catch (Exception)
        {
        }
        _listener = null;
    }
}
