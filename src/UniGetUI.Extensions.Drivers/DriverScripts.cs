using System.Text;

namespace UniGetUI.Extensions.Drivers;

internal static class DriverScripts
{
    public const string OutputMarker = "UNIGETUI-DRIVERS:";

    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        function Emit($value) {
            $json = ConvertTo-Json -InputObject $value -Depth 6 -Compress
            $json = [regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
            [Console]::Out.WriteLine('UNIGETUI-DRIVERS:' + $json)
        }
        function EmitError($record) {
            $ex = $record.Exception
            while ($ex.InnerException -and -not ($ex -is [System.Runtime.InteropServices.COMException])) { $ex = $ex.InnerException }
            Emit ([ordered]@{ error = [string]$ex.Message; hResult = [int]$ex.HResult })
        }
        function DateText($value) {
            if ($value -is [datetime]) { return $value.ToString('yyyy-MM-dd') }
            return $null
        }
        function NewSession {
            $session = New-Object -ComObject Microsoft.Update.Session
            $session.ClientApplicationID = 'UniGetUI'
            return $session
        }
        $Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0"

        """;

    private const string ScanBody = """
        try {
            $session = NewSession
            $result = $session.CreateUpdateSearcher().Search($Criteria)
            $devices = @{}
            if ($result.Updates.Count -gt 0) { try {
                $signed = @{}
                foreach ($driver in @(Get-CimInstance -ClassName Win32_PnPSignedDriver)) {
                    if ($driver.DeviceID) { $signed[[string]$driver.DeviceID] = $driver }
                }
                foreach ($entity in @(Get-CimInstance -ClassName Win32_PnPEntity)) {
                    foreach ($hardwareId in @($entity.HardwareID) + @($entity.CompatibleID)) {
                        if (-not $hardwareId) { continue }
                        $key = ([string]$hardwareId).ToUpperInvariant()
                        if (-not $devices.ContainsKey($key)) {
                            $devices[$key] = @{ Entity = $entity; Driver = $signed[[string]$entity.DeviceID] }
                        }
                    }
                }
            } catch { } }
            $updates = @()
            foreach ($update in $result.Updates) {
                $hardwareId = [string]$update.DriverHardwareID
                $device = $null
                if ($hardwareId) { $device = $devices[$hardwareId.ToUpperInvariant()] }
                $deviceName = $null; $installedVersion = $null; $installedDate = $null
                if ($device) {
                    $deviceName = [string]$device.Entity.Name
                    if ($device.Driver) {
                        $installedVersion = [string]$device.Driver.DriverVersion
                        $installedDate = DateText $device.Driver.DriverDate
                    }
                }
                $updates += [ordered]@{
                    id = [string]$update.Identity.UpdateID
                    revision = [int]$update.Identity.RevisionNumber
                    title = [string]$update.Title
                    description = [string]$update.Description
                    driverClass = [string]$update.DriverClass
                    manufacturer = [string]$update.DriverManufacturer
                    model = [string]$update.DriverModel
                    provider = [string]$update.DriverProvider
                    hardwareId = $hardwareId
                    driverDate = DateText $update.DriverVerDate
                    size = [int64]$update.MaxDownloadSize
                    rebootBehavior = [int]$update.InstallationBehavior.RebootBehavior
                    supportUrl = [string]$update.SupportUrl
                    eulaAccepted = [bool]$update.EulaAccepted
                    deviceName = $deviceName
                    installedVersion = $installedVersion
                    installedDate = $installedDate
                }
            }
            Emit ([ordered]@{ updates = $updates })
        } catch {
            EmitError $_
        }
        """;

    private const string InstallBody = """
        try {
            $session = NewSession
            $result = $session.CreateUpdateSearcher().Search($Criteria)
            $results = @()
            $found = @{}
            $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($update in $result.Updates) {
                $id = [string]$update.Identity.UpdateID
                if ($Ids -notcontains $id) { continue }
                $found[$id] = $true
                if (-not $update.EulaAccepted) {
                    $results += [ordered]@{ id = $id; resultCode = 101; hResult = 0; rebootRequired = $false }
                    continue
                }
                [void]$toDownload.Add($update)
            }
            foreach ($id in $Ids) {
                if (-not $found.ContainsKey($id)) {
                    $results += [ordered]@{ id = $id; resultCode = 100; hResult = 0; rebootRequired = $false }
                }
            }
            $rebootRequired = $false
            if ($toDownload.Count -gt 0) {
                $downloader = $session.CreateUpdateDownloader()
                $downloader.Updates = $toDownload
                $download = $downloader.Download()
                $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
                for ($i = 0; $i -lt $toDownload.Count; $i++) {
                    $update = $toDownload.Item($i)
                    if ($update.IsDownloaded) {
                        [void]$toInstall.Add($update)
                    } else {
                        $results += [ordered]@{ id = [string]$update.Identity.UpdateID; resultCode = 102; hResult = [int]$download.GetUpdateResult($i).HResult; rebootRequired = $false }
                    }
                }
                $shared = New-Object -ComObject Microsoft.Update.UpdateColl
                $batches = @()
                for ($i = 0; $i -lt $toInstall.Count; $i++) {
                    $update = $toInstall.Item($i)
                    if ([int]$update.InstallationBehavior.Impact -eq 2) {
                        $alone = New-Object -ComObject Microsoft.Update.UpdateColl
                        [void]$alone.Add($update)
                        $batches += ,$alone
                    } else {
                        [void]$shared.Add($update)
                    }
                }
                if ($shared.Count -gt 0) { $batches = @(,$shared) + $batches }
                foreach ($batch in $batches) {
                    try {
                        $installer = $session.CreateUpdateInstaller()
                        $installer.Updates = $batch
                        $install = $installer.Install()
                        if ($install.RebootRequired) { $rebootRequired = $true }
                        for ($i = 0; $i -lt $batch.Count; $i++) {
                            $one = $install.GetUpdateResult($i)
                            $results += [ordered]@{ id = [string]$batch.Item($i).Identity.UpdateID; resultCode = [int]$one.ResultCode; hResult = [int]$one.HResult; rebootRequired = [bool]$one.RebootRequired }
                        }
                    } catch {
                        $failure = $_.Exception
                        while ($failure.InnerException -and -not ($failure -is [System.Runtime.InteropServices.COMException])) { $failure = $failure.InnerException }
                        for ($i = 0; $i -lt $batch.Count; $i++) {
                            $results += [ordered]@{ id = [string]$batch.Item($i).Identity.UpdateID; resultCode = 4; hResult = [int]$failure.HResult; rebootRequired = $false }
                        }
                    }
                }
            }
            Emit ([ordered]@{ results = $results; rebootRequired = $rebootRequired })
        } catch {
            EmitError $_
        }
        """;

    public static string Scan() => Prelude + ScanBody;

    public static string Install(IEnumerable<string> updateIds)
    {
        string[] ids = [.. updateIds.Select(id => Guid.Parse(id).ToString("D"))];
        if (ids.Length == 0) throw new ArgumentException("No driver update was given.", nameof(updateIds));
        string literal = string.Join(", ", ids.Select(id => $"'{id}'"));
        return Prelude + $"$Ids = @({literal})\n" + InstallBody;
    }

    public const string StandardInputLoader = "$script = [Console]::In.ReadToEnd(); & ([scriptblock]::Create($script))";

    public static string EncodedStandardInputLoader { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(StandardInputLoader));
}
