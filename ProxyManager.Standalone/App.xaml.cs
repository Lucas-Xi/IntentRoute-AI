using System.Windows;

namespace ProxyManager.Standalone;

public partial class App : Application
{
    private void App_Startup(object sender, StartupEventArgs e)
    {
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
            MessageBox.Show(
                "IntentRoute AI 无法安全启动。\n\n" + SingBoxRuntime.RedactSecrets(ex.Message),
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
