param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
$windowsTargetFramework = 'net10.0-windows10.0.19041.0'
dotnet build (Join-Path $root 'MiRemoteControl.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = Join-Path $root 'artifacts\app'
$null = New-Item -ItemType Directory -Force -Path $output
foreach ($project in @('MiRemoteControl.Host', 'MiRemoteControl.Cli', 'MiRemoteControl.Desktop')) {
    $targetFramework = if ($project -eq 'MiRemoteControl.Host') { $windowsTargetFramework } else { 'net10.0' }
    Copy-Item -Path (Join-Path $root "src\$project\bin\$Configuration\$targetFramework\*") -Destination $output -Recurse -Force
}

# Remove files that belonged to the retired WPF/CheemsUI desktop. Keep this
# list exact so a user-installed file in artifacts/app is never swept broadly.
foreach ($staleFile in @('CheemsUI.dll', 'CheemsUI.pdb')) {
    $stalePath = Join-Path $output $staleFile
    if (Test-Path -LiteralPath $stalePath) { Remove-Item -LiteralPath $stalePath -Force }
}

# NuGet's sherpa-onnx meta package used to copy native libraries for every
# supported platform. The application now references only win-x64; remove any
# stale copies left by an older build without touching Whisper's runtimes.
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $output 'runtimes'))
foreach ($runtime in @('android-arm64', 'android-x64', 'linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64')) {
    $obsoleteRuntime = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $runtime))
    if (-not $obsoleteRuntime.StartsWith($runtimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid obsolete runtime path'
    }
    if (Test-Path -LiteralPath $obsoleteRuntime) { Remove-Item -LiteralPath $obsoleteRuntime -Recurse -Force }
}
foreach ($relativePath in @(
    'win-arm64\native\onnxruntime.dll',
    'win-arm64\native\sherpa-onnx-c-api.dll',
    'win-x86\native\onnxruntime.dll',
    'win-x86\native\sherpa-onnx-c-api.dll'
)) {
    $obsoleteFile = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relativePath))
    if (-not $obsoleteFile.StartsWith($runtimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid obsolete runtime file path'
    }
    if (Test-Path -LiteralPath $obsoleteFile) { Remove-Item -LiteralPath $obsoleteFile -Force }
}
$pluginsRoot = [IO.Path]::GetFullPath((Join-Path $output 'plugins'))
$null = New-Item -ItemType Directory -Force -Path $pluginsRoot
$remotePluginsRoot = [IO.Path]::GetFullPath((Join-Path $pluginsRoot 'remotes'))
$targetPluginsRoot = [IO.Path]::GetFullPath((Join-Path $pluginsRoot 'targets'))
$null = New-Item -ItemType Directory -Force -Path $remotePluginsRoot
$null = New-Item -ItemType Directory -Force -Path $targetPluginsRoot
$pluginPackages = Get-ChildItem -LiteralPath (Join-Path $root 'plugins') -Directory | ForEach-Object {
    $manifestPath = Join-Path $_.FullName 'plugin.json'
    $projectPath = Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File | Select-Object -First 1
    if (-not (Test-Path -LiteralPath $manifestPath) -or $null -eq $projectPath) { return }
    # plugin.json is UTF-8 (it carries localized names); read it explicitly so
    # Windows PowerShell does not decode it with the ANSI code page.
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.id -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid plugin id in $manifestPath" }
    $destination = switch ($manifest.kind) {
        'remote' { $remotePluginsRoot }
        'target' { $targetPluginsRoot }
        default { throw "Invalid plugin kind in $manifestPath" }
    }
    @{
        ProjectDirectory = $_.FullName
        ProjectName = $projectPath.BaseName
        Id = $manifest.id
        Directory = $destination
        File = "$($manifest.id).mrcplugin"
    }
}
foreach ($package in $pluginPackages) {
    foreach ($legacyPath in @(
        (Join-Path $pluginsRoot $package.Id),
        (Join-Path $pluginsRoot $package.File)
    )) {
        $resolvedLegacyPath = [IO.Path]::GetFullPath($legacyPath)
        if (-not $resolvedLegacyPath.StartsWith($pluginsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Invalid legacy plugin path'
        }
        if (Test-Path -LiteralPath $resolvedLegacyPath) {
            Remove-Item -LiteralPath $resolvedLegacyPath -Recurse -Force
        }
    }
    $pluginBundle = Join-Path $package.Directory $package.File
    if (Test-Path -LiteralPath $pluginBundle) { Remove-Item -LiteralPath $pluginBundle -Force }
    $pluginBuildOutput = Join-Path $package.ProjectDirectory "bin\$Configuration\$windowsTargetFramework"
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $pluginBuildOutput,
        $pluginBundle,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Output "Plugin: $pluginBundle"
}
Write-Output "Ready: $output\MiRemoteControl.exe"
