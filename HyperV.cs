using System.Text;
using System.Text.Json;

namespace HvBackup;

public sealed class VmInfo
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public int Generation { get; set; }
    public long MemoryStartupBytes { get; set; }
    public int ProcessorCount { get; set; }
    public string CheckpointType { get; set; } = "";
    public string ConfigLocation { get; set; } = "";
    public string SnapshotFileLocation { get; set; } = "";
    public string SwitchName { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public List<string> Disks { get; set; } = new();

    /// <summary>Суммарный объём виртуальных дисков машины (как их видит гостевая ОС).</summary>
    public long DiskSizeBytes { get; set; }

    public string MemoryText => MemoryStartupBytes >= 1L << 30
        ? $"{MemoryStartupBytes / (double)(1L << 30):0.#} ГБ"
        : $"{MemoryStartupBytes / (double)(1 << 20):0} МБ";

    public string DiskSizeText => DiskSizeBytes > 0
        ? ThrottledCopier.FormatBytes(DiskSizeBytes)
        : "—";
}

public sealed class ChainItem
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string ParentPath { get; set; } = "";
    public string ChildPath { get; set; } = "";
    public bool IsActive { get; set; }
}

public sealed class DiskInfo
{
    public string ControllerType { get; set; } = "";
    public int ControllerNumber { get; set; }
    public int ControllerLocation { get; set; }
    public string ActivePath { get; set; } = "";
    public List<ChainItem> Chain { get; set; } = new();
}

