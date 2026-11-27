using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HvBackup;

/// <summary>Запуск сценариев Windows PowerShell 5.1 (модуль Hyper-V грузится только в Windows PowerShell).</summary>
public static class Ps
{
    public const string Marker = "__HVB__";

    public sealed class Result
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public string Raw { get; init; } = "";
        public JsonElement Data { get; init; }
    }

    public static string PowerShellPath
    {
        get
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var p = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }
    }

    /// <summary>
    /// Выполняет сценарий. Тело сценария обязано присвоить результат в переменную $__hv.
    /// </summary>
    public static Result Run(string body, int timeoutSeconds = 600)
    {
        var script = Prefix + body + Suffix;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new Result { Ok = false, Error = "Не удалось запустить PowerShell: " + ex.Message };
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            return new Result { Ok = false, Error = $"Превышено время выполнения ({timeoutSeconds} с)." };
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        var idx = stdout.LastIndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            var err = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            if (err.Length > 800) err = err[..800] + "…";
            return new Result { Ok = false, Error = string.IsNullOrWhiteSpace(err) ? "PowerShell не вернул результат." : err };
        }

        var json = stdout[(idx + Marker.Length)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                var msg = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "неизвестная ошибка";
                return new Result { Ok = false, Error = msg, Raw = stdout };
            }
            var data = default(JsonElement);
            if (root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Undefined)
                data = d.Clone(); // копия, чтобы не зависеть от освобождения JsonDocument
            return new Result { Ok = true, Raw = stdout, Data = data };
        }
        catch (Exception ex)
        {
            return new Result { Ok = false, Error = "Не удалось разобрать ответ PowerShell: " + ex.Message, Raw = stdout };
        }
    }

    /// <summary>Выполняет сценарий и десериализует полезную нагрузку.</summary>
    public static (bool Ok, T? Value, string Error) RunJson<T>(string body, int timeoutSeconds = 600)
    {
        var res = Run(body, timeoutSeconds);
        if (!res.Ok) return (false, default, res.Error);
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var value = JsonSerializer.Deserialize<T>(res.Data.GetRawText(), opts);
            return (true, value, "");
        }
        catch (Exception ex)
        {
            return (false, default, "Не удалось разобрать данные: " + ex.Message);
        }
    }

    /// <summary>Проверяет синтаксис сценария без выполнения (используется в самопроверке).</summary>
    public static (bool Ok, string Error) CheckSyntax(string body)
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(body));
        var probe = Prefix +
            "$src = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('" + b64 + "'));" +
            "$errs = $null;" +
            "$null = [System.Management.Automation.Language.Parser]::ParseInput($src, [ref]$null, [ref]$errs);" +
            "if ($errs -and $errs.Count -gt 0) { $__hv = ($errs | ForEach-Object { $_.Message }) -join '; ' } else { $__hv = $true }" +
            Suffix;

        var res = Run(probe, 60);
        if (!res.Ok) return (false, res.Error);
        try
        {
            var text = res.Data.GetRawText();
            if (text.Trim() == "true") return (true, "");
            return (false, text.Trim().Trim('"').Replace("\\n", " ").Replace("\\r", ""));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private const string Prefix =
        "$OutputEncoding = [System.Text.Encoding]::UTF8;\n" +
        "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; } catch { };\n" +
        "$ProgressPreference = 'SilentlyContinue';\n" +
        "$ErrorActionPreference = 'Stop';\n" +
        "try {\n";

    private const string Suffix =
        "\n  Write-Output ('" + Marker + "' + ([PSCustomObject]@{ ok = $true;  data = $__hv } | ConvertTo-Json -Depth 10 -Compress));\n" +
        "} catch {\n" +
        "  $__m = $_.Exception.Message;\n" +
        "  if ($_.Exception.InnerException) { $__m = $__m + ' / ' + $_.Exception.InnerException.Message }\n" +
        "  Write-Output ('" + Marker + "' + ([PSCustomObject]@{ ok = $false; error = $__m } | ConvertTo-Json -Compress));\n" +
        "  exit 1;\n" +
        "}";
}
