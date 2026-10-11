$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $taskRoot 'scripts/ReleaseTools.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('pet-release-tests-' + [guid]::NewGuid().ToString('N'))
$checks = 0
function Assert-Pet([bool]$Value, [string]$Message) {
    if (!$Value) { throw $Message }; $script:checks++
}
function New-TestRelease([string]$Name, [string]$Version) {
    $fixtureRelease = Join-Path $fixtureRoot ('lib/' + $Name)
    New-Item -ItemType Directory -Path $fixtureRelease -Force | Out-Null
    $fixtureHashes = @{}
    foreach ($fixtureName in @('GptSpritePet.exe','GptSpritePet.dll','GptSpritePet.Core.dll','GptSpritePet.deps.json','GptSpritePet.runtimeconfig.json')) {
        $fixtureFile = Join-Path $fixtureRelease $fixtureName
        Set-Content -LiteralPath $fixtureFile -Value ($Version + ':' + $fixtureName)
        $fixtureHashes[$fixtureName] = (Get-FileHash -LiteralPath $fixtureFile -Algorithm SHA256).Hash
    }
    @{Version=$Version; Files=$fixtureHashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixtureRelease 'release.json')
    return $fixtureRelease
}
try {
    $legacy = New-TestRelease '0.41' '0.41'
    Assert-Pet ((Get-PetRelease $fixtureRoot).Current -eq 'lib/0.41') 'Legacy release discovery failed.'
    $current = New-TestRelease '0.42-fixture' '0.42'
    Set-PetRelease $fixtureRoot 'lib/0.42-fixture' '0.42'
    $pointer = Join-Path $fixtureRoot 'pet-release.json'
    $before = Get-Content -LiteralPath $pointer -Raw
    Assert-Pet ((Get-PetRelease $fixtureRoot).Previous -eq 'lib/0.41') 'Rollback release not retained.'
    $broken = New-TestRelease 'broken' '0.42'
    Remove-Item -LiteralPath (Join-Path $broken 'GptSpritePet.Core.dll')
    $rejected = $false
    try { Set-PetRelease $fixtureRoot 'lib/broken' '0.42' } catch { $rejected = $true }
    Assert-Pet ($rejected -and (Get-Content -LiteralPath $pointer -Raw) -eq $before) 'An incomplete release changed the active pointer.'
    $corrupt = New-TestRelease 'corrupt' '0.42'
    Add-Content -LiteralPath (Join-Path $corrupt 'GptSpritePet.dll') -Value 'tampered'
    $rejected = $false
    try { Set-PetRelease $fixtureRoot 'lib/corrupt' '0.42' } catch { $rejected = $true }
    Assert-Pet ($rejected -and (Get-Content -LiteralPath $pointer -Raw) -eq $before) 'A checksum failure changed the active pointer.'
    foreach ($path in @('../outside','lib/../../outside','C:\outside')) {
        $rejected = $false; try { Resolve-PetReleasePath $fixtureRoot $path | Out-Null } catch { $rejected = $true }
        Assert-Pet $rejected 'An escaped release path was accepted.'
    }
    # An active runtime can pin its DLL while a different release is published.
    $fixturePinned = [IO.File]::Open((Join-Path $current 'GptSpritePet.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $next = New-TestRelease '0.43-fixture' '0.43'
        Set-PetRelease $fixtureRoot 'lib/0.43-fixture' '0.43'
        Assert-Pet ((Get-PetRelease $fixtureRoot).Current -eq 'lib/0.43-fixture') 'Publishing failed while the previous DLL was in use.'
        Assert-Pet ((Get-PetRelease $fixtureRoot).Previous -eq 'lib/0.42-fixture') 'Atomic switch lost the previous release.'
    }
    finally { $fixturePinned.Dispose() }
    Set-PetRelease $fixtureRoot 'lib/0.42-fixture' '0.42'
    Assert-Pet ((Get-PetRelease $fixtureRoot).Current -eq 'lib/0.42-fixture') 'Rollback switch failed.'
}
finally {
    $fixtureResolved = [IO.Path]::GetFullPath($fixtureRoot)
    $fixtureBoundary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$fixtureResolved.StartsWith($fixtureBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup outside temp.' }
    if (Test-Path -LiteralPath $fixtureResolved) { Remove-Item -LiteralPath $fixtureResolved -Recurse -Force }
}
Write-Host "发布流程检查通过：$checks 项。"
