using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>
/// Самопроверка MCP-сервера: реестр инструментов, схемы, документ OpenAPI,
/// страница Swagger и работа встроенного сервера вживую.
/// <para>
/// Проверки считаются общим счётчиком самопроверки программы — проверка передаётся
/// снаружи, поэтому вывод единый.
/// </para>
/// </summary>
public static class McpSelfTest
{
    /// <summary>Проверка самопроверки: имя, результат, подробность (подробность необязательна).</summary>
    public delegate void CheckDelegate(string name, bool ok, string detail = "");

    /// <summary>Выполнить проверки MCP-сервера.</summary>
    /// <param name="check">Проверка: имя, результат, подробность.</param>
    public static void Run(CheckDelegate check)
    {
        TestRegistry(check);
        TestSchemas(check);
        TestOpenApi(check);
        TestToolsList(check);
        TestSwaggerPage(check);
        TestParsers(check);
        TestDispatcher(check);
        TestHost(check);
        TestHostSwitching(check);
    }

    private static void TestRegistry(CheckDelegate check)
    {
        var tools = ToolRegistry.All;
        check("инструменты описаны", tools.Count > 0, "инструментов: " + tools.Count);

        var names = tools.Select(t => t.Name).ToList();
        check("имена уникальны", names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count);

        var latin = names.All(n => n.Length > 0 && n.All(c => char.IsLetterOrDigit(c) || c == '_'));
        check("имена — латиница и подчёркивания", latin);

        var described = tools.All(t => t.Description.Length >= 40);
        check("у каждого инструмента есть описание", described);

        var titled = tools.All(t => t.Title.Length > 0);
        check("у каждого инструмента есть название", titled);

        var handlers = tools.All(t => t.Handler != null);
        check("у каждого инструмента есть обработчик", handlers);

        var expected = new[] { "list_vms", "list_backups", "create_backup", "delete_backup",
            "delete_all_backups", "get_schedule", "set_schedule", "restore_backup",
            "get_settings", "update_settings", "get_status", "get_logs", "get_job", "list_jobs" };
        var missing = expected.Where(e => !names.Contains(e)).ToList();
        check("есть базовый набор инструментов", missing.Count == 0,
            missing.Count > 0 ? "не хватает: " + string.Join(", ", missing) : "");

        var pathsOk = tools.All(t => t.Path.StartsWith("/api/"));
        check("у всех инструментов свой REST-адрес", pathsOk);
    }

    private static void TestSchemas(CheckDelegate check)
    {
        var valid = true;
        var problem = "";

        foreach (var tool in ToolRegistry.All)
        {
            var schema = tool.Schema;
            if ((schema["type"]?.GetValue<string>()) != "object")
            {
                valid = false;
                problem = tool.Name + ": тип схемы не object";
                break;
            }

            if (schema["properties"] is not JsonObject properties)
            {
                valid = false;
                problem = tool.Name + ": нет свойств";
                break;
            }

            if (schema["required"] is JsonArray required)
            {
                foreach (var r in required)
                {
                    var name = r?.GetValue<string>() ?? "";
                    if (!properties.ContainsKey(name))
                    {
                        valid = false;
                        problem = tool.Name + ": обязательный параметр " + name + " не описан";
                        break;
                    }
                }
            }

            if (!valid) break;
        }

        check("схемы корректны", valid, problem);

        // параметры из пути должны быть в схеме
        var pathOk = true;
        var pathProblem = "";
        foreach (var tool in ToolRegistry.All)
        {
            foreach (var p in tool.PathParams())
            {
                var properties = tool.Schema["properties"] as JsonObject;
                if (properties == null || !properties.ContainsKey(p))
                {
                    pathOk = false;
                    pathProblem = tool.Name + ": параметр пути " + p + " не описан в схеме";
                    break;
                }
            }
            if (!pathOk) break;
        }
        check("параметры пути описаны в схеме", pathOk, pathProblem);

        // query-параметры тоже
        var queryOk = true;
        var queryProblem = "";
        foreach (var tool in ToolRegistry.All)
        {
            var properties = tool.Schema["properties"] as JsonObject;
            foreach (var q in tool.Query)
            {
                if (properties == null || !properties.ContainsKey(q))
                {
                    queryOk = false;
                    queryProblem = tool.Name + ": query-параметр " + q + " не описан в схеме";
                    break;
                }
            }
            if (!queryOk) break;
        }
        check("query-параметры описаны в схеме", queryOk, queryProblem);
    }

