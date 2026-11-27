using System.Text;

namespace HvBackup.Mcp;

/// <summary>
/// Запуск MCP-сервера из командной строки.
/// <para>
/// Режим stdio нужен MCP-клиентам, которые запускают программу как дочерний процесс.
/// Режим HTTP поднимает тот же сервер на 127.0.0.1 — им удобно проверять работу
/// и пользоваться REST-эндпоинтами без графического окна.
/// </para>
/// </summary>
public static class McpRunner
{
    /// <summary>Режим stdio: JSON-RPC через стандартный ввод/вывод.</summary>
    public static int RunStdio(string[] args)
    {
        AppLog.Init(consoleMode: false);

        var context = McpContext.Create();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            McpStdio.RunAsync(context, stop.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка MCP-сервера: " + ex.Message);
            AppLog.Error("Ошибка MCP-сервера: " + ex.Message);
            return 2;
        }
    }

    /// <summary>Режим HTTP: сервер на 127.0.0.1 с REST и Swagger, до Ctrl+C.</summary>
    public static int RunHttp(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        AppLog.Init(consoleMode: true);

        var port = IntArg(args, "--port", 8787);
        var context = McpContext.Create();

        using var server = new HttpApiServer(context);
        try
        {
            server.Start(port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Не удалось запустить HTTP-режим: " + ex.Message);
            return 2;
        }

        Console.WriteLine("MCP-сервер «ГиперВ Резерв»: " + server.BaseUrl);
        Console.WriteLine("MCP-эндпоинт: " + server.BaseUrl + "/mcp");
        Console.WriteLine("Документация Swagger: " + server.BaseUrl + "/swagger");
        Console.WriteLine("Остановка — Ctrl+C.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            Task.Delay(Timeout.Infinite, stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // остановка по Ctrl+C
        }

        server.Stop();
        return 0;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value))
            return value;
        return fallback;
    }
}
