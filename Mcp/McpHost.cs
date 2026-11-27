namespace HvBackup.Mcp;

/// <summary>
/// Встроенный MCP-сервер: работает внутри процесса программы «ГиперВ Резерв».
/// <para>
/// Программа запускается с правами администратора, поэтому сервер сразу
/// получает доступ к Hyper-V: список машин, копирование и восстановление
/// работают без отдельного запроса UAC.
/// Сервер слушает только 127.0.0.1 — извне он недоступен.
/// </para>
/// </summary>
public sealed class McpHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ToolContext _context;
    private HttpApiServer? _server;

    public McpHost(AppSettings settings)
    {
        _settings = settings;
        _context = McpContext.Create(settings);
    }

    /// <summary>Сервер запущен и слушает порт.</summary>
    public bool IsRunning => _server?.IsRunning == true;

    /// <summary>Порт, на котором работает сервер (или заданный в настройках).</summary>
    public int Port => _server?.Port ?? _settings.McpPort;

    /// <summary>Базовый адрес сервера.</summary>
    public string BaseUrl => "http://127.0.0.1:" + Port;

    /// <summary>Адрес MCP-эндпоинта (JSON-RPC и SSE).</summary>
    public string McpUrl => BaseUrl + "/mcp";

    /// <summary>Адрес документации Swagger.</summary>
    public string SwaggerUrl => BaseUrl + "/swagger";

    /// <summary>Последняя ошибка запуска (пусто — если всё в порядке).</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Запустить сервер на указанном порту.</summary>
    public bool Start(int port, out string error)
    {
        error = "";

        if (IsRunning && _server!.Port == port) return true;

        Stop();

        try
        {
            var server = new HttpApiServer(_context);
            server.Start(port);
            _server = server;
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LastError = error;
            AppLog.Error("MCP-сервер не запустился: " + ex.Message);
            return false;
        }
    }

    /// <summary>Остановить сервер.</summary>
    public void Stop()
    {
        var server = _server;
        _server = null;
        try { server?.Dispose(); } catch { }
    }

    /// <summary>
    /// Привести сервер в соответствие с настройками: включён/выключен и порт.
    /// <para>Используется при старте программы и при перечитывании настроек с диска.</para>
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings.McpEnabled = settings.McpEnabled;
        _settings.McpPort = settings.McpPort;

        if (!settings.McpEnabled)
        {
            if (IsRunning)
            {
                Stop();
                AppLog.Info("MCP-сервер остановлен (настройка выключена).");
            }
            return;
        }

        if (IsRunning && _server!.Port == settings.McpPort) return;

        if (Start(settings.McpPort, out var error))
        {
            AppLog.Info("MCP-сервер включён: " + McpUrl);
        }
        else
        {
            AppLog.Error("MCP-сервер не запустился на порту " + settings.McpPort + ": " + error);
        }
    }

    /// <summary>
    /// Готовая конфигурация для MCP-клиента (формат «mcpServers»).
    /// <para>Подходит для клиентов, поддерживающих HTTP-транспорт.</para>
    /// </summary>
    public string ClientConfigJson()
    {
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["hvbackup"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpUrl
                }
            }
        };

        return config.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public void Dispose() => Stop();
}
