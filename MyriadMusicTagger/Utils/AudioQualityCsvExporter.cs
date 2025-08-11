using CsvHelper;
using CsvHelper.Configuration;
using MyriadMusicTagger.Core;
using Serilog;
using System.Globalization;
using System.Text;

namespace MyriadMusicTagger.Utils
{
    /// <summary>
    /// Utility for exporting audio quality analysis results to CSV format
    /// Provides structured data export suitable for external re-sourcing workflows
    /// </summary>
    public class AudioQualityCsvExporter
    {
        /// <summary>
        /// Exports a quality report to CSV file
        /// </summary>
        /// <param name="report">The quality report to export</param>
        /// <param name="filePath">Path where to save the CSV file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ExportToCsvAsync(QualityReport report, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                Log.Information("Exporting quality report to CSV: {FilePath}", filePath);

                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Encoding = Encoding.UTF8,
                    HasHeaderRecord = true
                };

                using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
                using var csv = new CsvWriter(writer, config);

                // Register the class map for proper CSV formatting
                csv.Context.RegisterClassMap<AudioQualityResultCsvMap>();

                // Filter results based on report settings
                var resultsToExport = FilterResultsForExport(report);

                Log.Debug("Exporting {Count} results out of {Total} total results", 
                    resultsToExport.Count, report.Results.Count);

                // Write CSV data
                await csv.WriteRecordsAsync(resultsToExport.Select(r => new AudioQualityResultCsv(r)), cancellationToken);

