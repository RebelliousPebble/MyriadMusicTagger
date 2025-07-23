using Terminal.Gui;
using MyriadMusicTagger.Services;
using Serilog;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for managing the duplicate detection UI
    /// </summary>
    public class DuplicateDetectionController
    {
        private readonly DuplicateDetectionService _duplicateDetectionService;
        private List<DuplicateGroup> _duplicateGroups = new List<DuplicateGroup>();
        private List<DuplicateTableRow> _tableRows = new List<DuplicateTableRow>();

        public DuplicateDetectionController(DuplicateDetectionService duplicateDetectionService)
        {
            _duplicateDetectionService = duplicateDetectionService ?? throw new ArgumentNullException(nameof(duplicateDetectionService));
        }

        /// <summary>
        /// Shows the duplicate detection dialog
        /// </summary>
        public void ShowDuplicateDetectionDialog()
        {
            var progressDialog = new Dialog("Duplicate Detection", 70, 14);
            
            var titleLabel = new Label("Scanning database for duplicate songs...")
            {
                X = 1, Y = 1, Width = Dim.Fill() - 2
            };

            // API Progress
            var apiLabel = new Label("1. Retrieving songs from database...")
            {
                X = 1, Y = 3, Width = Dim.Fill() - 2
            };

            var apiProgressBar = new ProgressBar()
            {
                X = 1, Y = 4, Width = Dim.Fill() - 2, Height = 1
            };

            // Analysis Progress  
            var analysisLabel = new Label("2. Analyzing for duplicates...")
            {
                X = 1, Y = 6, Width = Dim.Fill() - 2
            };

            var analysisProgressBar = new ProgressBar()
            {
                X = 1, Y = 7, Width = Dim.Fill() - 2, Height = 1
            };

            var statusLabel = new Label("Starting scan...")
            {
                X = 1, Y = 9, Width = Dim.Fill() - 2
            };

            progressDialog.Add(titleLabel, apiLabel, apiProgressBar, analysisLabel, analysisProgressBar, statusLabel);
            
            // Start the search in a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    Application.MainLoop.Invoke(() =>
                    {
                        statusLabel.Text = "Starting API retrieval...";
                        apiProgressBar.Fraction = 0.0f;
                        analysisProgressBar.Fraction = 0.0f;
                    });

                    _duplicateGroups = await _duplicateDetectionService.FindDuplicateSongsAsync(
                        // API Progress Callback
                        (progress) => {
                            Application.MainLoop.Invoke(() =>
                            {
                                apiProgressBar.Fraction = progress;
                                statusLabel.Text = $"Retrieving songs from API... ({progress:P0})";
                            });
                        },
                        // Analysis Progress Callback
                        (progress) => {
                            Application.MainLoop.Invoke(() =>
                            {
                                analysisProgressBar.Fraction = progress;
                                statusLabel.Text = $"Analyzing for duplicates... ({progress:P0})";
                            });
                        });

                    Application.MainLoop.Invoke(() =>
                    {
                        statusLabel.Text = "Processing results...";
                    });

                    // Process groups into table rows
                    _tableRows = CreateTableRows(_duplicateGroups);

                    Application.MainLoop.Invoke(() =>
                    {
                        apiProgressBar.Fraction = 1.0f;
                        analysisProgressBar.Fraction = 1.0f;
                        Application.RequestStop(progressDialog);
                        
                        if (_duplicateGroups.Any())
                        {
                            ShowDuplicateTableDialog();
                        }
                        else
                        {
                            MessageBox.Query("Duplicate Detection Complete", 
                                "No duplicate songs were found in the database.", "Ok");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during duplicate detection");
                    Application.MainLoop.Invoke(() =>
                    {
                        Application.RequestStop(progressDialog);
                        MessageBox.ErrorQuery("Error", $"An error occurred during duplicate detection:\\n{ex.Message}", "Ok");
                    });
                }
            });

            Application.Run(progressDialog);
        }

        /// <summary>
        /// Creates table rows from duplicate groups, similar to Myriad's interface
        /// </summary>
        private List<DuplicateTableRow> CreateTableRows(List<DuplicateGroup> groups)
        {
            var rows = new List<DuplicateTableRow>();

            foreach (var group in groups)
            {
                // Add group header row
                var groupHeader = new DuplicateTableRow
                {
                    IsGroupHeader = true,
                    IsExpanded = true,
                    GroupId = group.GroupId,
                    DisplayText = $"Title/Artists: {group.Songs.First().Title} - {group.Songs.First().Artist}",
                    DuplicateCount = group.Songs.Count,
                    Group = group
                };
                rows.Add(groupHeader);

                // Add individual song rows
                foreach (var song in group.Songs)
                {
                    var songRow = new DuplicateTableRow
                    {
                        IsGroupHeader = false,
                        Song = song,
                        GroupId = group.GroupId,
                        MediaId = song.MediaId,
                        Title = song.Title,
                        Artist = song.Artist,
                        Duration = song.Duration,
                        Categories = string.Join(", ", song.Categories),
                        IsSelected = song.IsSelected
                    };
                    rows.Add(songRow);
                }
            }

            return rows;
        }

        /// <summary>
        /// Shows the main duplicate table dialog inspired by Myriad's interface
        /// </summary>
        private void ShowDuplicateTableDialog()
        {
            var dialog = new Dialog("Duplicate Songs Found", 120, 42);
            
            // Header with summary
            var summaryLabel = new Label($"Found {_duplicateGroups.Count} groups with {_duplicateGroups.Sum(g => g.Songs.Count)} total duplicate songs.")
            {
                X = 1, Y = 1, Width = Dim.Fill() - 2
            };

            // Enhanced instructions
            var instructionLabel1 = new Label("HOW TO USE: Each group contains songs that appear to be duplicates of each other.")
            {
                X = 1, Y = 3, Width = Dim.Fill() - 2, ColorScheme = Colors.TopLevel
            };
            
            var instructionLabel2 = new Label("• CHECK songs you want to DELETE (leave at least one unchecked to keep)")
            {
                X = 1, Y = 4, Width = Dim.Fill() - 2
            };
            
            var instructionLabel3 = new Label("• Use SPACE or MOUSE CLICK to check/uncheck, ENTER to expand/collapse groups")
            {
                X = 1, Y = 5, Width = Dim.Fill() - 2
            };
            
            var instructionLabel4 = new Label("• Use buttons below for batch operations, then click 'Delete Selected' when ready")
            {
                X = 1, Y = 6, Width = Dim.Fill() - 2
            };

            // Create a custom table view with proper height to leave room for buttons
            var tableView = new ListView()
            {
                X = 1, Y = 8, Width = Dim.Fill() - 2, Height = Dim.Fill() - 14
            };

            // Load table data
            LoadTableData(tableView);

            // Add a separator line before buttons
            var separatorLabel = new Label("─".PadRight(118, '─'))
            {
                X = 1, Y = Pos.Bottom(dialog) - 8, Width = Dim.Fill() - 2
            };

            // Action buttons - positioned at bottom with proper spacing
            var expandAllButton = new Button("Expand All")
            {
                X = 1, Y = Pos.Bottom(dialog) - 6
            };
            expandAllButton.Clicked += () => ToggleAllGroups(true, tableView);

            var collapseAllButton = new Button("Collapse All")
            {
                X = 15, Y = Pos.Bottom(dialog) - 6
            };
            collapseAllButton.Clicked += () => ToggleAllGroups(false, tableView);

            var selectAllButton = new Button("Select All")
            {
                X = 30, Y = Pos.Bottom(dialog) - 6
            };
            selectAllButton.Clicked += () => SelectAllSongs(true, tableView);

            var selectNoneButton = new Button("Select None")
            {
                X = 44, Y = Pos.Bottom(dialog) - 6
            };
            selectNoneButton.Clicked += () => SelectAllSongs(false, tableView);

            // Warning label above delete button
            var warningLabel = new Label("⚠ WARNING: Deletion cannot be undone!")
            {
                X = 60, Y = Pos.Bottom(dialog) - 4,
                ColorScheme = Colors.Error
            };

            var deleteSelectedButton = new Button("Delete Selected")
            {
                X = 60, Y = Pos.Bottom(dialog) - 2
            };
            deleteSelectedButton.Clicked += () => DeleteSelectedDuplicates(tableView);

            var closeButton = new Button("Close")
            {
                X = Pos.Right(dialog) - 10, Y = Pos.Bottom(dialog) - 2,
                IsDefault = true
            };
            closeButton.Clicked += () => Application.RequestStop(dialog);

            // Handle key events for table interaction - using KeyDown for better control
            tableView.KeyDown += (e) =>
            {
                if (e.KeyEvent.Key == Key.Space)
                {
                    ToggleSelectedRow(tableView);
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.Enter)
                {
                    ToggleGroupExpansion(tableView);
                    e.Handled = true;
                }
            };

            // Handle mouse clicks for selection
            tableView.MouseClick += (e) =>
            {
                if (e.MouseEvent.Flags == MouseFlags.Button1Clicked)
                {
                    var clickPosition = e.MouseEvent.Y;
                    var visibleRows = GetVisibleRows();
                    
                    // Adjust for ListView content offset and check bounds
                    if (clickPosition >= 0 && clickPosition < visibleRows.Count)
                    {
                        var row = visibleRows[clickPosition];
                        
                        // Only toggle selection for data rows (not group headers)
                        if (!row.IsGroupHeader && row.Song != null)
                        {
                            // Set the ListView's selected item to match the clicked row
                            tableView.SelectedItem = clickPosition;
                            
                            // Toggle the selection
                            row.Song.IsSelected = !row.Song.IsSelected;
                            row.IsSelected = row.Song.IsSelected;
                            LoadTableData(tableView);
                            tableView.SelectedItem = clickPosition; // Maintain selection
                        }
                        else if (row.IsGroupHeader)
                        {
                            // If it's a group header, toggle group expansion
                            tableView.SelectedItem = clickPosition;
                            ToggleGroupExpansion(tableView);
                        }
                    }
                    e.Handled = true;
                }
            };

            dialog.Add(summaryLabel, instructionLabel1, instructionLabel2, instructionLabel3, instructionLabel4, tableView, 
                      separatorLabel, expandAllButton, collapseAllButton, selectAllButton, selectNoneButton, 
                      warningLabel, deleteSelectedButton, closeButton);

            Application.Run(dialog);
        }

        /// <summary>
        /// Loads table data into the ListView
        /// </summary>
        private void LoadTableData(ListView tableView)
        {
            var visibleRows = GetVisibleRows();
            var displayData = visibleRows.Select(row => FormatRowForDisplay(row)).ToList();
            tableView.SetSource(displayData);
        }

        /// <summary>
        /// Gets the currently visible rows (considering collapsed/expanded state)
        /// </summary>
        private List<DuplicateTableRow> GetVisibleRows()
        {
            var visibleRows = new List<DuplicateTableRow>();
            
            foreach (var row in _tableRows)
            {
                if (row.IsGroupHeader)
                {
                    visibleRows.Add(row);
                    
                    // Only add child rows if group is expanded
                    if (row.IsExpanded)
                    {
                        var childRows = _tableRows.Where(r => !r.IsGroupHeader && r.GroupId == row.GroupId);
                        visibleRows.AddRange(childRows);
                    }
                }
            }
            
            return visibleRows;
        }

        /// <summary>
        /// Formats a table row for display
        /// </summary>
        private string FormatRowForDisplay(DuplicateTableRow row)
        {
            if (row.IsGroupHeader)
            {
                var expandIcon = row.IsExpanded ? "▼" : "▶";
                return $"{expandIcon} {row.DisplayText} ({row.DuplicateCount} duplicates)";
            }
            else
            {
                var selectedIcon = row.IsSelected ? "[X]" : "[ ]";
                var categories = string.IsNullOrEmpty(row.Categories) ? "" : $" | Cat: {row.Categories}";
                return $"    {selectedIcon} ID: {row.MediaId} | {row.Title} - {row.Artist} | {row.Duration}{categories}";
            }
        }

        /// <summary>
        /// Toggles selection of the currently focused row
        /// </summary>
        private void ToggleSelectedRow(ListView tableView)
        {
            var selectedIndex = tableView.SelectedItem;
            if (selectedIndex < 0) return;

            var visibleRows = GetVisibleRows();
            if (selectedIndex >= visibleRows.Count) return;

            var row = visibleRows[selectedIndex];
            if (!row.IsGroupHeader && row.Song != null)
            {
                row.Song.IsSelected = !row.Song.IsSelected;
                row.IsSelected = row.Song.IsSelected;
                LoadTableData(tableView);
                tableView.SelectedItem = selectedIndex; // Maintain selection
            }
        }

        /// <summary>
        /// Toggles expansion of the currently focused group
        /// </summary>
        private void ToggleGroupExpansion(ListView tableView)
        {
            var selectedIndex = tableView.SelectedItem;
            if (selectedIndex < 0) return;

            var visibleRows = GetVisibleRows();
            if (selectedIndex >= visibleRows.Count) return;

            var row = visibleRows[selectedIndex];
            if (row.IsGroupHeader)
            {
                row.IsExpanded = !row.IsExpanded;
                LoadTableData(tableView);
                tableView.SelectedItem = selectedIndex; // Maintain selection
            }
        }

        /// <summary>
        /// Expands or collapses all groups
        /// </summary>
        private void ToggleAllGroups(bool expand, ListView tableView)
        {
            foreach (var row in _tableRows.Where(r => r.IsGroupHeader))
            {
                row.IsExpanded = expand;
            }
            LoadTableData(tableView);
        }

        /// <summary>
        /// Selects or deselects all songs
        /// </summary>
        private void SelectAllSongs(bool select, ListView tableView)
        {
            foreach (var group in _duplicateGroups)
            {
                foreach (var song in group.Songs)
                {
                    song.IsSelected = select;
                }
            }

            // Update table rows
            foreach (var row in _tableRows.Where(r => !r.IsGroupHeader))
            {
                row.IsSelected = select;
            }

            LoadTableData(tableView);
        }

        /// <summary>
        /// Deletes the selected duplicate songs
        /// </summary>
        private void DeleteSelectedDuplicates(ListView tableView)
        {
            var selectedSongs = _duplicateGroups
                .SelectMany(g => g.Songs)
                .Where(s => s.IsSelected)
                .ToList();
                
            if (!selectedSongs.Any())
            {
                MessageBox.ErrorQuery("No Selection", 
                    "Please select songs to delete by checking the boxes next to them.\\n\\n" +
                    "Tip: Use Space key to check/uncheck songs, or use the batch selection buttons.",
                    "Ok");
                return;
            }

            // Count how many groups will have songs remaining
            var groupsWithRemaining = _duplicateGroups
                .Where(g => g.Songs.Any(s => !s.IsSelected))
                .Count();
                
            var groupsCompletelyDeleted = _duplicateGroups
                .Where(g => g.Songs.All(s => s.IsSelected))
                .Count();

            var confirmMessage = $"You are about to delete {selectedSongs.Count} song(s) from your Myriad database.\\n\\n";
            
            if (groupsCompletelyDeleted > 0)
            {
                confirmMessage += $"⚠ WARNING: {groupsCompletelyDeleted} duplicate group(s) will have ALL songs deleted!\\n" +
                                "This means no copies will remain in your database.\\n\\n";
            }
            
            confirmMessage += $"Groups with songs remaining: {groupsWithRemaining}\\n" +
                            $"Groups completely deleted: {groupsCompletelyDeleted}\\n\\n" +
                            "This action cannot be undone!\\n\\n" +
                            "Do you want to proceed?";
            
            var result = MessageBox.Query("Confirm Deletion", confirmMessage, "Yes", "No");
            
            if (result == 0) // Yes
            {
                var progressDialog = new Dialog("Deleting Songs", 50, 8);
                var progressLabel = new Label("Deleting selected songs...") 
                { 
                    X = 1, Y = 1, Width = Dim.Fill() - 2 
                };
                var progressBar = new ProgressBar() 
                { 
                    X = 1, Y = 3, Width = Dim.Fill() - 2, Height = 1 
                };

                progressDialog.Add(progressLabel, progressBar);
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var mediaIds = selectedSongs.Select(s => s.MediaId).ToList();
                        
                        Application.MainLoop.Invoke(() =>
                        {
                            progressBar.Fraction = 0.5f;
                        });

                        var success = await _duplicateDetectionService.DeleteMediaItemsAsync(mediaIds);

                        Application.MainLoop.Invoke(() =>
                        {
                            progressBar.Fraction = 1.0f;
                            Application.RequestStop(progressDialog);
                            
                            if (success)
                            {
                                // Remove deleted songs from groups
                                foreach (var group in _duplicateGroups.ToList())
                                {
                                    foreach (var song in selectedSongs)
                                    {
                                        group.Songs.Remove(song);
                                    }

                                    // Remove groups with 1 or fewer songs
                                    if (group.Songs.Count <= 1)
                                    {
                                        _duplicateGroups.Remove(group);
                                    }
                                }

                                // Recreate table rows and reload
                                _tableRows = CreateTableRows(_duplicateGroups);
                                LoadTableData(tableView);
                                
                                if (_duplicateGroups.Count == 0)
                                {
                                    MessageBox.Query("Deletion Complete", 
                                        "All duplicate songs have been processed!", "Ok");
                                }
                                else
                                {
                                    MessageBox.Query("Success", $"Successfully deleted {selectedSongs.Count} song(s).", "Ok");
                                }
                            }
                            else
                            {
                                MessageBox.ErrorQuery("Error", "Some songs could not be deleted. Check the log for details.", "Ok");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during song deletion");
                        Application.MainLoop.Invoke(() =>
                        {
                            Application.RequestStop(progressDialog);
                            MessageBox.ErrorQuery("Error", $"An error occurred during deletion:\\n{ex.Message}", "Ok");
                        });
                    }
                });

                Application.Run(progressDialog);
            }
        }
    }

    /// <summary>
    /// Represents a row in the duplicate detection table
    /// </summary>
    public class DuplicateTableRow
    {
        public bool IsGroupHeader { get; set; }
        public bool IsExpanded { get; set; } = true;
        public int GroupId { get; set; }
        public string DisplayText { get; set; } = "";
        public int DuplicateCount { get; set; }
        public DuplicateGroup? Group { get; set; }
        
        // Song-specific properties
        public DuplicateCandidate? Song { get; set; }
        public int MediaId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Duration { get; set; } = "";
        public string Categories { get; set; } = "";
        public bool IsSelected { get; set; }
    }
}
