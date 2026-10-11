param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRuntime = Join-Path $taskRoot 'runtime'
$taskDestination = Join-Path $taskRuntime 'ffmpeg.exe'

function Test-PetFfmpeg([string]$Path) {
    $taskVersionOutput = & $Path -hide_banner -version 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg 无法运行，请使用 Windows x64 构建。' }
    $taskDecoders = & $Path -hide_banner -decoders 2>&1
    if ($LASTEXITCODE -ne 0 -or ($taskDecoders -join "`n") -notmatch '\blibvpx-vp9\b') {
        throw 'FFmpeg 缺少透明 WebM 所需的 libvpx-vp9 解码器。'
    }
    $taskEncoders = & $Path -hide_banner -encoders 2>&1
    if ($LASTEXITCODE -ne 0 -or ($taskEncoders -join "`n") -notmatch '\blibvpx-vp9\b') {
        throw 'FFmpeg 缺少动作转换所需的 libvpx-vp9 编码器。'
    }
    Write-Host ($taskVersionOutput | Select-Object -First 1)
}

if (Test-Path -LiteralPath $taskDestination -PathType Leaf) {
    Test-PetFfmpeg $taskDestination
    Write-Host '运行依赖已就绪，无需下载。'
    return
}

# Windows build provider listed at https://ffmpeg.org/download.html
$taskDownloadUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$taskTempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskTempName = 'fuwaru-ffmpeg-' + [guid]::NewGuid().ToString('N')
$taskTemp = [IO.Path]::GetFullPath((Join-Path $taskTempParent $taskTempName))
$taskTempParentPrefix = $taskTempParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$taskTemp.StartsWith($taskTempParentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '运行依赖临时目录路径无效。'
}
New-Item -ItemType Directory -Path $taskTemp | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $taskArchive = Join-Path $taskTemp 'essentials.zip'
    Write-Host '正在从 gyan.dev 下载 FFmpeg Windows essentials ZIP…'
    Invoke-WebRequest -Uri $taskDownloadUrl -OutFile $taskArchive -UseBasicParsing
    $taskChecksumResponse = Invoke-WebRequest -Uri ($taskDownloadUrl + '.sha256') -UseBasicParsing
    $taskExpectedHash = [regex]::Match([string]$taskChecksumResponse.Content, '\b[a-fA-F0-9]{64}\b').Value
    if (!$taskExpectedHash -or (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskExpectedHash) {
        throw 'FFmpeg 下载校验失败，请稍后重试。'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskExtract = [IO.Path]::GetFullPath((Join-Path $taskTemp 'extracted'))
    $taskExtractPrefix = $taskExtract + [IO.Path]::DirectorySeparatorChar
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
    try {
        # Validate every archive path before extracting any content.
        foreach ($taskEntry in $taskZip.Entries) {
            $taskEntryName = $taskEntry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ([IO.Path]::IsPathRooted($taskEntryName) -or $taskEntryName.Contains(':')) {
                throw 'FFmpeg 压缩包包含不安全路径。'
            }
            $taskEntryPath = [IO.Path]::GetFullPath((Join-Path $taskExtract $taskEntryName))
            if (!$taskEntryPath.StartsWith($taskExtractPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'FFmpeg 压缩包路径超出临时解压目录。'
            }
        }
    }
    finally { $taskZip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($taskArchive, $taskExtract)
    $taskCandidates = @(Get-ChildItem -LiteralPath $taskExtract -Recurse -File -Filter 'ffmpeg.exe' |
        Where-Object { $_.Directory.Name -eq 'bin' })
    if ($taskCandidates.Count -ne 1) { throw 'FFmpeg 压缩包中未找到唯一的 bin/ffmpeg.exe。' }
    Test-PetFfmpeg $taskCandidates[0].FullName
    New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
    # Do not overwrite a dependency installed concurrently by another process.
    [IO.File]::Copy($taskCandidates[0].FullName, $taskDestination, $false)
    Write-Host '运行依赖已安装到 runtime/ffmpeg.exe。'
}
finally {
    $taskCleanupPath = [IO.Path]::GetFullPath($taskTemp)
    if (!$taskCleanupPath.StartsWith($taskTempParentPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($taskCleanupPath) -ne $taskTempName) {
        throw '临时目录范围校验失败，已停止清理。'
    }
    if (Test-Path -LiteralPath $taskCleanupPath) {
        Remove-Item -LiteralPath $taskCleanupPath -Recurse -Force
    }
}
