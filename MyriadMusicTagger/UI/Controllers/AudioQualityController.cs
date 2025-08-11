using MyriadMusicTagger.Core;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Utils;
using NStack;
using Serilog;
using System.Data;
using Terminal.Gui;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Terminal.Gui controller for audio quality analysis feature
    /// Provides user interface for analyzing audio quality and exporting results
    /// </summary>
    public class AudioQualityController
    {
        private readonly AudioQualityAnalysisService _analysisService;
        private readonly AppSettings _settings;
        private QualityReport? _lastReport;
        private CancellationTokenSource? _cancellationTokenSource;

        public AudioQualityController(AudioQualityAnalysisService analysisService, AppSettings settings)
        {
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Shows the main audio quality analysis dialog
        /// </summary>
        public void ShowAudioQualityDialog()
        {
            var dialog = new Dialog("Audio Quality Analysis", 100, 25);

            // Title and description
            var titleLabel = new Label("Audio Quality Analysis")
            {
                X = 1,
                Y = 1
            };

            var descLabel = new Label("Analyze audio quality of tracks in your Myriad database to identify files that may need re-sourcing.")
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 2
            };

            // Analysis options
            var analyzeAllCheck = new CheckBox("Analyze all tracks in database")
            {
                X = 1,
                Y = 6,
                Checked = true
            };

            var specificIdsLabel = new Label("Or enter specific Media IDs (comma-separated):")
            {
                X = 1,
                Y = 8
            };

            var specificIdsText = new TextField("")
            {
                X = 1,
                Y = 9,
                Width = 50
            };

            var thresholdLabel = new Label("Re-rip threshold (%):")
            {
                X = 1,
                Y = 11
            };

            var thresholdText = new TextField("60")
            {
                X = 25,
                Y = 11,
                Width = 10
            };

            // Progress area
            var statusLabel = new Label("Ready to start analysis")
            {
                X = 1,
                Y = 14,
                Width = Dim.Fill() - 2
            };

            var progressBar = new ProgressBar()
            {
                X = 1,
                Y = 16,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            var detailsLabel = new Label("")
            {
                X = 1,
                Y = 18,
                Width = Dim.Fill() - 2,
                Height = 2
            };

            dialog.Add(titleLabel, descLabel, analyzeAllCheck, specificIdsLabel, specificIdsText, 
                      thresholdLabel, thresholdText, statusLabel, progressBar, detailsLabel);

            // Buttons
            var startButton = new Button("Start Analysis")
            {
                X = 5,
                Y = Pos.Bottom(dialog) - 4
            };

            var exportButton = new Button("Export Results")
            {
                X = Pos.Right(startButton) + 3,
                Y = Pos.Bottom(dialog) - 4,
                Enabled = false
            };

            var cancelButton = new Button("Cancel")
            {
                X = Pos.Right(exportButton) + 3,
                Y = Pos.Bottom(dialog) - 4
            };

            var closeButton = new Button("Close")
            {
                X = Pos.Right(cancelButton) + 3,
                Y = Pos.Bottom(dialog) - 4
            };

            // Button event handlers
            startButton.Clicked += async () =>
            {
                try
                {
                    startButton.Enabled = false;
                    cancelButton.Enabled = true;
                    exportButton.Enabled = false;

                    _cancellationTokenSource = new CancellationTokenSource();

                    // Parse threshold
                    if (!float.TryParse(thresholdText.Text.ToString(), out var threshold))
                    {
                        threshold = 60.0f;
                    }

                    var settings = new QualityAnalysisSettings
                    {
                        ReRipThreshold = threshold,
                        AnalyzeAllTracks = analyzeAllCheck.Checked
                    };

                    // Parse specific IDs if needed
                    if (!analyzeAllCheck.Checked)
                    {
                        var idsText = specificIdsText.Text.ToString();
                        if (string.IsNullOrWhiteSpace(idsText))
                        {
                            MessageBox.ErrorQuery("Error", "Please enter media IDs for specific track analysis.", "Ok");
                            startButton.Enabled = true;
                            cancelButton.Enabled = false;
                            return;
                        }

                        try
                        {
                            settings.SpecificMediaIds = idsText
                                .Split(',')
                                .Select(id => int.Parse(id.Trim()))
                                .ToList();
                        }
                        catch
                        {
                            MessageBox.ErrorQuery("Error", "Invalid media IDs. Please enter comma-separated numbers.", "Ok");
                            startButton.Enabled = true;
                            cancelButton.Enabled = false;
                            return;
                        }
                    }

                    var analysisService = new AudioQualityAnalysisService(_settings, settings);

                    // Subscribe to progress events
                    analysisService.ProgressChanged += (sender, progress) =>
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            progressBar.Fraction = progress.OverallProgress;
                            statusLabel.Text = progress.CurrentPhase;
                            
                            var details = $"Processed: {progress.ProcessedTracks}/{progress.TotalTracks}";
                            if (progress.TracksPerSecond > 0)
                            {
                                details += $" | Speed: {progress.TracksPerSecond:F1} tracks/sec";
                            }
                            if (progress.EstimatedTimeRemaining.TotalSeconds > 0)
                            {
                                details += $" | ETA: {progress.EstimatedTimeRemaining:mm\\:ss}";
                            }
                            detailsLabel.Text = details;
                        });
                    };

                    analysisService.StatusChanged += (sender, status) =>
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            statusLabel.Text = status;
                        });
                    };

                    // Start analysis
                    if (settings.AnalyzeAllTracks)
                    {
                        _lastReport = await analysisService.AnalyzeAllTracksAsync(_cancellationTokenSource.Token);
                    }
                    else
                    {
                        _lastReport = await analysisService.AnalyzeSpecificTracksAsync(settings.SpecificMediaIds, _cancellationTokenSource.Token);
                    }

                    // Update results
                    Application.MainLoop.Invoke(() =>
                    {
                        exportButton.Enabled = true;
                        progressBar.Fraction = 1.0f;
                        statusLabel.Text = "Analysis completed successfully";
                        detailsLabel.Text = $"Results: {_lastReport.SuccessfulAnalyses}/{_lastReport.TotalTracksAnalyzed} successful | " +
                                          $"Avg Quality: {_lastReport.AverageQualityScore:F1} | Re-rip needed: {_lastReport.TracksNeedingReRip}";
                        
                        MessageBox.Query("Analysis Complete", 
                            $"Analysis completed!\n\n" +
                            $"Total tracks: {_lastReport.TotalTracksAnalyzed}\n" +
                            $"Successful: {_lastReport.SuccessfulAnalyses}\n" +
                            $"Average quality: {_lastReport.AverageQualityScore:F1}\n" +
                            $"Tracks needing re-rip: {_lastReport.TracksNeedingReRip} ({_lastReport.PercentageNeedingReRip:F1}%)", "Ok");
                    });
                }
                catch (OperationCanceledException)
                {
                    Application.MainLoop.Invoke(() =>
                    {
                        statusLabel.Text = "Analysis cancelled";
                        detailsLabel.Text = "";
                        progressBar.Fraction = 0;
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during audio quality analysis");
                    Application.MainLoop.Invoke(() =>
                    {
                        statusLabel.Text = "Analysis failed";
                        detailsLabel.Text = $"Error: {ex.Message}";
                        MessageBox.ErrorQuery("Analysis Error", $"Analysis failed: {ex.Message}", "Ok");
                    });
                }
                finally
                {
                    Application.MainLoop.Invoke(() =>
                    {
                        startButton.Enabled = true;
                        cancelButton.Enabled = false;
                    });
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            };

            exportButton.Clicked += () => ShowExportDialog();

            cancelButton.Clicked += () =>
            {
                _cancellationTokenSource?.Cancel();
                cancelButton.Enabled = false;
            };

            closeButton.Clicked += () => Application.RequestStop(dialog);

            dialog.AddButton(startButton);
            dialog.AddButton(exportButton);
            dialog.AddButton(cancelButton);
            dialog.AddButton(closeButton);

            Application.Run(dialog);
        }

        /// <summary>
        /// Shows the export options dialog
        /// </summary>
        private void ShowExportDialog()
        {
            if (_lastReport == null)
                return;

            var dialog = new Dialog("Export Results", 80, 20);

            var exportTypeLabel = new Label("Export Type:")
            {
                X = 1,
                Y = 1
            };

            var exportTypeRadio = new RadioGroup(new NStack.ustring[] 
            {
                "All results",
                "Only tracks needing re-rip",
                "Summary statistics only"
            })
            {
                X = 1,
                Y = 3,
                SelectedItem = 1
            };

            var filePathLabel = new Label("File Path:")
            {
                X = 1,
                Y = 8
            };

            var filePathText = new TextField(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "audio_quality_report.csv"))
            {
                X = 12,
                Y = 8,
                Width = Dim.Fill() - 15
            };

            var browseButton = new Button("Browse...")
            {
                X = Pos.Right(filePathText) + 1,
                Y = 8
            };

            browseButton.Clicked += () =>
            {
                var saveDialog = new SaveDialog("Save Quality Report", "Save CSV file as:");
                saveDialog.AllowedFileTypes = new string[] { ".csv" };
                Application.Run(saveDialog);

                if (!saveDialog.Canceled && !string.IsNullOrEmpty(saveDialog.FilePath.ToString()))
                {
                    filePathText.Text = saveDialog.FilePath.ToString();
                }
            };

            var exportButton = new Button("Export")
            {
                X = 5,
                Y = Pos.Bottom(dialog) - 4,
                IsDefault = true
            };

            var cancelButton = new Button("Cancel")
            {
                X = Pos.Right(exportButton) + 3,
                Y = Pos.Bottom(dialog) - 4
            };

            exportButton.Clicked += async () =>
            {
                try
                {
                    var filePath = filePathText.Text.ToString();
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        MessageBox.ErrorQuery("Error", "Please specify a file path.", "Ok");
                        return;
                    }

                    exportButton.Enabled = false;
                    var exporter = new AudioQualityCsvExporter();

                    switch (exportTypeRadio.SelectedItem)
                    {
                        case 0: // All results
                            await exporter.ExportToCsvAsync(_lastReport, filePath);
                            break;
                        case 1: // Only re-rip tracks
                            await exporter.ExportReRipListToCsvAsync(_lastReport, filePath);
                            break;
                        case 2: // Summary only
                            await exporter.ExportSummaryToCsvAsync(_lastReport, filePath);
                            break;
                    }

                    MessageBox.Query("Export Complete", $"Results exported successfully to:\n{filePath}", "Ok");
                    Application.RequestStop(dialog);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error exporting results");
                    MessageBox.ErrorQuery("Export Error", $"Failed to export results: {ex.Message}", "Ok");
                }
                finally
                {
                    exportButton.Enabled = true;
                }
            };

            cancelButton.Clicked += () => Application.RequestStop(dialog);

            dialog.Add(exportTypeLabel, exportTypeRadio, filePathLabel, filePathText, browseButton);
            dialog.AddButton(exportButton);
            dialog.AddButton(cancelButton);

            Application.Run(dialog);
        }

        /// <summary>
        /// Shows detailed results in a table view
        /// </summary>
        private void ShowResultsDetailDialog()
        {
            if (_lastReport == null || !_lastReport.Results.Any())
                return;

            var dialog = new Dialog("Quality Analysis Results", 140, 40);

            // Filter options
            var filterFrame = new FrameView("Filter")
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = 5
            };

            var showAllRadio = new RadioGroup(new NStack.ustring[] { "All tracks", "Only problematic tracks", "Only excellent tracks" })
            {
                X = 1,
                Y = 1,
                SelectedItem = 0
            };

            var sortLabel = new Label("Sort by:")
            {
                X = 40,
                Y = 1
            };

            var sortCombo = new ComboBox()
            {
                X = 50,
                Y = 1,
                Width = 25,
                Height = 5
            };
            sortCombo.SetSource(new string[] { "Quality Score", "Title", "Artist", "Spectral Score", "Dynamic Range", "Clipping Penalty" });
            sortCombo.SelectedItem = 0;

            filterFrame.Add(showAllRadio, sortLabel, sortCombo);

            // Results table
            var tableView = new TableView()
            {
                X = 1,
                Y = 7,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 10,
                FullRowSelect = true,
                MultiSelect = false
            };

            // Apply button
            var applyFilterButton = new Button("Apply Filter")
            {
                X = 80,
                Y = 2
            };

            applyFilterButton.Clicked += () => UpdateResultsTable(tableView, showAllRadio.SelectedItem, sortCombo.SelectedItem);

            filterFrame.Add(applyFilterButton);

            // Initial population
            UpdateResultsTable(tableView, 0, 0);

            var closeButton = new Button("Close")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(dialog) - 3,
                IsDefault = true
            };

            closeButton.Clicked += () => Application.RequestStop(dialog);

            dialog.Add(filterFrame, tableView);
            dialog.AddButton(closeButton);

            Application.Run(dialog);
        }

        /// <summary>
        /// Updates the results table with filtered and sorted data
        /// </summary>
        private void UpdateResultsTable(TableView tableView, int filterType, int sortType)
        {
            if (_lastReport == null)
                return;

            var results = _lastReport.Results.Where(r => r.ProcessingSuccessful).ToList();

            // Apply filter
            switch (filterType)
            {
                case 1: // Only problematic
                    results = results.Where(r => r.RecommendReRip).ToList();
                    break;
                case 2: // Only excellent
                    results = results.Where(r => r.OverallQualityScore >= 90).ToList();
                    break;
            }

            // Apply sort
            results = sortType switch
            {
                0 => results.OrderBy(r => r.OverallQualityScore).ToList(),
                1 => results.OrderBy(r => r.Title).ToList(),
                2 => results.OrderBy(r => r.Artist).ToList(),
                3 => results.OrderBy(r => r.SpectralAnalysis.SpectralScore).ToList(),
                4 => results.OrderBy(r => r.DynamicRange.DynamicRangeScore).ToList(),
                5 => results.OrderBy(r => r.ClippingAnalysis.ClippingPenalty).ToList(),
                _ => results.OrderBy(r => r.OverallQualityScore).ToList()
            };

            // Create table
            var table = new DataTable();
            table.Columns.Add("ID", typeof(int));
            table.Columns.Add("Title", typeof(string));
            table.Columns.Add("Artist", typeof(string));
            table.Columns.Add("Quality", typeof(string));
            table.Columns.Add("Spectral", typeof(string));
            table.Columns.Add("Dynamic", typeof(string));
            table.Columns.Add("Clipping", typeof(string));
            table.Columns.Add("Noise", typeof(string));
            table.Columns.Add("Channel", typeof(string));
            table.Columns.Add("Re-rip", typeof(string));
            table.Columns.Add("Issues", typeof(string));

            foreach (var result in results)
            {
                table.Rows.Add(
                    result.MediaId,
                    result.Title.Length > 25 ? result.Title.Substring(0, 22) + "..." : result.Title,
                    result.Artist.Length > 20 ? result.Artist.Substring(0, 17) + "..." : result.Artist,
                    $"{result.OverallQualityScore:F1}",
                    $"{result.SpectralAnalysis.SpectralScore:F1}",
                    $"{result.DynamicRange.DynamicRangeScore:F1}",
                    $"{result.ClippingAnalysis.ClippingPenalty:F1}",
                    $"{result.NoiseFloor.NoiseFloorScore:F1}",
                    $"{result.ChannelQuality.ChannelScore:F1}",
                    result.RecommendReRip ? "YES" : "No",
                    result.QualityIssues.Count > 0 ? result.QualityIssues.First() : ""
                );
            }

            tableView.Table = table;
        }
    }
}
