param([string]$OutputDirectory, [string]$DecoderDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$taskVersion = Get-PetVersion $taskRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'output/portable' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskName = 'FUWARU-' + $taskVersion + '-windows-x64'
$taskPackage = Join-Path $taskOutput $taskName
if (Test-Path -LiteralPath $taskPackage) { throw '目标便携文件夹已存在，请选择新的输出目录。' }
if (!$DecoderDirectory) { $DecoderDirectory = Join-Path $taskRoot 'output/slim-decoder' }
$taskDecoderDirectory = [IO.Path]::GetFullPath($DecoderDirectory)
$taskDecoder = Get-Item -LiteralPath (Join-Path $taskDecoderDirectory 'ffmpeg.exe')
if ($taskDecoder.Length -gt 20MB) { throw '视频解码器超过精简版大小限制。' }
if (!(Test-Path -LiteralPath (Join-Path $taskDecoderDirectory 'licenses') -PathType Container) -or
    !(Test-Path -LiteralPath (Join-Path $taskDecoderDirectory 'build-info.json'))) { throw '精简解码器缺少来源与许可资料。' }
$taskReleaseRelative = 'lib/' + $taskVersion
$taskRelease = Join-Path $taskPackage $taskReleaseRelative
New-Item -ItemType Directory -Path $taskRelease -Force | Out-Null
$taskDotnet = Join-Path $taskRoot 'tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
& $taskDotnet publish (Join-Path $taskRoot 'src/GptSpritePet/GptSpritePet.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -o $taskRelease --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows 便携程序发布失败。' }
$taskRuntimeConfig = Get-Content -LiteralPath (Join-Path $taskRelease 'GptSpritePet.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($taskRuntimeConfig.runtimeOptions.framework -or $taskRuntimeConfig.runtimeOptions.frameworks) { throw '便携程序仍依赖系统 .NET，已停止打包。' }
foreach ($taskRequired in @('GptSpritePet.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskRelease $taskRequired))) { throw ('便携程序缺少文件：' + $taskRequired) }
}
$taskNugetPackages = $env:NUGET_PACKAGES
if (!$taskNugetPackages) {
    $taskNugetOutput = & $taskDotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw '无法定位运行时许可证。' }
    $taskNugetPackages = (($taskNugetOutput | Select-Object -First 1) -split ':',2)[1].Trim()
}
foreach ($taskFramework in $taskRuntimeConfig.runtimeOptions.includedFrameworks) {
    $taskRuntimePackage = $taskFramework.name.ToLowerInvariant() + '.runtime.win-x64'
    $taskRuntimePackageRoot = Join-Path $taskNugetPackages ($taskRuntimePackage + '/' + $taskFramework.version)
    $taskLicenseFiles = @(Get-ChildItem -LiteralPath $taskRuntimePackageRoot -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$' })
    if (!$taskLicenseFiles.Count) { throw ('缺少运行时许可证：' + $taskRuntimePackage) }
    $taskLicenseDirectory = Join-Path $taskRelease ('licenses/' + $taskRuntimePackage)
    New-Item -ItemType Directory -Path $taskLicenseDirectory -Force | Out-Null
    foreach ($taskLicense in $taskLicenseFiles) { Copy-Item -LiteralPath $taskLicense.FullName -Destination $taskLicenseDirectory }
}
$taskHashes = @{}
Get-ChildItem -LiteralPath $taskRelease -File | ForEach-Object { $taskHashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
@{Version=$taskVersion; Files=$taskHashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRelease 'release.json') -Encoding UTF8
Set-PetRelease $taskPackage $taskReleaseRelative $taskVersion

function Copy-PetPackageFile([string]$Relative) {
    $taskSource = Join-Path $taskRoot $Relative
    $taskTarget = Join-Path $taskPackage $Relative
    New-Item -ItemType Directory -Path (Split-Path $taskTarget -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskSource -Destination $taskTarget
}
foreach ($taskFile in @('Directory.Build.props','启动芙哇噜.cmd','关闭芙哇噜.cmd','scripts/launch-dragon-girl.ps1','scripts/ReleaseTools.ps1')) { Copy-PetPackageFile $taskFile }
$taskPackageRuntime = Join-Path $taskPackage 'runtime'
New-Item -ItemType Directory -Path $taskPackageRuntime -Force | Out-Null
Copy-Item -LiteralPath $taskDecoder.FullName -Destination (Join-Path $taskPackageRuntime 'ffmpeg.exe')
Copy-Item -LiteralPath (Join-Path $taskDecoderDirectory 'licenses') -Destination $taskPackageRuntime -Recurse
Copy-Item -LiteralPath (Join-Path $taskDecoderDirectory 'build-info.json') -Destination $taskPackageRuntime
((Get-FileHash -LiteralPath (Join-Path $taskPackageRuntime 'ffmpeg.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) | Set-Content -LiteralPath (Join-Path $taskPackageRuntime 'ffmpeg.sha256') -Encoding ASCII
function Get-PetAssetReferences($Value) {
    if ($null -eq $Value) { return }
    if ($Value -is [string]) { return }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
        foreach ($taskItem in $Value) { Get-PetAssetReferences $taskItem }
    }
    else {
        foreach ($taskProperty in $Value.PSObject.Properties) {
            if ($taskProperty.Name -in @('Video','SpriteSheet') -and $taskProperty.Value -is [string]) { $taskProperty.Value }
            elseif ($taskProperty.Name -in @('Frames','SpriteSheets','BlinkFrames')) {
                foreach ($taskItem in $taskProperty.Value) {
                    if ($taskItem -is [string]) { $taskItem } else { Get-PetAssetReferences $taskItem }
                }
            }
            else { Get-PetAssetReferences $taskProperty.Value }
        }
    }
}
foreach ($taskSkin in @('assets/dragon-girl-0.40','assets/imported/dragon-girl-calibrated')) {
    Copy-PetPackageFile ($taskSkin + '/skin.json')
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot ($taskSkin + '/skin.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($taskAsset in (Get-PetAssetReferences $taskManifest | Sort-Object -Unique)) {
        $taskSkinBoundary = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskSkin)) + [IO.Path]::DirectorySeparatorChar
        $taskAssetPath = [IO.Path]::GetFullPath((Join-Path $taskSkinBoundary $taskAsset))
        if (!$taskAssetPath.StartsWith($taskSkinBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw '动作素材路径超出素材目录。' }
        Copy-PetPackageFile ($taskSkin + '/' + $taskAsset)
    }
}
Copy-PetPackageFile 'assets/edge-peek/peek.json'
$taskPeek = Get-Content -LiteralPath (Join-Path $taskRoot 'assets/edge-peek/peek.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($taskAsset in (@($taskPeek.Atlas) + @($taskPeek.Frames.PSObject.Properties.Value) | Sort-Object -Unique)) {
    if ($taskAsset) { Copy-PetPackageFile ('assets/edge-peek/' + $taskAsset) }
}
@'
芙哇噜 Windows x64 便携版

1. 将整个 ZIP 解压到你拥有写入权限的普通文件夹，会得到 FUWARU 便携版文件夹。
2. 进入该文件夹，双击“启动芙哇噜.cmd”，不要在 ZIP 内运行或只复制 EXE。
3. 包内已包含 .NET 运行环境和精简视频解码器，首次启动也无需联网下载依赖。
   无需安装 SDK 或手动编译。
4. 设置入口在人物右键或托盘菜单中，可开启开机自启动。
5. 默认包含现用全部 9 个动作。个人设置和成长记录在此文件夹 local-data 内生成。
6. 升级时先退出旧版；请保留自己的 local-data，人物素材路径若失效可在设置中重新选择。
7. 使用 Codex 用量展示需要在自己的电脑上登录 Codex；运行桌宠本身不要求 Codex 账号。

项目与更新：https://github.com/leftdeep/GPT-pet-FUWARU/releases/latest
精简解码器用于播放透明 WebM；制作或转换新动作请使用源码项目与完整 FFmpeg。
精简解码器的来源与许可在 runtime 中，对应源码与构建流程随 GitHub Release 单独提供。
.NET 许可和第三方说明保留在 lib 中。
'@ | Set-Content -LiteralPath (Join-Path $taskPackage '使用说明.txt') -Encoding UTF8
$taskZip = Join-Path $taskOutput ($taskName + '.zip')
if (Test-Path -LiteralPath $taskZip) { throw '目标 ZIP 已存在，请选择新的输出目录，避免覆盖。' }
Compress-Archive -LiteralPath $taskPackage -DestinationPath $taskZip -CompressionLevel Optimal
$taskZipHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
($taskZipHash + '  ' + [IO.Path]::GetFileName($taskZip)) | Set-Content -LiteralPath ($taskZip + '.sha256') -Encoding ASCII
@{Version=$taskVersion; PackageDirectory=$taskPackage; Zip=$taskZip; SHA256=$taskZipHash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'package-result.json') -Encoding UTF8
Write-Host ('Windows 便携包已生成：' + $taskZip)
