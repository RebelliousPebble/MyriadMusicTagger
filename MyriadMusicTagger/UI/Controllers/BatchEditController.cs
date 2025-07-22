using Terminal.Gui;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Utils;
using Serilog;
using MetaBrainz.MusicBrainz.Interfaces.Entities;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for batch edit table operations
    /// </summary>
    public class BatchEditController
    {
        private readonly BatchProcessingService _batchProcessingService;
        private BatchFilter _currentBatchFilter = BatchFilter.All;
        private List<BatchProcessItem> _filteredBatchItems = new();

        public BatchEditController(BatchProcessingService batchProcessingService)
        {
            _batchProcessingService = batchProcessingService ?? throw new ArgumentNullException(nameof(batchProcessingService));
        }

        /// <summary>
        /// Shows the batch edit table dialog
        /// </summary>
        public void ShowBatchEditTable()
        {
            if (_batchProcessingService.BatchItems.Count == 0)
            {
                MessageBox.Query("Batch Edit", "No items were processed in the batch.", "Ok");
                return;
            }

            // Reset filter to show all items when opening the table
            _currentBatchFilter = BatchFilter.All;

            var editDialog = new Dialog("Batch Edit Table", 140, 35);
            var tableView = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 3, FullRowSelect = true };
            var table = new System.Data.DataTable();

            // Add columns
            table.Columns.Add("✓", typeof(string));
            table.Columns.Add("Item #", typeof(int));
            table.Columns.Add("Old Title", typeof(string));
            table.Columns.Add("Old Artist", typeof(string));
            table.Columns.Add("New Title", typeof(string));
            table.Columns.Add("New Artist", typeof(string));
            table.Columns.Add("Conf.", typeof(string));
            table.Columns.Add("Status", typeof(string));

            tableView.Table = table;
            RefreshBatchTableView(tableView, table);

            // Configure table style
            tableView.Style.AlwaysShowHeaders = true;
            tableView.Style.ShowHorizontalScrollIndicators = true;
            tableView.Style.SmoothHorizontalScrolling = true;

            editDialog.Add(tableView);

            // Create buttons
            var editButton = new Button("Edit") { X = 1, Y = Pos.Bottom(tableView) + 1 };
            var toggleSelectButton = new Button("Toggle Select") { X = Pos.Right(editButton) + 1, Y = editButton.Y };
            var saveButton = new Button("Save Selected") { X = Pos.Right(toggleSelectButton) + 1, Y = editButton.Y };

            // Add filter buttons
            var filterAllButton = new Button("All") { X = Pos.Right(saveButton) + 3, Y = editButton.Y };
            var filterUnselectedButton = new Button("Unselected") { X = Pos.Right(filterAllButton) + 1, Y = editButton.Y };
            var filterSelectedButton = new Button("Selected") { X = Pos.Right(filterUnselectedButton) + 1, Y = editButton.Y };
            var filterErrorsButton = new Button("Errors") { X = Pos.Right(filterSelectedButton) + 1, Y = editButton.Y };

            var exitButton = new Button("Exit") { X = Pos.Right(filterErrorsButton) + 3, Y = editButton.Y };

            // Add keyboard shortcuts for the table
            tableView.KeyPress += (args) => {
                if (args.KeyEvent.Key == Key.Space)
                {
                    if (tableView.SelectedRow >= 0 && tableView.SelectedRow < _filteredBatchItems.Count)
                    {
                        ToggleItemSelection(_filteredBatchItems[tableView.SelectedRow]);
                        RefreshBatchTableView(tableView, table);
                        args.Handled = true;
                    }
                }
                else if (args.KeyEvent.Key == Key.Enter)
                {
                    if (tableView.SelectedRow >= 0 && tableView.SelectedRow < _filteredBatchItems.Count)
                    {
                        var selectedItem = _filteredBatchItems[tableView.SelectedRow];
                        EditBatchItem(selectedItem);
                        RefreshBatchTableView(tableView, table);
                        args.Handled = true;
                    }
                }
                else if (args.KeyEvent.Key == Key.Tab)
                {
                    editButton.SetFocus();
                    args.Handled = true;
                }
            };

            // Button event handlers
            editButton.Clicked += () => {
                if (tableView.SelectedRow < 0 || tableView.SelectedRow >= _filteredBatchItems.Count) return;
                var selectedItem = _filteredBatchItems[tableView.SelectedRow];
                EditBatchItem(selectedItem);
                RefreshBatchTableView(tableView, table);
            };

            toggleSelectButton.Clicked += () => {
                if (tableView.SelectedRow < 0 || tableView.SelectedRow >= _filteredBatchItems.Count) return;
                ToggleItemSelection(_filteredBatchItems[tableView.SelectedRow]);
                RefreshBatchTableView(tableView, table);
            };

            saveButton.Clicked += () => {
                SaveBatchChanges();
                RefreshBatchTableView(tableView, table);
            };

            // Filter button event handlers
            filterAllButton.Clicked += () => {
                _currentBatchFilter = BatchFilter.All;
                editDialog.Title = $"Batch Edit Table - All Items ({_batchProcessingService.BatchItems.Count} total)";
                UpdateFilterButtonStates(filterAllButton, filterUnselectedButton, filterSelectedButton, filterErrorsButton);
                RefreshBatchTableView(tableView, table);
            };

            filterUnselectedButton.Clicked += () => {
                _currentBatchFilter = BatchFilter.Unselected;
                var unselectedCount = _batchProcessingService.BatchItems.Count(i => !i.IsSelected);
                editDialog.Title = $"Batch Edit Table - Unselected Items ({unselectedCount} items)";
                UpdateFilterButtonStates(filterUnselectedButton, filterAllButton, filterSelectedButton, filterErrorsButton);
                RefreshBatchTableView(tableView, table);
            };

            filterSelectedButton.Clicked += () => {
                _currentBatchFilter = BatchFilter.Selected;
                var selectedCount = _batchProcessingService.BatchItems.Count(i => i.IsSelected);
                editDialog.Title = $"Batch Edit Table - Selected Items ({selectedCount} items)";
                UpdateFilterButtonStates(filterSelectedButton, filterAllButton, filterUnselectedButton, filterErrorsButton);
                RefreshBatchTableView(tableView, table);
            };

            filterErrorsButton.Clicked += () => {
                _currentBatchFilter = BatchFilter.HasErrors;
                var errorCount = _batchProcessingService.BatchItems.Count(i => !string.IsNullOrEmpty(i.Error));
                editDialog.Title = $"Batch Edit Table - Items with Errors ({errorCount} items)";
                UpdateFilterButtonStates(filterErrorsButton, filterAllButton, filterUnselectedButton, filterSelectedButton);
                RefreshBatchTableView(tableView, table);
            };

            exitButton.Clicked += () => { Application.RequestStop(editDialog); };

            // Set up tab order for button navigation
            editButton.TabIndex = 0;
            toggleSelectButton.TabIndex = 1;
            saveButton.TabIndex = 2;
            filterAllButton.TabIndex = 3;
            filterUnselectedButton.TabIndex = 4;
            filterSelectedButton.TabIndex = 5;
            filterErrorsButton.TabIndex = 6;
            exitButton.TabIndex = 7;

            // Add keyboard shortcut to return focus to table from buttons
            var returnToTableHandler = (View.KeyEventEventArgs args) => {
                if (args.KeyEvent.Key == Key.Esc) { tableView.SetFocus(); args.Handled = true; }
            };

            editButton.KeyPress += returnToTableHandler;
            toggleSelectButton.KeyPress += returnToTableHandler;
            saveButton.KeyPress += returnToTableHandler;
            filterAllButton.KeyPress += returnToTableHandler;
            filterUnselectedButton.KeyPress += returnToTableHandler;
            filterSelectedButton.KeyPress += returnToTableHandler;
            filterErrorsButton.KeyPress += returnToTableHandler;
            exitButton.KeyPress += returnToTableHandler;

            editDialog.Add(editButton, toggleSelectButton, saveButton, filterAllButton, filterUnselectedButton, filterSelectedButton, filterErrorsButton, exitButton);

            // Initialize filter button states (All is active by default)
            UpdateFilterButtonStates(filterAllButton, filterUnselectedButton, filterSelectedButton, filterErrorsButton);
            editDialog.Title = $"Batch Edit Table - All Items ({_batchProcessingService.BatchItems.Count} total)";

            // Set initial focus to the table
            tableView.SetFocus();
            Application.Run(editDialog);
        }

        /// <summary>
        /// Refreshes the batch table view with filtered data
        /// </summary>
        private void RefreshBatchTableView(TableView tableView, System.Data.DataTable table)
        {
            // Apply current filter to create filtered list
            _filteredBatchItems.Clear();
            foreach (var item in _batchProcessingService.BatchItems)
            {
                bool includeItem = _currentBatchFilter switch
                {
                    BatchFilter.All => true,
                    BatchFilter.Unselected => !item.IsSelected,
                    BatchFilter.Selected => item.IsSelected,
                    BatchFilter.HasErrors => !string.IsNullOrEmpty(item.Error),
                    BatchFilter.NeedsReview => string.IsNullOrEmpty(item.Error) &&
                                             ((item.AvailableMatches?.Any() ?? false) && string.IsNullOrEmpty(item.NewTitle)) ||
                                             (!item.IsSelected && !string.IsNullOrEmpty(item.NewTitle)),
                    _ => true
                };

                if (includeItem)
                {
                    _filteredBatchItems.Add(item);
                }
            }

            // Clear and repopulate table with filtered items
            table.Rows.Clear();
            foreach (var item in _filteredBatchItems)
            {
                string selectedMark = item.IsSelected ? "✓" : " ";
                string confidenceStr = item.ConfidenceScore > 0 ? $"{item.ConfidenceScore:P0}" : "-";
                string status;
                if (!string.IsNullOrEmpty(item.Error)) status = $"Error: {item.Error.Split('\n')[0]}";
                else if (item.AvailableMatches?.Any() ?? false && string.IsNullOrEmpty(item.NewTitle)) status = "Needs Selection";
                else if (!item.IsSelected && !string.IsNullOrEmpty(item.NewTitle)) status = "Review & Select";
                else if (item.IsSelected) status = "Selected";
                else status = "Needs Review";

                // Truncate long titles to improve table readability
                string oldTitle = TextUtils.TruncateText(item.OldTitle, 22);
                string oldArtist = TextUtils.TruncateText(item.OldArtist, 18);
                string newTitle = TextUtils.TruncateText(item.NewTitle ?? "", 22);
                string newArtist = TextUtils.TruncateText(item.NewArtist ?? "", 18);
                string statusText = TextUtils.TruncateText(status, 13);

                table.Rows.Add(selectedMark, item.ItemNumber, oldTitle, oldArtist, newTitle, newArtist, confidenceStr, statusText);
            }
            tableView.SetNeedsDisplay();
        }

        /// <summary>
        /// Edits a batch item
        /// </summary>
        private void EditBatchItem(BatchProcessItem item)
        {
            var editDialog = new Dialog($"Edit Item: {item.ItemNumber}", 70, 20);
            var details = new System.Text.StringBuilder();
            details.AppendLine($"Item Number: {item.ItemNumber}");
            details.AppendLine($"Old Title: {item.OldTitle}");
            details.AppendLine($"Old Artist: {item.OldArtist}");
            if (!string.IsNullOrEmpty(item.Error)) { details.AppendLine($"Error: {item.Error}"); }

            var detailsLabel = new Label(details.ToString()) { X = 1, Y = 1, Width = Dim.Fill() - 2 };
            var newTitleLabel = new Label("New Title:") { X = 1, Y = Pos.Bottom(detailsLabel) + 1 };
            var newTitleField = new TextField(item.NewTitle ?? "") { X = Pos.Right(newTitleLabel) + 1, Y = Pos.Top(newTitleLabel), Width = Dim.Fill() - 20 };
            var newArtistLabel = new Label("New Artist:") { X = 1, Y = Pos.Bottom(newTitleLabel) };
            var newArtistField = new TextField(item.NewArtist ?? "") { X = Pos.Right(newArtistLabel) + 1, Y = Pos.Top(newArtistLabel), Width = Dim.Fill() - 20 };
            editDialog.Add(detailsLabel, newTitleLabel, newTitleField, newArtistLabel, newArtistField);

            if (string.IsNullOrEmpty(item.NewTitle) && (item.AvailableMatches?.Any() ?? false) && string.IsNullOrEmpty(item.Error))
            {
                var selectMatchButton = new Button("Choose from Matches") { X = Pos.Center(), Y = Pos.Bottom(newArtistLabel) + 1 };
                selectMatchButton.Clicked += () => {
                    var recordingInfo = ShowMatchSelectionDialog(item.AvailableMatches);
                    if (recordingInfo != null)
                    {
                        item.NewTitle = recordingInfo.Title ?? string.Empty;
                        item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
                        item.RecordingInfo = recordingInfo;
                        item.ConfidenceScore = item.AvailableMatches.FirstOrDefault(f => f.RecordingInfo == recordingInfo)?.Score ?? item.ConfidenceScore;
                        newTitleField.Text = item.NewTitle;
                        newArtistField.Text = item.NewArtist;
                        item.Error = string.Empty;
                        MessageBox.Query("Match Selected", "Metadata populated from selection.", "Ok");
                    }
                };
                editDialog.Add(selectMatchButton);
            }

            var okButton = new Button("Ok") { X = Pos.Center() - 8, Y = Pos.Bottom(editDialog) - 3, IsDefault = true };
            okButton.Clicked += () => {
                item.NewTitle = newTitleField.Text?.ToString() ?? string.Empty;
                item.NewArtist = newArtistField.Text?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(item.NewTitle) && !string.IsNullOrWhiteSpace(item.NewArtist))
                {
                    item.IsSelected = true;
                    item.Error = string.Empty;
                }
                else
                {
                    item.IsSelected = false;
                    MessageBox.ErrorQuery("Missing Info", "Title and Artist cannot be empty if item is to be saved.", "Ok");
                }
                Application.RequestStop(editDialog);
            };

            var cancelButton = new Button("Cancel") { X = Pos.Right(okButton) + 1, Y = okButton.Y };
            cancelButton.Clicked += () => { Application.RequestStop(editDialog); };

            editDialog.AddButton(okButton);
            editDialog.AddButton(cancelButton);
            newTitleField.SetFocus();
            Application.Run(editDialog);
        }

        /// <summary>
        /// Shows match selection dialog for available matches
        /// </summary>
        private IRecording? ShowMatchSelectionDialog(List<ProcessingUtils.FingerprintMatch> matches)
        {
            if (matches == null || !matches.Any())
            {
                MessageBox.ErrorQuery("Selection Error", "No matches available to select from.", "Ok");
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
        /// Toggles the selection status of a batch item
        /// </summary>
        private void ToggleItemSelection(BatchProcessItem item)
        {
            if (!string.IsNullOrEmpty(item.Error) || string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist))
            {
                MessageBox.ErrorQuery("Selection Error", "This item cannot be selected due to errors or missing metadata. Edit the item first.", "Ok");
            }
            else
            {
                item.IsSelected = !item.IsSelected;
            }
        }

        /// <summary>
        /// Saves changes for all selected batch items
        /// </summary>
        private void SaveBatchChanges()
        {
            var selectedItems = _batchProcessingService.BatchItems.Where(i => i.IsSelected && string.IsNullOrEmpty(i.Error)).ToList();
            if (!selectedItems.Any())
            {
                MessageBox.Query("Save Changes", "No items are currently selected for saving or all selected items have errors.", "Ok");
                return;
            }

            if (MessageBox.Query("Confirm Save", $"Are you sure you want to save changes for {selectedItems.Count} item(s)?", "Yes", "No") == 1)
                return;

            var progressDialog = new Dialog("Saving Changes...", 50, 7);
            var progressLabel = new Label($"Saving {selectedItems.Count} items...") { X = 1, Y = 1, Width = Dim.Fill() - 2 };
            var currentItemLabel = new Label("") { X = 1, Y = 2, Width = Dim.Fill() - 2 };
            progressDialog.Add(progressLabel, currentItemLabel);
            var saveProgressToken = Application.Begin(progressDialog);

            var result = _batchProcessingService.SaveSelectedItems((current, total, status) => {
                currentItemLabel.Text = status;
                Application.Refresh();
            });

            Application.End(saveProgressToken);

            var summaryMessage = TextUtils.CreateSaveSummary(result.SuccessCount, result.FailedCount);
            MessageBox.Query("Save Complete", summaryMessage, "Ok");
        }

        /// <summary>
        /// Updates filter button states to show which one is active
        /// </summary>
        private void UpdateFilterButtonStates(Button activeButton, params Button[] inactiveButtons)
        {
            var activeText = activeButton.Text?.ToString() ?? "";
            if (activeText.EndsWith(" ●"))
            {
                return; // Already marked as active
            }

            // Remove active indicator from all buttons first
            foreach (var button in inactiveButtons)
            {
                var text = button.Text?.ToString() ?? "";
                if (text.EndsWith(" ●"))
                {
                    button.Text = text.Substring(0, text.Length - 2);
                }
            }

            // Add active indicator to the current button
            activeButton.Text = activeText + " ●";
        }
    }

    /// <summary>
    /// Enum for batch table filtering options
    /// </summary>
    public enum BatchFilter { All, Unselected, Selected, HasErrors, NeedsReview }
}
