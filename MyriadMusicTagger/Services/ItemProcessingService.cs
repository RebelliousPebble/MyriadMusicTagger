using MyriadMusicTagger.Core;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Serilog;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service responsible for processing individual items through fingerprinting and metadata lookup
    /// </summary>
    public class ItemProcessingService
    {
        private readonly MyriadApiService _apiService;

        public ItemProcessingService(MyriadApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        }

        /// <summary>
        /// Processes a single item by reading from API, fingerprinting, and finding matches
        /// </summary>
        /// <param name="itemNumber">Item number to process</param>
        /// <returns>Processing result with matches and metadata</returns>
        public ItemProcessingResult ProcessItem(int itemNumber)
        {
            try
            {
                // Read item from API
                var itemResult = _apiService.ReadItem(itemNumber);
                if (itemResult == null)
                {
                    return new ItemProcessingResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Failed to read item from API",
                        ItemNumber = itemNumber
                    };
                }

                // Validate media file exists
                var pathToFile = itemResult.MediaLocation;
                if (string.IsNullOrEmpty(pathToFile) || !File.Exists(pathToFile))
                {
                    return new ItemProcessingResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Media file not found or path is invalid: {pathToFile}",
                        ItemNumber = itemNumber,
                        ItemResult = itemResult
                    };
                }

                // Fingerprint the file
                List<ProcessingUtils.FingerprintMatch> matches;
                try
                {
                    matches = ProcessingUtils.Fingerprint(pathToFile);
                }
                catch (ProcessingUtils.ProcessingException pex)
                {
                    return new ItemProcessingResult
                    {
                        IsSuccess = false,
                        ErrorMessage = pex.Message,
                        ItemNumber = itemNumber,
                        ItemResult = itemResult
                    };
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unexpected error in Fingerprint call for {PathToFile}", pathToFile);
                    return new ItemProcessingResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"An unexpected error occurred during fingerprinting: {ex.Message}",
                        ItemNumber = itemNumber,
                        ItemResult = itemResult
                    };
                }

                if (matches.Count == 0)
                {
                    return new ItemProcessingResult
                    {
                        IsSuccess = true,
                        ItemNumber = itemNumber,
                        ItemResult = itemResult,
                        Matches = new List<ProcessingUtils.FingerprintMatch>(),
                        HasMatches = false
                    };
                }

                // Score the matches
                var (parsedArtist, parsedTitle) = FingerprintMatchScorer.ParseArtistAndTitle(
                    itemResult.Title ?? "", itemResult.Copyright?.Performer ?? "");
                var scoredMatches = FingerprintMatchScorer.ScoreMatches(matches, parsedTitle, parsedArtist);

                return new ItemProcessingResult
                {
                    IsSuccess = true,
                    ItemNumber = itemNumber,
                    ItemResult = itemResult,
                    Matches = scoredMatches.Select(m => m.Match).ToList(),
                    ScoredMatches = scoredMatches,
                    HasMatches = true,
                    BestMatch = scoredMatches.FirstOrDefault().Match,
                    BestMatchScore = scoredMatches.FirstOrDefault().Score
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing item {ItemNumber}", itemNumber);
                return new ItemProcessingResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Processing error: {ex.Message}",
                    ItemNumber = itemNumber
                };
            }
        }

        /// <summary>
        /// Saves metadata changes to the Myriad system
        /// </summary>
        /// <param name="itemNumber">Item number to update</param>
        /// <param name="recordingInfo">Recording information to save</param>
        /// <returns>True if successful</returns>
        public bool SaveMetadataChanges(int itemNumber, IRecording recordingInfo)
        {
            try
            {
                var titleUpdate = new MyriadTitleSchema
                {
                    ItemTitle = recordingInfo.Title ?? string.Empty,
                    Artists = recordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()
                };

                return _apiService.UpdateItemMetadata(itemNumber, titleUpdate);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving metadata for item {ItemNumber}", itemNumber);
                return false;
            }
        }

        /// <summary>
        /// Creates a batch item from API result
        /// </summary>
        public BatchProcessItem CreateBatchItem(int itemNumber, Result response)
        {
            return new BatchProcessItem
            {
                ItemNumber = itemNumber,
                OldTitle = response.Title ?? string.Empty,
                OldArtist = response.Copyright?.Performer ?? string.Empty,
                MediaLocation = response.MediaLocation ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Result of processing a single item
    /// </summary>
    public class ItemProcessingResult
    {
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int ItemNumber { get; set; }
        public Result? ItemResult { get; set; }
        public List<ProcessingUtils.FingerprintMatch> Matches { get; set; } = new List<ProcessingUtils.FingerprintMatch>();
        public List<(ProcessingUtils.FingerprintMatch Match, double Score)> ScoredMatches { get; set; } = new List<(ProcessingUtils.FingerprintMatch, double)>();
        public bool HasMatches { get; set; }
        public ProcessingUtils.FingerprintMatch? BestMatch { get; set; }
        public double BestMatchScore { get; set; }
    }
}
