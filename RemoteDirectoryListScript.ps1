$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RootPath)) {
    throw "RootPath is required"
}

$resolvedRoot = (Resolve-Path -LiteralPath $RootPath).Path.TrimEnd('\')
$rootLength = $resolvedRoot.Length

Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse |
    Where-Object { -not $_.Extension.Equals('.log', [System.StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
    $relativePath = $_.FullName.Substring($rootLength).TrimStart('\').Replace('\', '/')
    $lastWriteTime = $_.LastWriteTime.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    "$relativePath`t$($_.FullName)`t$lastWriteTime`t$hash"
}
