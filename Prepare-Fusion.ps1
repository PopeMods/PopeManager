param([Parameter(Mandatory=$true)][string]$HostApk)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$hostPath = (Resolve-Path -LiteralPath $HostApk).Path
$loaderFolder = Join-Path $PSScriptRoot 'loader'
$libFolder = Join-Path $PSScriptRoot 'Tools\FusionGenerator\lib'
New-Item -ItemType Directory -Path $loaderFolder,$libFolder -Force | Out-Null
$apk = [IO.Compression.ZipFile]::OpenRead($hostPath)
try {
    $entry = $apk.GetEntry('assets/BepInEx-arm64.zip')
    if ($null -eq $entry) { throw 'This APK has no FusionCore BepInEx ARM64 payload.' }
    $buffer = [IO.MemoryStream]::new()
    $source = $entry.Open()
    try { $source.CopyTo($buffer) } finally { $source.Dispose() }
    $buffer.Position = 0
    $managed = [IO.Compression.ZipArchive]::new($buffer)
    try {
        foreach ($dll in $managed.Entries) {
            if ($dll.FullName -cmatch '^core/[^/]+\.dll$') {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($dll, (Join-Path $libFolder $dll.Name), $true)
            }
        }
    } finally { $managed.Dispose(); $buffer.Dispose() }
} finally { $apk.Dispose() }
$destinationHost = Join-Path $loaderFolder 'FusionCore-Quest.apk'
if (-not [IO.Path]::Equals($hostPath, [IO.Path]::GetFullPath($destinationHost))) {
    Copy-Item -LiteralPath $hostPath -Destination $destinationHost -Force
}
dotnet publish (Join-Path $PSScriptRoot 'Tools\FusionGenerator\FusionGenerator.csproj') -c Release -o (Join-Path $PSScriptRoot 'tools\fusion-generator')
if ($LASTEXITCODE -ne 0) { throw 'Fusion generator build failed.' }
Get-ChildItem -LiteralPath $libFolder -Filter '*.dll' | Copy-Item -Destination (Join-Path $PSScriptRoot 'tools\fusion-generator') -Force
dotnet (Join-Path $PSScriptRoot 'tools\fusion-generator\FusionGenerator.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Fusion generator smoke check failed.' }
$receipt = @{
    schemaVersion = 1
    hostSha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash
    description = 'PC generator references extracted from this exact FusionCore host APK.'
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'tools\fusion-generator\host-receipt.json') -Encoding UTF8
