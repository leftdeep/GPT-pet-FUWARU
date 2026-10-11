using System;
using System.Drawing;
using System.Linq;
using GptSpritePet.Core;
using Forms = System.Windows.Forms;

namespace GptSpritePet;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.Timer _dismissTimer = new() { Interval = 80 };
    private DateTimeOffset? _outsideSince;
    private readonly Icon _ownedIcon;
    public TrayController(PetWindow pet)
    {
        _menu.Font = new Font("Microsoft YaHei UI", 9.5f);
        _menu.BackColor = Color.White; _menu.ForeColor = Color.FromArgb(54, 48, 63);
        _menu.Padding = new Forms.Padding(6);
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new PurpleMenuColors()) { RoundedEdges = true };
        _menu.Opened += (_, _) => { _outsideSince = null; _dismissTimer.Start(); };
        _menu.Closed += (_, _) => { _dismissTimer.Stop(); _outsideSince = null; };
        _dismissTimer.Tick += (_, _) =>
        {
            if (ContainsPointer(_menu, Forms.Cursor.Position)) { _outsideSince = null; return; }
            _outsideSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _outsideSince.Value >= TimeSpan.FromMilliseconds(250))
                _menu.Close(Forms.ToolStripDropDownCloseReason.AppClicked);
        };
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var purple = new SolidBrush(Color.FromArgb(128, 99, 172));
            graphics.FillEllipse(purple, 2, 2, 28, 28);
            graphics.FillEllipse(Brushes.White, 8, 12, 4, 5); graphics.FillEllipse(Brushes.White, 20, 12, 4, 5);
        }
        var handle = bitmap.GetHicon();
        using (var temporary = Icon.FromHandle(handle)) _ownedIcon = (Icon)temporary.Clone();
        NativeDesktop.DestroyIcon(handle);
        _icon = new Forms.NotifyIcon { Icon = _ownedIcon, Text = "芙哇噜 " + ProjectPaths.Version + " · 双击恢复，右键设置", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => pet.Dispatcher.Invoke(pet.RestoreFromTray);
        _menu.Opening += (_, _) =>
        {
            pet.Dispatcher.Invoke(pet.RegisterPetInteraction);
            var commands = pet.Dispatcher.Invoke(pet.CreateMenuCommands);
            var old = _menu.Items.Cast<Forms.ToolStripItem>().ToArray(); _menu.Items.Clear();
            foreach (var item in old) item.Dispose();
            Forms.ToolStripItem Render(PetMenuItem command)
            {
                if (command.Label is null) return new Forms.ToolStripSeparator();
                var item = new Forms.ToolStripMenuItem(command.Label) { Checked = command.Checked ?? false,
                    Enabled = command.Enabled, Padding = new Forms.Padding(6), ToolTipText = command.Tip, Tag = command.Tag };
                if (command.Children is { } children) foreach (var child in children) item.DropDownItems.Add(Render(child));
                if (command.Execute is { } execute) item.Click += (_, _) => pet.Dispatcher.Invoke(() => { pet.RegisterPetInteraction(); execute(); });
                return item;
            }
            _menu.Items.Add(Render(new PetMenuItem("显示并恢复鼠标互动", pet.RestoreFromTray)));
            _menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var command in commands) _menu.Items.Add(Render(command));
        };
    }
    private static bool ContainsPointer(Forms.ToolStripDropDown menu, Point point) => menu.Visible &&
        (menu.RectangleToScreen(menu.ClientRectangle).Contains(point) || menu.Items.OfType<Forms.ToolStripDropDownItem>()
            .Any(item => item.HasDropDownItems && item.DropDown.Visible && ContainsPointer(item.DropDown, point)));

    public void Dispose() { _dismissTimer.Dispose(); _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _ownedIcon.Dispose(); }

    private sealed class PurpleMenuColors : Forms.ProfessionalColorTable
    {
        public PurpleMenuColors() { UseSystemColors = false; }
        private static Color Lavender => Color.FromArgb(242, 234, 251);
        private static Color Line => Color.FromArgb(229, 216, 242);
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color MenuItemSelected => Lavender;
        public override Color MenuItemSelectedGradientBegin => Lavender;
        public override Color MenuItemSelectedGradientEnd => Lavender;
        public override Color MenuItemPressedGradientBegin => Lavender;
        public override Color MenuItemPressedGradientMiddle => Lavender;
        public override Color MenuItemPressedGradientEnd => Lavender;
        public override Color MenuItemBorder => Line;
        public override Color MenuBorder => Line;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Color.White;
        public override Color CheckBackground => Lavender;
        public override Color CheckSelectedBackground => Lavender;
        public override Color CheckPressedBackground => Lavender;
    }
}