    private static void TestOpenApi(CheckDelegate check)
    {
        JsonObject doc = OpenApi.Build(8787);
        check("документ собирается", true);
        string openapiText = "";
        if (doc.TryGetPropertyValue("openapi", out var openapiNode) && openapiNode != null)
            openapiText = openapiNode.ToString();
        check("указан формат OpenAPI", openapiText.StartsWith("3.0"), openapiText);

        var paths = doc["paths"] as JsonObject;
        check("есть разделы путей", paths != null && paths.Count > 0, "путей: " + (paths?.Count ?? 0));

        var operations = 0;
        var ids = new List<string>();
        if (paths != null)
        {
            foreach (var path in paths)
                foreach (var method in path.Value!.AsObject())
                {
                    operations++;
                    var id = method.Value?["operationId"]?.GetValue<string>() ?? "";
                    ids.Add(id);
                }
        }

        check("все операции попали в документ", operations == ToolRegistry.All.Count,
            "в документе: " + operations + ", в реестре: " + ToolRegistry.All.Count);
        check("идентификаторы операций совпадают с инструментами",
            ids.OrderBy(x => x).SequenceEqual(ToolRegistry.All.Select(t => t.Name).OrderBy(x => x)));

        var described = true;
        if (paths != null)
        {
            foreach (var path in paths)
                foreach (var method in path.Value!.AsObject())
                {
                    var op = method.Value;
                    if (op == null) { described = false; break; }
                    var summary = op["summary"]?.GetValue<string>() ?? "";
                    var description = op["description"]?.GetValue<string>() ?? "";
                    if (summary.Length == 0 || description.Length < 40) described = false;
                }
        }
        check("у операций есть названия и описания", described);

        var json = doc.ToJsonString();
        check("документ сериализуется в JSON", json.Length > 500, "символов: " + json.Length);

        var reparsed = JsonNode.Parse(json) as JsonObject;
        check("документ разбирается обратно", reparsed != null);
    }

    private static void TestToolsList(CheckDelegate check)
    {
        var list = McpDispatcher.ToolsList();
        check("список инструментов собирается", list.Count == ToolRegistry.All.Count);

        var first = list.FirstOrDefault() as JsonObject;
        check("у инструмента есть имя", first?["name"]?.GetValue<string>() is { Length: > 0 });
        check("у инструмента есть описание", (first?["description"]?.GetValue<string>() ?? "").Length > 40);
        check("у инструмента есть схема", first?["inputSchema"] != null);

        var json = list.ToJsonString();
        check("список сериализуется", json.Length > 200);
        check("список разбирается обратно", JsonNode.Parse(json) != null);
    }

    private static void TestSwaggerPage(CheckDelegate check)
    {
        var html = SwaggerPage.Html;
        check("страница не пустая", html.Length > 2000, "символов: " + html.Length);
        check("есть заголовок на русском", html.Contains("ГиперВ Резерв"));
        check("читает документ OpenAPI", html.Contains("/swagger/v1/swagger.json"));
        check("есть кнопка выполнения", html.Contains("Выполнить"));
        check("нет внешних скриптов", !html.Contains("cdn.") && !html.Contains("unpkg") && !html.Contains("jsdelivr"));
        check("нет внешних стилей", !html.Contains("https://") || !html.Contains("<link"));
    }

    private static void TestParsers(CheckDelegate check)
    {
        check("вид расписания daily", ToolRegistry.TryParseKind("daily", out var daily) && daily == ScheduleKind.Daily);
        check("вид расписания weekly", ToolRegistry.TryParseKind("weekly", out var weekly) && weekly == ScheduleKind.Weekly);
        check("вид расписания every_minutes", ToolRegistry.TryParseKind("every_minutes", out var every) && every == ScheduleKind.EveryMinutes);
        check("вид расписания disabled", ToolRegistry.TryParseKind("disabled", out var disabled) && disabled == ScheduleKind.Disabled);
        check("неизвестный вид расписания отвергается", !ToolRegistry.TryParseKind("часто", out _));

        check("режим hot_export", ToolRegistry.TryParseMode("hot_export", out var hot) && hot == CopyMode.HotExport);
        check("режим vm_export", ToolRegistry.TryParseMode("vm_export", out var vmExport) && vmExport == CopyMode.VmExport);
        check("режим checkpoint_throttled", ToolRegistry.TryParseMode("checkpoint_throttled", out var cp) && cp == CopyMode.CheckpointThrottled);
        check("неизвестный режим отвергается", !ToolRegistry.TryParseMode("быстро", out _));
    }

    /// <summary>Проверка общей обработки JSON-RPC — она работает и для stdio, и для HTTP.</summary>
    private static void TestDispatcher(CheckDelegate check)
    {
        var context = McpContext.Create();

        var init = McpDispatcher.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            context, CancellationToken.None).GetAwaiter().GetResult();

