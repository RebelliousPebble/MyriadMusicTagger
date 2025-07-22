using Terminal.Gui;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Core;
using MyriadMusicTagger.Utils;
using Serilog;
using MetaBrainz.MusicBrainz.Interfaces.Entities;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for single item processing UI operations
    /// </summary>
    public class SingleItemController
    {
        private readonly ItemProcessingService _itemProcessingService;
        private readonly RecentItemsManager _recentItemsManager;

        public SingleItemController(ItemProcessingService itemProcessingService, RecentItemsManager recentItemsManager)
        {
            _itemProcessingService = itemProcessingService ?? throw new ArgumentNullException(nameof(itemProcessingService));
            _recentItemsManager = recentItemsManager ?? throw new ArgumentNullException(nameof(recentItemsManager));
        }

        /// <summary>
        /// Shows the single item processing dialog
        /// </summary>
        public void ShowSingleItemDialog()
        {
            var dialog = new Dialog("Process Single Item", 60, 10);

            var itemNumberLabel = new Label("Enter Item Number:") { X = 1, Y = 1 };
            var itemNumberField = new TextField("") { X = Pos.Right(itemNumberLabel) + 1, Y = 1, Width = 10 };

            var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 5, IsDefault = true };
            processButton.Clicked += () =>
            {
                if (int.TryParse(itemNumberField.Text.ToString(), out int itemNumber) && itemNumber > 0)
                {
                    Application.RequestStop(dialog);
                    try
                    {
                        ProcessItem(itemNumber);
                    }
                    catch (ProcessingUtils.ProcessingException pex)
                    {
                        MessageBox.ErrorQuery("Processing Error", pex.Message, "Ok");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.ErrorQuery("Unexpected Error", $"An error occurred: {ex.Message}", "Ok");
                        Log.Error(ex, "Error in ProcessSingleItemGui after ProcessItem call for item {ItemNumber}", itemNumber);
                    }
                }
                else
                {
                    MessageBox.ErrorQuery("Invalid Input", "Please enter a valid positive number.", "Ok");
                }
            };

            var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y };
            cancelButton.Clicked += () => { Application.RequestStop(dialog); };

            dialog.Add(itemNumberLabel, itemNumberField, processButton, cancelButton);
            itemNumberField.SetFocus();
            Application.Run(dialog);
        }

        /// <summary>
        /// Processes a single item through the complete workflow
        /// </summary>
        /// <param name="itemNumber">Item number to process</param>
        public void ProcessItem(int itemNumber)
        {
            // Show loading dialog
            var loadingDialog = new Dialog("Processing...", 50, 7) { Title = $"Item {itemNumber}" };
            var statusLabel = new Label($"Fetching details for item {itemNumber}...") { X = 1, Y = 1, Width = Dim.Fill(2) };
            loadingDialog.Add(statusLabel);
            var loadingToken = Application.Begin(loadingDialog);

            ItemProcessingResult result;
            try
            {
                result = _itemProcessingService.ProcessItem(itemNumber);
            }
            catch (Exception ex)
            {
                Application.End(loadingToken);
                MessageBox.ErrorQuery("Processing Error", ex.Message, "Ok");
                return;
            }

            Application.End(loadingToken);

            if (!result.IsSuccess)
            {
                MessageBox.ErrorQuery("Processing Error", result.ErrorMessage, "Ok");
                return;
            }

            if (result.ItemResult != null)
            {
                DisplayItemDetails(result.ItemResult);
            }

            if (!result.HasMatches)
            {
                MessageBox.Query("No Matches", "No audio fingerprint matches found.", "Ok");
                return;
            }

            // Show processing dialog for fingerprinting
            var processingDialog = new Dialog("Processing...", 50, 7) { Title = $"Item {itemNumber}" };
            var fingerprintStatusLabel = new Label($"Processing matches for item {itemNumber}...") { X = 1, Y = 1, Width = Dim.Fill(2) };
            processingDialog.Add(fingerprintStatusLabel);
            var processingToken = Application.Begin(processingDialog);

            IRecording? selectedRecording = null;
            
            if (result.BestMatchScore > 0.9 && result.BestMatch?.RecordingInfo != null)
            {
                selectedRecording = result.BestMatch.RecordingInfo;
                fingerprintStatusLabel.Text = $"Found high confidence match: {selectedRecording.Title} by {selectedRecording.ArtistCredit?.FirstOrDefault()?.Name}";
                Application.Refresh();
                System.Threading.Thread.Sleep(1000);
            }
            else
            {
                fingerprintStatusLabel.Text = "Multiple potential matches found.";
                Application.Refresh();
                System.Threading.Thread.Sleep(500);
            }

            Application.End(processingToken);

            if (selectedRecording == null && result.Matches.Any())
            {
                selectedRecording = ShowMatchSelectionDialog(result.Matches);
            }

            if (selectedRecording != null)
            {
                DisplayNewMetadata(selectedRecording);
                
                if (AskToSaveChanges())
                {
                    SaveChangesToMyriad(itemNumber, selectedRecording);
                }
                else
                {
                    MessageBox.Query("Save Cancelled", "Changes were not saved.", "Ok");
                }
            }
            else
            {
                MessageBox.Query("No Match Selected", "No match was selected or confirmed. No changes made.", "Ok");
            }

            _recentItemsManager.AddToRecentItems(itemNumber);
        }

        /// <summary>
        /// Displays item details in a message box
        /// </summary>
        private void DisplayItemDetails(Result item)
        {
            MessageBox.Query("Current Item Details", TextUtils.FormatItemDetails(item), "Ok");
        }

        /// <summary>
        /// Shows match selection dialog and returns selected recording
        /// </summary>
        private IRecording? ShowMatchSelectionDialog(List<ProcessingUtils.FingerprintMatch> matches)
        {
            if (matches == null || !matches.Any())
            {
                MessageBox.ErrorQuery("Selection Error", "No fingerprint matches available to select from.", "Ok");
                return null;
            }

            var dialog = new Dialog("Select Best Match", 70, 20);
            var matchItems = new List<string>();
            var matchMap = new Dictionary<string, IRecording>();

            var scoredMatchesForDisplay = matches.Select(m => {
                if (m.RecordingInfo == null) return (Match: m, Score: 0.0, Recording: (IRecording?)null);
                return (Match: m, Score: m.Score, Recording: m.RecordingInfo);
            }).OrderByDescending(m => m.Score).ToList();

            foreach (var matchTuple in scoredMatchesForDisplay)
            {
                var recording = matchTuple.Recording;
                if (recording == null) continue;

                string artistNames = recording.ArtistCredit?.Any() == true ? 
                    string.Join(", ", recording.ArtistCredit.Select(a => a.Name ?? string.Empty)) : "[no artist]";
                string releaseTitle = recording.Releases?.FirstOrDefault()?.Title ?? "[no album]";
                string confidence = (matchTuple.Score * 100).ToString("F0") + "%";
                string displayText = $"{confidence} - {recording.Title ?? ""} - {artistNames} - {releaseTitle}";
                
                matchItems.Add(displayText);
                if (!matchMap.ContainsKey(displayText)) 
                { 
                    matchMap.Add(displayText, recording); 
                }
            }

            if (!matchItems.Any()) 
            { 
                MessageBox.ErrorQuery("Selection Error", "No valid matches could be displayed.", "Ok"); 
                return null; 
            }

            var listView = new ListView(matchItems) { X = 1, Y = 1, Width = Dim.Fill() - 2, Height = Dim.Fill() - 4 };
            IRecording? selectedRecording = null;
            
            var selectButton = new Button("Select") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
            selectButton.Clicked += () => {
                if (listView.SelectedItem >= 0 && listView.SelectedItem < matchItems.Count)
                {
                    selectedRecording = matchMap[matchItems[listView.SelectedItem]];
                    if (selectedRecording != null)
                    {
                        DisplayNewMetadata(selectedRecording);
                    }
                }
                Application.RequestStop(dialog);
            };
            
            var noneButton = new Button("None") { X = Pos.Right(selectButton) + 1, Y = selectButton.Y };
            noneButton.Clicked += () => { 
                selectedRecording = null; 
                Application.RequestStop(dialog); 
            };
            
            dialog.Add(listView, selectButton, noneButton);
            listView.SetFocus();
            Application.Run(dialog);
            return selectedRecording;
        }

        /// <summary>
        /// Displays new metadata information
        /// </summary>
        private void DisplayNewMetadata(IRecording recordingInfo)
        {
            MessageBox.Query("Selected Metadata", TextUtils.FormatMetadata(recordingInfo), "Ok");
        }

        /// <summary>
        /// Asks user if they want to save changes
        /// </summary>
        private bool AskToSaveChanges()
        {
            int result = MessageBox.Query("Save Changes", "Do you want to save these changes to Myriad?", "Yes", "No");
            return result == 0;
        }

        /// <summary>
        /// Saves changes to the Myriad system
        /// </summary>
        private void SaveChangesToMyriad(int itemNumber, IRecording recordingInfo)
        {
            var savingDialog = new Dialog("Saving...", 40, 7) { Title = $"Item {itemNumber}" };
            savingDialog.Add(new Label($"Saving changes for item {itemNumber}...") { X = 1, Y = 1 });
            var savingToken = Application.Begin(savingDialog);

            try
            {
                bool success = _itemProcessingService.SaveMetadataChanges(itemNumber, recordingInfo);
                Application.End(savingToken);
                
                if (success)
                {
                    MessageBox.Query("Success", "Metadata successfully updated in Myriad system!", "Ok");
                }
                else
                {
                    MessageBox.ErrorQuery("Save Failed", "Failed to save metadata changes.", "Ok");
                }
            }
            catch (Exception ex)
            {
                Application.End(savingToken);
                MessageBox.ErrorQuery("Save Error", $"Error saving changes: {ex.Message}", "Ok");
                Log.Error(ex, "Error saving changes for item {ItemNumber}", itemNumber);
            }
        }
    }
}
