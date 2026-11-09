Push-Location $PSScriptRoot
if (!(Test-Path "./Bonsai.exe")) {
    $release = "https://github.com/bonsai-rx/bonsai/releases/latest/download/Bonsai.zip"
    $configPath = "./Bonsai.config"
    if (Test-Path $configPath) {
        [xml]$config = Get-Content $configPath
        $bootstrapper = $config.PackageConfiguration.Packages.Package.where{$_.id -eq 'Bonsai'}
        if ($bootstrapper) {
            $version = $bootstrapper.version
            $release = "https://github.com/bonsai-rx/bonsai/releases/download/$version/Bonsai.zip"
        }
    }
    Invoke-WebRequest $release -OutFile "temp.zip" -ErrorAction Stop
    Move-Item -Path "NuGet.config" "temp.config" -ErrorAction SilentlyContinue
    Expand-Archive "temp.zip" -DestinationPath "." -Force
    Move-Item -Path "temp.config" "NuGet.config" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "temp.zip"
}
# The workflow's layout (config\main.layout: the camera preview, and its exposure and gain) into
# the editor's per-machine file, as scripts\deploy.ps1 does at the first deploy. rig-setup runs
# this again when the layout changes, which the deploy never would (3 Nov 2026: a node added in
# front of Hardware moved its window index). A different one already here is kept beside it.
$layout = ".\Settings\src\main.layout"
$tracked = "..\config\main.layout"
if (Test-Path $tracked) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $layout) -Force | Out-Null
    if ((Test-Path $layout) -and ((Get-FileHash $layout).Hash -ne (Get-FileHash $tracked).Hash)) {
        Copy-Item $layout ".\Settings\src\main.saved.layout" -Force
    }
    Copy-Item $tracked $layout -Force
}
& .\Bonsai.exe --no-editor
$restoreExit = $LASTEXITCODE
Pop-Location
exit $restoreExit
