using System.Text;

namespace HvBackup;

/// <summary>Генератор restore.ps1 и сопроводительных файлов для копии.</summary>
public static class RestoreScript
{
    private static string Q(string s) => s.Replace("'", "''");

    public sealed class DiskSpec
    {
        public string File { get; init; } = "";          // имя файла в папке Disks
        public string Source { get; init; } = "";        // исходный путь в ВМ
        public string ParentFile { get; init; } = "";     // файл родителя (если есть) внутри копии
        public long SizeBytes { get; init; }
        public int ControllerNumber { get; init; }
        public int ControllerLocation { get; init; }
        public string ControllerType { get; init; } = "SCSI";
        public bool IsTop { get; init; }                 // верхний диск цепочки (его подключаем к ВМ)
    }

    public static string Build(
        string vmName,
        int generation,
        long memoryStartupBytes,
        int processorCount,
        string switchName,
        string macAddress,
        IReadOnlyList<DiskSpec> disks,
        string topDiskFile,
        string createdLocal,
        string consistency)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<#");
        sb.AppendLine($"    Восстановление виртуальной машины \"{vmName}\" из горячей копии.");
        sb.AppendLine($"    Копия создана: {createdLocal}");
        sb.AppendLine($"    Консистентность: {consistency}");
        sb.AppendLine();
        sb.AppendLine("    Запускать из окна PowerShell ОТ ИМЕНИ АДМИНИСТРАТОРА.");
        sb.AppendLine("    Пример:  .\\restore.ps1 -Destination D:\\Restore -Start");
        sb.AppendLine("    Ключ -VmName задаёт имя восстанавливаемой машины (по умолчанию — как в оригинале).");
        sb.AppendLine("#>");
        sb.AppendLine("param(");
        sb.AppendLine($"    [string]$Destination = 'D:\\Restore\\{Q(vmName)}',");
        sb.AppendLine("    [switch]$Flatten,");
        sb.AppendLine("    [string]$VmName = '',");
        sb.AppendLine("    [switch]$Start");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$vmName = '{Q(vmName)}'");
        sb.AppendLine("if ($VmName) { $vmName = $VmName }");
        sb.AppendLine($"$generation = {generation}");
        sb.AppendLine($"$memory = {memoryStartupBytes}");
        sb.AppendLine($"$cpu = {processorCount}");
        sb.AppendLine($"$switch = '{Q(switchName)}'");
        sb.AppendLine($"$mac = '{Q(macAddress)}'");
        sb.AppendLine();
        sb.AppendLine("$here = Split-Path -Parent $MyInvocation.MyCommand.Path");
        sb.AppendLine("$src = Join-Path $here 'Disks'");
        sb.AppendLine("if (-not (Test-Path $src)) { throw \"В папке копии нет подпапки Disks.\" }");
        sb.AppendLine("if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }");
        sb.AppendLine("$dst = Join-Path $Destination 'Disks'");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Копирование дисков в целевую папку...' -ForegroundColor Cyan");
        sb.AppendLine("if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }");
        sb.AppendLine("Get-ChildItem $src -File | ForEach-Object {");
        sb.AppendLine("    Copy-Item $_.FullName -Destination (Join-Path $dst $_.Name) -Force");
        sb.AppendLine("    Write-Host (\"  \" + $_.Name) -ForegroundColor DarkGray");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Восстанавливаем связи цепочки дисков (родитель -> потомок) внутри новой папки");
        var linkLines = disks
            .Where(x => !string.IsNullOrEmpty(x.ParentFile))
            .Select(d => $"    @{{ child = '{Q(d.File)}'; parent = '{Q(d.ParentFile)}' }}")
            .ToList();
        sb.AppendLine("$links = @(");
        sb.AppendLine(string.Join(",", linkLines));
        sb.AppendLine(")");
        sb.AppendLine("foreach ($l in $links) {");
        sb.AppendLine("    $c = Join-Path $dst $l.child");
        sb.AppendLine("    $p = Join-Path $dst $l.parent");
        sb.AppendLine("    try { Set-VHD -Path $c -ParentPath $p -ErrorAction Stop; Write-Host (\"  цепочка: \" + $l.child + \" -> \" + $l.parent) -ForegroundColor DarkGray }");
        sb.AppendLine("    catch { Write-Warning (\"Не удалось связать \" + $l.child + \" с \" + $l.parent + \": \" + $_.Exception.Message) }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$attach = Join-Path $dst '" + Q(topDiskFile) + "'");
        sb.AppendLine();
        sb.AppendLine("if ($Flatten) {");
        sb.AppendLine("    Write-Host 'Объединение цепочки в один VHDX...' -ForegroundColor Cyan");
        sb.AppendLine("    $flat = Join-Path $dst '" + Q(Path.GetFileNameWithoutExtension(topDiskFile)) + "-flat.vhdx'");
        sb.AppendLine("    $child = Get-ChildItem $dst -Filter '*.avhdx' | Select-Object -First 1");
        sb.AppendLine("    if ($child) {");
        sb.AppendLine("        $parentFile = $null");
        sb.AppendLine("        foreach ($l in $links) { if ($l.child -eq $child.Name) { $parentFile = $l.parent } }");
        sb.AppendLine("        if ($parentFile) {");
        sb.AppendLine("            Convert-VHD -Path $child.FullName -DestinationPath $flat -ParentPath (Join-Path $dst $parentFile) -VHDType Dynamic");
        sb.AppendLine("            $attach = $flat");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Создание виртуальной машины...' -ForegroundColor Cyan");
        sb.AppendLine("$vmArgs = @{");
        sb.AppendLine("    Name = $vmName");
        sb.AppendLine("    Generation = $generation");
        sb.AppendLine("    MemoryStartupBytes = $memory");
        sb.AppendLine("    VHDPath = $attach");
        sb.AppendLine("    Path = $Destination");
        sb.AppendLine("}");
        sb.AppendLine("if ($switch) { $vmArgs['SwitchName'] = $switch }");
        sb.AppendLine("try {");
        sb.AppendLine("    $vm = New-VM @vmArgs");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Write-Warning (\"New-VM: \" + $_.Exception.Message + \" — пробуем создать без поколения 2.\")");
        sb.AppendLine("    $vmArgs.Remove('Generation')");
        sb.AppendLine("    $vm = New-VM @vmArgs");
        sb.AppendLine("}");
        sb.AppendLine("Set-VMProcessor -VM $vm -Count $cpu | Out-Null");
        sb.AppendLine("if ($mac) { try { Set-VMNetworkAdapter -VM $vm -StaticMacAddress $mac | Out-Null } catch {} }");

