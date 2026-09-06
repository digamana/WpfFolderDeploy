$ErrorActionPreference = 'Stop'
$drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
if ($null -eq $drive) {
    throw "Drive C: not found"
}

[math]::Round(($drive.FreeSpace / 1GB), 1).ToString([System.Globalization.CultureInfo]::InvariantCulture)
