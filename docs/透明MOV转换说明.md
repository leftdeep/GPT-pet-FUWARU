# 透明 MOV 转换

双击根目录 `转换透明MOV.cmd` 选择视频，或把 MOV 拖到该文件上。工具保留源文件，默认生成独立素材包到 `assets/converted`；不会覆盖当前龙娘。转换后可在桌宠设置中选择输出目录。

日常向 `animation` 添加动作时，使用新的 [动作素材固定流程](动作素材固定流程.md)：登记 `animation/actions.json`，双击 `导入桌宠动作.cmd`，自动完成增量转换、验证和安装。下面的单视频入口仍保留用于独立素材制作。

使用 Python 3.11+ 标准库和 `runtime/ffmpeg.exe`。默认按干净站立参考帧的平均高度、水平像素中位数与脚底定位统一人物大小，整段只使用一次固定缩放和平移；全帧轮廓只负责安全范围检查。预乘 Alpha 的 Lanczos 缩放，输出 640×640、24 fps、VP9 Alpha、默认 CRF 40；配置驱动的龙娘批量导入使用同一标准。不进行绿幕抠图或重画角色。

0.32 的转换器在站立归一化时写入 `StandingHeightRatio`，即目标站立高度除以画布最大边长。程序用它将设置中的 160～360 转成实际站立高度，扩大透明窗口容纳特效。整库必须使用相同标定比例；导入时比例不一致，或与未标定旧素材混用，会拒绝写入，要求在新文件夹重建。`bounds` 输出不带标定，继续按旧画布方式显示。当前内置 WebM 沿用 0.31，不需要再次编码。

```powershell
python scripts/import-alpha-video.py "C:\素材\待机.mov" --output "assets/converted/my-pet" --state Idle --action-label 普通待机 --preview-group Idle
python scripts/import-alpha-video.py "C:\素材\张望.mov" --output "assets/converted/my-pet" --state IdleLook --action-label 待机张望 --preview-group Idle
python scripts/import-alpha-video.py "C:\素材\开心.mov" --output "assets/converted/my-pet" --state Happy --action-label 开心跳跃 --preview-group ClickInteraction --once
```

先导入 Idle，再添加其他动作。同一动作库使用一致构图；必要时通过 `--crop x,y,width,height` 或 `--keep-canvas` 统一裁切。`--once` 设置单次播放；`--crf` 越低，质量和文件大小越高。工具不会修补源视频的循环首尾。

输出包含 skin.json、WebM 和 conversion.json；后者记录源文件哈希、裁切、编码参数及透明验证。重复转换会保留旧 WebM，请确认新动作后按清单引用清理未使用文件。

`assets` 下的内置 `dragon-girl*` 素材包受保护，单视频转换器拒绝直接覆盖。批量流程保留 `assets/dragon-girl-0.40`，先复用其中审核 WebM，再在 `assets/imported/dragon-girl` 构建独立结果。旧的一次性重建脚本已清理，入口改为 `scripts/import-pet-actions.ps1`。

当前配置为既有源指定角色检测区域 `[280,0,720,720]`，排除角落标记。普通待机测量首尾各五帧；跳跃末尾残留爱心光点，明确使用开头五帧。全库使用 348.333 像素目标站立高度、576 像素脚底线。超界时停止，不自动单独缩小动作。张望沿用已审核 WebM；如需重建，应先在配置中补充实际存在的源文件。

通用转换器参数：`--normalization standing`（默认）；`--target-height` 指定全套共同站立高度；`--feet-y` 指定共同脚底线；`--reference-start 0` 用指定时刻开始的五帧代替首尾参考。未指定目标时，通用工具以画布高度的 60% 为目标，不会自动单独缩小超界的动作，会提示可用上限。首尾测量高度相差超过 15% 时拒绝自动测量，要求选择干净站立时刻。它不使用 AI 语义识别人物，参考帧必须没有道具和特效。

`--normalization bounds` 可使用 0.30 的全段内容缩放；不能保证不同动作中的人物同样大。源视频循环首尾姿态没有修改。

`--content-region x,y,width,height` 明确限定检测区域；应核对全片角色没有越出此区域，检测到角色触及区域左右边缘时拒绝转换。`--preview-group` 只控制菜单分组，单击自动触发的动作键仍为 Happy。循环动作预览一整轮；单次动作按全部帧播放。

`--start-frame` / `--end-frame` 可指定输出帧率下从零开始、末端不含的范围。分段仍使用完整原片分析轮廓与站姿，并从原片直接编码，避免二次有损压缩；支持 `--preview-group Walking`。

既有动作质量升级由 `animation/quality-profiles.json` 冻结旧裁切、固定校色和锚点，从原 MOV 等比例重制；不把旧 384 WebM 放大当作高清。详情见 [640 动作更新](动作640-CRF40更新-2026-10-10.md)。
