using MyriadMusicTagger.Core;
using MyriadMusicTagger.Utils;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Main service for coordinating audio quality analysis across the Myriad database
    /// Orchestrates database search, audio processing, and report generation
    /// </summary>
    public class AudioQualityAnalysisService
    {
        private readonly MyriadDatabaseSearcher _databaseSearcher;
        private readonly AppSettings _settings;
        private readonly QualityAnalysisSettings _analysisSettings;

        public event EventHandler<QualityAnalysisProgress>? ProgressChanged;
        public event EventHandler<string>? StatusChanged;

        public AudioQualityAnalysisService(AppSettings settings, QualityAnalysisSettings? analysisSettings = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _databaseSearcher = new MyriadDatabaseSearcher(settings);
            _analysisSettings = analysisSettings ?? new QualityAnalysisSettings();
        }

        /// <summary>
        /// Performs comprehensive audio quality analysis on all songs in the database
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation</param>
        /// <returns>Complete quality analysis report</returns>
        public async Task<QualityReport> AnalyzeAllTracksAsync(CancellationToken cancellationToken = default)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var report = new QualityReport();

            try
            {
                Log.Information("Starting comprehensive audio quality analysis");
                OnStatusChanged("Retrieving track list from database...");

                // Phase 1: Get all tracks from database
                var basicTracks = await GetTracksToAnalyzeAsync(cancellationToken);
                report.TotalTracksAnalyzed = basicTracks.Count;

                if (basicTracks.Count == 0)
                {
                    Log.Warning("No tracks found for analysis");
                    OnStatusChanged("No tracks found for analysis");
                    return report;
                }

                Log.Information("Found {Count} tracks for quality analysis", basicTracks.Count);
                OnStatusChanged($"Found {basicTracks.Count} tracks. Getting detailed information...");

                // Phase 2: Get detailed information (including file paths)
                var detailedTracks = await GetDetailedTrackInfoAsync(basicTracks, cancellationToken);
                var validTracks = detailedTracks.Where(t => !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)).ToList();

                Log.Information("Found {Count} tracks with valid file paths out of {Total}", validTracks.Count, detailedTracks.Count);
                report.TotalTracksAnalyzed = validTracks.Count;

                if (validTracks.Count == 0)
                {
                    report.FailedAnalyses = basicTracks.Count;
                    OnStatusChanged("No tracks with valid file paths found");
                    return report;
                }

                OnStatusChanged($"Analyzing audio quality of {validTracks.Count} tracks...");

                // Phase 3: Perform audio quality analysis
                var analysisResults = await AnalyzeTracksAsync(validTracks, cancellationToken);

                // Phase 4: Generate report
                report = GenerateQualityReport(analysisResults, overallStopwatch.Elapsed);
                
                Log.Information("Audio quality analysis completed: {Successful}/{Total} tracks analyzed successfully", 
                    report.SuccessfulAnalyses, report.TotalTracksAnalyzed);
                OnStatusChanged($"Analysis complete: {report.SuccessfulAnalyses}/{report.TotalTracksAnalyzed} tracks processed");

                return report;
            }
            catch (OperationCanceledException)
            {
                Log.Information("Audio quality analysis was cancelled");
                OnStatusChanged("Analysis cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during audio quality analysis");
                OnStatusChanged($"Analysis failed: {ex.Message}");
                throw;
            }
            finally
            {
                overallStopwatch.Stop();
            }
        }

        /// <summary>
        /// Analyzes specific tracks by Media ID
        /// </summary>
        /// <param name="mediaIds">List of media IDs to analyze</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Quality analysis report</returns>
        public async Task<QualityReport> AnalyzeSpecificTracksAsync(List<int> mediaIds, CancellationToken cancellationToken = default)
        {
            var overallStopwatch = Stopwatch.StartNew();

            try
            {
                Log.Information("Starting audio quality analysis for {Count} specific tracks", mediaIds.Count);
                OnStatusChanged($"Getting detailed information for {mediaIds.Count} tracks...");

                // Get detailed information for specified tracks
                var detailedTracks = await GetDetailedTracksInfo(mediaIds, cancellationToken);
                
                // Validate and filter tracks
                var validTracks = ValidateAndFilterTracks(detailedTracks);
                
                if (validTracks.Count == 0)
                {
                    OnStatusChanged("No tracks with valid file paths found");
                    return new QualityReport { TotalTracksAnalyzed = mediaIds.Count, FailedAnalyses = mediaIds.Count };
                }

                OnStatusChanged($"Analyzing audio quality of {validTracks.Count} tracks...");

                // Perform analysis
                var analysisResults = await AnalyzeTracksAsync(validTracks, cancellationToken, 0.2f); // Start from 20% progress

                // Generate report
                var report = GenerateQualityReport(analysisResults, overallStopwatch.Elapsed);
                
                Log.Information("Specific track analysis completed: {Successful}/{Total} tracks analyzed", 
                    report.SuccessfulAnalyses, report.TotalTracksAnalyzed);

                return report;
            }
            catch (OperationCanceledException)
            {
                Log.Information("Specific track analysis was cancelled");
                throw;
            }
            finally
            {
                overallStopwatch.Stop();
            }
        }

        /// <summary>
        /// Gets detailed track information for the specified media IDs
        /// </summary>
        private async Task<List<DetailedMediaItem>> GetDetailedTracksInfo(List<int> mediaIds, CancellationToken cancellationToken)
        {
            var detailedTracks = await _databaseSearcher.GetDetailedMediaItemsBatchAsync(mediaIds, 
                progress => OnProgressChanged(new QualityAnalysisProgress 
                { 
                    CurrentPhase = "Retrieving track information"
                }),
                cancellationToken);

            Log.Information("Retrieved {Count} detailed tracks, analyzing file paths...", detailedTracks.Count);
            return detailedTracks;
        }

        /// <summary>
        /// Validates tracks and filters those with valid file paths
        /// </summary>
        private List<DetailedMediaItem> ValidateAndFilterTracks(List<DetailedMediaItem> detailedTracks)
        {
            // Debug file path issues
            var emptyPaths = detailedTracks.Count(t => string.IsNullOrEmpty(t.FilePath));
            var nonExistentPaths = detailedTracks.Count(t => !string.IsNullOrEmpty(t.FilePath) && !File.Exists(t.FilePath));
            var validPaths = detailedTracks.Count(t => !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath));

            Log.Information("File path analysis: {EmptyPaths} empty paths, {NonExistent} non-existent files, {Valid} valid files", 
                emptyPaths, nonExistentPaths, validPaths);

            LogFilePathIssues(detailedTracks, emptyPaths, nonExistentPaths);

            return detailedTracks.Where(t => !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)).ToList();
        }

        /// <summary>
        /// Logs file path issues for debugging purposes
        /// </summary>
        private static void LogFilePathIssues(List<DetailedMediaItem> detailedTracks, int emptyPaths, int nonExistentPaths)
        {
            if (emptyPaths > 0)
            {
                var sampleEmptyPath = detailedTracks.Where(t => string.IsNullOrEmpty(t.FilePath)).Take(3).ToList();
                Log.Warning("Sample tracks with empty file paths:");
                foreach (var track in sampleEmptyPath)
                {
                    Log.Warning("   - ID {MediaId}: '{Title}' by '{Artist}' - FilePath: '{FilePath}'", 
                        track.MediaId, track.Title, track.Artist, track.FilePath ?? "<null>");
                }
            }

            if (nonExistentPaths > 0)
            {
                var sampleNonExistent = detailedTracks.Where(t => !string.IsNullOrEmpty(t.FilePath) && !File.Exists(t.FilePath)).Take(3).ToList();
                Log.Warning("Sample tracks with non-existent file paths:");
                foreach (var track in sampleNonExistent)
                {
                    Log.Warning("   - ID {MediaId}: '{Title}' by '{Artist}' - FilePath: '{FilePath}'", 
                        track.MediaId, track.Title, track.Artist, track.FilePath);
                }
                
                LogHelpfulGuidance(nonExistentPaths, detailedTracks);
            }
        }

        /// <summary>
        /// Logs helpful guidance for file path issues
        /// </summary>
        private static void LogHelpfulGuidance(int nonExistentPaths, List<DetailedMediaItem> detailedTracks)
        {
            if (nonExistentPaths > 0)
            {
                Log.Warning("No valid file paths found. This might be due to:");
                Log.Warning("   - Network drives not mounted (e.g., \\\\server\\share paths)");
                Log.Warning("   - Files moved after import");
                Log.Warning("   - Different drive mappings");
                Log.Warning("   - Relative paths missing base directory");
                
                // Show some examples of the paths we're seeing
                var pathExamples = detailedTracks.Where(t => !string.IsNullOrEmpty(t.FilePath))
                    .Take(5)
                    .Select(t => t.FilePath)
                    .ToList();
                
                if (pathExamples.Any())
                {
                    Log.Warning("Example file paths from your database:");
                    foreach (var path in pathExamples)
                    {
                        Log.Warning("   - {Path}", path);
                    }
                }
            }
        }

        /// <summary>
        /// Gets tracks to analyze based on settings
        /// </summary>
        private async Task<List<BasicMediaItem>> GetTracksToAnalyzeAsync(CancellationToken cancellationToken)
        {
            if (_analysisSettings.AnalyzeAllTracks)
            {
                return await _databaseSearcher.GetAllSongsBasicAsync(progress => 
                    OnProgressChanged(new QualityAnalysisProgress 
                    { 
                        CurrentPhase = "Retrieving track list"
                    }),
                    cancellationToken);
            }
            else if (_analysisSettings.SpecificMediaIds.Any())
            {
                var detailedTracks = await _databaseSearcher.GetDetailedMediaItemsBatchAsync(_analysisSettings.SpecificMediaIds, null, cancellationToken);
                return detailedTracks.Select(t => new BasicMediaItem
                {
                    MediaId = t.MediaId,
                    Title = t.Title,
                    Artist = t.Artist,
                    Duration = t.Duration,
                    Categories = t.Categories
                }).ToList();
            }
            else
            {
                // Filter by categories if specified
                var allTracks = await _databaseSearcher.GetAllSongsBasicAsync(null, cancellationToken);
                
                if (_analysisSettings.IncludeCategories.Any())
                {
                    allTracks = allTracks.Where(t => t.Categories.Any(c => _analysisSettings.IncludeCategories.Contains(c))).ToList();
                }
                
                if (_analysisSettings.ExcludeCategories.Any())
                {
                    allTracks = allTracks.Where(t => !t.Categories.Any(c => _analysisSettings.ExcludeCategories.Contains(c))).ToList();
                }
                
                return allTracks;
            }
        }

        /// <summary>
        /// Gets detailed track information including file paths
        /// </summary>
        private async Task<List<DetailedMediaItem>> GetDetailedTrackInfoAsync(List<BasicMediaItem> basicTracks, CancellationToken cancellationToken)
        {
            var mediaIds = basicTracks.Select(t => t.MediaId).ToList();
            
            return await _databaseSearcher.GetDetailedMediaItemsBatchAsync(mediaIds, progress =>
                OnProgressChanged(new QualityAnalysisProgress
                {
                    CurrentPhase = "Getting detailed track information"
                }),
                cancellationToken);
        }

        /// <summary>
        /// Performs audio quality analysis on the specified tracks
        /// </summary>
        private async Task<List<AudioQualityResult>> AnalyzeTracksAsync(List<DetailedMediaItem> tracks, CancellationToken cancellationToken, float progressOffset = 0.5f)
        {
            var results = new ConcurrentBag<AudioQualityResult>();
            var processedCount = 0;
            var totalTracks = tracks.Count;
            var analysisStopwatch = Stopwatch.StartNew();

            Log.Information("Starting audio analysis of {Count} tracks with {MaxConcurrency} concurrent processors", 
                totalTracks, _analysisSettings.MaxConcurrentAnalyses);

            using var processor = new AudioQualityProcessor(_analysisSettings);
            using var semaphore = new SemaphoreSlim(_analysisSettings.MaxConcurrentAnalyses);

            var analysisProgress = new QualityAnalysisProgress
            {
                TotalTracks = totalTracks,
                CurrentPhase = "Analyzing audio quality"
            };

            var analysisTasks = tracks.Select(async track =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var trackStopwatch = Stopwatch.StartNew();
                    
                    analysisProgress.CurrentTrack = $"{track.Artist} - {track.Title}";
                    OnProgressChanged(analysisProgress);

                    Log.Debug("Analyzing track: {Artist} - {Title} ({FilePath})", track.Artist, track.Title, track.FilePath);

                    var result = await processor.AnalyzeAudioQualityAsync(track.FilePath, track.MediaId, track.Title, track.Artist);
                    results.Add(result);

                    if (result.ProcessingSuccessful)
                    {
                        Interlocked.Increment(ref analysisProgress.SuccessfulAnalysesRef);
                        Log.Debug("Successfully analyzed {Artist} - {Title}, score: {Score:F1}", 
                            track.Artist, track.Title, result.OverallQualityScore);
                    }
                    else
                    {
                        Interlocked.Increment(ref analysisProgress.FailedAnalysesRef);
                        Log.Warning("Failed to analyze {Artist} - {Title}: {Error}", 
                            track.Artist, track.Title, result.ErrorMessage);
                    }

                    var completed = Interlocked.Increment(ref processedCount);
                    analysisProgress.ProcessedTracks = completed;
                    analysisProgress.ElapsedTime = analysisStopwatch.Elapsed;
                    
                    if (completed > 0)
                    {
                        analysisProgress.TracksPerSecond = completed / (float)analysisStopwatch.Elapsed.TotalSeconds;
                        var remainingTracks = totalTracks - completed;
                        analysisProgress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingTracks / analysisProgress.TracksPerSecond);
                    }

                    // Note: OverallProgress is calculated automatically based on ProcessedTracks/TotalTracks
                    OnProgressChanged(analysisProgress);

                    trackStopwatch.Stop();
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("Analysis cancelled for track: {Artist} - {Title}", track.Artist, track.Title);
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error analyzing track: {Artist} - {Title} ({FilePath})", track.Artist, track.Title, track.FilePath);
                    
                    results.Add(new AudioQualityResult
                    {
                        MediaId = track.MediaId,
                        Title = track.Title,
                        Artist = track.Artist,
                        FilePath = track.FilePath,
                        ProcessingSuccessful = false,
                        ErrorMessage = ex.Message
                    });

                    Interlocked.Increment(ref analysisProgress.FailedAnalysesRef);
                    var completed = Interlocked.Increment(ref processedCount);
                    analysisProgress.ProcessedTracks = completed;
                    OnProgressChanged(analysisProgress);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(analysisTasks);

            Log.Information("Audio analysis phase completed: {Successful} successful, {Failed} failed", 
                analysisProgress.SuccessfulAnalyses, analysisProgress.FailedAnalyses);

            return results.ToList();
        }

        /// <summary>
        /// Generates a comprehensive quality report from analysis results
        /// </summary>
        private QualityReport GenerateQualityReport(List<AudioQualityResult> results, TimeSpan totalProcessingTime)
        {
            var report = new QualityReport
            {
                TotalTracksAnalyzed = results.Count,
                SuccessfulAnalyses = results.Count(r => r.ProcessingSuccessful),
                FailedAnalyses = results.Count(r => !r.ProcessingSuccessful),
                TotalProcessingTime = totalProcessingTime,
                Results = results
            };

            var successfulResults = results.Where(r => r.ProcessingSuccessful).ToList();

            if (successfulResults.Any())
            {
                // Calculate summary statistics
                var qualityScores = successfulResults.Select(r => r.OverallQualityScore).OrderBy(s => s).ToList();
                report.AverageQualityScore = qualityScores.Average();
                if (qualityScores.Count % 2 == 1)
                {
                    report.MedianQualityScore = qualityScores[qualityScores.Count / 2];
                }
                else if (qualityScores.Count > 0)
                {
                    var midHigh = qualityScores.Count / 2;
                    var midLow = midHigh - 1;
                    report.MedianQualityScore = (qualityScores[midLow] + qualityScores[midHigh]) / 2f;
                }

                // Count tracks needing re-rip
                report.TracksNeedingReRip = successfulResults.Count(r => r.RecommendReRip);
                report.PercentageNeedingReRip = (float)report.TracksNeedingReRip / successfulResults.Count * 100;

                // Quality distribution
                report.QualityDistribution = new Dictionary<string, int>
                {
                    { "Excellent (90-100)", successfulResults.Count(r => r.OverallQualityScore >= 90) },
                    { "Good (75-89)", successfulResults.Count(r => r.OverallQualityScore >= 75 && r.OverallQualityScore < 90) },
                    { "Fair (60-74)", successfulResults.Count(r => r.OverallQualityScore >= 60 && r.OverallQualityScore < 75) },
                    { "Poor (40-59)", successfulResults.Count(r => r.OverallQualityScore >= 40 && r.OverallQualityScore < 60) },
                    { "Very Poor (0-39)", successfulResults.Count(r => r.OverallQualityScore < 40) }
                };

                // Common issues
                var allIssues = successfulResults.SelectMany(r => r.QualityIssues).ToList();
                report.CommonIssues = allIssues
                    .GroupBy(issue => issue)
                    .ToDictionary(g => g.Key, g => g.Count())
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(10)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            Log.Information("Quality report generated: Avg={0:F1}, Median={1:F1}, ReRip={2:F1}%", 
                report.AverageQualityScore, report.MedianQualityScore, report.PercentageNeedingReRip);

            return report;
        }

        /// <summary>
        /// Exports quality report to CSV file
        /// </summary>
        /// <param name="report">The quality report to export</param>
        /// <param name="filePath">Path where to save the CSV file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ExportReportToCsvAsync(QualityReport report, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                OnStatusChanged($"Exporting report to {filePath}...");
                
                var csvExporter = new AudioQualityCsvExporter();
                await csvExporter.ExportToCsvAsync(report, filePath, cancellationToken);
                
                Log.Information("Quality report exported to CSV: {FilePath}", filePath);
                OnStatusChanged($"Report exported successfully to {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting quality report to CSV: {FilePath}", filePath);
                OnStatusChanged($"Export failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets a summary of tracks that need re-ripping
        /// </summary>
        /// <param name="report">The quality report</param>
        /// <returns>List of tracks recommended for re-ripping</returns>
        public List<AudioQualityResult> GetTracksNeedingReRip(QualityReport report)
        {
            return report.Results
                .Where(r => r.ProcessingSuccessful && r.RecommendReRip)
                .OrderBy(r => r.OverallQualityScore)
                .ToList();
        }

        /// <summary>
        /// Gets tracks with specific quality issues
        /// </summary>
        /// <param name="report">The quality report</param>
        /// <param name="issueType">Type of issue to filter by</param>
        /// <returns>List of tracks with the specified issue</returns>
        public List<AudioQualityResult> GetTracksWithIssue(QualityReport report, string issueType)
        {
            return report.Results
                .Where(r => r.ProcessingSuccessful && r.QualityIssues.Any(issue => issue.Contains(issueType, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.OverallQualityScore)
                .ToList();
        }

        private void OnProgressChanged(QualityAnalysisProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }
    }
}
