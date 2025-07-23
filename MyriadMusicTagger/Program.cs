using MyriadMusicTagger.Core;
using MyriadMusicTagger.UI.Controllers;
using Serilog;
using System.Text;
using Terminal.Gui;

public class Program
{
    public static void Main(string[] args)
    {
        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = (Exception)e.ExceptionObject;
            Log.Error(exception, "Unhandled exception occurred");
            if (Application.Driver != null)
            {
                Application.Shutdown();
            }
            Console.Error.WriteLine($"An unexpected error occurred: {exception.Message}");
            Console.Error.WriteLine("Check the log.txt file for more details.");
        };

        // Configure logging
        using var log = new LoggerConfiguration()
            .WriteTo.File("log.txt")
            .MinimumLevel.Debug()
            .CreateLogger();
        Log.Logger = log;
        
        Console.OutputEncoding = Encoding.UTF8;
        
        // Initialize application manager
        var applicationManager = new MyriadMusicTagger.Core.ApplicationManager();
        
        // Load and apply settings
        var settings = MyriadMusicTagger.SettingsManager.LoadSettings();
        applicationManager.Initialize(settings);
        
        // Initialize Terminal.Gui
        Application.Init();

        try
        {
            // Create main application controller
            var mainController = new MainApplicationController(applicationManager);
            mainController.InitializeControllers();

            // Set up the UI
            var top = Application.Top;
            var mainWindow = mainController.CreateMainWindow();
            var menu = mainController.CreateMenuBar();
            
            top.Add(menu, mainWindow);
            
            // Run the application
            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
    }
}
