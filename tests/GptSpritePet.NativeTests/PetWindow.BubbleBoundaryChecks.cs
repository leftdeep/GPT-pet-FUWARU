using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunBubbleBoundaryTest()
    {
        var checks = new Dictionary<string, bool>();
        RunBubbleBoundaryChecks(checks);
        var passed = checks.Values.All(value => value);
        File.WriteAllText(Path.Combine(_store.DirectoryPath, "bubble-boundary-test.json"),
            JsonSerializer.Serialize(new { passed, dpiScale = DpiScale, workArea = CharacterWorkArea(), checks }, SettingsStore.JsonOptions));
        ExitPet();
        Environment.ExitCode = passed ? 0 : 1;
    }

    private void RunBubbleBoundaryChecks(Dictionary<string, bool> checks)
    {
        var saved = _settings;
        var savedRect = NativeDesktop.WindowRect(_handle);
        var savedUsage = _usageService.Snapshot;
        var now = DateTimeOffset.Now;
        var simulated = new AccountUsageSnapshot { State = UsageConnectionState.Ready, LimitsAvailable = true,
            LimitsUpdatedAt = now, Message = "边界检查模拟用量",
            Quotas = [new("codex", "Codex", 40, 300, now.AddHours(2)),
                new("codex", "Codex", 35, 10080, now.AddDays(6))] };
        try
        {
            LeaveEdgePeek();
            foreach (var size in new[] { 160, 190, 360 })
            foreach (var scale in new[] { 1d, 1.5, 2 })
            {
                ApplySettings(saved with { Size = size, UsageBubbleScale = scale, UsageCardVisible = true,
                    ClickThrough = false, AutoWalk = false, Paused = true });
                UpdateUsage(simulated);
                _tick.Stop(); _behavior.Wake(); _sprite.State = PetState.Idle; _sprite.ResetAnimation(); UpdateLayout();
                var area = CharacterWorkArea();
                foreach (var x in new[] { 0, 1, 2 })
                foreach (var y in new[] { 0, 1, 2 })
                {
                    SetUsageHover(false); UpdateLayout();
                    var local = LocalCharacterBounds();
                    // Keep the character just inside the edge: no automatic peek attachment.
                    var targetX = x == 0 ? area.X + 24 : x == 1 ? area.X + (area.Width - local.Width) / 2 : area.Right - local.Width - 24;
                    var targetY = y == 0 ? area.Y + 24 : y == 1 ? area.Y + (area.Height - local.Height) / 2 : area.Bottom - local.Height - 24;
                    MoveClamped(targetX - local.X, targetY - local.Y, area); UpdateLayout();
                    var before = CharacterScreenBounds();
                    SetUsageHover(true); UpdateLayout(); PositionHeadBubbles(); UpdateLayout();
                    var key = $"bubble_normal_{size}_{scale}_{x}_{y}";
                    checks[key + "_fits"] = PeekCardFits(area);
                    var after = CharacterScreenBounds();
                    checks[key + "_stable"] = Math.Abs(before.X - after.X) <= 1 && Math.Abs(before.Y - after.Y) <= 1;
                    var first = _usageBadge.PointToScreen(new Point());
                    var last = _usageBadge.PointToScreen(new Point(_usageBadge.ActualWidth, _usageBadge.ActualHeight));
                    checks[key + "_clear"] = last.Y <= after.Y + 1 || first.Y >= after.Bottom - 1
                        || last.X <= after.X + 1 || first.X >= after.Right - 1;
                    if (!checks[key + "_fits"] || !checks[key + "_clear"])
                        File.AppendAllText(Path.Combine(_store.DirectoryPath, "bubble-boundary-failures.jsonl"),
                            JsonSerializer.Serialize(new { key, before, after, first, last, area,
                                window = NativeDesktop.WindowRect(_handle), Width, Height,
                                head = _usageHeadTop, foot = _usageFootBottom,
                                left = Canvas.GetLeft(_usageBadge), top = Canvas.GetTop(_usageBadge) }) + Environment.NewLine);
                    if (size == 160 && scale == 2 && x == 2 && y == 0)
                        SaveBitmap(RenderElement(_canvas), Path.Combine(_store.DirectoryPath, "bubble-top-right-preview.png"));
                }
                SetUsageHover(false);
                foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
                {
                    EnterEdgePeek(edge, area); UpdateLayout();
                    foreach (var end in new[] { 0, 1 })
                    {
                        AttachPeek(area, end == 0 ? area.X + 1 : area.Right - 1, end == 0 ? area.Y + 1 : area.Bottom - 1);
                        SetUsageHover(true); UpdateLayout(); PositionHeadBubbles(); UpdateLayout();
                        checks[$"bubble_peek_{size}_{scale}_{edge}_{end}_fits"] = PeekCardFits(area);
                        SetUsageHover(false);
                    }
                    LeaveEdgePeek();
                }
            }
            ApplySettings(saved with { Size = 190, UsageBubbleScale = 1, UsageCardVisible = true,
                ClickThrough = false, AutoWalk = false, Paused = true });
            UpdateUsage(simulated); UpdateLayout();
            var centerArea = CharacterWorkArea(); var centerLocal = LocalCharacterBounds();
            MoveClamped(centerArea.X + centerArea.Width / 2 - centerLocal.X - centerLocal.Width / 2,
                centerArea.Y + centerArea.Height / 2 - centerLocal.Bottom, centerArea);
            var initial = CharacterScreenBounds();
            ApplySettings(_settings with { UsageBubbleScale = 2 }); UpdateLayout();
            var enlarged = CharacterScreenBounds();
            checks["bubble_scale_keeps_character_placement"] = Math.Abs(initial.X - enlarged.X) <= 1 && Math.Abs(initial.Bottom - enlarged.Bottom) <= 1;
            ApplySettings(_settings with { Size = 360 }); UpdateLayout();
            var resized = CharacterScreenBounds();
            checks["bubble_size_keeps_center_and_feet"] = Math.Abs(initial.X + initial.Width / 2 - resized.X - resized.Width / 2) <= 1
                && Math.Abs(initial.Bottom - resized.Bottom) <= 1;
            UpdateUsage(simulated); SetUsageHover(true); UpdateLayout(); PositionHeadBubbles();
            var head = _usageHeadTop; var foot = _usageFootBottom; var readingTop = Canvas.GetTop(_usageBadge);
            _sprite.Advance(.25); PositionHeadBubbles();
            checks["bubble_reading_anchors_frozen"] = head == _usageHeadTop && foot == _usageFootBottom && readingTop == Canvas.GetTop(_usageBadge);
            SetUsageHover(false);
            checks["bubble_hidden_clears_anchors"] = _usageHeadTop is null && _usageFootBottom is null;
            var restoreBefore = CharacterScreenBounds();
            HidePet(); RestoreFromTray(); UpdateLayout();
            var restoreAfter = CharacterScreenBounds();
            checks["bubble_hidden_restore_keeps_placement"] = IsVisible && Math.Abs(restoreBefore.X - restoreAfter.X) <= 1
                && Math.Abs(restoreBefore.Bottom - restoreAfter.Bottom) <= 1;
        }
        finally
        {
            SetUsageHover(false); LeaveEdgePeek(); ApplySettings(saved); _tick.Stop();
            MoveClamped(savedRect.X, savedRect.Y, CharacterWorkArea(savedRect.X, savedRect.Y));
            UpdateUsage(savedUsage); UpdateLayout();
        }
    }
}