        check("initialize отвечает", init != null && init.Contains("serverInfo"), Truncate(init ?? ""));
        check("версия протокола согласована", init != null && init.Contains("2025-06-18"));
        check("запрос без метода даёт ошибку",
            McpDispatcher.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"нет_такого\"}", context, CancellationToken.None)
                .GetAwaiter().GetResult()?.Contains("-32601") == true);

        check("уведомление не требует ответа",
            McpDispatcher.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", context, CancellationToken.None)
                .GetAwaiter().GetResult() == null);

        check("неразобранный запрос не роняет сервер",
            McpDispatcher.HandleAsync("{это не json", context, CancellationToken.None)
                .GetAwaiter().GetResult()?.Contains("-32700") == true);
    }

    /// <summary>Проверка встроенного сервера вживую: запуск, HTTP-эндпоинты, остановка.</summary>
    private static void TestHost(CheckDelegate check)
    {
        var port = FreePort();
        var context = McpContext.Create();
        using var server = new HttpApiServer(context);

        try
        {
            server.Start(port);
        }
        catch (Exception ex)
        {
            check("сервер запускается на свободном порту", false, ex.Message);
            return;
        }

        check("сервер запускается на свободном порту", server.IsRunning, "порт " + port);
        check("сервер слушает только 127.0.0.1", server.BaseUrl.StartsWith("http://127.0.0.1:"));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            var health = client.GetStringAsync(server.BaseUrl + "/health").GetAwaiter().GetResult();
            check("отвечает /health", health.Contains("\"status\"") && health.Contains("\"ok\""),
                health.Length > 0 ? health.Substring(0, Math.Min(120, health.Length)) : "");
        }
        catch (Exception ex)
        {
            check("отвечает /health", false, ex.Message);
        }

        try
        {
            var swagger = client.GetStringAsync(server.BaseUrl + "/swagger").GetAwaiter().GetResult();
            check("отвечает /swagger", swagger.Contains("ГиперВ Резерв"));
        }
        catch (Exception ex)
        {
            check("отвечает /swagger", false, ex.Message);
        }

        try
        {
            var openapi = client.GetStringAsync(server.BaseUrl + "/swagger/v1/swagger.json").GetAwaiter().GetResult();
            check("отдаёт документ OpenAPI", openapi.Contains("\"openapi\""));
        }
        catch (Exception ex)
        {
            check("отдаёт документ OpenAPI", false, ex.Message);
        }

        // MCP через HTTP: initialize → tools/list → вызов инструмента
        try
        {
            var init = PostJson(client, server.BaseUrl + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");
            check("MCP: initialize через POST /mcp", init.Contains("serverInfo"), Truncate(init));

            var list = PostJson(client, server.BaseUrl + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
            check("MCP: tools/list через POST /mcp", list.Contains("list_backups"), Truncate(list));

            var call = PostJson(client, server.BaseUrl + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_status\",\"arguments\":{}}}");
            check("MCP: вызов инструмента get_status", call.Contains("\"content\""), Truncate(call));
        }
        catch (Exception ex)
        {
            check("MCP: JSON-RPC через POST /mcp", false, ex.Message);
        }

        // REST-эндпоинт инструмента
        try
        {
            var status = client.GetStringAsync(server.BaseUrl + "/api/status").GetAwaiter().GetResult();
            check("REST: /api/status отвечает", status.Contains("backupRoot"), Truncate(status));
        }
        catch (Exception ex)
        {
            check("REST: /api/status отвечает", false, ex.Message);
        }

        try
        {
            var unknown = client.GetAsync(server.BaseUrl + "/api/несуществует").GetAwaiter().GetResult();
            check("неизвестный адрес даёт 404", (int)unknown.StatusCode == 404);
        }
        catch (Exception ex)
        {
            check("неизвестный адрес даёт 404", false, ex.Message);
        }

        server.Stop();
        check("сервер останавливается", !server.IsRunning);
    }

    /// <summary>Проверка переключения встроенного сервера настройками (как в окне настроек).</summary>
    private static void TestHostSwitching(CheckDelegate check)
    {
        var settings = new AppSettings { McpEnabled = false, McpPort = FreePort() };
        using var host = new McpHost(settings);

        host.ApplySettings(settings);
        check("выключенный сервер не поднимается", !host.IsRunning);

        settings.McpEnabled = true;
        host.ApplySettings(settings);
        check("сервер поднимается по настройке", host.IsRunning, host.BaseUrl);

        check("адрес MCP-эндпоинта верный",
            host.McpUrl == "http://127.0.0.1:" + settings.McpPort + "/mcp", host.McpUrl);
        check("конфигурация клиента содержит адрес", host.ClientConfigJson().Contains(host.McpUrl));

        settings.McpEnabled = false;
        host.ApplySettings(settings);
        check("сервер останавливается по настройке", !host.IsRunning);
    }

    private static string PostJson(HttpClient client, string url, string json)    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return client.PostAsync(url, content).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string Truncate(string text) =>
        text.Length > 160 ? text[..160] + "…" : text;

    /// <summary>Находит свободный порт на 127.0.0.1.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
