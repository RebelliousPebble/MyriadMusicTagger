using MyriadMusicTagger.Core;
using Serilog;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service for handling batch processing operations
    /// </summary>
    public class BatchProcessingService
    {
        private readonly ItemProcessingService _itemProcessingService;
        private readonly List<BatchProcessItem> _batchProcessItems = new();

        public BatchProcessingService(ItemProcessingService itemProcessingService)
        {
            _itemProcessingService = itemProcessingService ?? throw new ArgumentNullException(nameof(itemProcessingService));
        }

        /// <summary>
        /// Gets the current batch items
        /// </summary>
        public IReadOnlyList<BatchProcessItem> BatchItems => _batchProcessItems.AsReadOnly();

        /// <summary>
        /// Clears the current batch
        /// </summary>
        public void ClearBatch()
        {
            _batchProcessItems.Clear();
        }

        /// <summary>
        /// Processes a range of items
        /// </summary>
        /// <param name="startItem">Starting item number</param>
        /// <param name="endItem">Ending item number</param>
        /// <param name="progressCallback">Callback for progress updates</param>
        /// <returns>Batch processing statistics</returns>
        public BatchProcessingResult ProcessItemRange(int startItem, int endItem, Action<int, int, string>? progressCallback = null)
        {
            _batchProcessItems.Clear();
            var stats = new BatchProcessingResult();

            for (int itemNumber = startItem; itemNumber <= endItem; itemNumber++)
            {
                progressCallback?.Invoke(itemNumber, endItem - startItem + 1, $"Reading item {itemNumber}...");

                try
                {
                    var processingResult = _itemProcessingService.ProcessItem(itemNumber);
                    
                    if (!processingResult.IsSuccess || processingResult.ItemResult == null)
                    {
                        _batchProcessItems.Add(new BatchProcessItem
                        {
                            ItemNumber = itemNumber,
                            Error = processingResult.ErrorMessage ?? "Unknown error",
                            IsSelected = false
                        });
                        stats.ErrorCount++;
                        continue;
                    }

                    var batchItem = _itemProcessingService.CreateBatchItem(itemNumber, processingResult.ItemResult);

                    progressCallback?.Invoke(itemNumber, endItem - startItem + 1, $"Fingerprinting item {itemNumber}...");

                    try
                    {
                        ProcessBatchItem(batchItem, processingResult);
                        
                        if (batchItem.IsSelected)
                            stats.SuccessCount++;
                        else if (!string.IsNullOrEmpty(batchItem.Error))
                            stats.ErrorCount++;
                        else if (batchItem.AvailableMatches?.Any() ?? false)
                            stats.NeedsReviewCount++;
                        else
                            stats.NoMatchCount++;
                    }
                    catch (ProcessingUtils.ProcessingException pex)
                    {
                        batchItem.Error = pex.Message;
                        Log.Warning(pex, "ProcessingException for batch item {ItemNumber} during fingerprinting/lookup.", itemNumber);
                        stats.ErrorCount++;
                    }
                    catch (Exception ex)
                    {
                        batchItem.Error = $"Unexpected error: {ex.Message}";
                        Log.Error(ex, "Unexpected error in ProcessBatchItem for item {ItemNumber}", itemNumber);
                        stats.ErrorCount++;
                    }

                    _batchProcessItems.Add(batchItem);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Outer loop error processing item {itemNumber} in batch");
                    _batchProcessItems.Add(new BatchProcessItem
                    {
                        ItemNumber = itemNumber,
                        Error = $"Failed to process: {ex.Message}",
                        IsSelected = false
                    });
                    stats.ErrorCount++;
                }
            }

            return stats;
        }

        /// <summary>
        /// Processes items from a list of cart numbers
        /// </summary>
        /// <param name="cartNumbers">List of cart numbers to process</param>
        /// <param name="progressCallback">Callback for progress updates</param>
        /// <returns>Batch processing statistics</returns>
        public BatchProcessingResult ProcessCartNumbers(List<int> cartNumbers, Action<int, int, string>? progressCallback = null)
        {
            _batchProcessItems.Clear();
            var stats = new BatchProcessingResult();

            for (int i = 0; i < cartNumbers.Count; i++)
            {
                var itemNumber = cartNumbers[i];
                progressCallback?.Invoke(i + 1, cartNumbers.Count, $"Reading item {itemNumber}...");

                try
                {
                    var processingResult = _itemProcessingService.ProcessItem(itemNumber);
                    
                    if (!processingResult.IsSuccess || processingResult.ItemResult == null)
                    {
                        _batchProcessItems.Add(new BatchProcessItem
                        {
                            ItemNumber = itemNumber,
                            Error = processingResult.ErrorMessage ?? "Unknown error",
                            IsSelected = false
                        });
                        stats.ErrorCount++;
                        continue;
                    }

                    var batchItem = _itemProcessingService.CreateBatchItem(itemNumber, processingResult.ItemResult);

                    progressCallback?.Invoke(i + 1, cartNumbers.Count, $"Fingerprinting item {itemNumber}...");

                    try
                    {
                        ProcessBatchItem(batchItem, processingResult);
                        
                        if (batchItem.IsSelected)
                            stats.SuccessCount++;
                        else if (!string.IsNullOrEmpty(batchItem.Error))
                            stats.ErrorCount++;
                        else if (batchItem.AvailableMatches?.Any() ?? false)
                            stats.NeedsReviewCount++;
                        else
                            stats.NoMatchCount++;
                    }
                    catch (ProcessingUtils.ProcessingException pex)
                    {
                        batchItem.Error = pex.Message;
                        Log.Warning(pex, "ProcessingException for CSV batch item {ItemNumber} during fingerprinting/lookup.", itemNumber);
                        stats.ErrorCount++;
                    }
                    catch (Exception ex)
                    {
                        batchItem.Error = $"Unexpected error: {ex.Message}";
                        Log.Error(ex, "Unexpected error in ProcessBatchItem for CSV item {ItemNumber}", itemNumber);
                        stats.ErrorCount++;
                    }

                    _batchProcessItems.Add(batchItem);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Outer loop error processing CSV item {itemNumber}");
                    _batchProcessItems.Add(new BatchProcessItem
                    {
                        ItemNumber = itemNumber,
                        Error = $"Failed to process: {ex.Message}",
                        IsSelected = false
                    });
                    stats.ErrorCount++;
                }
            }

            return stats;
        }

        /// <summary>
        /// Saves changes for selected batch items
        /// </summary>
        /// <param name="progressCallback">Callback for progress updates</param>
        /// <returns>Save operation statistics</returns>
        public SaveBatchResult SaveSelectedItems(Action<int, int, string>? progressCallback = null)
        {
            var selectedItems = _batchProcessItems.Where(i => i.IsSelected && string.IsNullOrEmpty(i.Error)).ToList();
            var result = new SaveBatchResult();

            for (int i = 0; i < selectedItems.Count; i++)
            {
                var item = selectedItems[i];
                progressCallback?.Invoke(i + 1, selectedItems.Count, $"Saving item {item.ItemNumber}...");

                try
                {
                    Core.MyriadTitleSchema titleUpdate;
                    
                    if (item.RecordingInfo != null && 
                        item.RecordingInfo.Title == item.NewTitle && 
                        (string.Join(", ", item.RecordingInfo.ArtistCredit?.Select(a => a.Name ?? string.Empty) ?? Array.Empty<string>()) == item.NewArtist || 
                         item.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name == item.NewArtist))
                    {
                        titleUpdate = new Core.MyriadTitleSchema
                        {
                            ItemTitle = item.RecordingInfo.Title ?? string.Empty,
                            Artists = item.RecordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()
                        };
                    }
                    else
                    {
                        List<string> artistsList = new List<string>();
                        if (item.NewArtist != null)
                        {
                            artistsList = item.NewArtist.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
                        }
                        titleUpdate = new Core.MyriadTitleSchema { ItemTitle = item.NewTitle, Artists = artistsList };
                    }

                    if (string.IsNullOrWhiteSpace(titleUpdate.ItemTitle) || !titleUpdate.Artists.Any())
                    {
                        item.Error = "Title or Artist is empty, cannot save.";
                        item.IsSelected = false;
                        result.FailedCount++;
                        Log.Warning($"Skipping save for item {item.ItemNumber} due to missing title/artist after final check.");
                        continue;
                    }

                    if (_itemProcessingService.SaveMetadataChanges(item.ItemNumber, item.RecordingInfo!))
                    {
                        result.SuccessCount++;
                        item.Error = string.Empty;
                    }
                    else
                    {
                        result.FailedCount++;
                        item.Error = "Failed to save metadata";
                        item.IsSelected = false;
                    }
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    item.Error = $"Exception: {ex.Message}";
                    item.IsSelected = false;
                    Log.Error(ex, $"Exception while saving item {item.ItemNumber}");
                }
            }

            return result;
        }

        /// <summary>
        /// Processes a batch item using existing processing result
        /// </summary>
        private void ProcessBatchItem(BatchProcessItem item, ItemProcessingResult processingResult)
        {
            if (string.IsNullOrEmpty(item.MediaLocation) || !File.Exists(item.MediaLocation))
            {
                item.Error = "Media file not found";
                item.IsSelected = false;
                return;
            }

            if (!processingResult.HasMatches || !processingResult.Matches.Any())
            {
                item.Error = "No fingerprint matches found";
                item.IsSelected = false;
                item.AvailableMatches = new List<ProcessingUtils.FingerprintMatch>();
                return;
            }

            var bestMatch = processingResult.ScoredMatches.FirstOrDefault();
            if (bestMatch.Score > 0.80 && bestMatch.Match.RecordingInfo != null)
            {
                var recordingInfo = bestMatch.Match.RecordingInfo;
                item.NewTitle = recordingInfo.Title ?? string.Empty;
                item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
                item.IsSelected = true;
                item.ConfidenceScore = bestMatch.Score;
                item.RecordingInfo = recordingInfo;
            }
            else
            {
                item.IsSelected = false;
                item.ConfidenceScore = bestMatch.Score;
                item.AvailableMatches = processingResult.Matches;
            }
        }
    }

    /// <summary>
    /// Statistics from batch processing operation
    /// </summary>
    public class BatchProcessingResult
    {
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public int NeedsReviewCount { get; set; }
        public int NoMatchCount { get; set; }
    }

    /// <summary>
    /// Statistics from save batch operation
    /// </summary>
    public class SaveBatchResult
    {
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
    }
}
