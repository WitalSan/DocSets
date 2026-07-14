$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_HOME = Join-Path $repo "DocSets.Tests\obj\dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$packages = Join-Path $env:USERPROFILE ".nuget\packages"
if (Test-Path -LiteralPath $packages) {
    $env:NUGET_PACKAGES = $packages
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$installation = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $installation) { throw "Visual Studio with MSBuild was not found." }
$env:VSINSTALLDIR = $installation.TrimEnd('\') + '\'
$msbuild = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
$vstest = Join-Path $installation "Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"
$project = Join-Path $repo "DocSets.Tests\DocSets.Tests.csproj"
$assembly = Join-Path $repo "DocSets.Tests\bin\Debug\net472\DocSets.Tests.dll"

& $msbuild $project /t:Restore,Build /p:Configuration=Debug /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $vstest $assembly
exit $LASTEXITCODE