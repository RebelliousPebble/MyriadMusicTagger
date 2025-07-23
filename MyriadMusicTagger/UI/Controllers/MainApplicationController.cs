using Terminal.Gui;
using MyriadMusicTagger.Core;
using MyriadMusicTagger.UI.Controllers;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Main application controller that coordinates all UI operations
    /// </summary>
    public class MainApplicationController
    {
        private readonly ApplicationManager _applicationManager;
        private SingleItemController? _singleItemController;
        private BatchProcessingController? _batchProcessingController;
        private RecentItemsController? _recentItemsController;
        private CdRippingController? _cdRippingController;
        private DuplicateDetectionController? _duplicateDetectionController;

        public MainApplicationController(ApplicationManager applicationManager)
        {
            _applicationManager = applicationManager ?? throw new ArgumentNullException(nameof(applicationManager));
        }

        /// <summary>
        /// Initializes the controllers after settings are applied
        /// </summary>
        public void InitializeControllers()
        {
            _singleItemController = new SingleItemController(_applicationManager.ItemProcessingService, _applicationManager.RecentItemsManager);
            _batchProcessingController = new BatchProcessingController(_applicationManager.BatchProcessingService, _applicationManager.RecentItemsManager);
            _recentItemsController = new RecentItemsController(_applicationManager.ItemProcessingService, _applicationManager.RecentItemsManager);
            _cdRippingController = new CdRippingController(_applicationManager.CdRippingService);
            _duplicateDetectionController = new DuplicateDetectionController(_applicationManager.DuplicateDetectionService);
        }

        /// <summary>
        /// Creates and configures the main application window
        /// </summary>
        /// <returns>The configured main window</returns>
        public Window CreateMainWindow()
        {
            var mainWindow = new Window("Myriad Music Tagger")
            {
                X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill()
            };

            return mainWindow;
        }

        /// <summary>
        /// Creates the main menu bar
        /// </summary>
        /// <returns>The configured menu bar</returns>
        public MenuBar CreateMenuBar()
        {
            var menu = new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_File", new MenuItem[]
                {
                    new MenuItem("_Settings", "", () => ShowSettingsAndUpdateRuntime()),
                    new MenuItem("_Exit", "", () => Application.RequestStop(), null, null, Key.Q | Key.CtrlMask)
                }),
                new MenuBarItem("_Process", new MenuItem[]
                {
                    new MenuItem("_Single Item", "", () => ProcessSingleItem(), null, null, Key.S | Key.CtrlMask),
                    new MenuItem("_Batch of Items", "", () => ProcessBatchItems(), null, null, Key.B | Key.CtrlMask),
                    new MenuItem("_CSV File", "", () => ProcessCsvFile(), null, null, Key.C | Key.CtrlMask),
                    new MenuItem("_Recent Items", "", () => ProcessRecentItems(), null, null, Key.R | Key.CtrlMask),
                    new MenuItem("", "", null), // Separator
                    new MenuItem("Rip _CD", "", () => RipCd(), null, null, Key.D | Key.CtrlMask),
                    new MenuItem("Find _Duplicates", "", () => FindDuplicates(), null, null, Key.F | Key.CtrlMask)
                }),
                new MenuBarItem("_Help", new MenuItem[]
                {
                    new MenuItem("_View Help", "", () => ShowHelp(), null, null, Key.F1)
                })
            });

            return menu;
        }

        /// <summary>
        /// Shows settings dialog and updates runtime configuration
        /// </summary>
        private void ShowSettingsAndUpdateRuntime()
        {
            var updatedSettings = SettingsManager.ShowSettingsDialogAndReturn(_applicationManager.CurrentSettings);
            if (updatedSettings != null)
            {
                _applicationManager.UpdateSettings(updatedSettings);
                InitializeControllers(); // Reinitialize controllers with new settings
            }
        }

        /// <summary>
        /// Processes a single item
        /// </summary>
        private void ProcessSingleItem()
        {
            _singleItemController?.ShowSingleItemDialog();
        }

        /// <summary>
        /// Processes a batch of items
        /// </summary>
        private void ProcessBatchItems()
        {
            _batchProcessingController?.ShowBatchProcessingDialog();
        }

        /// <summary>
        /// Processes items from a CSV file
        /// </summary>
        private void ProcessCsvFile()
        {
            _batchProcessingController?.ShowCsvProcessingDialog();
        }

        /// <summary>
        /// Processes recent items
        /// </summary>
        private void ProcessRecentItems()
        {
            _recentItemsController?.ShowRecentItemsDialog();
        }

        /// <summary>
        /// Shows the CD ripping dialog
        /// </summary>
        private void RipCd()
        {
            _cdRippingController?.ShowCdRippingDialog();
        }

        /// <summary>
        /// Shows the duplicate detection dialog
        /// </summary>
        private void FindDuplicates()
        {
            _duplicateDetectionController?.ShowDuplicateDetectionDialog();
        }

        /// <summary>
        /// Shows the help dialog
        /// </summary>
        private void ShowHelp()
        {
            var helpDialog = new Dialog("Help & Information", 60, 15);
            var helpText = @"• Use arrow keys and Enter to navigate menus.
• Alt + highlighted letter activates menu items.
• Tab/Shift+Tab to move between UI elements.
• Esc to close dialogs/windows.
• Ctrl+Q to quit the application.

For more information, visit:
https://github.com/RebelliousPebble/MyriadMusicTagger";
            var helpTextView = new TextView() 
            { 
                X = 1, Y = 1, 
                Width = Dim.Fill() - 2, 
                Height = Dim.Fill() - 2, 
                Text = helpText, 
                ReadOnly = true 
            };
            var okButton = new Button("Ok") { X = Pos.Center(), Y = Pos.Bottom(helpDialog) - 3, IsDefault = true };
            okButton.Clicked += () => Application.RequestStop(helpDialog);
            helpDialog.Add(helpTextView);
            helpDialog.AddButton(okButton);
            Application.Run(helpDialog);
        }
    }
}
