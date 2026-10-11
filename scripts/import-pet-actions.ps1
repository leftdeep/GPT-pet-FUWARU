param(
    [string]$Config = 'animation/actions.json',
    [string]$Python,
    [switch]$Apply,
    [switch]$VerifyAll
)
$ErrorActionPreference = 'Stop'
if ($Apply -and [Security.Principal.WindowsIdentity]::GetCurrent().Name -match '\\CodexSandbox(Offline|Online)$') {
    throw '安装需要桌面用户会话：请在普通终端运行，或由 Codex 使用已授权的桌面会话执行。当前未切换素材或启动隔离桌宠。'
}
$taskRoot = Split-Path $PSScriptRoot -Parent
if (!$Python) {
    $taskBundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (Test-Path -LiteralPath $taskBundledPython) { $Python = $taskBundledPython }
    else {
        $taskCommand = Get-Command python.exe -ErrorAction SilentlyContinue
        if (!$taskCommand) { throw '需要 Python 3.11+，也可以使用 -Python 指定路径。' }
        $Python = $taskCommand.Source
    }
}
$env:PYTHONIOENCODING = 'utf-8'
$taskArgs = @((Join-Path $PSScriptRoot 'import_pet_actions.py'), '--config', $Config)
if ($VerifyAll) { $taskArgs += '--verify-all' }
& $Python @taskArgs
if ($LASTEXITCODE -ne 0) { throw '导入未完成。请查看 output/animation-import/last-run.json 和对应动作日志。当前桌宠素材未切换。' }
$taskReport = Get-Content -LiteralPath (Join-Path $taskRoot 'output/animation-import/last-run.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($taskReport.status -ne 'ok') { throw '素材验证未通过。' }
Write-Host ('预览：' + $taskReport.preview)
if (!$Apply) {
    Write-Host ('已生成素材包：' + $taskReport.outputSkin + '；加 -Apply 可安装到当前桌宠。')
    return
}
$taskSettingsFile = Join-Path $taskRoot 'local-data/settings.json'
if (!(Test-Path -LiteralPath $taskSettingsFile)) {
    # The existing launcher creates normal defaults, including position, if this is the first run.
    & (Join-Path $PSScriptRoot 'launch-dragon-girl.ps1')
}
$taskSettings = Get-Content -LiteralPath $taskSettingsFile -Raw -Encoding utf8 | ConvertFrom-Json
$taskInstallReceipt = Join-Path $taskRoot 'local-data/action-import-install.json'
$taskManifestHash = (Get-FileHash -LiteralPath (Join-Path $taskReport.outputSkin 'skin.json') -Algorithm SHA256).Hash
$taskPreviousInstall = $null
if (Test-Path -LiteralPath $taskInstallReceipt) {
    try { $taskPreviousInstall = Get-Content -LiteralPath $taskInstallReceipt -Raw -Encoding utf8 | ConvertFrom-Json }
    catch { $taskPreviousInstall = $null }
}
if ($taskSettings.SkinDirectory -eq $taskReport.outputSkin -and
    $taskPreviousInstall -and $taskPreviousInstall.ManifestSha256 -eq $taskManifestHash -and
    $taskPreviousInstall.SkinDirectory -eq $taskReport.outputSkin) {
    Write-Host '没有素材变化，无需重启桌宠。'
    return
}
& (Join-Path $PSScriptRoot 'launch-dragon-girl.ps1') -Quit
# Normal exit may save a newer position; read again before changing just the skin selection.
$taskOriginalSettings = [IO.File]::ReadAllBytes($taskSettingsFile)
$taskSettings = [Text.Encoding]::UTF8.GetString($taskOriginalSettings).TrimStart([char]0xFEFF) | ConvertFrom-Json
$taskSettings | Add-Member -NotePropertyName SkinDirectory -NotePropertyValue $taskReport.outputSkin -Force
$taskTemporarySettings = $taskSettingsFile + '.import-' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($taskTemporarySettings, ($taskSettings | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $taskTemporarySettings -Destination $taskSettingsFile -Force
    & (Join-Path $PSScriptRoot 'launch-dragon-girl.ps1')
    [IO.File]::WriteAllText($taskInstallReceipt,
        (@{SkinDirectory=$taskReport.outputSkin; ManifestSha256=$taskManifestHash} | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
} catch {
    [IO.File]::WriteAllBytes($taskSettingsFile, $taskOriginalSettings)
    & (Join-Path $PSScriptRoot 'launch-dragon-girl.ps1')
    throw
} finally {
    if (Test-Path -LiteralPath $taskTemporarySettings) { Remove-Item -LiteralPath $taskTemporarySettings }
}
Write-Host '已切换桌宠素材。位置、大小、行为开关和成长存档继续保留。'
