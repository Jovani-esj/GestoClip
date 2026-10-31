param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch",
    [string]$SetVersion = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$verFile = Join-Path $root "version.json"
$meta = Get-Content $verFile -Raw | ConvertFrom-Json

if ($SetVersion) {
    $newVersion = $SetVersion
} else {
    $parts = $meta.version.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        "major" { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
        "minor" { $parts[1]++; $parts[2] = 0 }
        "patch" { $parts[2]++ }
    }
    $newVersion = "$($parts[0]).$($parts[1]).$($parts[2])"
}
$newCode = [int]$meta.versionCode + 1

$meta.version = $newVersion
$meta.versionCode = $newCode
$meta | ConvertTo-Json | Set-Content $verFile -Encoding UTF8

# 1. Actualizar Android build.gradle.kts
$gradleFile = Join-Path $root "gestoclip-android\app\build.gradle.kts"
$gradleText = Get-Content $gradleFile -Raw
$gradleText = $gradleText -replace 'versionCode\s*=\s*\d+', "versionCode = $newCode"
$gradleText = $gradleText -replace 'versionName\s*=\s*"[^"]+"', "versionName = `"$newVersion`""
Set-Content $gradleFile $gradleText -NoNewline -Encoding UTF8

# 2. Actualizar Windows GestoClip.App.csproj
$csprojFile = Join-Path $root "gestoclip-windows\GestoClip.App\GestoClip.App.csproj"
$csprojText = Get-Content $csprojFile -Raw
$csprojText = $csprojText -replace '<Version>[^<]+</Version>', "<Version>$newVersion</Version>"
$csprojText = $csprojText -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$newVersion.0</AssemblyVersion>"
$csprojText = $csprojText -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$newVersion.0</FileVersion>"
Set-Content $csprojFile $csprojText -NoNewline -Encoding UTF8

# 3. Actualizar textos de versión en SelfInstaller.cs y MainWindow.xaml
$installerFile = Join-Path $root "gestoclip-windows\GestoClip.App\Platform\SelfInstaller.cs"
$installerText = Get-Content $installerFile -Raw
$installerText = $installerText -replace 'Instalar GestoClip v[0-9]+\.[0-9]+(\.[0-9]+)?', "Instalar GestoClip v$newVersion"
$installerText = $installerText -replace 'SetValue\("DisplayVersion",\s*"[^"]+"\)', "SetValue(`"DisplayVersion`", `"$newVersion`")"
Set-Content $installerFile $installerText -NoNewline -Encoding UTF8

Write-Host "Versión actualizada a v$newVersion (Android versionCode = $newCode)" -ForegroundColor Green
