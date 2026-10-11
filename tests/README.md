# 自动验证

在项目根目录运行 `build.ps1` 检查正式程序、Core 和发布流程；添加 `-NativeChecks` 检查原生窗口，添加 `-Publish` 发布校验过的新目录。

- `GptSpritePet.Tests`：跨平台核心逻辑、真实透明 WebM 解码、5,000 个历史日志的增量扫描、缓存维护等检查。在项目根目录运行，使用保留的历史素材验证兼容性。
- `GptSpritePet.NativeTests`：Windows WPF 测试宿主；链接当前运行源码，并仅在此项目内编译 UI 检查 partial。使用 `scripts/test-native.ps1`；结果写入 `verification/<版本>/native-*`，不使用主存档。
- `release-tools.test.ps1`：完整性、SHA-256、路径约束、被占用 DLL、原子切换及回退的临时目录检查；也兼容 Windows PowerShell 5.1。
- `test_pet_video_converter.py` / `test_standing_normalization.py`：视频转换器检查。
- `test_pet_action_import.py` / `test_pet_color.py` / `test_pet_anchor.py`：固定导入、校色、头部/双脚校准，以及已确认成品原字节安装、缓存处理模式区分和错误审核的原子拒绝。

正式程序不提供原生检查入口。测试宿主有 `--smoke-test` 与 `--menu-smoke-test`，必须使用独立 `--data-dir`；日常使用优先采用脚本中准备好的隔离目录。

- `test_pet_calibration.py`：新透明 MOV 自动校色与头部/双脚校准、完整预览、分段共同标定、跳跃及特效保留、参考/人工标注哈希、缓存和不可靠站姿的原子拒绝。