                Log.Information("Successfully exported {Count} quality analysis results to {FilePath}", 
                    resultsToExport.Count, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting quality report to CSV: {FilePath}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Exports only tracks that need re-ripping to CSV
        /// </summary>
        /// <param name="report">The quality report</param>
        /// <param name="filePath">Path where to save the CSV file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ExportReRipListToCsvAsync(QualityReport report, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                Log.Information("Exporting re-rip list to CSV: {FilePath}", filePath);

                var reRipTracks = report.Results
                    .Where(r => r.ProcessingSuccessful && r.RecommendReRip)
                    .OrderBy(r => r.OverallQualityScore)
                    .ToList();

                var tempReport = new QualityReport
                {
                    Results = reRipTracks,
                    IncludeOnlyProblematic = true
                };

                await ExportToCsvAsync(tempReport, filePath, cancellationToken);

                Log.Information("Successfully exported {Count} tracks needing re-rip to {FilePath}", 
                    reRipTracks.Count, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting re-rip list to CSV: {FilePath}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Exports summary statistics to CSV
        /// </summary>
        /// <param name="report">The quality report</param>
        /// <param name="filePath">Path where to save the CSV file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ExportSummaryToCsvAsync(QualityReport report, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                Log.Information("Exporting quality summary to CSV: {FilePath}", filePath);

                var summaryData = new List<QualitySummaryCsv>
                {
                    new() { Metric = "Total Tracks Analyzed", Value = report.TotalTracksAnalyzed.ToString() },
                    new() { Metric = "Successful Analyses", Value = report.SuccessfulAnalyses.ToString() },
                    new() { Metric = "Failed Analyses", Value = report.FailedAnalyses.ToString() },
                    new() { Metric = "Average Quality Score", Value = $"{report.AverageQualityScore:F1}" },
                    new() { Metric = "Median Quality Score", Value = $"{report.MedianQualityScore:F1}" },
                    new() { Metric = "Tracks Needing Re-rip", Value = report.TracksNeedingReRip.ToString() },
                    new() { Metric = "Percentage Needing Re-rip", Value = $"{report.PercentageNeedingReRip:F1}%" },
                    new() { Metric = "Total Processing Time", Value = report.TotalProcessingTime.ToString(@"hh\:mm\:ss") }
                };

                // Add quality distribution
                foreach (var kvp in report.QualityDistribution)
                {
                    summaryData.Add(new QualitySummaryCsv { Metric = kvp.Key, Value = kvp.Value.ToString() });
                }

                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Encoding = Encoding.UTF8,
                    HasHeaderRecord = true
                };

                using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
                using var csv = new CsvWriter(writer, config);

                await csv.WriteRecordsAsync(summaryData, cancellationToken);

                Log.Information("Successfully exported quality summary to {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting quality summary to CSV: {FilePath}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Filters results based on export settings
        /// </summary>
        private List<AudioQualityResult> FilterResultsForExport(QualityReport report)
        {
            var results = report.Results.Where(r => r.ProcessingSuccessful).ToList();

            if (report.IncludeOnlyProblematic)
            {
                results = results.Where(r => r.RecommendReRip).ToList();
            }

            if (report.MinimumScoreForExport > 0)
            {
                results = results.Where(r => r.OverallQualityScore >= report.MinimumScoreForExport).ToList();
            }

            return results.OrderBy(r => r.OverallQualityScore).ToList();
        }
    }

    /// <summary>
    /// CSV representation of audio quality result
    /// </summary>
    public class AudioQualityResultCsv
    {
        public AudioQualityResultCsv() { }

        public AudioQualityResultCsv(AudioQualityResult result)
        {
            MediaId = result.MediaId;
            Title = result.Title;
            Artist = result.Artist;
            FilePath = result.FilePath;
            Duration = result.Duration;
            QualityScore = Math.Round(result.OverallQualityScore, 1);
            SpectralScore = Math.Round(result.SpectralAnalysis.SpectralScore, 1);
            DynamicRangeScore = Math.Round(result.DynamicRange.DynamicRangeScore, 1);
            ClippingPenalty = Math.Round(result.ClippingAnalysis.ClippingPenalty, 1);
            NoiseFloorScore = Math.Round(result.NoiseFloor.NoiseFloorScore, 1);
            ChannelScore = Math.Round(result.ChannelQuality.ChannelScore, 1);
            RecommendReRip = result.RecommendReRip ? "Yes" : "No";
            
            // Spectral details
            FrequencyRolloffHz = Math.Round(result.SpectralAnalysis.FrequencyRolloffPoint, 0);
            HighFrequencyContent = Math.Round(result.SpectralAnalysis.HighFrequencyContent, 1);
            HasMp3Artifacts = result.SpectralAnalysis.HasMp3Artifacts ? "Yes" : "No";
            Mp3ArtifactConfidence = Math.Round(result.SpectralAnalysis.Mp3ArtifactConfidence * 100, 1);
            
            // Dynamic range details
            DynamicRangeDb = Math.Round(result.DynamicRange.DynamicRange, 1);
            PeakLevelDb = Math.Round(result.DynamicRange.PeakLevel, 1);
            RmsLevelDb = Math.Round(result.DynamicRange.RmsLevel, 1);
            IsOverCompressed = result.DynamicRange.IsOverCompressed ? "Yes" : "No";
            
            // Clipping details
            ClippingPercentage = Math.Round(result.ClippingAnalysis.ClippingPercentage, 3);
            ClippingEvents = result.ClippingAnalysis.ClippingEventsCount;
            HasSustainedClipping = result.ClippingAnalysis.HasSustainedClipping ? "Yes" : "No";
            
            // Noise floor details
            NoiseFloorDb = Math.Round(result.NoiseFloor.NoiseFloorLevel, 1);
            SignalToNoiseRatio = Math.Round(result.NoiseFloor.SignalToNoiseRatio, 1);
            HasTapeHiss = result.NoiseFloor.HasTapeHiss ? "Yes" : "No";
            HasHum = result.NoiseFloor.HasHum ? "Yes" : "No";
            
            // Channel details
            IsMono = result.ChannelQuality.IsMono ? "Yes" : "No";
            StereoWidth = Math.Round(result.ChannelQuality.StereoWidth, 2);
            ChannelCorrelation = Math.Round(result.ChannelQuality.ChannelCorrelation, 2);
            HasPhaseIssues = result.ChannelQuality.HasPhaseIssues ? "Yes" : "No";
            
            // Summary
            QualityIssues = string.Join("; ", result.QualityIssues);
            Notes = result.Notes;
            ProcessingTime = result.ProcessingTime.ToString(@"mm\:ss\.fff");
            AnalyzedDateTime = result.AnalyzedDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public int MediaId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Duration { get; set; } = "";
        public double QualityScore { get; set; }
        public double SpectralScore { get; set; }
        public double DynamicRangeScore { get; set; }
        public double ClippingPenalty { get; set; }
        public double NoiseFloorScore { get; set; }
        public double ChannelScore { get; set; }
        public string RecommendReRip { get; set; } = "";
        
        // Detailed metrics
        public double FrequencyRolloffHz { get; set; }
        public double HighFrequencyContent { get; set; }
        public string HasMp3Artifacts { get; set; } = "";
        public double Mp3ArtifactConfidence { get; set; }
        public double DynamicRangeDb { get; set; }
        public double PeakLevelDb { get; set; }
        public double RmsLevelDb { get; set; }
        public string IsOverCompressed { get; set; } = "";
        public double ClippingPercentage { get; set; }
        public int ClippingEvents { get; set; }
        public string HasSustainedClipping { get; set; } = "";
        public double NoiseFloorDb { get; set; }
        public double SignalToNoiseRatio { get; set; }
        public string HasTapeHiss { get; set; } = "";
        public string HasHum { get; set; } = "";
        public string IsMono { get; set; } = "";
        public double StereoWidth { get; set; }
        public double ChannelCorrelation { get; set; }
        public string HasPhaseIssues { get; set; } = "";
        public string QualityIssues { get; set; } = "";
        public string Notes { get; set; } = "";
        public string ProcessingTime { get; set; } = "";
        public string AnalyzedDateTime { get; set; } = "";
    }

    /// <summary>
    /// CSV class map for audio quality results
    /// </summary>
    public sealed class AudioQualityResultCsvMap : ClassMap<AudioQualityResultCsv>
    {
        public AudioQualityResultCsvMap()
        {
            Map(m => m.MediaId).Name("Media ID");
            Map(m => m.Title).Name("Title");
            Map(m => m.Artist).Name("Artist");
            Map(m => m.FilePath).Name("File Path");
            Map(m => m.Duration).Name("Duration");
            Map(m => m.QualityScore).Name("Overall Quality Score");
            Map(m => m.SpectralScore).Name("Spectral Score");
            Map(m => m.DynamicRangeScore).Name("Dynamic Range Score");
            Map(m => m.ClippingPenalty).Name("Clipping Penalty");
            Map(m => m.NoiseFloorScore).Name("Noise Floor Score");
            Map(m => m.ChannelScore).Name("Channel Score");
            Map(m => m.RecommendReRip).Name("Recommend Re-rip");
            
            Map(m => m.FrequencyRolloffHz).Name("Frequency Rolloff (Hz)");
            Map(m => m.HighFrequencyContent).Name("High Frequency Content (%)");
            Map(m => m.HasMp3Artifacts).Name("Has MP3 Artifacts");
            Map(m => m.Mp3ArtifactConfidence).Name("MP3 Artifact Confidence (%)");
            
            Map(m => m.DynamicRangeDb).Name("Dynamic Range (dB)");
            Map(m => m.PeakLevelDb).Name("Peak Level (dB)");
            Map(m => m.RmsLevelDb).Name("RMS Level (dB)");
            Map(m => m.IsOverCompressed).Name("Is Over Compressed");
            
            Map(m => m.ClippingPercentage).Name("Clipping Percentage");
            Map(m => m.ClippingEvents).Name("Clipping Events");
            Map(m => m.HasSustainedClipping).Name("Has Sustained Clipping");
            
            Map(m => m.NoiseFloorDb).Name("Noise Floor (dB)");
            Map(m => m.SignalToNoiseRatio).Name("Signal to Noise Ratio (dB)");
            Map(m => m.HasTapeHiss).Name("Has Tape Hiss");
            Map(m => m.HasHum).Name("Has Hum");
            
            Map(m => m.IsMono).Name("Is Mono");
            Map(m => m.StereoWidth).Name("Stereo Width");
            Map(m => m.ChannelCorrelation).Name("Channel Correlation");
            Map(m => m.HasPhaseIssues).Name("Has Phase Issues");
            
            Map(m => m.QualityIssues).Name("Quality Issues");
            Map(m => m.Notes).Name("Notes");
            Map(m => m.ProcessingTime).Name("Processing Time");
            Map(m => m.AnalyzedDateTime).Name("Analyzed Date/Time");
        }
    }

    /// <summary>
    /// CSV representation of summary statistics
    /// </summary>
    public class QualitySummaryCsv
    {
        public string Metric { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
