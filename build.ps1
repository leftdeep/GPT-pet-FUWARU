param([switch]$Publish, [switch]$NativeChecks)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
. (Join-Path $taskRoot 'scripts/ReleaseTools.ps1')
$taskVersion = Get-PetVersion $taskRoot
$taskDotnet = Join-Path $taskRoot 'tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot 'tools/cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot 'tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskProject = Join-Path $taskRoot 'src/GptSpritePet/GptSpritePet.csproj'
& $taskDotnet build $taskProject -c Release --nologo --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '龙娘桌宠编译失败。' }
$taskTests = Join-Path $taskRoot 'tests/GptSpritePet.Tests/GptSpritePet.Tests.csproj'
& $taskDotnet restore $taskTests --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '测试依赖还原失败。' }
& $taskDotnet run --project $taskTests -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw '核心测试失败。' }
& (Join-Path $taskRoot 'tests/release-tools.test.ps1')
if ($NativeChecks) { & (Join-Path $taskRoot 'scripts/test-native.ps1') }
if ($Publish) {
    $taskLib = Join-Path $taskRoot 'lib'
    New-Item -ItemType Directory -Path $taskLib -Force | Out-Null
    $taskId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $taskStage = Join-Path $taskLib ('.staging-' + $taskId)
    $taskRelative = 'lib/' + $taskVersion + '-' + $taskId
    $taskFinal = Resolve-PetReleasePath $taskRoot $taskRelative
    try {
        & $taskDotnet publish $taskProject -c Release --self-contained false -o $taskStage --nologo --no-restore --no-build
        if ($LASTEXITCODE -ne 0) { throw '龙娘桌宠发布失败。' }
        $taskAssemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskStage 'GptSpritePet.dll')).FileVersion
        if ($taskAssemblyVersion -ne ($taskVersion + '.0.0')) { throw '发布文件版本与统一版本号不一致。' }
        $taskHashes = @{}
        Get-ChildItem -LiteralPath $taskStage -File | ForEach-Object {
            $taskHashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
        @{Version=$taskVersion; Files=$taskHashes} | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $taskStage 'release.json') -Encoding UTF8
        Move-Item -LiteralPath $taskStage -Destination $taskFinal
        Set-PetRelease $taskRoot $taskRelative $taskVersion
        Write-Host "发布完成：$taskRelative（下一次启动生效；运行中的版本可正常退出）"
    }
    finally {
        $taskStageResolved = [IO.Path]::GetFullPath($taskStage)
        if (!$taskStageResolved.StartsWith(([IO.Path]::GetFullPath($taskLib) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            throw '临时发布目录超出 lib，已停止清理。'
        }
        if (Test-Path -LiteralPath $taskStageResolved) { Remove-Item -LiteralPath $taskStageResolved -Recurse -Force }
    }
}
