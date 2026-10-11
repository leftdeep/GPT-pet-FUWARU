param([switch]$Quit, [switch]$ResetPosition, [switch]$Previous)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$taskRelease = Get-PetRelease $taskRoot
$taskSelectedRelease = $taskRelease.Current
if ($Previous -and !$Quit) {
    if (!$taskRelease.Previous) { throw '没有可用的上一发布版本。' }
    $taskSelectedRelease = $taskRelease.Previous
}
$taskExe = Join-Path (Resolve-PetReleasePath $taskRoot $taskSelectedRelease) 'GptSpritePet.exe' 
$taskData = Join-Path $taskRoot 'local-data'
$taskSkin = Join-Path $taskRoot 'assets\dragon-girl-0.40'
if (!(Test-Path -LiteralPath $taskExe)) { throw '找不到程序。请先运行 build.ps1 -Publish。' }
$taskArgs = @('--data-dir', ('"' + $taskData + '"'))
if ($Quit) {
    Start-Process -FilePath $taskExe -ArgumentList ($taskArgs + '--quit') -WindowStyle Hidden -Wait
    return
}
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
$taskSettingsFile = Join-Path $taskData 'settings.json'
if (!(Test-Path -LiteralPath $taskSettingsFile)) {
    Add-Type -AssemblyName System.Windows.Forms
    $taskArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $taskSettings = @{
        Size=300; X=($taskArea.Right - 340); Y=($taskArea.Bottom - 330)
        SkinDirectory=$taskSkin; AutoWalk=$true; Topmost=$true; ClickThrough=$false
        UsageEnabled=$true; UsageCardVisible=$true
    }
    $taskSettings | ConvertTo-Json | Set-Content -LiteralPath $taskSettingsFile -Encoding utf8
}
else {
    $taskSavedSettings = Get-Content -LiteralPath $taskSettingsFile -Raw -Encoding utf8 | ConvertFrom-Json
    $taskOldDefaults = @((Join-Path $taskRoot 'assets\dragon-girl'), (Join-Path $taskRoot 'assets\dragon-girl-0.31'), (Join-Path $taskRoot 'assets\dragon-girl-0.32'), (Join-Path $taskRoot 'assets\dragon-girl-0.36'))
    if ($taskSavedSettings.SkinDirectory -and
        [IO.Path]::GetFullPath($taskSavedSettings.SkinDirectory) -in $taskOldDefaults) {
        $taskSavedSettings.SkinDirectory = $taskSkin
        $taskSavedSettings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskSettingsFile -Encoding utf8
    }
}
if ($ResetPosition) { $taskArgs += '--reset-position' }
$taskPet = Start-Process -FilePath $taskExe -ArgumentList $taskArgs -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
$taskPet.Id | Set-Content -LiteralPath (Join-Path $taskData 'pet.pid')
