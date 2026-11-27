using System.Diagnostics;

namespace HvBackup;

/// <summary>Автозапуск программы при входе в Windows (через Планировщик задач, от имени SYSTEM).</summary>
public static class AutoStart
{
    private const string TaskName = "HvBackup Auto";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Error) Enable(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/Create");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(TaskName);
            psi.ArgumentList.Add("/SC");
            psi.ArgumentList.Add("ONLOGON");
            psi.ArgumentList.Add("/RU");
            psi.ArgumentList.Add("SYSTEM");
            psi.ArgumentList.Add("/RL");
            psi.ArgumentList.Add("HIGHEST");
            psi.ArgumentList.Add("/TR");
            psi.ArgumentList.Add($"\"{exePath}\"");
            psi.ArgumentList.Add("/F");

            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return p.ExitCode == 0 ? (true, outp.Trim()) : (false, (err + outp).Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Error) Disable()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(TaskName);
            psi.ArgumentList.Add("/F");

            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            return p.ExitCode == 0 ? (true, outp.Trim()) : (false, outp.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