        // Дополнительные виртуальные диски (кроме системного) подключаем отдельно.
        var extraTops = disks.Where(d => d.IsTop && !string.Equals(d.File, topDiskFile, StringComparison.OrdinalIgnoreCase)).ToList();
        if (extraTops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Дополнительные диски виртуальной машины");
            foreach (var d in extraTops)
                sb.AppendLine($"$extra = @{{ file = '{Q(d.File)}'; controllerNumber = {d.ControllerNumber}; controllerLocation = {d.ControllerLocation}; controllerType = '{Q(d.ControllerType)}' }}");
            sb.AppendLine("foreach ($e in $extra) {");
            sb.AppendLine("    $p = Join-Path $dst $e.file");
            sb.AppendLine("    if (-not (Test-Path $p)) { continue }");
            sb.AppendLine("    try {");
            sb.AppendLine("        Add-VMHardDiskDrive -VM $vm -ControllerType $e.controllerType -ControllerNumber $e.controllerNumber -ControllerLocation $e.controllerLocation -Path $p | Out-Null");
            sb.AppendLine("        Write-Host (\"  подключён диск \" + $e.file) -ForegroundColor DarkGray");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        Write-Warning (\"Не удалось подключить \" + $e.file + \": \" + $_.Exception.Message)");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        sb.AppendLine("Write-Host (\"Машина \" + $vmName + \" создана.\") -ForegroundColor Green");
        sb.AppendLine("if ($Start) { Start-VM -VM $vm | Out-Null; Write-Host 'Машина запущена.' -ForegroundColor Green }");
        return sb.ToString();
    }

    public static string BuildReadme(string vmName, string createdLocal, string consistency, IReadOnlyList<DiskSpec> disks, string topDiskFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ГОРЯЧАЯ КОПИЯ ВИРТУАЛЬНОЙ МАШИНЫ");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Виртуальная машина : {vmName}");
        sb.AppendLine($"Дата копии         : {createdLocal}");
        sb.AppendLine($"Консистентность    : {consistency}");
        sb.AppendLine();
        sb.AppendLine("СОСТАВ КОПИИ");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("Disks\\        — файлы виртуальных дисков (копия цепочки)");
        sb.AppendLine("vm-info.json  — параметры ВМ (память, ЦП, ключ и т.п.)");
        sb.AppendLine("restore.ps1   — скрипт восстановления (запускать от администратора)");
        sb.AppendLine();
        sb.AppendLine("ДИСКИ");
        foreach (var d in disks)
        {
            var rel = string.IsNullOrEmpty(d.ParentFile) ? "(корневой)" : "родитель: " + d.ParentFile;
            sb.AppendLine($"  {d.File,-34} {ThrottledCopier.FormatBytes(d.SizeBytes),12}  {rel}");
        }
        sb.AppendLine();
        sb.AppendLine($"Подключаемый диск верхнего уровня: {topDiskFile}");
        sb.AppendLine();
        sb.AppendLine("КАК ВОССТАНОВИТЬ");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("  1. Откройте PowerShell от имени администратора.");
        sb.AppendLine("  2. cd <папка копии>");
        sb.AppendLine("  3. .\\restore.ps1 -Destination D:\\Restore\\" + vmName + " -Start");
        sb.AppendLine("     (ключ -Flatten объединит цепочку дисков в один VHDX)");
        sb.AppendLine();
        sb.AppendLine("ВАЖНО");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("  * ВМ во время копирования продолжала работать.");
        sb.AppendLine("  * Контрольная точка снимается автоматически после копирования,");
        sb.AppendLine("    поэтому разностные диски в копии — снимок состояния на момент копии.");
        return sb.ToString();
    }
}
