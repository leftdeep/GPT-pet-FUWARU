# 芙哇噜 · FUWARU

Windows 透明动画桌宠，基于 C#、WPF 与 .NET 10。包含拖动、走路、睡觉、贴边探头、成长记录、Codex 用量展示和重置公告监测。设置界面支持开机自启动。

## 构建与启动

普通用户请下载 [Releases 中的 Windows 便携版](https://github.com/leftdeep/GPT-pet-FUWARU/releases/latest) `FUWARU-*-windows-x64.zip`，解压后进入 `FUWARU` 文件夹，双击 `启动芙哇噜.cmd`。包内已含 .NET 运行环境、精简视频解码器和现用 9 个动作；首次启动也无需联网下载依赖。不要把 GitHub 的 **Code → Download ZIP** 或 **Source code (zip)** 当作运行程序，它们是需要自行编译的源码。

以下步骤用于开发者从源码构建：

需要 Windows 10/11 x64、.NET 10 SDK 和 .NET 10 Windows Desktop Runtime。在项目根目录使用 PowerShell 执行：

```powershell
./scripts/setup-runtime.ps1
./build.ps1 -Publish
```

依赖脚本会检查已有的 `runtime/ffmpeg.exe`；缺少时，从 [FFmpeg 官方下载页](https://ffmpeg.org/download.html)列出的 [gyan.dev Windows builds](https://www.gyan.dev/ffmpeg/builds/)下载 essentials ZIP，验证 SHA-256 后安装。FFmpeg 由用户在本机获取，仓库不附其二进制。

构建完成后双击 `启动芙哇噜.cmd`，首次启动默认使用 `assets/imported/dragon-girl-calibrated` 中现用全部 9 个动作：待机、走路、开心、害羞、生气、东张西望、困倦、睡觉、行礼。成品标准为 640×640、24 fps、VP9 Alpha、CRF 40；走路和睡觉分段后共 13 个视频。

`assets/dragon-girl-0.40` 是启动程序与测试仍引用的默认回退素材；`assets/edge-peek` 保存贴边图片。个人设置与存档会在本机生成。

## 检查

```powershell
# 构建、核心检查与发布流程检查
./build.ps1
# Windows 原生界面检查，使用独立测试存档
./build.ps1 -NativeChecks
```

可选的动作转换检查需要 Python 3.11+、NumPy 和 Pillow，例如：

```powershell
python ./tests/test_pet_quality.py
python ./tests/test_pet_action_import.py
```

更多测试说明见 [tests/README.md](tests/README.md)，动作工具说明见 [scripts/README.md](scripts/README.md)。

## 仓库内容

提交源码、测试、构建和转换脚本、现用成品素材及相关说明。排除所有 Git 历史元数据、animation 原始 MOV 和配置、DSH 程序、历史发布、SDK、缓存、个人设置、成长存档、账号用量及诊断日志。

原始素材与冻结质量基准未附，已有动作不能从此仓库直接重建转换过程；成品动画可以直接运行。保留的操作文档可能引用未上传的本机目录或历史验证报告。
