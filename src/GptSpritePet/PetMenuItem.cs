using System;

namespace GptSpritePet;

/// <summary>Commands and state shared by the WPF pet menu and WinForms tray menu.</summary>
internal sealed record PetMenuItem(string? Label, Action? Execute = null, bool? Checked = null,
    bool Enabled = true, string? Icon = null, string? Tip = null, object? Tag = null, PetMenuItem[]? Children = null)
{
    internal static PetMenuItem Separator { get; } = new(Label: null);
}
