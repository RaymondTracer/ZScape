using Avalonia;
using System.Threading.Tasks;
using ZScape.Services;

namespace ZScape;

class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        var logger = LoggingService.Instance;

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                logger.Exception("AppDomain unhandled exception", ex);
            }
            else
            {
                logger.Error($"AppDomain unhandled non-exception: {eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            logger.Exception("Unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.Exception("Fatal startup exception", ex);
            throw;
        }
    }

    /// <summary>
    /// Avalonia configuration - do not remove; also used by visual designer.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
