using Terminal.Gui;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Core;
using Serilog;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for recent items processing operations
    /// </summary>
    public class RecentItemsController
    {
        private readonly ItemProcessingService _itemProcessingService;
        private readonly RecentItemsManager _recentItemsManager;

        public RecentItemsController(ItemProcessingService itemProcessingService, RecentItemsManager recentItemsManager)
        {
            _itemProcessingService = itemProcessingService ?? throw new ArgumentNullException(nameof(itemProcessingService));
            _recentItemsManager = recentItemsManager ?? throw new ArgumentNullException(nameof(recentItemsManager));
        }

        /// <summary>
        /// Shows the recent items processing dialog
        /// </summary>
        public void ShowRecentItemsDialog()
        {
            if (_recentItemsManager.Count == 0)
            {
                MessageBox.Query("Recent Items", "No recent items found.", "Ok");
                return;
            }

            var dialog = new Dialog("Recent Items", 50, 15 + Math.Min(_recentItemsManager.Count, 10));
            var recentItemsList = _recentItemsManager.GetRecentItems().Select(i => $"Item {i}").ToList();
            var listView = new ListView(recentItemsList) 
            { 
                X = 1, Y = 1, 
                Width = Dim.Fill() - 2, 
                Height = Dim.Fill() - 4, 
                AllowsMarking = false, 
                AllowsMultipleSelection = false 
            };
            dialog.Add(listView);
            
            int selectedItemNumber = -1;
            
            var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
            processButton.Clicked += () => {
                if (listView.SelectedItem >= 0 && listView.SelectedItem < recentItemsList.Count)
                {
                    if (int.TryParse(recentItemsList[listView.SelectedItem].Split(' ')[1], out int itemNumber))
                    {
                        selectedItemNumber = itemNumber;
                        Application.RequestStop(dialog);
                    }
                    else
                    {
                        MessageBox.ErrorQuery("Error", "Could not parse selected item number.", "Ok");
                    }
                }
                else
                {
                    MessageBox.Query("Selection", "Please select an item to process.", "Ok");
                }
            };
            
            var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y };
            cancelButton.Clicked += () => { selectedItemNumber = -1; Application.RequestStop(dialog); };
            
            dialog.AddButton(processButton);
            dialog.AddButton(cancelButton);
            listView.SetFocus();
            Application.Run(dialog);
            
            if (selectedItemNumber != -1)
            {
                try
                {
                    var singleItemController = new SingleItemController(_itemProcessingService, _recentItemsManager);
                    singleItemController.ProcessItem(selectedItemNumber);
                }
                catch (ProcessingUtils.ProcessingException pex)
                {
                    MessageBox.ErrorQuery("Processing Error", pex.Message, "Ok");
                }
                catch (Exception ex)
                {
                    MessageBox.ErrorQuery("Unexpected Error", $"An error occurred: {ex.Message}", "Ok");
                    Log.Error(ex, "Error in ProcessRecentItemsGui after ProcessItem call for item {ItemNumber}", selectedItemNumber);
                }
            }
        }
    }
}
