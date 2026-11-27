using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>Документ OpenAPI 3.0 — собирается из реестра инструментов.</summary>
public static class OpenApi
{
    public static JsonObject Build(int port)
    {
        var paths = new JsonObject();
        var tags = new JsonArray();

        var seenTags = new List<string>();

        foreach (var tool in ToolRegistry.All)
        {
            if (!seenTags.Contains(tool.Tag))
            {
                seenTags.Add(tool.Tag);
                tags.Add(new JsonObject { ["name"] = tool.Tag });
            }

            var pathParams = tool.PathParams();
            var parameters = new JsonArray();
            var bodyProperties = new JsonObject();
            var bodyRequired = new JsonArray();

            if (tool.Schema["properties"] is JsonObject properties)
            {
                var required = tool.Schema["required"] as JsonArray;
                foreach (var pair in properties)
                {
                    var schema = pair.Value?.DeepClone() as JsonObject ?? new JsonObject();

                    if (pathParams.Contains(pair.Key))
                    {
                        parameters.Add(Parameter(pair.Key, "path", schema, true));
                        continue;
                    }

                    if (tool.Query.Contains(pair.Key))
                    {
                        parameters.Add(Parameter(pair.Key, "query", schema, false));
                        continue;
                    }

                    bodyProperties[pair.Key] = schema;
                    if (required != null && required.Any(r => r?.GetValue<string>() == pair.Key))
                        bodyRequired.Add(pair.Key);
                }
            }

            var operation = new JsonObject
            {
                ["operationId"] = tool.Name,
                ["summary"] = tool.Title,
                ["description"] = tool.Description,
                ["tags"] = new JsonArray { tool.Tag },
                ["parameters"] = parameters,
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject
                    {
                        ["description"] = "Операция выполнена, результат — в теле ответа (JSON).",
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = new JsonObject { ["type"] = "object" }
                            }
                        }
                    },
                    ["400"] = new JsonObject
                    {
                        ["description"] = "Ошибка: неверные аргументы либо операция не выполнена. Текст ошибки — в поле error."
                    },
                    ["404"] = new JsonObject
                    {
                        ["description"] = "Неизвестный адрес."
                    }
                }
            };

            if (bodyProperties.Count > 0)
            {
                var bodySchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = bodyProperties
                };
                if (bodyRequired.Count > 0) bodySchema["required"] = bodyRequired;

                operation["requestBody"] = new JsonObject
                {
                    ["required"] = bodyRequired.Count > 0,
                    ["description"] = "Параметры операции.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = bodySchema }
                    }
                };
            }

            if (!paths.ContainsKey(tool.Path)) paths[tool.Path] = new JsonObject();
            var byPath = paths[tool.Path]!.AsObject();
            byPath[tool.Method.ToLowerInvariant()] = operation;
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "ГиперВ Резерв — API управления копиями Hyper-V",
                ["version"] = McpVersion.Value,
                ["description"] =
                    "Горячее резервное копирование виртуальных машин Hyper-V без остановки машины. " +
                    "Операции: список машин и копий, копирование, удаление копий, работа с расписанием, " +
                    "восстановление, настройки и журнал. Операции с Hyper-V выполняются от имени " +
                    "администратора — иначе сервер вернёт понятную ошибку."
            },
            ["servers"] = new JsonArray
            {
                new JsonObject { ["url"] = $"http://127.0.0.1:{port}", ["description"] = "Локальный сервер" }
            },
            ["tags"] = tags,
            ["paths"] = paths
        };
    }

    private static JsonObject Parameter(string name, string where, JsonObject schema, bool required)
    {
        var result = new JsonObject
        {
            ["name"] = name,
            ["in"] = where,
            ["required"] = required || where == "path",
            ["description"] = schema["description"]?.GetValue<string>() ?? "",
            ["schema"] = schema
        };

        // в схеме параметра описание не нужно — оно уже выше
        var copy = schema.DeepClone().AsObject();
        copy.Remove("description");
        result["schema"] = copy;

        return result;
    }
}
