using System.Configuration;
using System.Data;
using System.Windows;

namespace SourceUnpack.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        // UI Thread Exceptions
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        
        // Non-UI Thread Exceptions
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        
        // Unobserved Task Exceptions
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatalError(e.Exception, "UI Thread");
        e.Handled = true; 
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogFatalError(e.ExceptionObject as Exception, "AppDomain (Non-UI)");
    }

    private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatalError(e.Exception, "TaskScheduler");
        e.SetObserved();
    }

    private void LogFatalError(Exception? ex, string source)
    {
        if (ex == null) return;

        string message = $"CRASH [{source}]: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
        if (ex.InnerException != null)
        {
            message += $"\n\nInner Exception:\n{ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }

        try
        {
            string logFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
            System.IO.File.AppendAllText(logFile, $"\n\n[{DateTime.Now}] {message}\n==================================================\n");
            
            System.Windows.MessageBox.Show($"Application Crashed!\n\nLog saved to: {logFile}\n\nError:\n{ex.Message}", "SourceUnpack Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Fallback if file write fails
            System.Windows.MessageBox.Show(message, "Fatal Error (Log Failed)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string settingsDir = System.IO.Path.Combine(appData, "SourceUnpack");
        string settingsFile = System.IO.Path.Combine(settingsDir, "settings.txt");

        // Load settings
        string? gameDir = null;
        string? outputDir = null;

        if (System.IO.File.Exists(settingsFile))
        {
            try
            {
                var lines = System.IO.File.ReadAllLines(settingsFile);
                if (lines.Length >= 1) gameDir = lines[0];
                if (lines.Length >= 2) outputDir = lines[1];
            }
            catch { }
        }

        // Check if settings are valid (need setup if missing)
        // Since we moved config to MainWindow, we don't block startup with Wizard.
        // User will interpret empty fields in MainWindow as "need setup".

        var window = new MainWindow();
        // Force VM update
        if (window.DataContext is SourceUnpack.App.ViewModels.MainViewModel vm)
        {
             if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir)) 
                vm.GameDirectory = gameDir;
                
             if (!string.IsNullOrEmpty(outputDir) && System.IO.Directory.Exists(outputDir)) 
                vm.OutputDirectory = outputDir;
        }

        window.Show();
    }
}

