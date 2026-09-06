#$OutputEncoding = [console]::InputEncoding = [console]::OutputEncoding = [Text.UTF8Encoding]::UTF8
 
param(
    [string]$SourcePath,
    [string]$DestinationRoot
)

# 建立時間戳資料夾
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$DestinationPath = Join-Path $DestinationRoot $timestamp

Write-Host "來源: $SourcePath"
Write-Host "目的: $DestinationPath"

# 保留最新 7 個 yyyyMMddHHmmss 格式的資料夾
$regex = '^\d{14}$'
$backupFolders = Get-ChildItem -Path $DestinationRoot -Directory |
    Where-Object { $_.Name -match $regex } |
    Sort-Object Name -Descending

$foldersToDelete = $backupFolders | Select-Object -Skip 7
foreach ($folder in $foldersToDelete) {
    Write-Host "刪除過舊備份資料夾: $($folder.FullName)"
    Remove-Item -Path $folder.FullName -Recurse -Force
}

# 建立目的資料夾
if (!(Test-Path $DestinationPath)) {
    New-Item -ItemType Directory -Path $DestinationPath | Out-Null
}

# 遞迴複製排除 logs 資料夾
function Copy-Folder {
    param(
        [string]$src,
        [string]$dst
    )
    Get-ChildItem -Path $src -Force | ForEach-Object {
        if ($_.Name -eq 'logs' -and $_.PSIsContainer) {
            Write-Host "跳過資料夾: $($_.FullName)"
            return
        }
		if ($_.Name -eq 'LogFolder' -and $_.PSIsContainer) {
            Write-Host "跳過資料夾: $($_.FullName)"
            return
        }
		if ($_.Name -eq 'LogFiles' -and $_.PSIsContainer) {
            Write-Host "跳過資料夾: $($_.FullName)"
            return
        }
		if ($_.Name -eq 'CDN' -and $_.PSIsContainer) {
            Write-Host "跳過資料夾: $($_.FullName)"
            return
        }
        $targetPath = Join-Path $dst $_.Name
        if ($_.PSIsContainer) {
            if (!(Test-Path $targetPath)) {
                New-Item -ItemType Directory -Path $targetPath | Out-Null
            }
            Copy-Folder -src $_.FullName -dst $targetPath
        }
        else {
            Copy-Item $_.FullName -Destination $targetPath -Force
        }
    }
}

Copy-Folder -src $SourcePath -dst $DestinationPath

Write-Host "備份完成：$DestinationPath"
