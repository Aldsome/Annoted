using System.IO;
using System.Windows;
using Annoted.Core.Interfaces;
using Annoted.Infrastructure.Services;
using Annoted.Wpf.ViewModels;
using Annoted.Wpf.Views;

namespace Annoted.Wpf;

public partial class App : Application
{
    public static IWhisperModelManager? ModelManager { get; private set; }
    private bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            // Show at most one dialog, then shut down — avoids cascading popups on repeating faults.
            if (!_errorShown)
            {
                _errorShown = true;
                MessageBox.Show("Annoted hit an error (logged to annoted-crash.log):\n\n" + args.Exception, "Annoted — Error");
            }
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);

        try
        {
            var portable = File.Exists(Path.Combine(AppContext.BaseDirectory, ".portable"));
            var storage  = new StorageService(portable, AppContext.BaseDirectory);
            ModelManager = new WhisperModelManager(storage.ModelsFolder);

            var audio     = new AudioService();
            var dictation = new DictationService();

            // Restore the saved input device for both capture paths.
            var savedDevice = storage.LoadSettings().InputDeviceNumber;
            audio.InputDeviceNumber = savedDevice;
            dictation.InputDeviceNumber = savedDevice;

            var sidebar   = new AudioSidebarViewModel(audio, dictation, storage, ModelManager);
            var mainVm    = new MainViewModel(storage, sidebar);

            MainWindow = new MainWindow(mainVm);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show("Annoted failed to start:\n\n" + ex, "Annoted — Startup Error");
            Shutdown(1);
        }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "annoted-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
