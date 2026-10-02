$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$required = @('SilicaCore.dll','Si_AdminMod.dll','UnityEngine.CoreModule.dll','Newtonsoft.Json.dll')
foreach ($name in $required) {
    $path = Join-Path $projectDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing $name. Put the file from the DEDICATED SERVER folder next to Build Logistics.bat and try again."
    }
    Write-Host "Found $name"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed or Windows cannot find it.'
}
$serverRoot = [IO.Path]::GetFullPath((Join-Path $projectDir '..\..'))
if (-not (Test-Path -LiteralPath (Join-Path $serverRoot 'MelonLoader\net35\0Harmony.dll') -PathType Leaf)) {
    throw 'Missing dedicated-server MelonLoader/net35/0Harmony.dll. Keep this project under Silica-1283/si_logistics.'
}
Write-Host 'Compiling for the dedicated server. Please wait...'
dotnet build (Join-Path $projectDir 'Si_Logistics.csproj') -c Release --ignore-failed-sources "-p:ServerRoot=$serverRoot"
if ($LASTEXITCODE -ne 0) {
    throw 'Build FAILED. Send me a screenshot showing the FIRST error near the top of this window.'
}
Write-Host "Build succeeded: $projectDir\bin\Release\netstandard2.1\Si_Logistics.dll"
Write-Host 'Do not install this DLL yet. It needs a controlled server test.'
