# 保留的脚本

当前默认转换标准为 **640×640、24 fps、VP9 Alpha、CRF 40**。批量配置与通用转换器一致；共同站立高度为 348.333 像素、脚底线 576。现有 9 个动作、13 个播放分段已从原 MOV 重制，详情见 [640 动作更新](../docs/动作640-CRF40更新-2026-10-10.md)。

- `launch-dragon-girl.ps1`：读取 `pet-release.json` 启动当前完整发布程序，使用 `assets/dragon-girl-0.40` 和 `local-data`；将旧内置素材路径迁移到 0.40，保留其他设置和成长存档；加 `-Quit` 正常退出；`-Previous` 启动上一发布，`-ResetPosition` 显式重置位置，普通启动保留位置。
- `ReleaseTools.ps1`：统一读取版本与发布指针，验证路径和 SHA-256，原子切换发布版本。
- `test-native.ps1`：构建独立原生测试宿主，并在 verification 下检查窗口和菜单。
- `convert-alpha-video.ps1`：选择或拖入透明 MOV，调用 Python 转换器；默认输出到 `assets/converted`。
- `import-alpha-video.py`：转换器命令行入口。
- `pet_video_converter.py`：检查透明通道，统一裁切、缩放、编码 VP9 Alpha 并生成或更新 skin.json。
- `pet_quality.py`：读取 `animation/quality-profiles.json` 的旧动作裁切、固定校色和锚点，按比例从原 MOV 重制；校验原视频及旧成品指纹，最后只做一次有损压缩。
- `import-pet-actions.ps1`：配置驱动的一键动作导入；`-Apply` 验证后安装，`-VerifyAll` 重新解码验证缓存。
- `import_pet_actions.py`：读取 `animation/actions.json`，复用审核 WebM、增量缓存、批量转换、生成预览与简短报告，最后原子提交素材清单。
- `pet_color.py`：固定 RGB 曲线与鲜艳色保护，生成 3D LUT；对审核 WebM 校色时无损编码并严格验证解码 Alpha 一致。
- `pet_anchor.py`：固定的头部与双脚仿射校准，保持纵向位移，检查完整特效边界与计划 Alpha。
- `pet_calibration.py`：新素材默认按待机拟合颜色、横向轮廓及头部/双脚锚点，保护真实纵向运动；不可靠测量需要绑定视频哈希的人工标注，生成完整对比。
- `pet_preview.py`：统一的颜色参考拟合、透明混合、视频解码和完整动作对比辅助函数；预览依赖 NumPy/Pillow。
- `preview-pet-actions.py`：读取正式配置和颜色参考，生成完整对比、锚点标记、首尾放大图和简短报告；不安装或修改设置。

转换器支持 `--preview-group Idle` / `ClickInteraction` / `Walking`，`--content-region` 限定角色检测区域；缩放使用预乘 Alpha，转换报告保留这些参数。`--start-frame` / `--end-frame` 按输出帧率从原片直接编码分段，完整原片仍用于共同构图。

固定流程入口为根目录 `导入桌宠动作.cmd`，说明见 [动作素材固定流程](../docs/动作素材固定流程.md)。常规导入不需要读取或重新修改这些脚本。

颜色与头部/双脚校准已统一为正式流程，说明见 [动作锚点校准](../docs/动作锚点校准.md)。双击根目录 `查看锚点对比.cmd` 查看完整对比，重建对比运行 `python scripts/preview-pet-actions.py`；安装仍使用 `import-pet-actions.ps1 -Apply`。

已确认成品保留 `reviewedFinal`。普通导入继续复用审核 WebM；本次已授权的分辨率升级通过冻结的 `qualityProfiles` 从 MOV 重制，保持原颜色和几何配方，不对旧 WebM 再叠加校正。原视频、布局、人工锚点或基准指纹改变时停止，要求独立候选复核。自动检查另含 `tests/test_pet_quality.py`。

历史人物抠图、骨骼走路、旧 PNG 序列制作和旧发布检查脚本已删除。基础转换器使用 `runtime/ffmpeg.exe` 和 Python 3.11+ 标准库；新素材校准使用已包含 NumPy/Pillow 的运行时。

一次性的批量重建、界面替换和站立截图测量脚本已清理。运行入口和通用转换器继续保留，自动检查源代码位于 `tests/`。
