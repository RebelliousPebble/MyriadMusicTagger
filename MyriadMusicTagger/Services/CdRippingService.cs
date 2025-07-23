using ManagedBass;
using ManagedBass.Cd;
using ManagedBass.Enc;
using Newtonsoft.Json;
using RestSharp;
using Serilog;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service for CD ripping and importing to Myriad
    /// </summary>
    public class CdRippingService
    {
        private readonly MyriadApiService _myriadApiService;
        private readonly AppSettings _settings;
        private readonly RestClient _resClient;
        private bool _isInitialized = false;
        private int _driveIndex = 0;

        // Windows API for CD ejection
        [DllImport("winmm.dll")]
        private static extern int mciSendString(string command, string returnValue, int returnLength, IntPtr winHandle);

        public event EventHandler<CdRippingProgressEventArgs>? ProgressChanged;
        public event EventHandler<string>? StatusChanged;

        public CdRippingService(MyriadApiService myriadApiService, AppSettings settings)
        {
            _myriadApiService = myriadApiService ?? throw new ArgumentNullException(nameof(myriadApiService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            var resOptions = new RestClientOptions
            {
                BaseUrl = new Uri(settings.RESApiUrl.TrimEnd('/'))
            };
            _resClient = new RestClient(resOptions);
        }

        /// <summary>
        /// Initializes the CD drive and Bass library
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // Initialize Bass
                if (!Bass.Init())
                {
                    Log.Error("Failed to initialize Bass library: {Error}", Bass.LastError);
                    return false;
                }

                // Check for CD drives - use a simple approach
                _isInitialized = true;
                Log.Information("CD ripping service initialized");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error initializing CD ripping service");
                return false;
            }
        }

        /// <summary>
        /// Checks if a CD is inserted in the drive
        /// </summary>
        public bool IsCdInserted()
        {
            if (!_isInitialized)
                return false;

            try
            {
                // Try to create a CD stream to test if CD is ready
                // Handle potential issues with CD detection
                int testStream = 0;
                try
                {
                    testStream = BassCd.CreateStream(_driveIndex, 1, BassFlags.Decode);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error creating test CD stream, CD may not be inserted");
                    return false;
                }

                if (testStream != 0)
                {
                    Bass.StreamFree(testStream);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error checking CD status");
                return false;
            }
        }

        /// <summary>
        /// Gets CD information including track count and total duration
        /// </summary>
        public CdInfo? GetCdInfo()
        {
            if (!_isInitialized || !IsCdInserted())
                return null;

            try
            {
                var cdInfo = new CdInfo
                {
                    TrackCount = 0,
                    TotalLengthSeconds = 0,
                    Tracks = new List<CdTrackInfo>()
                };

                // Try to get track information by testing each track
                for (int track = 1; track <= 99; track++) // Test up to 99 tracks
                {
                    int trackStream = 0;
                    try
                    {
                        trackStream = BassCd.CreateStream(_driveIndex, track, BassFlags.Decode);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Error creating stream for track {Track}", track);
                        break; // Stop trying further tracks
                    }

                    if (trackStream == 0)
                        break; // No more tracks

                    long trackLength = Bass.ChannelGetLength(trackStream);
                    double trackSeconds = Bass.ChannelBytes2Seconds(trackStream, trackLength);
                    Bass.StreamFree(trackStream);

                    if (trackLength > 0 && trackSeconds > 0)
                    {
                        var trackInfo = new CdTrackInfo
                        {
                            TrackNumber = track,
                            LengthSeconds = trackSeconds,
                            StartFrame = 0 // We'll calculate this later if needed
                        };
                        cdInfo.Tracks.Add(trackInfo);
                        cdInfo.TrackCount++;
                        cdInfo.TotalLengthSeconds += trackInfo.LengthSeconds;
                    }
                }

                return cdInfo.TrackCount > 0 ? cdInfo : null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting CD information");
                return null;
            }
        }

        /// <summary>
        /// Rips all tracks from the CD and imports them to Myriad
        /// </summary>
        public async Task<bool> RipAndImportCdAsync(string tempDirectory, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized || !IsCdInserted())
                return false;

            var cdInfo = GetCdInfo();
            if (cdInfo == null)
                return false;

            OnStatusChanged("Starting CD rip...");
            
            try
            {
                // Create temporary directory
                if (!Directory.Exists(tempDirectory))
                    Directory.CreateDirectory(tempDirectory);

                var successfulImports = 0;
                var totalTracks = cdInfo.TrackCount;

                // Rip each track
                for (int track = 1; track <= totalTracks; track++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    OnStatusChanged($"Ripping track {track} of {totalTracks}...");
                    OnProgressChanged(new CdRippingProgressEventArgs(track, totalTracks, 0, "Ripping"));

                    var wavFilePath = Path.Combine(tempDirectory, $"Track_{track:D2}.wav");
                    
                    if (await RipTrackToWavAsync(track, wavFilePath, cancellationToken))
                    {
                        OnStatusChanged($"Importing track {track} to Myriad...");
                        OnProgressChanged(new CdRippingProgressEventArgs(track, totalTracks, 50, "Importing"));

                        if (await ImportTrackToMyriadAsync(wavFilePath, track, cancellationToken))
                        {
                            successfulImports++;
                            OnProgressChanged(new CdRippingProgressEventArgs(track, totalTracks, 100, "Completed"));
                        }
                        else
                        {
                            OnProgressChanged(new CdRippingProgressEventArgs(track, totalTracks, 100, "Import Failed"));
                        }

                        // Clean up temporary file
                        try
                        {
                            File.Delete(wavFilePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to delete temporary file: {FilePath}", wavFilePath);
                        }
                    }
                    else
                    {
                        OnProgressChanged(new CdRippingProgressEventArgs(track, totalTracks, 100, "Rip Failed"));
                    }
                }

                OnStatusChanged($"CD ripping completed. {successfulImports} of {totalTracks} tracks imported successfully.");
                return successfulImports > 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during CD ripping process");
                OnStatusChanged($"CD ripping failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Rips a single track to WAV format
        /// </summary>
        private async Task<bool> RipTrackToWavAsync(int trackNumber, string outputPath, CancellationToken cancellationToken)
        {
            try
            {
                // Create CD stream for the track
                int cdStream = BassCd.CreateStream(_driveIndex, trackNumber, BassFlags.Decode);
                if (cdStream == 0)
                {
                    Log.Error("Failed to create CD stream for track {Track}: {Error}", trackNumber, Bass.LastError);
                    return false;
                }

                // Get track length for progress reporting
                long trackLength = Bass.ChannelGetLength(cdStream);
                var trackInfo = BassCd.GetTrackLength(_driveIndex, trackNumber);
                
                // Create WAV encoder
                int encoder = BassEnc.EncodeStart(cdStream, outputPath, EncodeFlags.AutoFree, null);
                if (encoder == 0)
                {
                    Log.Error("Failed to start WAV encoding for track {Track}: {Error}", trackNumber, Bass.LastError);
                    Bass.StreamFree(cdStream);
                    return false;
                }

                // Process the stream in chunks
                var buffer = new byte[65536]; // 64KB buffer
                long totalBytesRead = 0;

                await Task.Run(() =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int bytesRead = Bass.ChannelGetData(cdStream, buffer, buffer.Length);
                        if (bytesRead <= 0)
                            break;

                        totalBytesRead += bytesRead;
                        
                        // Report progress if we have length information
                        if (trackLength > 0)
                        {
                            double progressPercent = (double)totalBytesRead / trackLength * 100;
                            OnProgressChanged(new CdRippingProgressEventArgs(trackNumber, 0, (int)Math.Min(progressPercent, 99), "Ripping"));
                        }
                    }
                }, cancellationToken);

                BassEnc.EncodeStop(encoder);
                Bass.StreamFree(cdStream);

                if (cancellationToken.IsCancellationRequested)
                {
                    try { File.Delete(outputPath); } catch { }
                    return false;
                }

                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error ripping track {Track} to {OutputPath}", trackNumber, outputPath);
                return false;
            }
        }

        /// <summary>
        /// Imports a track file to Myriad using the RES API
        /// </summary>
        private async Task<bool> ImportTrackToMyriadAsync(string filePath, int trackNumber, CancellationToken cancellationToken)
        {
            try
            {
                var importParams = new
                {
                    SourceType = "Files",
                    FileSourcePath = filePath,
                    DestinationMediaItemType = "Song",
                    OverwriteDestination = false,
                    DestinationMediaRangeType = "UseDatabaseRanges",
                    SourceFileSelectionType = "SpecificFileOnly",
                    SpecificSourceFilename = Path.GetFileName(filePath),
                    PostImportAction = "None"
                };

                var request = new RestRequest("/api/Media/ImportMediaFile", Method.Post);
                request.AddHeader("X-API-Key", _settings.RESWriteKey);
                request.AddHeader("Content-Type", "application/json");
                request.AddJsonBody(importParams);

                var response = await _resClient.ExecuteAsync(request, cancellationToken);
                
                if (response.IsSuccessful)
                {
                    Log.Information("Successfully imported track {Track} from {FilePath}", trackNumber, filePath);
                    return true;
                }
                else
                {
                    Log.Error("Failed to import track {Track}: {StatusCode} - {Content}", trackNumber, response.StatusCode, response.Content);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error importing track {Track} from {FilePath}", trackNumber, filePath);
                return false;
            }
        }

        /// <summary>
        /// Ejects the CD from the drive
        /// </summary>
        public bool EjectCd()
        {
            if (!_isInitialized)
                return false;

            try
            {
                // Use Windows MCI command to eject CD
                int result = mciSendString("set cdaudio door open", "", 0, IntPtr.Zero);
                if (result == 0)
                {
                    Log.Information("CD ejected successfully");
                    return true;
                }
                else
                {
                    Log.Warning("Failed to eject CD using MCI command, result: {Result}", result);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error ejecting CD");
                return false;
            }
        }

        /// <summary>
        /// Releases resources
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_isInitialized)
                {
                    Bass.Free();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error disposing CD ripping service");
            }
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        private void OnProgressChanged(CdRippingProgressEventArgs args)
        {
            ProgressChanged?.Invoke(this, args);
        }
    }

    /// <summary>
    /// Information about a CD
    /// </summary>
    public class CdInfo
    {
        public int TrackCount { get; set; }
        public double TotalLengthSeconds { get; set; }
        public List<CdTrackInfo> Tracks { get; set; } = new List<CdTrackInfo>();
    }

    /// <summary>
    /// Information about a CD track
    /// </summary>
    public class CdTrackInfo
    {
        public int TrackNumber { get; set; }
        public double LengthSeconds { get; set; }
        public int StartFrame { get; set; }
    }

    /// <summary>
    /// Event arguments for CD ripping progress
    /// </summary>
    public class CdRippingProgressEventArgs : EventArgs
    {
        public int CurrentTrack { get; }
        public int TotalTracks { get; }
        public int ProgressPercent { get; }
        public string Status { get; }

        public CdRippingProgressEventArgs(int currentTrack, int totalTracks, int progressPercent, string status)
        {
            CurrentTrack = currentTrack;
            TotalTracks = totalTracks;
            ProgressPercent = progressPercent;
            Status = status;
        }
    }
}
