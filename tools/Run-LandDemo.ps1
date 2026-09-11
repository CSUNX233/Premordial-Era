param(
    [string]$GodotPath = (Join-Path $env:USERPROFILE 'Desktop\Godot-DotNet\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe')
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) {
    throw 'Godot .NET executable not found. Supply its path with -GodotPath.'
}

Push-Location -LiteralPath $projectRoot
try {
    dotnet build NativeEpoch.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $GodotPath --path $projectRoot -- --stage3-land-demo
    if ($LASTEXITCODE -ne 0) { throw "Godot exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