public sealed class CheckpointInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed class LocatorFix
{
    public string Child { get; set; } = "";
    public string Parent { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Тексты сценариев PowerShell (вынесены отдельно — их проверяет самопроверка).</summary>
public static class PsScripts
{
    public const string ListVms = """
$items = @()
foreach ($v in @(Get-VM)) {
  $disks = @()
  $diskSize = [int64]0
  try {
    foreach ($d in @(Get-VMHardDiskDrive -VM $v -ErrorAction SilentlyContinue | Where-Object { $_.ControllerType -ne 'DVD' })) {
      if ($d.Path) {
        $disks += [string]$d.Path
        try {
          $h = Get-VHD -Path ([string]$d.Path) -ErrorAction SilentlyContinue
          if ($h) { $diskSize += [int64]$h.Size }
        } catch {}
      }
    }
  } catch {}
  $sw = ''
  $mac = ''
  try {
    $na = @(Get-VMNetworkAdapter -VM $v -ErrorAction SilentlyContinue | Where-Object { $_.SwitchName })
    if ($na.Count -gt 0) { $sw = [string]$na[0].SwitchName; $mac = [string]$na[0].MacAddress }
  } catch {}
  $items += [PSCustomObject]@{
    Name                 = [string]$v.Name
    Id                   = [string]$v.Id
    State                = [string]$v.State
    Generation           = [int]$v.Generation
    MemoryStartupBytes   = [int64]$v.MemoryStartup
    ProcessorCount       = [int]$v.ProcessorCount
    CheckpointType       = [string]$v.CheckpointType
    ConfigLocation       = [string]$v.ConfigurationLocation
    SnapshotFileLocation = [string]$v.SnapshotFileLocation
    SwitchName           = $sw
    MacAddress           = $mac
    Disks                = @($disks)
    DiskSizeBytes        = $diskSize
  }
}
$__hv = @($items)
""";

    public const string GetDiskChain = """
$name = '__VMNAME__'
$vm = Get-VM -Name $name
$out = @()
foreach ($d in @(Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue | Where-Object { $_.ControllerType -ne 'DVD' })) {
  $chain = @()
  $p = [string]$d.Path
  $n = 0
  while ($p -and $n -lt 32) {
    $n++
    $parent = $null; $child = $null; $size = 0
    try {
      $h = Get-VHD -Path $p -ErrorAction SilentlyContinue
      if ($h) {
        $size = [int64]$h.Size
        if ($h.ParentPath) { $parent = [string]$h.ParentPath }
        if ($h.ChildPath)   { $child  = [string]$h.ChildPath }
      }
    } catch {}
    if ($size -eq 0 -and (Test-Path -LiteralPath $p)) {
      try { $size = [int64](Get-Item -LiteralPath $p).Length } catch {}
    }
    $chain += [PSCustomObject]@{ path=$p; size=$size; parent=$parent; child=$child; isActive=($p -eq $d.Path) }
    $p = $parent
  }
  $out += [PSCustomObject]@{
    controllerType     = [string]$d.ControllerType
    controllerNumber   = [int]($d.ControllerNumber -as [int])
    controllerLocation = [int]($d.ControllerLocation -as [int])
    activePath         = [string]$d.Path
    chain              = @($chain)
  }
}
$__hv = @($out)
""";

    public const string CreateCheckpoint = """
$name = '__VMNAME__'
$snap = '__SNAPNAME__'
$vm = Get-VM -Name $name
$null = Checkpoint-VM -VM $vm -SnapshotName $snap
$s = Get-VMSnapshot -VM $vm -Name $snap -ErrorAction SilentlyContinue
if (-not $s) { $s = @(Get-VMSnapshot -VM $vm) | Sort-Object CreationTime -Descending | Select-Object -First 1 }
if (-not $s) { throw 'Контрольная точка не найдена после создания.' }
$__hv = [PSCustomObject]@{ id=[string]$s.Id; name=[string]$s.Name; type=[string]$s.SnapshotType }
""";

    public const string RemoveCheckpoint = """
$name = '__VMNAME__'
$snap = '__SNAPNAME__'
$vm = Get-VM -Name $name
Remove-VMSnapshot -VM $vm -Name $snap -Confirm:$false
$__hv = $true
""";

    public const string GetCheckpointType = """
$vm = Get-VM -Name '__VMNAME__'
$__hv = [string]$vm.CheckpointType
""";

    public const string SetCheckpointType = """
$vm = Get-VM -Name '__VMNAME__'
Set-VM -VM $vm -CheckpointType '__TYPE__' | Out-Null
$__hv = [string](Get-VM -Name '__VMNAME__').CheckpointType
""";

    public const string FixLocators = """
$payload = ConvertFrom-Json '__JSON__'
$report = @()
foreach ($p in @($payload.links)) {
  $child  = [string]$p.child
  $parent = [string]$p.parent
  $status = 'ok'
  $msg = ''
  try {
    if ($parent) {
      Set-VHD -Path $child -ParentPath $parent -ErrorAction Stop
      $ph = Get-VHD -Path $parent -ErrorAction SilentlyContinue
      if ($ph -and $ph.ChildPath -and ([string]$ph.ChildPath -ne $child)) {
        try {
          $v = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName MSFT_VHD -Filter ("Path='" + ($parent -replace "'", "''") + "'") -ErrorAction Stop
          if ($v) {
            $null = Invoke-CimMethod -InputObject $v -MethodName SetChildLocatorPath -Arguments @{ ChildPath = $child } -ErrorAction Stop
          }
        } catch {
          $status = 'warn'
          $msg = 'не удалось обновить child locator: ' + $_.Exception.Message
        }
      }
    }
  } catch {
    $status = 'error'
    $msg = $_.Exception.Message
  }
  $report += [PSCustomObject]@{ child=$child; parent=$parent; status=$status; message=$msg }
}
foreach ($t in @($payload.tops)) {
  if (-not $t) { continue }
  $status = 'ok'; $msg = ''
  try {
    $v = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName MSFT_VHD -Filter ("Path='" + ([string]$t -replace "'", "''") + "'") -ErrorAction Stop
    if ($v) {
      $null = Invoke-CimMethod -InputObject $v -MethodName SetChildLocatorPath -Arguments @{ ChildPath = '' } -ErrorAction Stop
    }
  } catch {
    $status = 'warn'
    $msg = 'не удалось очистить child locator верхнего диска: ' + $_.Exception.Message
  }
  $report += [PSCustomObject]@{ child=[string]$t; parent=''; status=$status; message=$msg }
}
$__hv = @($report)
""";

    public const string ExportVm = """
$name = '__VMNAME__'
$dest = '__DEST__'
$vm = Get-VM -Name $name -ErrorAction Stop
if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
if ('__LIVE__' -eq 'true' -and $vm.State -eq 'Running') {
    # Работающая ВМ: снимаем согласованное Production-состояние (VSS).
    # Машина при этом не останавливается и не ставится на паузу.
    Export-VM -VM $vm -Path $dest -CaptureLiveState CaptureDataConsistentState -ErrorAction Stop | Out-Null
} else {
    Export-VM -VM $vm -Path $dest -ErrorAction Stop | Out-Null
}
$__hv = $true
""";

    public const string CheckAccess = """
$null = Get-VM
$__hv = $true
""";

    // ─────────────────────────────── Восстановление ───────────────────────────────

    /// <summary>Пути хранения виртуальных машин и дисков на этом хосте.</summary>
    public const string GetHostPaths = """
$h = Get-VMHost
$__hv = @{
    vmPath = $h.VirtualMachinePath
    vhdPath = $h.VirtualHardDiskPath
    snapshotPath = $h.SnapshotFilePath
}
""";

    /// <summary>
    /// Импорт виртуальной машины из экспорта Hyper-V.
    /// Copy + GenerateNewId — импорт создаёт новую машину с новым идентификатором,
    /// поэтому исходная ВМ может остаться на месте.
    /// </summary>
    public const string ImportVm = """
$src = '__SRC__'
$name = '__NAME__'
$newId = '__NEWID__'
$vhdPath = '__VHD__'
$vmPath = '__VMPATH__'
$start = '__START__'
if (-not (Test-Path -LiteralPath $src)) { throw ("Папка копии не найдена: " + $src) }
$cfgDir = Join-Path $src 'Virtual Machines'
$vmcx = $null
if (Test-Path -LiteralPath $cfgDir) {
    $vmcx = Get-ChildItem -LiteralPath $cfgDir -Filter '*.vmcx' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $vmcx) { throw ("В папке копии нет файла конфигурации .vmcx: " + $cfgDir) }
$args = @{ Path = $vmcx.FullName; Copy = $true; ErrorAction = 'Stop' }
if ($newId -eq 'true') { $args['GenerateNewId'] = $true }
if ($vhdPath) { $args['VhdDestinationPath'] = $vhdPath }
if ($vmPath) { $args['VirtualMachinePath'] = $vmPath }
$vm = Import-VM @args
if ($name -and $vm.Name -ne $name) {
    try {
        Rename-VM -VM $vm -NewName $name -ErrorAction Stop | Out-Null
        $vm = Get-VM -Id $vm.Id
    } catch {
        Write-Warning ("Не удалось переименовать машину в «" + $name + "»: " + $_.Exception.Message)
    }
}
if ($start -eq 'true') {
    try { Start-VM -VM $vm | Out-Null }
    catch { Write-Warning ("Не удалось запустить машину: " + $_.Exception.Message) }
}
$__hv = @{ name = [string]$vm.Name; id = $vm.Id.ToString(); state = [string]$vm.State }
""";

    /// <summary>
    /// Запуск готового restore.ps1 из копии режима «контрольная точка».
    /// Скрипты, созданные программой до 1.13, не знают параметра -VmName:
    /// для них имя задаётся переименованием созданной машины.
    /// </summary>
    public const string RunRestoreScript = """
$script = '__SCRIPT__'
$dest = '__DEST__'
$start = '__START__'
$newName = '__NAME__'
if (-not (Test-Path -LiteralPath $script)) { throw ("Не найден скрипт восстановления: " + $script) }
if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

# Имя, под которым скрипт создаёт машину: берём из vm-info.json рядом со скриптом.
$origName = ''
$info = Join-Path (Split-Path -Parent $script) 'vm-info.json'
if (Test-Path -LiteralPath $info) {
    try { $origName = [string]((Get-Content -LiteralPath $info -Raw | ConvertFrom-Json).vmName) } catch { $origName = '' }
}

# Умеет ли скрипт принять имя при восстановлении?
$supportsName = $false
try { $supportsName = (Get-Command -Name $script -ErrorAction Stop).Parameters.ContainsKey('VmName') } catch { $supportsName = $false }

$wanted = ''
if ($supportsName) {
    $wanted = $newName
} elseif ($origName) {
    $wanted = $origName
}
if ($wanted -and (Get-VM -Name $wanted -ErrorAction SilentlyContinue)) {
    throw ("Виртуальная машина «" + $wanted + "» уже существует — выберите другое имя или удалите прежнюю машину.")
}

$before = @(Get-VM | ForEach-Object { [string]$_.Id })
if ($supportsName) {
    if ($start -eq 'true') { & $script -Destination $dest -VmName $newName -Start | Out-Null }
    else { & $script -Destination $dest -VmName $newName | Out-Null }
} else {
    if ($start -eq 'true') { & $script -Destination $dest -Start | Out-Null }
    else { & $script -Destination $dest | Out-Null }
}

$vm = @(Get-VM | Where-Object { $before -notcontains [string]$_.Id }) | Select-Object -First 1
if (-not $vm) { throw ("Скрипт отработал, но новая виртуальная машина не найдена. Папка: " + $dest) }
if ($newName -and $vm.Name -ne $newName) {
    try {
        Rename-VM -VM $vm -NewName $newName -ErrorAction Stop | Out-Null
        $vm = Get-VM -Id $vm.Id
    } catch {
        Write-Warning ("Не удалось переименовать машину в «" + $newName + "»: " + $_.Exception.Message)
    }
}
$__hv = @{ name = [string]$vm.Name; id = $vm.Id.ToString(); state = [string]$vm.State }
""";
}

/// <summary>Обёртка над командлетами Hyper-V. Все вызовы идут через Windows PowerShell 5.1.</summary>
public static class HyperV
{
    private static string Q(string s) => s.Replace("'", "''");

    private static string Fill(string template, string vmName) =>
        template.Replace("__VMNAME__", Q(vmName));

    // ─────────────────────────────── Список ВМ ───────────────────────────────
    public static (bool Ok, List<VmInfo> Vms, string Error) ListVms()
    {
        var (ok, list, err) = Ps.RunJson<List<VmInfo>>(PsScripts.ListVms, 120);
        return (ok, list ?? new List<VmInfo>(), err);
    }

    // ─────────────────────── Цепочка дисков виртуальной машины ───────────────────────
    public static (bool Ok, List<DiskInfo> Disks, string Error) GetDiskChain(string vmName)
    {
        var body = Fill(PsScripts.GetDiskChain, vmName);
        var (ok, list, err) = Ps.RunJson<List<DiskInfo>>(body, 180);
        return (ok, list ?? new List<DiskInfo>(), err);
    }

    // ─────────────────────────── Контрольная точка ───────────────────────────
    public static (bool Ok, CheckpointInfo Info, string Error) CreateCheckpoint(string vmName, string snapshotName)
    {
        var body = Fill(PsScripts.CreateCheckpoint, vmName).Replace("__SNAPNAME__", Q(snapshotName));
        // Контрольная точка может сохранять состояние памяти — для больших ВМ это долго.
        var (ok, info, err) = Ps.RunJson<CheckpointInfo>(body, 3600);
        return (ok, info ?? new CheckpointInfo(), err);
    }

    public static (bool Ok, string Error) RemoveCheckpoint(string vmName, string snapshotName)
    {
        var body = Fill(PsScripts.RemoveCheckpoint, vmName).Replace("__SNAPNAME__", Q(snapshotName));
        var (ok, _, err) = Ps.RunJson<bool>(body, 7200);
        return (ok, err);
    }

    public static (bool Ok, string Type, string Error) GetCheckpointType(string vmName)
    {
        var body = Fill(PsScripts.GetCheckpointType, vmName);
        var (ok, type, err) = Ps.RunJson<string>(body, 60);
        return (ok, type ?? "", err);
    }

    public static (bool Ok, string NewType, string Error) SetCheckpointType(string vmName, string type)
    {
        var body = Fill(PsScripts.SetCheckpointType, vmName).Replace("__TYPE__", Q(type));
        var (ok, newType, err) = Ps.RunJson<string>(body, 120);
        return (ok, newType ?? type, err);
    }

    // ─────────────────────── Починка связей цепочки дисков ───────────────────────
    /// <summary>
    /// Перенастраивает связи скопированной цепочки: Set-VHD -ParentPath и, при необходимости,
    /// SetChildLocatorPath через CIM (в модуле Hyper-V нет параметра -ChildLocatorPath).
    /// </summary>
    public static (bool Ok, List<LocatorFix> Report, string Error) FixLocators(
        IReadOnlyList<(string Child, string Parent)> pairs,
        IReadOnlyList<string> tops)
    {
        var json = JsonSerializer.Serialize(new
        {
            links = pairs.Select(p => new { child = p.Child, parent = p.Parent }),
            tops
        });
        var body = PsScripts.FixLocators.Replace("__JSON__", Q(json));
        var (ok, report, err) = Ps.RunJson<List<LocatorFix>>(body, 300);
        return (ok, report ?? new List<LocatorFix>(), err);
    }

    // ──────────────────────────── Экспорт ВМ (быстрый режим) ────────────────────────────

    /// <summary>
    /// Горячий экспорт ВМ средствами Hyper-V. Работающая машина не останавливается:
    /// Hyper-V снимает согласованный Production-снимок (VSS) и выгружает конфигурацию с дисками.
    /// captureLiveState = false — обычный экспорт (для выключенной машины).
    /// </summary>
    public static (bool Ok, string Error) ExportVm(string vmName, string destination, bool captureLiveState)
    {
        var body = Fill(PsScripts.ExportVm, vmName)
            .Replace("__DEST__", Q(destination))
            .Replace("__LIVE__", captureLiveState ? "true" : "false");
        var (ok, _, err) = Ps.RunJson<bool>(body, 7200);
        return (ok, err);
    }

    /// <summary>Проверка прав и доступности Hyper-V.</summary>
    public static (bool Ok, string Error) CheckAccess()
    {
        var (ok, _, err) = Ps.RunJson<bool>(PsScripts.CheckAccess, 60);
        if (!ok && string.IsNullOrWhiteSpace(err)) err = "Нет доступа к Hyper-V.";
        return (ok, err);
    }

    // ─────────────────────────────── Восстановление ───────────────────────────────

    /// <summary>Пути хранения виртуальных машин и дисков на этом хосте.</summary>
    public static (bool Ok, string VmPath, string VhdPath, string Error) GetHostPaths()
    {
        var (ok, data, err) = Ps.RunJson<HostPaths>(PsScripts.GetHostPaths, 60);
        if (!ok || data == null) return (false, "", "", err);
        return (true, data.VmPath ?? "", data.VhdPath ?? "", "");
    }

    /// <summary>
    /// Импорт ВМ из экспорта Hyper-V. Копия файлов делается в указанные папки,
    /// идентификатор машины можно обновить (тогда исходная ВМ остаётся на месте).
    /// </summary>
    public static (bool Ok, string VmName, string VmId, string Error) ImportVm(
        string sourceFolder, string newName, string vhdPath, string vmPath, bool generateNewId, bool start)
    {
        var body = PsScripts.ImportVm
            .Replace("__SRC__", Q(sourceFolder))
            .Replace("__NAME__", Q(newName ?? ""))
            .Replace("__NEWID__", generateNewId ? "true" : "false")
            .Replace("__VHD__", Q(vhdPath ?? ""))
            .Replace("__VMPATH__", Q(vmPath ?? ""))
            .Replace("__START__", start ? "true" : "false");
        var (ok, data, err) = Ps.RunJson<ImportedVm>(body, 7200);
        if (!ok || data == null) return (false, "", "", err);
        return (true, data.Name ?? "", data.Id ?? "", "");
    }

    /// <summary>Запуск restore.ps1 из копии режима «контрольная точка».</summary>
    public static (bool Ok, string VmName, string VmId, string Error) RunRestoreScript(
        string scriptPath, string destination, string newName, bool start)
    {
        var body = PsScripts.RunRestoreScript
            .Replace("__SCRIPT__", Q(scriptPath))
            .Replace("__DEST__", Q(destination))
            .Replace("__NAME__", Q(newName ?? ""))
            .Replace("__START__", start ? "true" : "false");
        var (ok, data, err) = Ps.RunJson<ImportedVm>(body, 7200);
        if (!ok || data == null) return (false, "", "", err);
        return (true, data.Name ?? "", data.Id ?? "", "");
    }

    public sealed class HostPaths
    {
        public string VmPath { get; set; } = "";
        public string VhdPath { get; set; } = "";
        public string SnapshotPath { get; set; } = "";
    }

    public sealed class ImportedVm
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public string State { get; set; } = "";
    }
}
