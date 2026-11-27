using System.Text;

namespace HvBackup.Mcp;

/// <summary>
/// MCP-сервер в режиме stdio: JSON-RPC 2.0, сообщения — одна строка JSON на сообщение.
/// <para>
/// Всё, что попадает в stdout, должно быть JSON-RPC, поэтому обычный вывод
/// консоли (журнал, случайные сообщения) перенаправляется в пустой поток,
/// а протокол пишет в сохранённый выходной поток.
/// Обработка сообщений — общая с HTTP-режимом (<see cref="McpDispatcher"/>).
/// </para>
/// </summary>
public static class McpStdio
{
    public static async Task RunAsync(ToolContext context, CancellationToken ct)
    {
        var stdout = Console.OpenStandardOutput();
        var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

        // обычный вывод консоли не должен попадать в поток протокола
        var protocolOut = Console.Out;
        Console.SetOut(TextWriter.Null);

        var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Trim().Length == 0) continue;

                var response = await McpDispatcher.HandleAsync(line, context, ct).ConfigureAwait(false);
                if (response == null) continue;   // уведомление — ответа нет

                writer.WriteLine(response);
            }
        }
        catch (OperationCanceledException)
        {
            // сервер остановлен
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка MCP-сервера: " + ex.Message);
        }
        finally
        {
            Console.SetOut(protocolOut);
        }
    }
}
