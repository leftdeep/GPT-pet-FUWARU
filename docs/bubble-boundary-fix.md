# 0.53 用量气泡边缘裁切修复

普通模式按角色可见区域限制位置，透明窗口可以伸出屏幕。0.52 的气泡只按工作区移动，没有同时限制在父窗口内，造成小角色、大气泡靠边时正文与边框裁切。

0.53 在 `ApplyView()` 中提前预留气泡左右偏移及下方空间。校准视频的脚底可能超出名义方形视口，额外按当前可见范围估计下方余量，并为首次解码预留空间。悬停期间不动态扩大原生窗口。

`PositionHeadBubbles()` 将物理像素工作区转为 DIP，与窗口客户区取交集，留出边框和阴影余量。普通模式顶部不足时改放角色脚下；显示期间同时固定头部和脚底锚点。调整设置以角色水平中心与脚底保留位置，隐藏后恢复也保留原生位置。

没有接入反馈中安装包装脚本专用的 `--restore` 参数；本项目保留已有第二实例 `show` 重置位置的行为，托盘恢复本身已修复位置保持。

## 回归

新增真实 WPF 边界检查：尺寸 160/190/360、倍率 1/1.5/2，普通模式四边、四角和中心，以及四种贴边模式的两个角端。使用模拟额度及可能换行的重置标题，通过 `PointToScreen()` 验证屏幕与窗口边界、角色不移动和卡片不遮挡角色；另检查倍率与尺寸调整、锚点固定及隐藏恢复。共 320 项，并入完整 smoke。

仅运行这组检查：

```powershell
# 先构建测试程序，再准备独立数据目录中的 settings.json，指定有效 SkinDirectory。
./tools/dotnet/dotnet.exe build ./tests/GptSpritePet.NativeTests/GptSpritePet.NativeTests.csproj -c Release
./tests/GptSpritePet.NativeTests/bin/Release/net10.0-windows/GptSpritePet.NativeTests.exe --data-dir "C:\FUWARU-Test\bubble" --bubble-boundary-test
```

等待 GUI 测试进程退出后读取 `bubble-boundary-test.json`。生产程序拒绝测试参数。完整检查使用 `./build.ps1 -NativeChecks`；若播放解码器为精简版，用 `FUWARU_TEST_ENCODER` 指定完整 FFmpeg 供核心测试生成视频。

本机真实工作区为 3840×2088 物理像素、150% DPI。首次复现中 315 项检查有 55 项失败（包括遮挡），后续完善了换行标题及位置恢复检查。外部反馈的 125% DPI 结果属于反馈者实测，未当作本项目本机验证。尚需实机覆盖 100%/125%/200% DPI、不同 DPI 多显示器、负坐标显示器与特殊任务栏布局；极小工作区小于卡片本身时不能保证完整显示。
