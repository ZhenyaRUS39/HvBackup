using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>
/// HTTP-сервер: REST-эндпоинты, документация Swagger и MCP-транспорт.
/// <para>
/// Слушает только 127.0.0.1 — снаружи сервер недоступен.
/// Три точки входа:
/// <list type="bullet">
/// <item><c>POST /mcp</c> — JSON-RPC 2.0 без сессии: ответ приходит в теле запроса.</item>
/// <item><c>GET /mcp</c> — поток SSE: сервер отдаёт адрес для отправки сообщений.</item>
/// <item><c>POST /mcp/messages?sessionId=…</c> — сообщение в открытую SSE-сессию, ответ приходит в поток.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HttpApiServer : IDisposable
{
    private readonly ToolContext _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, SseSession> _sessions = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private Task? _loop;

    public HttpApiServer(ToolContext context)
    {
        _context = context;
    }

    /// <summary>Сервер слушает запросы.</summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Порт, на котором работает сервер.</summary>
    public int Port { get; private set; }

    /// <summary>Базовый адрес сервера.</summary>
    public string BaseUrl => "http://127.0.0.1:" + Port;

    /// <summary>Последняя ошибка запуска (пусто — если всё в порядке).</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Число открытых SSE-сессий.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Запустить сервер на указанном порту.</summary>
    /// <exception cref="HttpListenerException">Порт занят или недоступен.</exception>
    public void Start(int port)
    {
        if (IsRunning) return;

        LastError = "";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            LastError = "Не удалось занять порт " + port + ": " + ex.Message;
            try { listener.Close(); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            LastError = "Не удалось запустить HTTP-сервер: " + ex.Message;
            try { listener.Close(); } catch { }
            throw;
        }

        _listener = listener;
        Port = port;
        _loop = Task.Run(() => AcceptLoopAsync(_stop.Token));
        AppLog.Info("MCP-сервер: HTTP-режим запущен — " + BaseUrl + " (Swagger: " + BaseUrl + "/swagger).");
    }

    /// <summary>Остановить сервер и закрыть все открытые потоки SSE.</summary>
    public void Stop()
    {
        var listener = _listener;
        _listener = null;

        try { _stop.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }

        foreach (var session in _sessions.Values)
        {
            try { session.Close(); } catch { }
        }
        _sessions.Clear();

        try { _loop?.Wait(1500); } catch { }
        _loop = null;

        if (listener != null)
            AppLog.Info("MCP-сервер: HTTP-режим остановлен.");
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext request;
            try
            {
                request = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;   // сервер остановлен
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Ошибка HTTP-обработчика: " + ex.Message);
                    try
                    {
                        await WriteJsonAsync(request, 500, new JsonObject { ["error"] = ex.Message }).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ответ не отправлен — запрос закрывается
                    }
                }
            });
        }
    }

    private async Task HandleAsync(HttpListenerContext request, CancellationToken ct)
    {
        var path = request.Request.Url?.AbsolutePath ?? "/";
        var method = request.Request.HttpMethod.ToUpperInvariant();

        // ── служебные страницы ──
        if (path == "/" || path == "/index.html")
        {
            await WriteTextAsync(request, 200, "text/html; charset=utf-8", IndexPage(Port)).ConfigureAwait(false);
            return;
        }

        if (path == "/swagger" || path == "/swagger/" || path == "/swagger/index.html")
        {
            await WriteTextAsync(request, 200, "text/html; charset=utf-8", SwaggerPage.Html).ConfigureAwait(false);
            return;
        }

        if (path == "/swagger/v1/swagger.json")
        {
            await WriteJsonAsync(request, 200, OpenApi.Build(Port)).ConfigureAwait(false);
            return;
        }

        if (path == "/health")
        {
            await WriteJsonAsync(request, 200, new JsonObject
            {
                ["status"] = "ok",
                ["version"] = McpVersion.Value,
                ["tools"] = ToolRegistry.All.Count,
                ["mcp"] = "POST /mcp, GET /mcp (SSE)"
            }).ConfigureAwait(false);
            return;
        }

        // ── MCP-транспорт ──
        if (path == "/mcp" && method == "POST")
        {
            await HandleMcpPostAsync(request, ct).ConfigureAwait(false);
            return;
        }

        if (path == "/mcp" && method == "GET")
        {
            await HandleMcpStreamAsync(request, ct).ConfigureAwait(false);
            return;
        }

        if (path == "/mcp/messages" && method == "POST")
        {
            await HandleMcpMessageAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // ── REST-эндпоинты инструментов ──
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var tool in ToolRegistry.All)
        {
            if (!string.Equals(tool.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Matches(tool.Path, segments, out var values)) continue;

            var args = new JsonObject();
            var query = request.Request.QueryString;

            foreach (var (name, value) in values)
                args[name] = value;

            foreach (var key in query.AllKeys)
            {
                if (key == null) continue;
                if (args.ContainsKey(key)) continue;
                args[key] = query[key] ?? "";
            }

            // тело запроса
            string body = "";
            if (request.Request.HasEntityBody)
            {
                using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (body.Trim().Length > 0)
            {
                try
                {
                    if (JsonNode.Parse(body) is JsonObject bodyObject)
                        foreach (var pair in bodyObject) args[pair.Key] = pair.Value?.DeepClone();
                    else
                    {
                        await WriteJsonAsync(request, 400, new JsonObject { ["error"] = "Тело запроса должно быть JSON-объектом." }).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(request, 400, new JsonObject { ["error"] = "Не разобран JSON: " + ex.Message }).ConfigureAwait(false);
                    return;
                }
            }

            var result = await tool.Handler(_context, args, CancellationToken.None).ConfigureAwait(false);
            if (result.Ok)
            {
                await WriteJsonAsync(request, 200, result.Data ?? new JsonObject()).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(request, 400, new JsonObject
                {
                    ["error"] = result.Error,
                    ["tool"] = tool.Name
                }).ConfigureAwait(false);
            }
            return;
        }

        await WriteJsonAsync(request, 404, new JsonObject
        {
            ["error"] = "Неизвестный адрес: " + method + " " + path,
            ["swagger"] = "/swagger"
        }).ConfigureAwait(false);
    }

    /// <summary>POST /mcp — JSON-RPC без сессии, ответ в теле ответа.</summary>
    private async Task HandleMcpPostAsync(HttpListenerContext request, CancellationToken ct)
    {
        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        if (body.Trim().Length == 0)
        {
            await WriteJsonAsync(request, 400, new JsonObject { ["error"] = "Пустое тело запроса." }).ConfigureAwait(false);
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(request, 400, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Не разобран запрос: " + ex.Message }
            }).ConfigureAwait(false);
            return;
        }

        // пакет запросов — массив
        if (parsed is JsonArray batch)
        {
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                var text = item?.ToJsonString() ?? "{}";
                var answer = await McpDispatcher.HandleAsync(text, _context, ct).ConfigureAwait(false);
                if (answer != null) answers.Add(JsonNode.Parse(answer));
            }
            await WriteJsonAsync(request, 200, answers).ConfigureAwait(false);
            return;
        }

        var response = await McpDispatcher.HandleAsync(body, _context, ct).ConfigureAwait(false);
        if (response == null)
        {
            // уведомление — ответа нет
            await WriteJsonAsync(request, 202, new JsonObject { ["status"] = "accepted" }).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(request, 200, "application/json; charset=utf-8", response).ConfigureAwait(false);
    }

    /// <summary>GET /mcp — открывает поток SSE и отдаёт адрес для отправки сообщений.</summary>
    private async Task HandleMcpStreamAsync(HttpListenerContext request, CancellationToken ct)
    {
        var session = new SseSession(request.Response);
        var id = session.Id;
        _sessions[id] = session;

        var response = request.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.SendChunked = true;

        try
        {
            await session.WriteAsync("event: endpoint\ndata: /mcp/messages?sessionId=" + id + "\n\n").ConfigureAwait(false);
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested && session.IsOpen)
            {
                if (session.WaitForMessage(15000))
                {
                    while (session.TryTake(out var message))
                        await session.WriteAsync("event: message\ndata: " + message + "\n\n").ConfigureAwait(false);
                }
                else
                {
                    // heartbeat — соединение не должно «засыпать»
                    await session.WriteAsync(": ping\n\n").ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // клиент отключился
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            try { response.Close(); } catch { }
        }
    }

    /// <summary>POST /mcp/messages?sessionId=… — сообщение в открытую SSE-сессию.</summary>
    private async Task HandleMcpMessageAsync(HttpListenerContext request, CancellationToken ct)
    {
        var sessionId = request.Request.QueryString["sessionId"] ?? "";
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            await WriteJsonAsync(request, 404, new JsonObject { ["error"] = "Сессия не найдена или закрыта." }).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        var response = await McpDispatcher.HandleAsync(body, _context, ct).ConfigureAwait(false);
        if (response != null) session.Enqueue(response);

        await WriteJsonAsync(request, 202, new JsonObject { ["status"] = "accepted" }).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerContext request)
    {
        if (!request.Request.HasEntityBody) return "";
        using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>Совпадает ли путь с шаблоном инструмента (учитывая {параметры}).</summary>
    private static bool Matches(string template, string[] segments, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parts = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != segments.Length) return false;

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('{') && parts[i].EndsWith('}'))
            {
                values[parts[i].Trim('{', '}')] = Uri.UnescapeDataString(segments[i]);
                continue;
            }
            if (!string.Equals(parts[i], segments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static async Task WriteJsonAsync(HttpListenerContext request, int status, JsonNode? body)
    {
        var text = (body ?? new JsonObject()).ToJsonString(McpDispatcher.JsonOptions);
        await WriteTextAsync(request, status, "application/json; charset=utf-8", text).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(HttpListenerContext request, int status, string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        request.Response.StatusCode = status;
        request.Response.ContentType = contentType;
        request.Response.ContentLength64 = bytes.Length;
        await request.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        request.Response.Close();
    }

    private static string IndexPage(int port) => """
        <!DOCTYPE html>
        <html lang="ru"><head><meta charset="utf-8"><title>ГиперВ Резерв — API</title>
        <style>body{font-family:Segoe UI,sans-serif;margin:40px;line-height:1.6}
        a{color:#0067c0}code{background:#f2f2f2;padding:2px 5px}</style></head><body>
        <h1>ГиперВ Резерв — API</h1>
        <p>Сервер работает. Описание всех операций — в Swagger:</p>
        <ul>
          <li><a href="/swagger">/swagger</a> — документация с описаниями и возможностью выполнить запрос</li>
          <li><a href="/swagger/v1/swagger.json">/swagger/v1/swagger.json</a> — документ OpenAPI 3.0</li>
          <li><a href="/health">/health</a> — проверка работы сервера</li>
        </ul>
        <h2>MCP</h2>
        <ul>
          <li><code>POST /mcp</code> — JSON-RPC 2.0 (ответ в теле запроса)</li>
          <li><code>GET /mcp</code> — поток SSE, адрес для сообщений приходит первым событием</li>
        </ul>
        <p>Базовый адрес API: <code>http://127.0.0.1:PORT</code></p>
        </body></html>
        """.Replace("PORT", port.ToString());
}

/// <summary>Открытый поток SSE: очередь сообщений и запись в поток ответа.</summary>
internal sealed class SseSession
{
    private readonly Queue<string> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly HttpListenerResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public bool IsOpen { get; private set; } = true;

    public SseSession(HttpListenerResponse response)
    {
        _response = response;
    }

    public void Enqueue(string message)
    {
        lock (_gate) _queue.Enqueue(message);
        try { _signal.Release(); } catch { }
    }

    /// <summary>Ждёт сообщение. true — сообщение есть, false — таймаут (нужен heartbeat).</summary>
    public bool WaitForMessage(int timeoutMs) => _signal.Wait(timeoutMs);

    public bool TryTake(out string message)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                message = "";
                return false;
            }
            message = _queue.Dequeue();
            return true;
        }
    }

    /// <summary>Записывает фрагмент в поток ответа.</summary>
    public async Task WriteAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            await _response.OutputStream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close()
    {
        IsOpen = false;
        try { _signal.Release(); } catch { }
    }
}
