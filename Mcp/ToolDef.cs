using System.Text.Json.Nodes;

namespace HvBackup.Mcp;

/// <summary>
/// Описание одного инструмента сервера.
/// <para>
/// Из одного описания получаются и список инструментов MCP, и REST-эндпоинты,
/// и документ OpenAPI — поэтому описание ведётся только здесь.
/// </para>
/// </summary>
public sealed class ToolDef
{
    /// <summary>Имя инструмента (латиницей, как требует MCP).</summary>
    public required string Name { get; init; }

    /// <summary>Короткое название по-русски.</summary>
    public required string Title { get; init; }

    /// <summary>Подробное описание по-русски (попадает и в MCP, и в Swagger).</summary>
    public required string Description { get; init; }

    /// <summary>HTTP-метод для REST-режима: GET, POST, PUT, DELETE.</summary>
    public required string Method { get; init; }

    /// <summary>Путь REST-эндпоинта, например /api/backups/{vm}.</summary>
    public required string Path { get; init; }

    /// <summary>Параметры, которые передаются в строке запроса (query).</summary>
    public string[] Query { get; init; } = Array.Empty<string>();

    /// <summary>Группа в Swagger (по умолчанию — «Копирование»).</summary>
    public string Tag { get; init; } = "Копирование";

    /// <summary>JSON Schema всех параметров инструмента.</summary>
    public required JsonObject Schema { get; init; }

    /// <summary>Обработчик вызова.</summary>
    public required Func<ToolContext, JsonObject, CancellationToken, Task<ToolResult>> Handler { get; init; }

    /// <summary>Имена параметров, объявленных в пути (например {vm}).</summary>
    public List<string> PathParams()
    {
        var result = new List<string>();
        foreach (var part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith('{') && part.EndsWith('}'))
                result.Add(part.Trim('{', '}'));
        return result;
    }
}

/// <summary>Результат вызова инструмента.</summary>
public sealed class ToolResult
{
    public bool Ok { get; init; } = true;

    /// <summary>Данные результата (для MCP — текст JSON, для REST — тело ответа).</summary>
    public JsonObject? Data { get; init; }

    /// <summary>Сообщение об ошибке (если Ok = false).</summary>
    public string Error { get; init; } = "";

    public static ToolResult Success(JsonObject data) => new() { Ok = true, Data = data };

    public static ToolResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>Контекст вызова инструмента.</summary>
public sealed class ToolContext
{
    public required AppSettings Settings { get; init; }

    /// <summary>Загрузчик настроек с диска (нужен, чтобы видеть изменения из GUI).</summary>
    public required Func<AppSettings> ReloadSettings { get; init; }

    /// <summary>Сохранитель настроек.</summary>
    public required Action<AppSettings> SaveSettings { get; init; }

    /// <summary>Фоновые операции (копии).</summary>
    public required JobRunner Jobs { get; init; }

    /// <summary>Журнал.</summary>
    public required Action<string> Log { get; init; }
}

/// <summary>Помощники для сборки JSON Schema.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, schema, isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired) required.Add(name);
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };

        if (required.Count > 0) result["required"] = required;
        return result;
    }

    public static JsonObject Str(string description, string? defaultValue = null, string[]? examples = null)
    {
        var result = new JsonObject
        {
            ["type"] = "string",
            ["description"] = description
        };
        if (defaultValue != null) result["default"] = defaultValue;
        if (examples != null) result["examples"] = new JsonArray(Array.ConvertAll(examples, e => (JsonNode?)e));
        return result;
    }

    public static JsonObject Int(string description, int? defaultValue = null, int? min = null, int? max = null)
    {
        var result = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description
        };
        if (defaultValue.HasValue) result["default"] = defaultValue.Value;
        if (min.HasValue) result["minimum"] = min.Value;
        if (max.HasValue) result["maximum"] = max.Value;
        return result;
    }

    public static JsonObject Bool(string description, bool defaultValue = false)
    {
        return new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = defaultValue
        };
    }

    public static JsonObject Enum(string description, string[] values, string? defaultValue = null)
    {
        var result = new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = new JsonArray(Array.ConvertAll(values, v => (JsonNode?)v))
        };
        if (defaultValue != null) result["default"] = defaultValue;
        return result;
    }
}
