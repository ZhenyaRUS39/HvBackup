using System.Text.Json;
using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>
/// Обработка сообщений MCP (JSON-RPC 2.0).
/// <para>
/// Один и тот же обработчик работает и в режиме stdio (сообщения построчно),
/// и в HTTP-режиме (POST /mcp) — логика протокола не дублируется.
/// </para>
/// </summary>
public static class McpDispatcher
{
    /// <summary>Версии протокола, которые сервер понимает.</summary>
    public static readonly string[] SupportedProtocols = { "2024-11-05", "2025-03-26", "2025-06-18" };

    public const string DefaultProtocol = "2024-11-05";

    /// <summary>
    /// Обрабатывает одно сообщение. Возвращает ответ (JSON) или null,
    /// если сообщение было уведомлением и ответа не требуется.
    /// </summary>
    public static async Task<string?> HandleAsync(string message, ToolContext context, CancellationToken ct)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(message) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            return Error(null, -32700, "Не разобран запрос: " + ex.Message).ToJsonString();
        }

        var id = request["id"];
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    var protocol = SupportedProtocols.Contains(requested ?? "") ? requested! : DefaultProtocol;
                    var result = new JsonObject
                    {
                        ["protocolVersion"] = protocol,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false }
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "hvbackup",
                            ["title"] = "ГиперВ Резерв",
                            ["version"] = McpVersion.Value
                        },
                        ["instructions"] =
                            "Управление горячим резервным копированием виртуальных машин Hyper-V. " +
                            "Копии делаются без остановки машины. Сервер встроен в программу «ГиперВ Резерв» " +
                            "и работает с правами администратора, поэтому операции с Hyper-V доступны."
                    };
                    return Response(id, result).ToJsonString();
                }

                case "notifications/initialized":
                case "initialized":
                    return null;   // уведомление — ответа не требуется

                case "ping":
                    return Response(id, new JsonObject()).ToJsonString();

                case "tools/list":
                    return Response(id, new JsonObject { ["tools"] = ToolsList() }).ToJsonString();

                case "tools/call":
                {
                    var name = parameters?["name"]?.GetValue<string>() ?? "";
                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

                    var tool = ToolRegistry.Find(name);
                    if (tool == null)
                    {
                        return Response(id, new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "text", ["text"] = "Инструмент не найден: " + name }
                            },
                            ["isError"] = true
                        }).ToJsonString();
                    }

                    var result = await tool.Handler(context, arguments, ct).ConfigureAwait(false);
                    var text = result.Ok
                        ? Pretty(result.Data ?? new JsonObject())
                        : "Ошибка: " + result.Error;

                    return Response(id, new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = text }
                        },
                        ["isError"] = !result.Ok
                    }).ToJsonString();
                }

                case "resources/list":
                    return Response(id, new JsonObject { ["resources"] = new JsonArray() }).ToJsonString();

                case "prompts/list":
                    return Response(id, new JsonObject { ["prompts"] = new JsonArray() }).ToJsonString();

                default:
                    if (id == null) return null;   // неизвестное уведомление — молча
                    return Error(id, -32601, "Метод не поддерживается: " + method).ToJsonString();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка обработки запроса MCP (" + method + "): " + ex.Message);
            if (id == null) return null;
            return Error(id, -32603, ex.Message).ToJsonString();
        }
    }

    /// <summary>Список инструментов в формате MCP.</summary>
    public static JsonArray ToolsList()
    {
        var list = new JsonArray();
        foreach (var tool in ToolRegistry.All)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone()
            });
        }
        return list;
    }

    internal static JsonObject Response(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    internal static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    internal static string Pretty(JsonNode node)
    {
        try
        {
            return node.ToJsonString(JsonOptions);
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    /// <summary>Общие параметры JSON: кириллица не экранируется — ответы читаются глазами.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
}
