$ErrorActionPreference = 'Stop'

function Get-PetVersion([string]$Root) {
    [xml]$petProps = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
    $petVersion = [string]$petProps.Project.PropertyGroup.PetReleaseVersion
    if ($petVersion -notmatch '^\d+\.\d+$') { throw 'Directory.Build.props 中的版本号无效。' }
    return $petVersion
}

function Resolve-PetReleasePath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) { throw '发布路径无效。' }
    $petBoundary = [IO.Path]::GetFullPath((Join-Path $Root 'lib')) + [IO.Path]::DirectorySeparatorChar
    $petResolved = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (!$petResolved.StartsWith($petBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径超出 lib 目录。' }
    return $petResolved
}

function Get-PetRelease([string]$Root) {
    $petPointer = Join-Path $Root 'pet-release.json'
    if (Test-Path -LiteralPath $petPointer) {
        $petRelease = Get-Content -LiteralPath $petPointer -Raw -Encoding UTF8 | ConvertFrom-Json
        $petPath = Resolve-PetReleasePath $Root $petRelease.Current
        if (!(Test-Path -LiteralPath (Join-Path $petPath 'GptSpritePet.exe'))) { throw '当前发布程序缺失，请重新发布。' }
        return $petRelease
    }
    $petLegacy = Get-ChildItem -LiteralPath (Join-Path $Root 'lib') -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'GptSpritePet.exe')) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$petLegacy) { throw '找不到程序。请先运行 build.ps1 -Publish。' }
    return [pscustomobject]@{Version=$petLegacy.Name; Current=('lib/' + $petLegacy.Name); Previous=$null}
}

function Set-PetRelease([string]$Root, [string]$RelativePath, [string]$Version) {
    $petPath = Resolve-PetReleasePath $Root $RelativePath
    $petRequired = @('GptSpritePet.exe','GptSpritePet.dll','GptSpritePet.Core.dll','GptSpritePet.deps.json','GptSpritePet.runtimeconfig.json','release.json')
    foreach ($petFile in $petRequired) {
        $petInfo = Get-Item -LiteralPath (Join-Path $petPath $petFile) -ErrorAction Stop
        if ($petInfo.Length -eq 0) { throw ('发布文件为空：' + $petFile) }
    }
    $petMetadata = Get-Content -LiteralPath (Join-Path $petPath 'release.json') -Raw | ConvertFrom-Json
    if ($petMetadata.Version -ne $Version) { throw '发布版本与校验清单不一致。' }
    foreach ($petFile in $petRequired | Where-Object { $_ -ne 'release.json' }) {
        $petExpected = $petMetadata.Files.PSObject.Properties[$petFile].Value
        if (!$petExpected -or (Get-FileHash -LiteralPath (Join-Path $petPath $petFile) -Algorithm SHA256).Hash -ne $petExpected) {
            throw ('发布文件校验失败：' + $petFile)
        }
    }
    $petPointer = Join-Path $Root 'pet-release.json'
    $petPrevious = if (Test-Path -LiteralPath $petPointer) { (Get-PetRelease $Root).Current } else {
        try { (Get-PetRelease $Root).Current } catch { $null }
    }
    if ($petPrevious -eq $RelativePath) { return }
    $petNewPointer = $petPointer + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        @{Version=$Version; Current=$RelativePath; Previous=$petPrevious; PublishedAt=[DateTimeOffset]::Now.ToString('O')} |
            ConvertTo-Json | Set-Content -LiteralPath $petNewPointer -Encoding UTF8
        if (Test-Path -LiteralPath $petPointer) { [IO.File]::Replace($petNewPointer, $petPointer, [NullString]::Value) }
        else { [IO.File]::Move($petNewPointer, $petPointer) }
    }
    finally { if (Test-Path -LiteralPath $petNewPointer) { Remove-Item -LiteralPath $petNewPointer } }
}
