using Terminal.Gui;
using MyriadMusicTagger.Services;
using Serilog;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for CD ripping operations
    /// </summary>
    public class CdRippingController
    {
        private readonly CdRippingService _cdRippingService;
        private CancellationTokenSource? _cancellationTokenSource;
        private Window? _rippingWindow;
        private Label? _statusLabel;
        private ProgressBar? _currentTrackProgressBar;
        private ProgressBar? _overallProgressBar;
        private Label? _currentTrackLabel;
        private Label? _overallProgressLabel;
        private Button? _cancelButton;
        private Button? _ejectButton;
        private bool _isRipping = false;

        public CdRippingController(CdRippingService cdRippingService)
        {
            _cdRippingService = cdRippingService ?? throw new ArgumentNullException(nameof(cdRippingService));
            
            // Subscribe to service events
            _cdRippingService.ProgressChanged += OnProgressChanged;
            _cdRippingService.StatusChanged += OnStatusChanged;
        }

        /// <summary>
        /// Shows the CD ripping dialog
        /// </summary>
        public void ShowCdRippingDialog()
        {
            // Initialize the CD service if not already done
            if (!_cdRippingService.Initialize())
            {
                MessageBox.ErrorQuery("CD Ripping Error", 
                    "Failed to initialize CD drive. Please ensure you have:\n" +
                    "• A CD drive connected\n" +
                    "• Required audio drivers installed\n" +
                    "• ManagedBass.Cd library properly configured", "Ok");
                return;
            }

            // Create the ripping window
            _rippingWindow = new Window("CD Ripping")
            {
                X = Pos.Center(),
                Y = Pos.Center(),
                Width = 80,
                Height = 20,
                Modal = true
            };

            CreateRippingUI();
            
            // Start checking for CDs in the background
            Task.Run(CheckForCdAndStart);

            Application.Run(_rippingWindow);
        }

        /// <summary>
        /// Creates the UI elements for the ripping window
        /// </summary>
        private void CreateRippingUI()
        {
            if (_rippingWindow == null) return;

            // Status label
            _statusLabel = new Label("Checking for CD...")
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            // Current track progress
            _currentTrackLabel = new Label("Current Track: -")
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            _currentTrackProgressBar = new ProgressBar()
            {
                X = 1,
                Y = 4,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            // Overall progress
            _overallProgressLabel = new Label("Overall Progress: -")
            {
                X = 1,
                Y = 6,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            _overallProgressBar = new ProgressBar()
            {
                X = 1,
                Y = 7,
                Width = Dim.Fill() - 2,
                Height = 1
            };

            // Buttons
            _cancelButton = new Button("Cancel")
            {
                X = Pos.Center() - 15,
                Y = Pos.Bottom(_rippingWindow) - 4,
                IsDefault = false
            };
            _cancelButton.Clicked += OnCancelClicked;

            _ejectButton = new Button("Eject CD")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(_rippingWindow) - 4,
                IsDefault = false
            };
            _ejectButton.Clicked += OnEjectClicked;

            var closeButton = new Button("Close")
            {
                X = Pos.Center() + 15,
                Y = Pos.Bottom(_rippingWindow) - 4,
                IsDefault = true
            };
            closeButton.Clicked += OnCloseClicked;

            // Add all elements to window
            _rippingWindow.Add(_statusLabel, _currentTrackLabel, _currentTrackProgressBar, 
                              _overallProgressLabel, _overallProgressBar, 
                              _cancelButton, _ejectButton, closeButton);
        }

        /// <summary>
        /// Checks for CD and starts ripping process
        /// </summary>
        private async Task CheckForCdAndStart()
        {
            try
            {
                while (_rippingWindow != null)
                {
                    if (_isRipping)
                    {
                        await Task.Delay(1000); // Wait while ripping
                        continue;
                    }

                    if (_cdRippingService.IsCdInserted())
                    {
                        var cdInfo = _cdRippingService.GetCdInfo();
                        if (cdInfo != null)
                        {
                            UpdateStatus($"CD detected with {cdInfo.TrackCount} tracks. Starting rip...");
                            await StartRipping();
                            
                            // After ripping completes, eject CD and wait for next one
                            if (!_isRipping) // Only if not cancelled
                            {
                                UpdateStatus("Ripping completed. Ejecting CD...");
                                _cdRippingService.EjectCd();
                                await Task.Delay(2000);
                                UpdateStatus("Waiting for next CD...");
                            }
                        }
                        else
                        {
                            UpdateStatus("CD detected but unable to read. Please try another CD.");
                            await Task.Delay(3000);
                        }
                    }
                    else
                    {
                        UpdateStatus("Waiting for CD to be inserted...");
                        await Task.Delay(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error in CD monitoring: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the CD ripping process
        /// </summary>
        private async Task StartRipping()
        {
            if (_isRipping) return;

            _isRipping = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                // Create temporary directory for ripping
                var tempDir = Path.Combine(Path.GetTempPath(), "MyriadCdRip", Guid.NewGuid().ToString());
                
                UpdateButtonStates();
                
                var success = await _cdRippingService.RipAndImportCdAsync(tempDir, _cancellationTokenSource.Token);
                
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    UpdateStatus("CD ripping cancelled by user.");
                }
                else if (success)
                {
                    UpdateStatus("CD ripping and import completed successfully!");
                }
                else
                {
                    UpdateStatus("CD ripping failed. Check logs for details.");
                }

                // Clean up temp directory
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during CD ripping process");
                UpdateStatus($"CD ripping failed: {ex.Message}");
            }
            finally
            {
                _isRipping = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                UpdateButtonStates();
            }
        }

        /// <summary>
        /// Updates button enabled states based on current operation
        /// </summary>
        private void UpdateButtonStates()
        {
            Application.MainLoop.Invoke(() =>
            {
                if (_cancelButton != null)
                    _cancelButton.Enabled = _isRipping;
                if (_ejectButton != null)
                    _ejectButton.Enabled = !_isRipping;
            });
        }

        /// <summary>
        /// Updates the status display
        /// </summary>
        private void UpdateStatus(string status)
        {
            Application.MainLoop.Invoke(() =>
            {
                if (_statusLabel != null)
                {
                    _statusLabel.Text = status;
                    _statusLabel.SetNeedsDisplay();
                }
            });
        }

        /// <summary>
        /// Event handler for service status changes
        /// </summary>
        private void OnStatusChanged(object? sender, string status)
        {
            UpdateStatus(status);
        }

        /// <summary>
        /// Event handler for service progress changes
        /// </summary>
        private void OnProgressChanged(object? sender, CdRippingProgressEventArgs e)
        {
            Application.MainLoop.Invoke(() =>
            {
                // Update current track progress
                if (_currentTrackLabel != null)
                {
                    _currentTrackLabel.Text = $"Track {e.CurrentTrack}: {e.Status}";
                    _currentTrackLabel.SetNeedsDisplay();
                }

                if (_currentTrackProgressBar != null)
                {
                    _currentTrackProgressBar.Fraction = e.ProgressPercent / 100.0f;
                    _currentTrackProgressBar.SetNeedsDisplay();
                }

                // Update overall progress
                if (_overallProgressLabel != null && e.TotalTracks > 0)
                {
                    var overallPercent = ((e.CurrentTrack - 1) * 100 + e.ProgressPercent) / e.TotalTracks;
                    _overallProgressLabel.Text = $"Overall Progress: {e.CurrentTrack}/{e.TotalTracks} tracks ({overallPercent:F1}%)";
                    _overallProgressLabel.SetNeedsDisplay();

                    if (_overallProgressBar != null)
                    {
                        _overallProgressBar.Fraction = overallPercent / 100.0f;
                        _overallProgressBar.SetNeedsDisplay();
                    }
                }
            });
        }

        /// <summary>
        /// Event handler for cancel button click
        /// </summary>
        private void OnCancelClicked()
        {
            if (_isRipping && _cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                UpdateStatus("Cancelling CD ripping...");
            }
        }

        /// <summary>
        /// Event handler for eject button click
        /// </summary>
        private void OnEjectClicked()
        {
            if (!_isRipping)
            {
                if (_cdRippingService.EjectCd())
                {
                    UpdateStatus("CD ejected successfully.");
                }
                else
                {
                    UpdateStatus("Failed to eject CD.");
                }
            }
        }

        /// <summary>
        /// Event handler for close button click
        /// </summary>
        private void OnCloseClicked()
        {
            // Cancel any ongoing ripping
            if (_isRipping && _cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
            }

            Application.RequestStop(_rippingWindow);
        }
    }
}
