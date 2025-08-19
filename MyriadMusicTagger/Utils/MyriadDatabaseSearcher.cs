using RestSharp;
using Newtonsoft.Json;
using Serilog;
using System.Text.RegularExpressions;

namespace MyriadMusicTagger.Utils
{
    /// <summary>
    /// Utility class for searching and retrieving media items from the Myriad database
    /// Extracted from DuplicateDetectionService to be reusable across features
    /// </summary>
    public class MyriadDatabaseSearcher
    {
        private readonly RestClient _resClient;
        private readonly RestClient _playoutClient;
        private readonly AppSettings _settings;

        public MyriadDatabaseSearcher(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            var resOptions = new RestClientOptions
            {
                BaseUrl = new Uri(settings.RESApiUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromMinutes(5), // 5 minute timeout for large datasets
                ThrowOnAnyError = false,
                ThrowOnDeserializationError = false
            };
            _resClient = new RestClient(resOptions);

            var playoutOptions = new RestClientOptions
            {
                BaseUrl = new Uri(settings.PlayoutApiUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromMinutes(5),
                ThrowOnAnyError = false,
                ThrowOnDeserializationError = false
            };
            _playoutClient = new RestClient(playoutOptions);
        }

        /// <summary>
        /// Gets all songs from the database with basic information
        /// </summary>
        /// <param name="progressCallback">Callback for progress updates (0.0 to 1.0)</param>
        /// <returns>List of basic media items</returns>
    public async Task<List<BasicMediaItem>> GetAllSongsBasicAsync(Action<float>? progressCallback = null, System.Threading.CancellationToken cancellationToken = default)
        {
            Log.Information("Starting to retrieve basic song information from RES API at {BaseUrl}", _resClient.Options.BaseUrl);
            
            var allSongs = new List<BasicMediaItem>();
            int batchSize = 100;
            int currentStartId = 1;
            bool hasMoreResults = true;
            int successfulBatches = 0;
            int estimatedTotalSongs = 10000;

            // Test API connectivity first
            if (!await TestApiConnectivity())
            {
                return allSongs;
            }

            while (hasMoreResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var request = new RestRequest("/api/Media/Search");
                    request.AddQueryParameter("itemType", "Song");
                    request.AddQueryParameter("maxResultCount", batchSize.ToString());
                    request.AddQueryParameter("returnInfo", "Basic");
                    request.AddQueryParameter("stationId", "-1");
                    request.AddQueryParameter("attributesStationId", "-1");
                    request.Timeout = TimeSpan.FromMinutes(2);
                    
                    if (currentStartId > 1)
                    {
                        request.AddQueryParameter("startId", currentStartId.ToString());
                        request.AddQueryParameter("endId", "999999999");
                    }
                    
                    request.AddHeader("X-API-Key", _settings.RESReadKey);

                    Log.Debug("Making API request for basic info batch starting at ID {StartId}", currentStartId);

                    var response = await _resClient.ExecuteAsync(request, cancellationToken);
                    
                    if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                    {
                        Log.Warning("Failed to retrieve songs batch starting at ID {StartId}. Status: {StatusCode}, Error: {Error}", 
                            currentStartId, response.StatusCode, response.ErrorMessage ?? "Unknown error");
                        break;
                    }

                    var searchResult = JsonConvert.DeserializeObject<SearchMediaResults>(response.Content);
                    
                    if (searchResult?.Items == null || !searchResult.Items.Any())
                    {
                        Log.Information("No more items found in batch starting at ID {StartId}", currentStartId);
                        break;
                    }

                    var batchItems = searchResult.Items
                        .Where(item => !string.IsNullOrWhiteSpace(item.Title))
                        .Select(item => new BasicMediaItem
                        {
                            MediaId = item.MediaId,
                            Title = item.Title?.Trim() ?? "",
                            Artist = ExtractArtist(item),
                            Duration = item.TotalLength ?? "",
                            Categories = ExtractCategories(item)
                        }).ToList();

                    allSongs.AddRange(batchItems);
                    successfulBatches++;
                    
                    // Increase batch size progressively after successful requests
                    if (successfulBatches >= 2 && batchSize < 500)
                    {
                        batchSize = Math.Min(batchSize * 2, 500);
                        Log.Debug("Increasing batch size to {BatchSize}", batchSize);
                    }
                    
                    currentStartId = searchResult.Items.Max(i => i.MediaId) + 1;
                    
                    // Update progress
                    if (progressCallback != null)
                    {
                        float progress = allSongs.Count < estimatedTotalSongs
                            ? Math.Min(0.8f, (float)allSongs.Count / estimatedTotalSongs)
                            : 0.9f;
                        progressCallback(progress);
                    }
                    
                    Log.Debug("Retrieved batch of {Count} songs, total so far: {Total}", 
                        batchItems.Count, allSongs.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error retrieving songs batch starting at ID {StartId}", currentStartId);
                    hasMoreResults = false;
                }
            }

            progressCallback?.Invoke(1.0f);
            Log.Information("Retrieved {Count} songs with basic information", allSongs.Count);
            return allSongs;
        }

        /// <summary>
        /// Gets detailed information for a specific media item, including file path
        /// Uses hybrid approach: RES API for metadata, Playout API for file paths
        /// </summary>
        /// <param name="mediaId">The media ID to get details for</param>
        /// <returns>Detailed media item information or null if not found</returns>
        public async Task<DetailedMediaItem?> GetDetailedMediaItemAsync(int mediaId)
        {
            const int maxRetries = 2; // Reduced from 3 to be more conservative
            const int baseDelayMs = 2000; // Increased to 2 seconds base delay
            
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // First, get metadata from RES Search API (has better artist/duration info)
                    var resRequest = new RestRequest("/api/Media/Search");
                    resRequest.AddQueryParameter("itemType", "Song");
                    resRequest.AddQueryParameter("maxResultCount", "1");
                    resRequest.AddQueryParameter("returnInfo", "Full");
                    resRequest.AddQueryParameter("startId", mediaId.ToString());
                    resRequest.AddQueryParameter("endId", mediaId.ToString());
                    resRequest.AddQueryParameter("stationId", "-1");
                    resRequest.AddQueryParameter("attributesStationId", "-1");
                    resRequest.Timeout = TimeSpan.FromSeconds(30);
                    resRequest.AddHeader("X-API-Key", _settings.RESReadKey);

                    Log.Debug("Getting metadata from RES API for media ID {MediaId} (attempt {Attempt}/{MaxAttempts})", 
                        mediaId, attempt + 1, maxRetries + 1);

                    var resResponse = await _resClient.ExecuteAsync(resRequest);
                    
                    // Handle rate limiting with retry
                    if (resResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt < maxRetries)
                        {
                            var delay = baseDelayMs * (int)Math.Pow(2, attempt); // Exponential backoff: 2s, 4s
                            Log.Debug("Rate limited for media ID {MediaId}, retrying in {Delay}ms", mediaId, delay);
                            await Task.Delay(delay);
                            continue; // Retry the request
                        }
                        else
                        {
                            Log.Warning("Failed to get metadata for media ID {MediaId} after {Attempts} attempts due to rate limiting", 
                                mediaId, maxRetries + 1);
                            return null;
                        }
                    }
                    
                    if (!resResponse.IsSuccessful || string.IsNullOrEmpty(resResponse.Content))
                    {
                        Log.Warning("Failed to get metadata for media ID {MediaId}. Status: \"{Status}\", Error: {Error}", 
                            mediaId, resResponse.StatusCode, resResponse.ErrorMessage ?? "Unknown error");
                        return null;
                    }

                    var searchResult = JsonConvert.DeserializeObject<SearchMediaResults>(resResponse.Content);
                    var resItem = searchResult?.Items?.FirstOrDefault(i => i.MediaId == mediaId);
                    
                    if (resItem == null)
                    {
                        Log.Warning("Media item {MediaId} not found in RES search", mediaId);
                        return null;
                    }

                    // Now get file path from Playout API (has correct file paths)
                    var playoutRequest = new RestRequest("/api/Media/ReadItem");
                    playoutRequest.AddQueryParameter("mediaId", mediaId.ToString());
                    playoutRequest.AddQueryParameter("attributesStationId", "-1");
                    playoutRequest.AddQueryParameter("additionalInfo", "Full");
                    playoutRequest.Timeout = TimeSpan.FromSeconds(30);
                    playoutRequest.AddHeader("X-API-Key", _settings.PlayoutReadKey);

                    Log.Debug("Getting file path from Playout API for media ID {MediaId}", mediaId);

                    var playoutResponse = await _playoutClient.ExecuteAsync(playoutRequest);
                    
                    if (!playoutResponse.IsSuccessful || string.IsNullOrEmpty(playoutResponse.Content))
                    {
                        Log.Warning("Failed to get file path for media ID {MediaId}. Status: \"{Status}\", Error: {Error}", 
                            mediaId, playoutResponse.StatusCode, playoutResponse.ErrorMessage ?? "Unknown error");
                        // Continue with empty file path rather than failing completely
                    }

                    string filePath = "";
                    if (playoutResponse.IsSuccessful && !string.IsNullOrEmpty(playoutResponse.Content))
                    {
                        var playoutResult = JsonConvert.DeserializeObject<MyriadMediaItem>(playoutResponse.Content);
                        var playoutItem = playoutResult?.Result;
                        if (playoutItem != null)
                        {
                            filePath = playoutItem.MediaLocation ?? playoutItem.OriginalMediaLocation ?? "";
                        }
                    }

                    // Combine the best data from both APIs
                    var detailedItem = new DetailedMediaItem
                    {
                        MediaId = resItem.MediaId,
                        Title = resItem.Title?.Trim() ?? "",
                        Artist = ExtractArtist(resItem),
                        Duration = resItem.TotalLength ?? "",
                        Categories = ExtractCategories(resItem),
                        FilePath = filePath,
                        ContentType = resItem.ContentType?.ToString() ?? "",
                        AudioFormat = resItem.AudioFormat,
                        CreatedDateTime = resItem.CreatedDateTime,
                        LastModDateTime = resItem.LastModDateTime
                    };

                    // Debug file path extraction for some samples
                    if (mediaId <= 100 || mediaId % 1000 == 0) // Log some samples
                    {
                        Log.Debug("Combined result for media ID {MediaId}: Title='{Title}', Artist='{Artist}', Duration='{Duration}', FilePath='{FilePath}'",
                            mediaId, detailedItem.Title, detailedItem.Artist, detailedItem.Duration, detailedItem.FilePath);
                    }

                    return detailedItem;
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                        Log.Warning(ex, "Exception while getting detailed info for media ID {MediaId} (attempt {Attempt}), retrying in {Delay}ms", 
                            mediaId, attempt + 1, delay);
                        await Task.Delay(delay);
                        continue;
                    }
                    else
                    {
                        Log.Error(ex, "Exception while getting detailed info for media ID {MediaId} after {Attempts} attempts", 
                            mediaId, maxRetries + 1);
                        return null;
                    }
                }
            }
            
