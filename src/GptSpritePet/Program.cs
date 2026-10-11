using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed class PetApplication : Application
{
    private Mutex? _instance;
    private CancellationTokenSource? _pipeCancellation;
    private SettingsStore? _store;
    public static bool SmokeTest { get; private set; }
    public static bool MenuSmokeTest { get; private set; }
    public static bool StartWithSettings { get; private set; }
    public static bool StartWithUsage { get; private set; }
    public static bool StartWithGrowth { get; private set; }
    public static bool StartWithResetHistory { get; private set; }
    public static bool StartWithResetCheck { get; private set; }
    public static bool QuitRequested { get; private set; }
    public static bool ResetPositionRequested { get; private set; }
    public static bool StatusRequested { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var dataDirectory = Path.Combine(ProjectPaths.Root, "local-data");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) dataDirectory = Path.GetFullPath(args[++i]);
#if NATIVE_CHECKS
            else if (args[i] == "--smoke-test") SmokeTest = true;
            else if (args[i] == "--menu-smoke-test") { SmokeTest = true; MenuSmokeTest = true; }
#else
            else if (args[i] is "--smoke-test" or "--menu-smoke-test")
                throw new ArgumentException("原生检查请使用 tests/GptSpritePet.NativeTests 测试程序。");
#endif
            else if (args[i] == "--settings") StartWithSettings = true;
            else if (args[i] == "--usage") StartWithUsage = true;
            else if (args[i] == "--growth") StartWithGrowth = true;
            else if (args[i] == "--reset-history") StartWithResetHistory = true;
            else if (args[i] == "--check-reset") StartWithResetCheck = true;
            else if (args[i] == "--quit") QuitRequested = true;
            else if (args[i] == "--reset-position") ResetPositionRequested = true;
            else if (args[i] == "--status") StatusRequested = true;
        }
        var app = new PetApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app._store = new SettingsStore(dataDirectory);
        // A sandbox or another Windows account must not own the interactive user's singleton or pipe.
        var userIdentity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var instanceName = "GptSpritePet-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity + "\0" + dataDirectory.ToUpperInvariant())))[..16];
        app._instance = new Mutex(true, "Local\\" + instanceName, out var first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", instanceName, PipeDirection.Out);
                pipe.Connect(1500);
                using var writer = new StreamWriter(pipe); writer.WriteLine(QuitRequested ? "quit" : StatusRequested ? "status" : ResetPositionRequested ? "reset" : StartWithResetCheck ? "check-reset" : StartWithResetHistory ? "reset-history" : StartWithGrowth ? "growth" : StartWithUsage ? "usage" : StartWithSettings ? "settings" : "show");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
            app._instance.Dispose(); return;
        }
        if (QuitRequested) { app._instance.ReleaseMutex(); app._instance.Dispose(); return; }
        app.DispatcherUnhandledException += (_, e) =>
        {
            app.Log(e.Exception);
            e.Handled = true;
            if (SmokeTest) app.Shutdown(1);
            else { MessageBox.Show("桌宠遇到错误，诊断信息已保存。请重新启动。", "芙哇噜"); app.Shutdown(1); }
        };
        try
        {
            var window = new PetWindow(app._store);
            app.MainWindow = window;
            app._pipeCancellation = new CancellationTokenSource();
            _ = app.Listen(instanceName, window, app._pipeCancellation.Token);
            app.Run(window);
        }
        catch (Exception ex)
        {
            app.Log(ex);
            if (!SmokeTest) MessageBox.Show("无法启动桌宠：" + ex.Message, "芙哇噜");
            Environment.ExitCode = 1;
        }
        finally
        {
            app._pipeCancellation?.Cancel();
            app._pipeCancellation?.Dispose();
            app._instance.ReleaseMutex(); app._instance.Dispose();
        }
    }

    private async Task Listen(string name, PetWindow window, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (command == "show") await Dispatcher.InvokeAsync(() => { window.RestoreFromTray(); window.ResetPosition(); });
                else if (command == "settings") await Dispatcher.InvokeAsync(window.OpenSettings);
                else if (command == "usage") await Dispatcher.InvokeAsync(window.OpenUsage);
                else if (command == "growth") await Dispatcher.InvokeAsync(window.OpenGrowth);
                else if (command == "reset-history") await Dispatcher.InvokeAsync(window.OpenResetHistory);
                else if (command == "check-reset") await Dispatcher.InvokeAsync(window.CheckResetNow);
                else if (command == "quit") await Dispatcher.InvokeAsync(window.ExitPet);
                else if (command == "reset") await Dispatcher.InvokeAsync(() => { window.RestoreFromTray(); window.ResetPosition(); });
                else if (command == "status") await Dispatcher.InvokeAsync(window.WriteRuntimeStatus);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(500, cancellation).ConfigureAwait(false); }
        }
    }

    private void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_store!.DirectoryPath);
            File.AppendAllText(Path.Combine(_store.DirectoryPath, "error.log"), DateTimeOffset.Now + " " + exception + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
