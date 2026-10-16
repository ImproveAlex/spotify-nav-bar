param(
    [switch]$Publish,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$appProject = Join-Path $projectRoot 'src\SpotifyBar\SpotifyBar.csproj'
$testProject = Join-Path $projectRoot 'tests\SpotifyBar.Tests\SpotifyBar.Tests.csproj'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'

function Invoke-Dotnet {
    & $sdk @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Invoke-Dotnet restore $appProject --configfile $nugetConfig '-p:NuGetAudit=false'
Invoke-Dotnet build $appProject --no-restore -c Release --nologo
if (-not $SkipTests) {
    Invoke-Dotnet restore $testProject --configfile $nugetConfig '-p:NuGetAudit=false'
    Invoke-Dotnet run --project $testProject -c Release --no-restore
}
if ($Publish) {
    $payloadDirectory = Join-Path $projectRoot "artifacts\payloads\$Runtime"
    $releaseDirectory = Join-Path $projectRoot "artifacts\$Runtime"
    $launcherBuild = Join-Path $projectRoot "artifacts\launcher\$Runtime"
    New-Item -ItemType Directory -Path $releaseDirectory, $launcherBuild -Force | Out-Null
    Invoke-Dotnet restore $appProject --configfile $nugetConfig -r $Runtime '-p:NuGetAudit=false' '-p:SelfContained=true' '-p:PublishSingleFile=true'
    Invoke-Dotnet publish $appProject -c Release -r $Runtime --self-contained true --no-restore `
        '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=embedded' `
        -o $payloadDirectory
    $payload = Join-Path $payloadDirectory 'SpotifyBar.Player.exe'
    $payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    $buildInfo = Join-Path $launcherBuild 'BuildInfo.g.cs'
    Set-Content -LiteralPath $buildInfo -Encoding UTF8 -Value ('namespace SpotifyBar { internal static class BuildInfo { public const string PayloadSha256 = "' + $payloadHash + '"; } }')
    $launcherCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $launcherCompiler)) { $launcherCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path -LiteralPath $launcherCompiler)) { throw 'The Windows .NET Framework compiler is needed to package the portable launcher.' }
    $launcher = Join-Path $releaseDirectory 'SpotifyBar.exe'
    & $launcherCompiler /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ "/out:$launcher" `
        "/resource:$payload,SpotifyBar.Player" "/win32manifest:$(Join-Path $projectRoot 'src\SpotifyBar\app.manifest')" `
        (Join-Path $projectRoot 'src\SpotifyBar.Launcher\Program.cs') `
        (Join-Path $projectRoot 'src\SpotifyBar.Launcher\PortablePaths.cs') $buildInfo
    if ($LASTEXITCODE -ne 0) { throw 'Portable launcher packaging failed.' }
    Write-Host "Portable executable: $launcher"
}
if ($SmokeTest) {
    if (Test-Path -LiteralPath $localSdk) { $env:DOTNET_ROOT = Split-Path $localSdk; $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT }
    $appExe = if ($Publish) { Join-Path $projectRoot "artifacts\$Runtime\SpotifyBar.exe" } else { Join-Path $projectRoot 'src\SpotifyBar\bin\Release\net10.0-windows\SpotifyBar.Player.exe' }
    if (-not $Publish) {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $projectRoot 'artifacts\portable-smoke\Documents\SpotifyBar\Runtime\Cache'
    }
    $reportPath = Join-Path $projectRoot 'artifacts\verification\smoke-test.json'
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
    $smokeApp = Start-Process -FilePath $appExe -ArgumentList '--smoke-test' -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    if (-not $smokeApp.WaitForExit(30000)) { Stop-Process -Id $smokeApp.Id; throw 'The UI smoke check timed out.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($smokeApp.ExitCode -ne 0 -or -not $report.allPassed) { throw 'UI smoke checks failed.' }
    Write-Host 'UI smoke checks passed. Screenshots and report: artifacts\verification'
}
