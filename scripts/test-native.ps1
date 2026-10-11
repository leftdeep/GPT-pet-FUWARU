param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$taskVersion = Get-PetVersion $taskRoot
$taskDotnet = Join-Path $taskRoot 'tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot 'tools/cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot 'tools/packages'
& $taskDotnet build (Join-Path $taskRoot 'tests/GptSpritePet.NativeTests/GptSpritePet.NativeTests.csproj') -c Release --nologo --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '原生测试程序编译失败。' }
$taskExe = Join-Path $taskRoot 'tests/GptSpritePet.NativeTests/bin/Release/net10.0-windows/GptSpritePet.NativeTests.exe'
foreach ($taskMode in @('smoke','menu')) {
    $taskData = Join-Path $taskRoot ("verification/$taskVersion/native-$taskMode")
    New-Item -ItemType Directory -Path $taskData -Force | Out-Null
    @{Size=240; X=600; Y=300; AutoWalk=$true; UsageEnabled=$false; UsageCardVisible=$true;
        ResetMonitoringEnabled=$false; SkinDirectory=(Join-Path $taskRoot 'assets/dragon-girl-0.40')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskData 'settings.json') -Encoding UTF8
    $taskResultName = if ($taskMode -eq 'smoke') { 'smoke-test.json' } else { 'menu-test.json' }
    $taskResultFile = Join-Path $taskData $taskResultName
    if (Test-Path -LiteralPath $taskResultFile) { Remove-Item -LiteralPath $taskResultFile }
    $taskFlag = if ($taskMode -eq 'smoke') { '--smoke-test' } else { '--menu-smoke-test' }
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--data-dir', ('"' + $taskData + '"'), $taskFlag) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    if (!$taskProcess.WaitForExit(90000)) { $taskProcess.Kill(); throw "原生 $taskMode 检查超时。" }
    if (!(Test-Path -LiteralPath $taskResultFile)) { throw "原生 $taskMode 检查未产生结果；请查看对应 error.log。" }
    $taskResult = Get-Content -LiteralPath $taskResultFile -Raw | ConvertFrom-Json
    $taskFailures = @($taskResult.checks.PSObject.Properties | Where-Object { !$_.Value } | ForEach-Object Name)
    if ($taskFailures.Count -gt 0 -or !$taskResult.passed -or $taskProcess.ExitCode -ne 0) { throw ("原生检查失败：" + ($taskFailures -join ', ')) }
    Write-Host ("原生 $taskMode 检查通过：" + @($taskResult.checks.PSObject.Properties).Count + ' 项。')
}
