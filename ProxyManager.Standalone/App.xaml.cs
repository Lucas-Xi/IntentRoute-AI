using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ProxyManager.Standalone;

public partial class App : Application
{
    internal const string SmokeDiagnosticPathVariable = "INTENTROUTE_SMOKE_DIAGNOSTIC_PATH";

    private void App_Startup(object sender, StartupEventArgs e)
    {
        ConfigureSmokeDiagnostics();
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (SingBoxRuntimeOwnershipException)
        {
            MessageBox.Show(
                "IntentRoute AI 已在运行，或者上一实例仍持有 sing-box 管理锁。请先关闭已有实例后再试。",
                "无法启动第二个实例",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
        }
        catch (Exception ex)
        {
            WriteConfiguredSmokeDiagnostic(ex);
            MessageBox.Show(
                "IntentRoute AI 无法安全启动。\n\n" + SingBoxRuntime.RedactSecrets(ex.Message),
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void WriteConfiguredSmokeDiagnostic(Exception exception)
    {
        var diagnosticPath = Environment.GetEnvironmentVariable(SmokeDiagnosticPathVariable);
        if (TryResolveDiagnosticPath(diagnosticPath, out var absolutePath))
            WriteSmokeDiagnostic(absolutePath, exception);
    }

    private void ConfigureSmokeDiagnostics()
    {
        var diagnosticPath = Environment.GetEnvironmentVariable(SmokeDiagnosticPathVariable);
        if (!TryResolveDiagnosticPath(diagnosticPath, out var absolutePath)) return;

        DispatcherUnhandledException += (_, eventArgs) =>
            WriteSmokeDiagnostic(absolutePath, eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            WriteSmokeDiagnostic(absolutePath, eventArgs.ExceptionObject as Exception);
    }

    // The smoke-diagnostic contract accepts exactly one absolute file path supplied by the
    // machine environment; relative or traversal-containing values are ignored.
    private static bool TryResolveDiagnosticPath(string? candidate, out string absolutePath)
    {
        absolutePath = "";
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            if (!Path.IsPathRooted(candidate) || candidate.Contains("..", StringComparison.Ordinal))
                return false;
            absolutePath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteSmokeDiagnostic(string path, Exception? exception)
    {
        try
        {
            var text = exception == null
                ? "Unhandled non-Exception failure."
                : SingBoxRuntime.RedactSecrets(exception.ToString());
            File.WriteAllText(path, text);
        }
        catch
        {
            // Diagnostics must never replace or suppress the original fatal error.
        }
    }
}
