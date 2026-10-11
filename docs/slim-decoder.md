# 芙哇噜播放解码器

便携版在 `runtime/ffmpeg.exe` 内置精简播放解码器，启动无需下载视频依赖。它只读取本地 WebM，使用 libvpx VP9 解码保留透明通道，经过 scale/format 输出 BGRA 原始帧。移除音频编解码器、网络协议、硬件加速、FFplay、FFprobe 和 VP9 编码器；FFmpeg 命令行程序自身要求的少量基础滤镜会自动保留。`rawvideo` 编码器是输出像素所必需的封装步骤，不负责压缩视频；`null` 输出和 `wrapped_avframe` 供解码诊断使用。

生产参数由 `src/GptSpritePet.Core/WebmFrameStore.cs` 决定：

```text
-nostdin -hide_banner -loglevel error -threads 1 -c:v libvpx-vp9
-i video.webm -map 0:v:0 -vf scale=640:640 -frames:v COUNT+1
-fps_mode passthrough -pix_fmt bgra -an -f rawvideo pipe:1
```

实际宽、高和帧数来自皮肤清单。`COUNT+1` 用于识别清单少报帧数，输出不能丢帧或重复帧。精简版不支持制作动作；开发转换仍使用 `scripts/setup-runtime.ps1` 安装的完整 FFmpeg。

## 构建

在 GitHub Actions Windows Runner 上，通过 `msys2/setup-msys2` 启用 `msystem: UCRT64`，安装：

```text
make diffutils perl curl tar gnupg
mingw-w64-ucrt-x86_64-gcc
mingw-w64-ucrt-x86_64-nasm
mingw-w64-ucrt-x86_64-pkgconf
mingw-w64-ucrt-x86_64-python
```

在 `shell: msys2 {0}` 步骤中执行：

```bash
bash scripts/build-slim-decoder.sh output/slim-decoder
```

固定使用 [FFmpeg 9.0.2 官方发布源码](https://ffmpeg.org/download.html)，以官方 PGP 签名及固定签名密钥验证；libvpx 使用已验证的 [v1.16.0 提交 `1024874c5919305883187e2953de8fcb4c3d7fa6`](https://chromium.googlesource.com/webm/libvpx/+/refs/tags/v1.16.0)，从上游官方 GitHub 镜像取得完整源码。使用 UCRT64 GCC 静态链接；PE 导入检查拒绝 MSYS、MinGW、libvpx 和 FFmpeg 的外部 DLL。仍依赖 Windows 系统 DLL，与普通 Windows 程序一致。

输出包含 `ffmpeg.exe`、`licenses/`、`build-info.json` 和 `corresponding-source.zip`。预计解码器为数 MiB，实际大小以构建记录为准。便携包复制前面三项；源码 ZIP 作为同一个 GitHub Release 的独立附件。源码 ZIP 含完整 FFmpeg/libvpx 源码压缩包、构建脚本、配置参数和生成配置、源码与二进制 SHA-256、工具/安装包版本及许可证。不能只保留上游源码链接。

下载并展开对应源码 ZIP 后，在 UCRT64 环境从其根目录运行以下命令，可以使用随包源码离线重建：

```bash
bash scripts/build-slim-decoder.sh output/slim-decoder .
```

编译器版本由 `configuration/tool-versions.txt` 记录；要做到字节级复现还需要使用相同的 MSYS2 包版本。该脚本不安装或升级本机工具。

## 验证与发布

发布前，把精简解码器用于 Core 和 Native 检查，检查所有当前动作的首帧透明度、完整帧数、缩放、切换和拖动。`WebmChecks` 的不透明视频夹具需要单独的完整编码器或固定测试夹具；精简播放版不应为了生成测试素材添加 VP9 编码器。动作转换 Python 测试仍使用完整开发工具。

此外，在脱离 MSYS2 DLL 搜索路径的普通 PowerShell 环境运行解码和桌宠启动测试。`-version` 成功不足以证明 VP9 Alpha 播放正常。发布包内的许可证应随二进制保留，完整对应源码 ZIP 应与二进制同时发布；FFmpeg 采用 LGPL-2.1-or-later，libvpx 采用 BSD-3-Clause，相关条款见输出的原始许可证。
