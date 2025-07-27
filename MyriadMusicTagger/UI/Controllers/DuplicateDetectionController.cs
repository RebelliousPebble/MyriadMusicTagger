using Terminal.Gui;
using MyriadMusicTagger.Services;
using Serilog;
using System.Data;

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
                // Apply auto-selection logic for tracks within 5 seconds of each other
                ApplyAutoSelectionLogic(group);

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
        /// Applies auto-selection logic for tracks within 5 seconds of each other.
        /// Keeps only the track with the highest cart number (MediaId), marks others for deletion.
        /// </summary>
        private void ApplyAutoSelectionLogic(DuplicateGroup group)
        {
            if (group.Songs.Count < 2) return;

            // Parse durations and find tracks within 5 seconds of each other
            var songsWithDuration = group.Songs
                .Select(song => new { Song = song, DurationSeconds = ParseDurationToSeconds(song.Duration) })
                .Where(x => x.DurationSeconds > 0) // Only include songs with valid durations
                .ToList();

            if (songsWithDuration.Count < 2) return;

            // Group songs by duration similarity (within 5 seconds)
            var durationGroups = new List<List<DuplicateCandidate>>();
            
            foreach (var songData in songsWithDuration)
            {
                var existingGroup = durationGroups.FirstOrDefault(dg =>
                    dg.Any(s => Math.Abs(ParseDurationToSeconds(s.Duration) - songData.DurationSeconds) <= 5));

                if (existingGroup != null)
                {
                    existingGroup.Add(songData.Song);
                }
                else
                {
                    durationGroups.Add(new List<DuplicateCandidate> { songData.Song });
                }
            }

            // For each duration group with multiple songs, keep only the one with highest cart number
            foreach (var durationGroup in durationGroups.Where(dg => dg.Count > 1))
            {
                // Sort by MediaId (cart number) descending, keep the first (highest)
                var sortedByCartNumber = durationGroup.OrderByDescending(s => s.MediaId).ToList();
                var keepSong = sortedByCartNumber.First();

                // Mark all others for deletion
                foreach (var song in durationGroup.Where(s => s.MediaId != keepSong.MediaId))
                {
                    song.IsSelected = true; // Mark for deletion
                }

                Log.Information("Auto-selected {Count} tracks for deletion in group {GroupId}, keeping cart {CartNumber}",
                    durationGroup.Count - 1, group.GroupId, keepSong.MediaId);
            }
        }

        /// <summary>
        /// Parses duration string (e.g., "00:03:45.123456") to total seconds
        /// </summary>
        private double ParseDurationToSeconds(string duration)
        {
            if (string.IsNullOrEmpty(duration)) return 0;

            try
            {
                // Handle formats like "00:03:45.123456" or "00:03:45"
                var parts = duration.Split(':');
                if (parts.Length < 2) return 0;

                double totalSeconds = 0;

                // Hours (if present)
                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[0], out int hours))
                        totalSeconds += hours * 3600;
                }

                // Minutes
                var minuteIndex = parts.Length >= 3 ? 1 : 0;
                if (int.TryParse(parts[minuteIndex], out int minutes))
                    totalSeconds += minutes * 60;

                // Seconds (with possible decimal)
                var secondIndex = parts.Length >= 3 ? 2 : 1;
                if (double.TryParse(parts[secondIndex], out double seconds))
                    totalSeconds += seconds;

                return totalSeconds;
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to parse duration '{Duration}': {Error}", duration, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Shows the main duplicate table dialog inspired by Myriad's interface
        /// </summary>
        private void ShowDuplicateTableDialog()
        {
            var dialog = new Dialog("Duplicate Songs Found", 140, 45);
            
            // Header with summary
            var summaryLabel = new Label($"Found {_duplicateGroups.Count} groups with {_duplicateGroups.Sum(g => g.Songs.Count)} total duplicate songs.")
            {
                X = 1, Y = 1, Width = Dim.Fill() - 2
            };

            // Enhanced instructions
            var instructionLabel1 = new Label("HOW TO USE: Navigate with arrow keys, SPACE to toggle delete checkboxes, ENTER to expand/collapse groups")
            {
                X = 1, Y = 3, Width = Dim.Fill() - 2, ColorScheme = Colors.TopLevel
            };
            
            var instructionLabel2 = new Label("• CHECK songs you want to DELETE (leave at least one unchecked per group to keep)")
            {
                X = 1, Y = 4, Width = Dim.Fill() - 2
            };

            var instructionLabel3 = new Label("• AUTO-SELECTION: Tracks within 5 seconds of each other are auto-selected (keeping highest cart number)")
            {
                X = 1, Y = 5, Width = Dim.Fill() - 2
            };

            // Create DataTable for the TableView
            var dataTable = CreateTableData();
            
            // Create the TableView - this is the proper table component
            var tableView = new TableView()
            {
                X = 1, Y = 7, Width = Dim.Fill() - 2, Height = Dim.Fill() - 13,
                FullRowSelect = true,
                MultiSelect = false,
                Table = dataTable
            };

            // Configure table to hide internal columns from display
            if (dataTable.Columns.Count > 7)
            {
                // Make internal columns invisible by renaming them to empty
                dataTable.Columns[7].ColumnName = ""; // IsGroupHeader 
                dataTable.Columns[8].ColumnName = ""; // GroupId
                dataTable.Columns[9].ColumnName = ""; // MediaId
            }

            // Handle key events - TableView should handle this much better
            tableView.KeyDown += (e) =>
            {
                if (e.KeyEvent.Key == Key.Space)
                {
                    ToggleRowSelectionInTable(tableView);
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.Enter)
                {
                    ToggleGroupExpansionInTable(tableView);
                    e.Handled = true;
                }
            };

            // Add a separator line before buttons
            var separatorLabel = new Label("─".PadRight(118, '─'))
            {
                X = 1, Y = 35, Width = Dim.Fill() - 2
            };

            // Action buttons - positioned with more spacing from bottom
            var expandAllButton = new Button("Expand All")
            {
                X = 1, Y = 37
            };
            expandAllButton.Clicked += () => ToggleAllGroupsInTable(true, tableView);

            var collapseAllButton = new Button("Collapse All")
            {
                X = 15, Y = 37
            };
            collapseAllButton.Clicked += () => ToggleAllGroupsInTable(false, tableView);

            var autoCollapseButton = new Button("Auto-Collapse")
            {
                X = 30, Y = 37
            };
            autoCollapseButton.Clicked += () => AutoCollapseResolvedGroups(tableView);

            var selectAllButton = new Button("Select All")
            {
                X = 48, Y = 37
            };
            selectAllButton.Clicked += () => SelectAllSongsInTable(true, tableView);

            var selectNoneButton = new Button("Select None")
            {
                X = 62, Y = 37
            };
            selectNoneButton.Clicked += () => SelectAllSongsInTable(false, tableView);

            // Warning label above delete button
            var warningLabel = new Label("⚠ WARNING: Deletion cannot be undone!")
            {
                X = 60, Y = 40,
                ColorScheme = Colors.Error
            };

            var deleteSelectedButton = new Button("Delete Selected")
            {
                X = 60, Y = 42
            };
            deleteSelectedButton.Clicked += () => DeleteSelectedDuplicatesFromTable(tableView);

            var closeButton = new Button("Close")
            {
                X = 105, Y = 42,
                IsDefault = true
            };
            closeButton.Clicked += () => Application.RequestStop(dialog);

            dialog.Add(summaryLabel, instructionLabel1, instructionLabel2, instructionLabel3, tableView, 
                      separatorLabel, expandAllButton, collapseAllButton, autoCollapseButton, selectAllButton, selectNoneButton, 
                      warningLabel, deleteSelectedButton, closeButton);

            Application.Run(dialog);
        }

        /// <summary>
        /// Toggles row selection in TableView
        /// </summary>
        private void ToggleRowSelectionInTable(TableView tableView)
        {
            if (tableView.Table == null) return;
            
            var selectedRow = tableView.SelectedRow;
            if (selectedRow < 0 || selectedRow >= tableView.Table.Rows.Count) return;
            
            var row = tableView.Table.Rows[selectedRow];
            var isGroupHeader = (bool)row["IsGroupHeader"];
            
            if (!isGroupHeader)
            {
                var mediaId = (int)row["MediaId"];
                
                // Find the song in our data
                foreach (var group in _duplicateGroups)
                {
                    var song = group.Songs.FirstOrDefault(s => s.MediaId == mediaId);
                    if (song != null)
                    {
                        song.IsSelected = !song.IsSelected;
                        
                        // Update the Del column
                        row["Del"] = song.IsSelected ? "[X]" : "[ ]";
                        
                        // Refresh the table
                        tableView.SetNeedsDisplay();
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Toggles group expansion in TableView
        /// </summary>
        private void ToggleGroupExpansionInTable(TableView tableView)
        {
            if (tableView.Table == null) return;
            
            var selectedRow = tableView.SelectedRow;
            if (selectedRow < 0 || selectedRow >= tableView.Table.Rows.Count) return;
            
            var row = tableView.Table.Rows[selectedRow];
            var isGroupHeader = (bool)row["IsGroupHeader"];
            
            if (isGroupHeader)
            {
                var groupId = (int)row["GroupId"];
                var group = _duplicateGroups.FirstOrDefault(g => g.GroupId == groupId);
                
                if (group != null)
                {
                    // Find the group in our table rows and toggle expansion
                    var groupRow = _tableRows.FirstOrDefault(r => r.IsGroupHeader && r.GroupId == groupId);
                    if (groupRow != null)
                    {
                        groupRow.IsExpanded = !groupRow.IsExpanded;
                        
                        // Rebuild the table data to show/hide child rows
                        var newDataTable = CreateTableData();
                        tableView.Table = newDataTable;
                        tableView.SetNeedsDisplay();
                    }
                }
            }
        }

        /// <summary>
        /// Toggles all groups expansion in TableView
        /// </summary>
        private void ToggleAllGroupsInTable(bool expand, TableView tableView)
        {
            foreach (var row in _tableRows.Where(r => r.IsGroupHeader))
            {
                row.IsExpanded = expand;
            }
            
            // Rebuild the table data
            var newDataTable = CreateTableData();
            tableView.Table = newDataTable;
            tableView.SetNeedsDisplay();
        }

        /// <summary>
        /// Auto-collapses groups that will only have one entry remaining after deletion
        /// </summary>
        private void AutoCollapseResolvedGroups(TableView tableView)
        {
            var collapsedGroups = 0;
            
            foreach (var group in _duplicateGroups)
            {
                var unselectedCount = group.Songs.Count(s => !s.IsSelected);
                
                if (unselectedCount <= 1)
                {
                    // This group will have 1 or 0 songs remaining after deletion - collapse it
                    var groupRow = _tableRows.FirstOrDefault(r => r.IsGroupHeader && r.GroupId == group.GroupId);
                    if (groupRow != null && groupRow.IsExpanded)
                    {
                        groupRow.IsExpanded = false;
                        collapsedGroups++;
                    }
                }
            }
            
            // Rebuild the table data
            var newDataTable = CreateTableData();
            tableView.Table = newDataTable;
            tableView.SetNeedsDisplay();
            
            if (collapsedGroups > 0)
            {
                Log.Information("Auto-collapsed {Count} resolved groups", collapsedGroups);
            }
        }

        /// <summary>
        /// Selects or deselects all songs in TableView
        /// </summary>
        private void SelectAllSongsInTable(bool select, TableView tableView)
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

            // Rebuild the table data
            var newDataTable = CreateTableData();
            tableView.Table = newDataTable;
            tableView.SetNeedsDisplay();
        }

        /// <summary>
        /// Deletes selected duplicates from TableView
        /// </summary>
        private void DeleteSelectedDuplicatesFromTable(TableView tableView)
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
                        
                        // Create progress reporter
                        var progress = new Progress<float>(value =>
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                progressBar.Fraction = value;
                                progressLabel.Text = $"Deleting {(int)(value * mediaIds.Count)}/{mediaIds.Count} songs...";
                            });
                        });

                        var success = await _duplicateDetectionService.DeleteMediaItemsAsync(mediaIds, progress);

                        Application.MainLoop.Invoke(() =>
                        {
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

                                // Rebuild table rows
                                _tableRows = CreateTableRows(_duplicateGroups);

                                // Refresh the table
                                var newDataTable = CreateTableData();
                                tableView.Table = newDataTable;
                                tableView.SetNeedsDisplay();

                                MessageBox.Query("Success", 
                                    $"Successfully deleted {selectedSongs.Count} duplicate songs.\\n\\n" +
                                    $"Remaining duplicate groups: {_duplicateGroups.Count}", 
                                    "Ok");
                            }
                            else
                            {
                                MessageBox.ErrorQuery("Error", 
                                    "Some songs could not be deleted. Check the log for details.", 
                                    "Ok");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during deletion");
                        Application.MainLoop.Invoke(() =>
                        {
                            Application.RequestStop(progressDialog);
                            MessageBox.ErrorQuery("Error", 
                                $"An error occurred during deletion: {ex.Message}", 
                                "Ok");
                        });
                    }
                });

                Application.Run(progressDialog);
            }
        }

        /// <summary>
        /// Creates a DataTable for the TableView with duplicate data (user-friendly columns only)
        /// </summary>
        private DataTable CreateTableData()
        {
            var dataTable = new DataTable();
            // Only show user-relevant columns
            dataTable.Columns.Add("Group", typeof(string));
            dataTable.Columns.Add("Del", typeof(string));
            dataTable.Columns.Add("ID", typeof(string));
            dataTable.Columns.Add("Title", typeof(string));
            dataTable.Columns.Add("Artist", typeof(string));
            dataTable.Columns.Add("Duration", typeof(string));
            dataTable.Columns.Add("Categories", typeof(string));
            
            // Keep internal columns but make them hidden-width
            dataTable.Columns.Add("IsGroupHeader", typeof(bool));
            dataTable.Columns.Add("GroupId", typeof(int));
            dataTable.Columns.Add("MediaId", typeof(int));

            foreach (var group in _duplicateGroups)
            {
                // Find the corresponding table row to check expansion state
                var groupTableRow = _tableRows.FirstOrDefault(r => r.IsGroupHeader && r.GroupId == group.GroupId);
                var isExpanded = groupTableRow?.IsExpanded ?? true;
                
                // Add group header row
                var expandIcon = isExpanded ? "▼" : "▶";
                var unselectedCount = group.Songs.Count(s => !s.IsSelected);
                var statusText = unselectedCount <= 1 ? " (Resolved)" : "";
                
                var groupRow = dataTable.NewRow();
                groupRow["Group"] = $"{expandIcon} Group {group.GroupId}";
                groupRow["Del"] = "";
                groupRow["ID"] = "";
                groupRow["Title"] = $"{group.Songs.First().Title} - {group.Songs.First().Artist}{statusText}";
                groupRow["Artist"] = $"({group.Songs.Count} duplicates)";
                groupRow["Duration"] = "";
                groupRow["Categories"] = "";
                groupRow["IsGroupHeader"] = true;
                groupRow["GroupId"] = group.GroupId;
                groupRow["MediaId"] = 0;
                dataTable.Rows.Add(groupRow);

                // Only add individual song rows if the group is expanded
                if (isExpanded)
                {
                    foreach (var song in group.Songs)
                    {
                        var songRow = dataTable.NewRow();
                        songRow["Group"] = "";
                        songRow["Del"] = song.IsSelected ? "[X]" : "[ ]";
                        songRow["ID"] = song.MediaId.ToString();
                        songRow["Title"] = song.Title;
                        songRow["Artist"] = song.Artist;
                        songRow["Duration"] = song.Duration;
                        songRow["Categories"] = string.Join(", ", song.Categories);
                        songRow["IsGroupHeader"] = false;
                        songRow["GroupId"] = group.GroupId;
                        songRow["MediaId"] = song.MediaId;
                        dataTable.Rows.Add(songRow);
                    }
                }
            }

            return dataTable;
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
        /// <summary>
        /// Updates a specific row's display without rebuilding the entire table - MINIMAL APPROACH
        /// </summary>
        private void UpdateRowDisplay(ListView tableView, int rowIndex, DuplicateTableRow row)
        {
            // Simply store the scroll position and restore it
            var currentSelection = tableView.SelectedItem;
            var currentTop = tableView.TopItem;
            
            // Rebuild only the visible rows to ensure accuracy
            var visibleRows = GetVisibleRows();
            var displayList = visibleRows.Select(r => FormatRowForDisplay(r)).ToList();
            
            // Set the source and immediately restore position
            tableView.SetSource(displayList);
            tableView.SelectedItem = currentSelection;
            tableView.TopItem = currentTop;
            tableView.SetNeedsDisplay();
        }

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
                
                // Update only this specific row without rebuilding the entire table
                UpdateRowDisplay(tableView, selectedIndex, row);
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

            // Update all visible rows while preserving scroll position
            var currentSelection = tableView.SelectedItem;
            var currentTop = tableView.TopItem;
            var visibleRows = GetVisibleRows();
            var displayList = visibleRows.Select(row => FormatRowForDisplay(row)).ToList();
            tableView.SetSource(displayList);
            tableView.SelectedItem = currentSelection;
            tableView.TopItem = currentTop;
            tableView.SetNeedsDisplay();
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
                        
                        // Create progress reporter
                        var progress = new Progress<float>(value =>
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                progressBar.Fraction = value;
                                progressLabel.Text = $"Deleting {(int)(value * mediaIds.Count)}/{mediaIds.Count} songs...";
                            });
                        });

                        var success = await _duplicateDetectionService.DeleteMediaItemsAsync(mediaIds, progress);

                        Application.MainLoop.Invoke(() =>
                        {
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