            return null; // Should never reach here, but just in case
        }

        /// <summary>
        /// Gets detailed information for multiple media items in batch
        /// </summary>
        /// <param name="mediaIds">List of media IDs to get details for</param>
        /// <param name="progressCallback">Progress callback for batch processing</param>
        /// <returns>List of detailed media items</returns>
        public async Task<List<DetailedMediaItem>> GetDetailedMediaItemsBatchAsync(
            List<int> mediaIds, 
            Action<float>? progressCallback = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var detailedItems = new List<DetailedMediaItem>();
            var totalItems = mediaIds.Count;
            var completedItems = 0;

            Log.Information("Getting detailed information for {Count} media items (sequential processing)", totalItems);

            // Process completely sequentially to avoid any rate limiting
            var delayBetweenRequests = 250; // 250ms delay between each request
            
            foreach (var mediaId in mediaIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await GetDetailedMediaItemAsync(mediaId);
                if (result != null)
                {
                    detailedItems.Add(result);
                }
                completedItems++;
                if (progressCallback != null && totalItems > 0)
                {
                    progressCallback((float)completedItems / totalItems);
                }
                // Add delay between requests to respect rate limits
                if (completedItems < totalItems)
                {
                    await Task.Delay(delayBetweenRequests, cancellationToken);
                }
            }

            Log.Information("Retrieved detailed information for {Count} out of {Total} requested items", 
                detailedItems.Count, totalItems);
            return detailedItems;
        }

        /// <summary>
        /// Retries getting detailed information for a list of media IDs with more conservative settings
        /// Useful for retrying items that failed due to rate limiting
        /// </summary>
        /// <param name="mediaIds">List of media IDs to retry</param>
        /// <param name="progressCallback">Progress callback for batch processing</param>
        /// <returns>List of detailed media items</returns>
        public async Task<List<DetailedMediaItem>> RetryDetailedMediaItemsAsync(
            List<int> mediaIds, 
            Action<float>? progressCallback = null)
        {
            var detailedItems = new List<DetailedMediaItem>();
            var totalItems = mediaIds.Count;
            var completedItems = 0;

            Log.Information("Retrying detailed information for {Count} media items with very conservative settings", totalItems);

            // Very conservative settings for retry
            var delayBetweenRequests = 5000; // 5 seconds between each request
            
            foreach (var mediaId in mediaIds)
            {
                var result = await GetDetailedMediaItemAsync(mediaId);
                if (result != null)
                {
                    detailedItems.Add(result);
                }
                
                completedItems++;
                if (progressCallback != null && totalItems > 0)
                {
                    progressCallback((float)completedItems / totalItems);
                }

                // Wait between each request to avoid rate limiting
                if (completedItems < totalItems)
                {
                    await Task.Delay(delayBetweenRequests);
                }
            }

            Log.Information("Retry completed: Retrieved detailed information for {Count} out of {Total} requested items", 
                detailedItems.Count, totalItems);
            return detailedItems;
        }

        /// <summary>
        /// Tests API connectivity
        /// </summary>
        private async Task<bool> TestApiConnectivity()
        {
            try
            {
                var testRequest = new RestRequest("/api/Media/Search");
                testRequest.AddQueryParameter("itemType", "Song");
                testRequest.AddQueryParameter("maxResultCount", "1");
                testRequest.AddQueryParameter("returnInfo", "Basic");
                testRequest.AddHeader("X-API-Key", _settings.RESReadKey);
                testRequest.Timeout = TimeSpan.FromSeconds(30);

                Log.Debug("Testing API connectivity...");
                var testResponse = await _resClient.ExecuteAsync(testRequest);
                
                if (!testResponse.IsSuccessful)
                {
                    Log.Error("API connectivity test failed. Status: {Status}, Error: {Error}", 
                        testResponse.StatusCode, testResponse.ErrorMessage);
                    
                    if (testResponse.StatusCode == 0)
                    {
                        Log.Error("Could not connect to RES API. Please verify:");
                        Log.Error("1. RES API service is running");
                        Log.Error("2. API URL is correct: {Url}", _resClient.Options.BaseUrl);
                        Log.Error("3. Network connectivity is available");
                    }
                    return false;
                }
                
                Log.Debug("API connectivity test successful");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to test API connectivity");
                return false;
            }
        }

        /// <summary>
        /// Extracts artist information from various fields in the media item
        /// </summary>
        private string ExtractArtist(MediaItem item)
        {
            // Try to get artist from Artists array first
            if (item.Artists != null && item.Artists.Any())
            {
                var firstArtist = item.Artists.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstArtist?.ArtistName))
                    return firstArtist.ArtistName.Trim();
            }

            // Try to get artist from copyright performer
            if (!string.IsNullOrWhiteSpace(item.Copyright?.Performer))
                return item.Copyright.Performer.Trim();

            // Check if the title contains " - " and extract artist from there
            if (!string.IsNullOrWhiteSpace(item.Title) && item.Title.Contains(" - "))
            {
                var parts = item.Title.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    return parts[0].Trim();
                }
            }

            return "";
        }

        /// <summary>
        /// Extracts category information from the media item
        /// </summary>
        private List<string> ExtractCategories(MediaItem item)
        {
            var categories = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(item.IngestCategoryName))
            {
                categories.Add(item.IngestCategoryName);
            }
            
            return categories;
        }
    }

    /// <summary>
    /// Basic media item information for initial searches
    /// </summary>
    public class BasicMediaItem
    {
        public int MediaId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Duration { get; set; } = "";
        public List<string> Categories { get; set; } = new List<string>();
    }

    /// <summary>
    /// Detailed media item information including file paths
    /// </summary>
    public class DetailedMediaItem : BasicMediaItem
    {
        public string FilePath { get; set; } = "";
        public string ContentType { get; set; } = "";
        public AudioFormat? AudioFormat { get; set; }
        public DateTime? CreatedDateTime { get; set; }
        public DateTime? LastModDateTime { get; set; }
    }

    // Re-use existing models from DuplicateDetectionService
    public class SearchMediaResults
    {
        public List<MediaItem> Items { get; set; } = new List<MediaItem>();
        public int NumberOfResultsLimitedTo { get; set; }
    }

    public class MediaItem
    {
        public int MediaId { get; set; }
        public string? Title { get; set; }
        public string? TotalLength { get; set; }
        public List<ArtistInfo>? Artists { get; set; }
        public MediaCopyright? Copyright { get; set; }
        public string? IngestCategoryName { get; set; }
        public string? MediaLocation { get; set; }
        public string? LocalMediaLocation { get; set; }
        public ContentType? ContentType { get; set; }
        public AudioFormat? AudioFormat { get; set; }
        public DateTime? CreatedDateTime { get; set; }
        public DateTime? LastModDateTime { get; set; }
    }

    public class ArtistInfo
    {
        public int ArtistId { get; set; }
        public string? ArtistName { get; set; }
        public int Verified { get; set; }
    }

    public class MediaCopyright
    {
        public string? CopyrightTitle { get; set; }
        public string? Performer { get; set; }
        public string? RecordingNumber { get; set; }
        public string? RecordLabel { get; set; }
        public string? Composer { get; set; }
        public string? Lyricist { get; set; }
        public string? Publisher { get; set; }
        public string? Isrc { get; set; }
        public string? License { get; set; }
    }

    public enum ContentType
    {
        AudioFile,
        VideoFile,
        AudioStream,
        VideoStream,
        AudioLineIn,
        MediaListAll,
        MediaListRotate,
        MediaListRandom,
        SplitGroup,
        NetworkSplitGroup,
        Command
    }

    public class AudioFormat
    {
        public int? SampleRate { get; set; }
        public int? BitsPerSample { get; set; }
        public int? Channels { get; set; }
        public string? Format { get; set; }
    }
}
