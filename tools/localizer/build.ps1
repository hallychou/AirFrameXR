$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$out = Join-Path $root 'out\localizer'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$candidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
    "$fw\csc.exe"
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'Could not find Roslyn csc.exe or the .NET Framework compiler.' }
if (-not (Test-Path "$fw\System.Windows.Forms.dll") -or -not (Test-Path "$fw\System.Web.Extensions.dll")) {
    throw '.NET Framework 4.x WinForms and System.Web.Extensions assemblies were not found.'
}

New-Item -ItemType Directory -Force $out | Out-Null
& $csc /nologo /noconfig /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 `
    "/r:$fw\System.dll" "/r:$fw\System.Core.dll" "/r:$fw\System.Windows.Forms.dll" "/r:$fw\System.Web.Extensions.dll" `
    "/out:$out\ChineseLauncher.exe" "$PSScriptRoot\ChineseLauncher.cs"
if ($LASTEXITCODE -ne 0) { throw "ChineseLauncher.exe compilation failed with exit code $LASTEXITCODE." }
Copy-Item "$PSScriptRoot\zh-CN.json" $out -Force
Write-Host "Built: $out\ChineseLauncher.exe"
