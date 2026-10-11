using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace GptSpritePet;

// Keep the registration separate from preferences: Windows is the source of truth.
internal sealed class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly Func<string?> _read;
    private readonly Action<string?> _write;
    internal string Command { get; }
    internal string ValueName { get; }
    internal bool Enabled => _read() is not null;

    internal WindowsStartup(string root, Func<string?>? read = null, Action<string?>? write = null)
    {
        root = Path.GetFullPath(root);
        var launcher = Path.Combine(root, "scripts", "launch-dragon-girl.ps1");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Command = $"\"{shell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{launcher}\"";
        ValueName = "Fuwaru-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];
        _read = read ?? (() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        });
        _write = write ?? (value =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (value is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, value, RegistryValueKind.String);
        });
    }

    internal void SetEnabled(bool enabled)
    {
        // Applying unrelated settings must not create or rewrite a Windows entry.
        var current = _read();
        if (enabled ? string.Equals(current, Command, StringComparison.OrdinalIgnoreCase) : current is null) return;
        _write(enabled ? Command : null);
        if (Enabled != enabled) throw new IOException("Windows 未能保存自启动设置。");
    }
}
