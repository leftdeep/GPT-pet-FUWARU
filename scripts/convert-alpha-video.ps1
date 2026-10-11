param(
    [string]$Source,
    [string]$Output,
    [ValidateSet('Idle','Walking','LyingDown','Sleeping','Waking','Happy','Thinking','Talking','Surprised','Dragging')]
    [string]$State = 'Idle',
    [ValidateRange(0,63)][int]$Crf = 40,
    [switch]$Once
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $Source) {
    Add-Type -AssemblyName System.Windows.Forms
    $taskDialog = [System.Windows.Forms.OpenFileDialog]::new()
    $taskDialog.Title = '选择 Premiere 导出的透明 MOV'
    $taskDialog.Filter = '透明视频 (*.mov;*.webm)|*.mov;*.webm|所有文件 (*.*)|*.*'
    try {
        if ($taskDialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
        $Source = $taskDialog.FileName
    } finally { $taskDialog.Dispose() }
}
$taskInput = Get-Item -LiteralPath $Source
if ($taskInput.PSIsContainer) { throw '请选择视频文件。' }
if (-not $Output) {
    $taskOutputName = $taskInput.BaseName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,4)
    $Output = Join-Path $taskRoot ('assets\converted\' + $taskOutputName)
}
$taskBundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (Test-Path -LiteralPath $taskBundledPython) { $taskPython = $taskBundledPython }
else {
    $taskPythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $taskPythonCommand) { throw '未找到 Python。请安装 Python 3.11 或更高版本。' }
    $taskPython = $taskPythonCommand.Source
}
$env:PYTHONIOENCODING = 'utf-8'
$taskArgs = @((Join-Path $PSScriptRoot 'import-alpha-video.py'), $taskInput.FullName,
    '--output', $Output, '--name', $taskInput.BaseName, '--state', $State, '--crf', $Crf.ToString())
if ($Once) { $taskArgs += '--once' }
& $taskPython @taskArgs
if ($LASTEXITCODE -ne 0) { throw '转换失败。请查看以上错误信息。' }
Write-Host "转换完成。桌宠设置 → 选择素材文件夹 → $Output"
