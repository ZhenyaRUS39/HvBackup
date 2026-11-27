using System.Runtime.InteropServices;
using HvBackup.Mcp;

namespace HvBackup;

internal static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        // Режим MCP (stdio): stdout занят протоколом — консоль родителя не подключаем.
        if (Has(args, "--mcp"))
            return McpRunner.RunStdio(args);

        // HTTP-режим для проверки и внешних клиентов без окна программы.
        if (Has(args, "--mcp-http"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            return McpRunner.RunHttp(args);
        }

        if (Has(args, "--selftest") || Has(args, "--listvms") || Has(args, "--version")
            || Has(args, "--preview") || Has(args, "--copies"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            AppLog.Init(consoleMode: true);
            if (Has(args, "--preview")) return RenderPreview(args);
            return RunConsole(args);
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Ошибка в интерфейсе не должна убивать программу: пишем в журнал и продолжаем работу.
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("Непредвиденная ошибка в интерфейсе: " + e.Exception);
            MessageBox.Show("Непредвиденная ошибка: " + e.Exception.Message +
                "\n\nПодробности записаны в журнал. Программа продолжает работу.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("Критическая ошибка в фоновом потоке: " + e.ExceptionObject);
        };

        bool createdNew;
        using var mutex = new Mutex(true, @"Global\HvBackup_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ГиперВ Резерв уже запущен. Значок программы находится в трее.",
                "ГиперВ Резерв", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        AppLog.Init(consoleMode: false);
        AppLog.Info("Программа запущена.");

        var settings = SettingsStore.Load();
        try
        {
            Application.Run(new MainForm(settings));
        }
        catch (Exception ex)
        {
            AppLog.Error("Критическая ошибка: " + ex);
            MessageBox.Show("Критическая ошибка: " + ex.Message, "ГиперВ Резерв",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        finally
        {
            AppLog.Info("Программа завершена.");
        }
        return 0;
    }

    /// <summary>Рисует окно программы в PNG (для предпросмотра оформления).</summary>
    private static int RenderPreview(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        var path = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(Path.GetTempPath(), "hvbackup-preview.png");

        ApplicationConfiguration.Initialize();
        Ui.Theme.Init(args.Any(a => a.Equals("--dark", StringComparison.OrdinalIgnoreCase)));

        var settings = SettingsStore.Load();
        if (args.Any(a => a.Equals("--schedule", StringComparison.OrdinalIgnoreCase)))
        {
            settings.Schedule = ScheduleKind.Daily;
            settings.Hour = 3;
            settings.Minute = 0;
            using var scheduleForm = new ScheduleForm(settings);
            scheduleForm.StartPosition = FormStartPosition.Manual;
            scheduleForm.Location = new Point(-4000, -4000);
            scheduleForm.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (var bmp = new Bitmap(scheduleForm.Width, scheduleForm.Height))
            {
                scheduleForm.DrawToBitmap(bmp, new Rectangle(0, 0, scheduleForm.Width, scheduleForm.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            scheduleForm.Hide();
            AppLog.Out("Предпросмотр сохранён: " + path);
            return 0;
        }

        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            using var settingsForm = new SettingsForm(settings);
            settingsForm.StartPosition = FormStartPosition.Manual;
            settingsForm.Location = new Point(-4000, -4000);
            settingsForm.Show();
            if (args.Any(a => a.Equals("--mcp-tab", StringComparison.OrdinalIgnoreCase)))
                settingsForm.SelectTab(3);
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (var bmp = new Bitmap(settingsForm.Width, settingsForm.Height))
            {
                settingsForm.DrawToBitmap(bmp, new Rectangle(0, 0, settingsForm.Width, settingsForm.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            settingsForm.Hide();
            AppLog.Out("Предпросмотр сохранён: " + path);
            return 0;
        }

        if (args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            using var restoreForm = new RestoreForm(settings) { SkipHostPaths = true };
            restoreForm.StartPosition = FormStartPosition.Manual;
            restoreForm.Location = new Point(-4000, -4000);
            restoreForm.Show();
            restoreForm.ApplyPreviewData();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (var bmp = new Bitmap(restoreForm.Width, restoreForm.Height))
            {
                restoreForm.DrawToBitmap(bmp, new Rectangle(0, 0, restoreForm.Width, restoreForm.Height));
                restoreForm.PaintRowIcons(bmp);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            restoreForm.Hide();
            AppLog.Out("Предпросмотр сохранён: " + path);
            return 0;
        }

        if (args.Any(a => a.Equals("--vm", StringComparison.OrdinalIgnoreCase)))
        {
            settings.SetPerVm("Win10-Test", new VmSettings
            {
                SpeedLimitMBps = 50,
                KeepCopies = 5,
                BackupRoot = @"E:\Backups\Win10",
                UseOwnSchedule = true,
                Schedule = ScheduleKind.Weekly,
                Hour = 4,
                Minute = 30,
                WeeklyDay = 3
            });
            if (!settings.SelectedVms.Contains("Win10-Test")) settings.SelectedVms.Add("Win10-Test");

            using var vmForm = new VmSettingsForm(settings, "Win10-Test");
            vmForm.StartPosition = FormStartPosition.Manual;
            vmForm.Location = new Point(-4000, -4000);
            vmForm.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (var bmp = new Bitmap(vmForm.Width, vmForm.Height))
            {
                vmForm.DrawToBitmap(bmp, new Rectangle(0, 0, vmForm.Width, vmForm.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            vmForm.Hide();
            AppLog.Out("Предпросмотр сохранён: " + path);
            return 0;
        }

        using var form = new MainForm(settings);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000); // не мигать на экране
        form.Show();
        Application.DoEvents();

        var demo = new List<VmInfo>
        {
            new() { Name = "Win10-Test", State = "Running", ProcessorCount = 4, MemoryStartupBytes = 8L << 30,
                    Disks = new List<string> { @"D:\VM\Win10.vhdx" }, DiskSizeBytes = 42L << 30, CheckpointType = "Production", SwitchName = "Default Switch" },
            new() { Name = "Win-Server-2019", State = "Running", ProcessorCount = 8, MemoryStartupBytes = 16L << 30,
                    Disks = new List<string> { @"D:\VM\Server.vhdx", @"D:\VM\Data.vhdx" }, DiskSizeBytes = 320L << 30, CheckpointType = "Standard", SwitchName = "Default Switch" },
            new() { Name = "Ubuntu-Dev", State = "Off", ProcessorCount = 2, MemoryStartupBytes = 4L << 30,
                    Disks = new List<string> { @"D:\VM\Ubuntu.vhdx" }, DiskSizeBytes = 20L << 30, CheckpointType = "Standard", SwitchName = "Default Switch" }
        };

        var log = string.Join(Environment.NewLine, new[]
        {
            "22:14:01 | ИНФО | Программа запущена.",
            "22:14:03 | ИНФО | ВМ «Win10-Test»: состояние Running, дисков 1, память 8 ГБ.",
            "22:14:03 | ИНФО | Контрольная точка «HvBackup 2026-11-25_22-14-03» создана, тип: Production. ВМ продолжает работать.",
            "22:14:05 | ИНФО | Копирование D:\\VM\\Win10.vhdx → ...\\Disks\\Win10.vhdx (41,2 ГБ).",
            "22:14:41 | ИНФО | Связи цепочки дисков в копии настроены.",
            "22:14:42 | ИНФО | Контрольная точка удалена, разностный диск слит с основным.",
            "22:14:44 | ИНФО | Копия «Win10-Test» готова: D:\\Backups\\Hyper-V\\Win10-Test\\2026-11-25_22-14-03."
        });

        form.ApplyPreviewData(demo, log);
        Application.DoEvents();

        // Живая демонстрация хода копирования: настоящее копирование с ограничением
        // скорости — в карточке должны появиться скорость, оставшееся время и объём.
        if (args.Any(a => a.Equals("--copy", StringComparison.OrdinalIgnoreCase)))
        {
            var runId = form.BeginProgress();
            var temp = Path.Combine(Path.GetTempPath(), "HvBackupDemo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var block = new byte[4 * 1024 * 1024];
            Random.Shared.NextBytes(block);
            using (var fs = new FileStream(Path.Combine(temp, "Win10.vhdx"), FileMode.Create, FileAccess.Write))
                for (int i = 0; i < 48; i++) fs.Write(block);

            var copyTask = Task.Run(async () =>
            {
                await ThrottledCopier.CopyDirectoryAsync(temp, Path.Combine(temp, "copy"), 40L * 1024 * 1024,
                    new ActionProgress<CopyProgress>(cp => form.UpdateProgress(new BackupProgress
                    {
                        Phase = "Копирование: Win10.vhdx",
                        Done = cp.BytesDone,
                        Total = cp.TotalBytes,
                        Speed = cp.BytesPerSecond,
                        Indeterminate = false
                    }, runId)),
                    CancellationToken.None);
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!copyTask.IsCompleted && sw.Elapsed.TotalSeconds < 1.5)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(40);
            }
            Application.DoEvents();
            try { Directory.Delete(temp, true); } catch { }
        }

        System.Threading.Thread.Sleep(400);
        Application.DoEvents();

        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            form.PaintRowIcons(bmp);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        form.Hide();
        AppLog.Out("Предпросмотр сохранён: " + path);
        return 0;
    }

    private static int RunConsole(string[] args)    {
        if (Has(args, "--version"))
        {
            Console.WriteLine("ГиперВ Резерв " + AppVersion.Value);
            return 0;
        }

        if (Has(args, "--copies"))
        {
            var settings = SettingsStore.Load();
            var copies = RestoreEngine.Scan(settings);
            Console.WriteLine($"Найдено копий: {copies.Count}");
            foreach (var c in copies)
                Console.WriteLine($"  {c.VmName,-26} {c.CreatedText,-17} {c.KindText,-20} {c.SizeText,10}  {c.Folder}");
            return copies.Count > 0 ? 0 : 1;
        }

        if (Has(args, "--listvms"))
        {
            var (ok, vms, err) = HyperV.ListVms();
            if (!ok)
            {
                Console.WriteLine("Не удалось получить список ВМ: " + err);
                return 2;
            }
            Console.WriteLine($"Найдено виртуальных машин: {vms.Count}");
            foreach (var vm in vms)
                Console.WriteLine($"  {vm.Name,-28} {vm.State,-10} {vm.ProcessorCount} vCPU  {vm.MemoryText,-8} " +
                                  $"дисков: {vm.Disks.Count}  КТ: {vm.CheckpointType}  сеть: {vm.SwitchName}");
            return 0;
        }

        return SelfTest.Run();
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
