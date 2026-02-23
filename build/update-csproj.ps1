param(
    [string]$CsprojPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --------------------------------------------------------
# Step 1: Detect dotnet-dump version
# (local manifest has higher priority than global)
# --------------------------------------------------------
function Get-DumpVersion([string[]]$lines) {
    $lines | Select-Object -Skip 2 |
        Where-Object { $_.Trim() -ne '' } |
        ForEach-Object { ($_ -split '\s+', 3)[1] } |
        Select-Object -First 1
}

$ver = Get-DumpVersion(dotnet tool list dotnet-dump 2>$null)
if (-not $ver) {
    $ver = Get-DumpVersion(dotnet tool list -g dotnet-dump 2>$null)
}
if (-not $ver) {
    Write-Error "dotnet-dump is not installed. Install it with: dotnet tool install -g dotnet-dump"
    exit 1
}
Write-Host "Detected dotnet-dump version: $ver"

# --------------------------------------------------------
# Step 2: Detect TFM from the .store directory
# --------------------------------------------------------
$storeTools = Join-Path $env:USERPROFILE ".dotnet\tools\.store\dotnet-dump\$ver\dotnet-dump\$ver\tools"
$tfm = Get-ChildItem $storeTools -Directory | Select-Object -First 1 -ExpandProperty Name
if (-not $tfm) {
    Write-Error "Could not determine TFM under: $storeTools"
    exit 1
}
Write-Host "Detected TFM: $tfm"

# --------------------------------------------------------
# Step 3: Build new DotnetDumpLibPath
# (uses MSBuild $(USERPROFILE) macro, not a literal path)
# --------------------------------------------------------
$newLibPath = '$(USERPROFILE)' + "\.dotnet\tools\.store\dotnet-dump\$ver\dotnet-dump\$ver\tools\$tfm\any"
Write-Host "New DotnetDumpLibPath: $newLibPath"

# --------------------------------------------------------
# Step 4: Patch the csproj using regex (preserves formatting)
# --------------------------------------------------------
if (-not (Test-Path $CsprojPath)) {
    Write-Error "csproj not found at: $CsprojPath"
    exit 1
}

$content = Get-Content $CsprojPath -Raw -Encoding UTF8

$pattern = '(?<=<DotnetDumpLibPath>)[^<]*(?=</DotnetDumpLibPath>)'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($content, $pattern)) {
    Write-Error "DotnetDumpLibPath element not found in csproj"
    exit 1
}

$newContent = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, $newLibPath)

if ($newContent -eq $content) {
    Write-Host "DotnetDumpLibPath is already up to date."
} else {
    [System.IO.File]::WriteAllText($CsprojPath, $newContent, [System.Text.UTF8Encoding]::new($false))
    Write-Host "csproj updated successfully."
}
Write-Host ""
Write-Host "Done."
Write-Host "  dotnet-dump : $ver"
Write-Host "  TFM         : $tfm"
Write-Host "  LibPath     : $newLibPath"
