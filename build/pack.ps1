# Builds the Eaglin.AiManager packages into the local NuGet feed (C:\nuget-local) and registers
# the feed once. Consumers reference the package by version. Run after any library change and
# bump <AiManagerVersion> in Directory.Build.props for any public API change.
param(
    [string] $Feed = "C:\nuget-local",
    [string] $Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$props = [xml](Get-Content (Join-Path $repo "Directory.Build.props"))
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.AiManagerVersion } | Where-Object { $_ }) | Select-Object -First 1
Write-Host "AiManager version $version -> $Feed"

New-Item -ItemType Directory -Force $Feed | Out-Null

$projects = @("src\Eaglin.AiManager\Eaglin.AiManager.csproj")
foreach ($rel in @("src\Eaglin.AiManager.Wpf\Eaglin.AiManager.Wpf.csproj", "src\Eaglin.AiManager.WinForms\Eaglin.AiManager.WinForms.csproj")) {
    if (Test-Path (Join-Path $repo $rel)) { $projects += $rel }
}
foreach ($rel in $projects) {
    dotnet pack (Join-Path $repo $rel) -c $Configuration -o $Feed
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $rel" }
}

$sources = dotnet nuget list source
if ($sources -notmatch [regex]::Escape($Feed)) {
    dotnet nuget add source $Feed --name eaglin-local
    Write-Host "Registered NuGet source 'eaglin-local' -> $Feed"
}
Write-Host "Done. Consumers: <PackageReference Include=""Eaglin.AiManager"" Version=""$version"" />"
