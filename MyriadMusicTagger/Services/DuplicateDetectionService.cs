using RestSharp;
using Newtonsoft.Json;
using Serilog;
using System.Text.RegularExpressions;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service for detecting duplicate songs in the Myriad database
    /// </summary>
    public class DuplicateDetectionService
    {
        private readonly RestClient _resClient;
        private readonly AppSettings _settings;

        public DuplicateDetectionService(AppSettings settings)
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
        }

        /// <summary>
        /// Searches for all songs in the database and groups them by potential duplicates
        /// </summary>
        /// <returns>Groups of duplicate songs</returns>
        public async Task<List<DuplicateGroup>> FindDuplicateSongsAsync()
        {
            return await FindDuplicateSongsAsync(null, null);
        }

        /// <summary>
        /// Searches for all songs in the database and groups them by potential duplicates with progress reporting
        /// </summary>
        /// <param name="apiProgressCallback">Callback for API retrieval progress (0.0 to 1.0)</param>
        /// <param name="analysisProgressCallback">Callback for duplicate analysis progress (0.0 to 1.0)</param>
        /// <returns>Groups of duplicate songs</returns>
        public async Task<List<DuplicateGroup>> FindDuplicateSongsAsync(
            Action<float>? apiProgressCallback, 
            Action<float>? analysisProgressCallback)
        {
            Log.Information("Starting duplicate detection for songs...");
            
            var allSongs = await GetAllSongsAsync(apiProgressCallback);
            Log.Information("Found {Count} songs in database", allSongs.Count);
            
            var duplicateGroups = GroupDuplicates(allSongs, analysisProgressCallback);
            Log.Information("Found {Count} groups with potential duplicates", duplicateGroups.Count);
            
            return duplicateGroups;
        }

        /// <summary>
        /// Gets all songs from the database using the RES API search endpoint
        /// </summary>
        private async Task<List<DuplicateCandidate>> GetAllSongsAsync()
        {
            return await GetAllSongsAsync(null);
        }

        /// <summary>
        /// Gets all songs from the database using the RES API search endpoint with progress reporting
        /// </summary>
        /// <param name="progressCallback">Callback for progress updates (0.0 to 1.0)</param>
        private async Task<List<DuplicateCandidate>> GetAllSongsAsync(Action<float>? progressCallback)
        {
            var allSongs = new List<DuplicateCandidate>();
            // Start with smaller batch size to avoid timeouts, increase if successful
            int batchSize = 100; 
            int currentStartId = 1;
            bool hasMoreResults = true;
            int successfulBatches = 0;
            int estimatedTotalSongs = 10000; // Initial estimate, will adjust as we learn more

            Log.Information("Starting to retrieve songs from RES API at {BaseUrl}", _resClient.Options.BaseUrl);

            // Test API connectivity first
            try
            {
                var testRequest = new RestRequest("/api/Media/Search");
                testRequest.AddQueryParameter("itemType", "Song");
                testRequest.AddQueryParameter("maxResultCount", "1");
                testRequest.AddQueryParameter("returnInfo", "Basic");
                testRequest.AddHeader("X-API-Key", _settings.RESReadKey);
                testRequest.Timeout = TimeSpan.FromSeconds(30); // Shorter timeout for connectivity test

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
                    return allSongs; // Return empty list
                }
                
                Log.Debug("API connectivity test successful");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to test API connectivity");
                return allSongs; // Return empty list
            }

            while (hasMoreResults)
            {
                try
                {
                    var request = new RestRequest("/api/Media/Search");
                    request.AddQueryParameter("itemType", "Song");
                    request.AddQueryParameter("maxResultCount", batchSize.ToString());
                    request.AddQueryParameter("returnInfo", "Full");
                    request.AddQueryParameter("stationId", "-1"); // Use current station
                    request.AddQueryParameter("attributesStationId", "-1"); // Use current station
                    request.Timeout = TimeSpan.FromMinutes(2); // 2 minute timeout per request
                    
                    // Only add startId/endId if it's greater than 1 (for first batch, get everything)
                    if (currentStartId > 1)
                    {
                        request.AddQueryParameter("startId", currentStartId.ToString());
                        // RES API requires both startId and endId when using range-based queries
                        // Use a very high endId to get all remaining items
                        request.AddQueryParameter("endId", "999999999");
                    }
                    
                    request.AddHeader("X-API-Key", _settings.RESReadKey);

                    Log.Debug("Making API request to {BaseUrl}{Resource} with parameters: itemType=Song, startId={StartId}, endId={EndId}, maxResultCount={MaxResults}", 
                        _resClient.Options.BaseUrl, request.Resource, 
                        currentStartId > 1 ? currentStartId.ToString() : "not set", 
                        currentStartId > 1 ? "999999999" : "not set", 
                        batchSize);

                    var response = await _resClient.ExecuteAsync(request);
                    
                    Log.Debug("API response: Status={Status}, ContentLength={ContentLength}", 
                        response.StatusCode, response.Content?.Length ?? 0);

                    if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                    {
                        var errorMessage = response.ErrorMessage ?? "Unknown error";
                        
                        if (response.StatusCode == 0 && errorMessage.Contains("canceled"))
                        {
                            Log.Error("Request was canceled/timed out for batch starting at ID {StartId}. This may indicate:", currentStartId);
                            Log.Error("1. RES API service is not responding");
                            Log.Error("2. Network connectivity issues");
                            Log.Error("3. Database query taking too long (large dataset)");
                            Log.Error("4. API service is overloaded");
                        }
                        
                        Log.Warning("Failed to retrieve songs batch starting at ID {StartId}. Status: {StatusCode}, Error: {Error}, Content: {Content}", 
                            currentStartId, response.StatusCode, errorMessage, response.Content ?? "No content");
                        break;
                    }

                    var searchResult = JsonConvert.DeserializeObject<SearchMediaResults>(response.Content);
                    
                    Log.Debug("Deserialized response: Items count = {ItemCount}", 
                        searchResult?.Items.Count ?? 0);
                    
                    if (searchResult?.Items == null || !searchResult.Items.Any())
                    {
                        Log.Information("No more items found in batch starting at ID {StartId}", currentStartId);
                        break;
                    }

                    var batchCandidates = searchResult.Items
                        .Where(item => !string.IsNullOrWhiteSpace(item.Title))
                        .Select(item => new DuplicateCandidate
                        {
                            MediaId = item.MediaId,
                            Title = item.Title?.Trim() ?? "",
                            Artist = ExtractArtist(item),
                            Duration = item.TotalLength ?? "",
                            Categories = ExtractCategories(item)
                        }).ToList();

                    allSongs.AddRange(batchCandidates);
                    successfulBatches++;
                    
                    // Increase batch size progressively after successful requests
                    if (successfulBatches >= 2 && batchSize < 500)
                    {
                        batchSize = Math.Min(batchSize * 2, 500);
                        Log.Debug("Increasing batch size to {BatchSize} after {SuccessfulBatches} successful batches", 
                            batchSize, successfulBatches);
                    }
                    
                    // Update the start ID for the next batch
                    currentStartId = searchResult.Items.Max(i => i.MediaId) + 1;
                    
                    // Update progress based on current songs retrieved
                    // For the first few batches, estimate progress conservatively
                    if (progressCallback != null)
                    {
                        float progress;
                        if (allSongs.Count < estimatedTotalSongs)
                        {
                            progress = Math.Min(0.8f, (float)allSongs.Count / estimatedTotalSongs);
                        }
                        else
                        {
                            // We've exceeded our estimate, adjust it
                            estimatedTotalSongs = (int)(allSongs.Count * 1.2f);
                            progress = 0.9f; // Close to completion
                        }
                        progressCallback(progress);
                    }
                    
                    Log.Debug("Retrieved batch of {Count} songs, total so far: {Total}", 
                        batchCandidates.Count, allSongs.Count);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error retrieving songs batch starting at ID {StartId}", currentStartId);
                    hasMoreResults = false;
                }
            }

            // Mark API retrieval as complete
            progressCallback?.Invoke(1.0f);

            return allSongs;
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
            
            // Add category from IngestCategoryName if available
            if (!string.IsNullOrWhiteSpace(item.IngestCategoryName))
            {
                categories.Add(item.IngestCategoryName);
            }
            
            // Could add more category sources here based on your Myriad configuration
            
            return categories;
        }

        /// <summary>
        /// Groups songs by potential duplicates using fuzzy matching
        /// </summary>
        private List<DuplicateGroup> GroupDuplicates(List<DuplicateCandidate> songs)
        {
            return GroupDuplicates(songs, null);
        }

        private List<DuplicateGroup> GroupDuplicates(List<DuplicateCandidate> songs, Action<float>? progressCallback)
        {
            Log.Information("Starting duplicate analysis of {Count} songs...", songs.Count);
            var duplicateGroups = new List<DuplicateGroup>();
            var processedSongs = new HashSet<int>();
            var progressCounter = 0;
            var totalSongs = songs.Count;

            // Pre-compute normalized versions for faster comparison
            Log.Debug("Pre-computing normalized song data for faster matching...");
            var normalizedSongs = songs.Select(song => new NormalizedSongData
            {
                Song = song,
                NormalizedTitle = NormalizeTextForMatching(song.Title),
                NormalizedArtist = NormalizeTextForMatching(song.Artist),
                TitleWords = GetSignificantWords(song.Title),
                ArtistWords = GetSignificantWords(song.Artist)
            }).ToList();

            Log.Debug("Starting duplicate detection with optimized algorithm...");

            foreach (var songData in normalizedSongs)
            {
                progressCounter++;
                
                // Update progress callback
                if (progressCallback != null && totalSongs > 0)
                {
                    var progress = (float)progressCounter / totalSongs;
                    progressCallback(progress);
                }
                
                // Log progress every 1000 songs
                if (progressCounter % 1000 == 0)
                {
                    Log.Debug("Processed {Progress}/{Total} songs ({Percentage:F1}%)", 
                        progressCounter, totalSongs, (double)progressCounter / totalSongs * 100);
                }

                if (processedSongs.Contains(songData.Song.MediaId))
                    continue;

                var duplicates = FindDuplicatesForSongOptimized(songData, normalizedSongs, processedSongs);
                
                if (duplicates.Count > 1) // Only groups with actual duplicates
                {
                    duplicateGroups.Add(new DuplicateGroup
                    {
                        GroupId = duplicateGroups.Count + 1,
                        Songs = duplicates.OrderBy(s => s.MediaId).ToList()
                    });

                    // Mark all songs in this group as processed
                    foreach (var duplicate in duplicates)
                    {
                        processedSongs.Add(duplicate.MediaId);
                    }
                }
                else
                {
                    processedSongs.Add(songData.Song.MediaId);
                }
            }

            Log.Information("Duplicate analysis completed. Found {GroupCount} duplicate groups from {SongCount} songs", 
                duplicateGroups.Count, songs.Count);
            return duplicateGroups;
        }

        /// <summary>
        /// Finds all duplicates for a specific song
        /// </summary>
        private List<DuplicateCandidate> FindDuplicatesForSong(DuplicateCandidate targetSong, 
            List<DuplicateCandidate> allSongs, HashSet<int> processedSongs)
        {
            var duplicates = new List<DuplicateCandidate> { targetSong };

            foreach (var song in allSongs)
            {
                if (song.MediaId == targetSong.MediaId || processedSongs.Contains(song.MediaId))
                    continue;

                if (AreDuplicates(targetSong, song))
                {
                    duplicates.Add(song);
                }
            }

            return duplicates;
        }

        /// <summary>
        /// Determines if two songs are duplicates using fuzzy matching
        /// </summary>
        private bool AreDuplicates(DuplicateCandidate song1, DuplicateCandidate song2)
        {
            // Normalize strings for comparison
            var title1 = NormalizeForComparison(song1.Title);
            var title2 = NormalizeForComparison(song2.Title);
            var artist1 = NormalizeForComparison(song1.Artist);
            var artist2 = NormalizeForComparison(song2.Artist);

            // Check if title and artist match closely
            if (IsStringMatch(title1, title2) && IsStringMatch(artist1, artist2))
                return true;

            // Check if title and artist are swapped (common in poorly maintained databases)
            if (IsStringMatch(title1, artist2) && IsStringMatch(artist1, title2))
                return true;

            // Check if one title contains the other title + artist combination
            var fullTitle1 = $"{title1} {artist1}".Trim();
            var fullTitle2 = $"{title2} {artist2}".Trim();
            
            if (IsStringMatch(title1, fullTitle2) || IsStringMatch(title2, fullTitle1))
                return true;

            return false;
        }

        /// <summary>
        /// Normalizes a string for comparison by removing special characters and converting to lowercase
        /// </summary>
        private string NormalizeForComparison(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            // Remove common prefixes/suffixes and normalize
            var normalized = input.ToLowerInvariant();
            
            // Remove common words and punctuation
            normalized = Regex.Replace(normalized, @"[^\w\s]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            normalized = normalized.Trim();

            // Remove common articles and words
            var wordsToRemove = new[] { "the", "a", "an", "feat", "ft", "featuring", "vs", "and" };
            foreach (var word in wordsToRemove)
            {
                normalized = Regex.Replace(normalized, $@"\b{word}\b", "", RegexOptions.IgnoreCase);
            }

            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            
            return normalized;
        }

        /// <summary>
        /// Checks if two normalized strings are similar enough to be considered a match
        /// </summary>
        private bool IsStringMatch(string str1, string str2)
        {
            if (string.IsNullOrWhiteSpace(str1) && string.IsNullOrWhiteSpace(str2))
                return true;

            if (string.IsNullOrWhiteSpace(str1) || string.IsNullOrWhiteSpace(str2))
                return false;

            // Exact match
            if (str1 == str2)
                return true;

            // Check if one contains the other (for cases where one has extra info)
            if (str1.Contains(str2) || str2.Contains(str1))
                return true;

            // Calculate Levenshtein distance for fuzzy matching
            var maxLength = Math.Max(str1.Length, str2.Length);
            if (maxLength == 0) return true;

            var distance = CalculateLevenshteinDistance(str1, str2);
            var similarity = 1.0 - (double)distance / maxLength;

            // Consider it a match if similarity is 85% or higher
            return similarity >= 0.85;
        }

        /// <summary>
        /// Calculates the Levenshtein distance between two strings
        /// </summary>
        private int CalculateLevenshteinDistance(string str1, string str2)
        {
            var matrix = new int[str1.Length + 1, str2.Length + 1];

            for (int i = 0; i <= str1.Length; i++)
                matrix[i, 0] = i;

            for (int j = 0; j <= str2.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= str1.Length; i++)
            {
                for (int j = 1; j <= str2.Length; j++)
                {
                    int cost = str1[i - 1] == str2[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(
                        matrix[i - 1, j] + 1,      // deletion
                        matrix[i, j - 1] + 1),     // insertion
                        matrix[i - 1, j - 1] + cost); // substitution
                }
            }

            return matrix[str1.Length, str2.Length];
        }

        /// <summary>
        /// Deletes the specified media items from the Myriad system
        /// </summary>
        public async Task<bool> DeleteMediaItemsAsync(List<int> mediaIds, IProgress<float>? progress = null)
        {
            Log.Information("Deleting {Count} media items", mediaIds.Count);
            
            var allSuccessful = true;
            var totalItems = mediaIds.Count;
            var completedItems = 0;
            
            foreach (var mediaId in mediaIds)
            {
                try
                {
                    var request = new RestRequest("/api/Media/DeleteMediaItem", Method.Post);
                    request.AddQueryParameter("mediaId", mediaId.ToString());
                    request.AddHeader("X-API-Key", _settings.RESWriteKey);

                    var response = await _resClient.ExecuteAsync(request);
                    
                    if (!response.IsSuccessful)
                    {
                        Log.Error("Failed to delete media item {MediaId}: {Error}", 
                            mediaId, response.ErrorMessage ?? "Unknown error");
                        allSuccessful = false;
                    }
                    else
                    {
                        Log.Debug("Successfully deleted media item {MediaId}", mediaId);
                    }
                    
                    completedItems++;
                    progress?.Report((float)completedItems / totalItems);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception while deleting media item {MediaId}", mediaId);
                    allSuccessful = false;
                    completedItems++;
                    progress?.Report((float)completedItems / totalItems);
                }
            }

            Log.Information("Deletion complete. Success: {Success}", allSuccessful);
            return allSuccessful;
        }

        /// <summary>
        /// Optimized method to find duplicates for a song using pre-computed normalized data
        /// </summary>
        private List<DuplicateCandidate> FindDuplicatesForSongOptimized(NormalizedSongData targetSong, 
            List<NormalizedSongData> allSongs, HashSet<int> processedSongs)
        {
            var duplicates = new List<DuplicateCandidate> { targetSong.Song };

            foreach (var songData in allSongs)
            {
                if (songData.Song.MediaId == targetSong.Song.MediaId || 
                    processedSongs.Contains(songData.Song.MediaId))
                    continue;

                if (AreDuplicatesOptimized(targetSong, songData))
                {
                    duplicates.Add(songData.Song);
                }
            }

            return duplicates;
        }

        /// <summary>
        /// Optimized duplicate checking using pre-computed normalized data
        /// </summary>
        private bool AreDuplicatesOptimized(NormalizedSongData song1, NormalizedSongData song2)
        {
            // Don't match very short titles (likely false positives like "Go", "GA", etc.)
            if (song1.NormalizedTitle.Length < 3 || song2.NormalizedTitle.Length < 3)
                return false;

            // Don't match if both artists are empty or very short
            if ((string.IsNullOrWhiteSpace(song1.NormalizedArtist) && string.IsNullOrWhiteSpace(song2.NormalizedArtist)) ||
                (song1.NormalizedArtist.Length < 2 || song2.NormalizedArtist.Length < 2))
                return false;

            // Quick exact match first
            if (song1.NormalizedTitle == song2.NormalizedTitle && 
                song1.NormalizedArtist == song2.NormalizedArtist)
            {
                Log.Debug("Exact match found: '{Title1}' by '{Artist1}' = '{Title2}' by '{Artist2}'", 
                    song1.Song.Title, song1.Song.Artist, song2.Song.Title, song2.Song.Artist);
                return true;
            }

            // Check for swapped fields (only if both title and artist are substantial)
            if (song1.NormalizedTitle.Length >= 5 && song1.NormalizedArtist.Length >= 5 &&
                song2.NormalizedTitle.Length >= 5 && song2.NormalizedArtist.Length >= 5)
            {
                if (song1.NormalizedTitle == song2.NormalizedArtist && 
                    song1.NormalizedArtist == song2.NormalizedTitle)
                {
                    Log.Debug("Swapped fields match found: '{Title1}' by '{Artist1}' = '{Title2}' by '{Artist2}'", 
                        song1.Song.Title, song1.Song.Artist, song2.Song.Title, song2.Song.Artist);
                    return true;
                }
            }

            // For fuzzy matching, require BOTH title and artist to be very similar
            var maxTitleLength = Math.Max(song1.NormalizedTitle.Length, song2.NormalizedTitle.Length);
            var maxArtistLength = Math.Max(song1.NormalizedArtist.Length, song2.NormalizedArtist.Length);
            
            // Require minimum lengths for fuzzy matching
            if (maxTitleLength < 5 || maxArtistLength < 3)
                return false;
            
            var titleDistance = CalculateLevenshteinDistance(song1.NormalizedTitle, song2.NormalizedTitle);
            var artistDistance = CalculateLevenshteinDistance(song1.NormalizedArtist, song2.NormalizedArtist);
            
            var titleSimilarity = 1.0 - (double)titleDistance / maxTitleLength;
            var artistSimilarity = 1.0 - (double)artistDistance / maxArtistLength;
            
            // Much stricter thresholds - both title AND artist must be very similar (95%+)
            // AND at least one must be nearly identical (98%+)
            bool titleVeryClose = titleSimilarity >= 0.95;
            bool artistVeryClose = artistSimilarity >= 0.95;
            bool titleNearlyIdentical = titleSimilarity >= 0.98;
            bool artistNearlyIdentical = artistSimilarity >= 0.98;
            
            bool isMatch = titleVeryClose && artistVeryClose && (titleNearlyIdentical || artistNearlyIdentical);
            
            if (isMatch)
            {
                Log.Debug("Fuzzy match found: '{Title1}' by '{Artist1}' = '{Title2}' by '{Artist2}' (Title: {TitleSim:F3}, Artist: {ArtistSim:F3})", 
                    song1.Song.Title, song1.Song.Artist, song2.Song.Title, song2.Song.Artist, titleSimilarity, artistSimilarity);
            }
            
            return isMatch;
        }

        /// <summary>
        /// Normalizes text for matching by removing common articles and formatting
        /// More conservative normalization to preserve distinguishing information
        /// </summary>
        private string NormalizeTextForMatching(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.ToLowerInvariant().Trim();
            
            // Only remove very common articles that don't affect meaning
            // Be more conservative - don't remove "feat", "ft" as they're distinguishing
            var wordsToRemove = new[] { "the ", " the ", " a ", " an " };
            foreach (var word in wordsToRemove)
            {
                normalized = normalized.Replace(word, " ");
            }
            
            // Handle leading articles
            if (normalized.StartsWith("the "))
                normalized = normalized.Substring(4);
            if (normalized.StartsWith("a "))
                normalized = normalized.Substring(2);
            if (normalized.StartsWith("an "))
                normalized = normalized.Substring(3);
            
            // Remove some punctuation but keep important distinguishing characters
            normalized = Regex.Replace(normalized, @"[^\w\s\(\)\-]", "");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            
            return normalized;
        }

        /// <summary>
        /// Gets significant words from text for faster comparison
        /// </summary>
        private string[] GetSignificantWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var normalized = NormalizeTextForMatching(text);
            return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                           .Where(w => w.Length > 2) // Only words longer than 2 characters
                           .ToArray();
        }
    }

    /// <summary>
    /// Represents a group of duplicate songs
    /// </summary>
    public class DuplicateGroup
    {
        public int GroupId { get; set; }
        public List<DuplicateCandidate> Songs { get; set; } = new List<DuplicateCandidate>();
    }

    /// <summary>
    /// Represents a candidate for duplicate detection
    /// </summary>
    public class DuplicateCandidate
    {
        public int MediaId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Duration { get; set; } = "";
        public List<string> Categories { get; set; } = new List<string>();
        public bool IsSelected { get; set; } = false;
    }

    /// <summary>
    /// Helper class for optimized duplicate detection
    /// </summary>
    public class NormalizedSongData
    {
        public DuplicateCandidate Song { get; set; } = null!;
        public string NormalizedTitle { get; set; } = "";
        public string NormalizedArtist { get; set; } = "";
        public string[] TitleWords { get; set; } = Array.Empty<string>();
        public string[] ArtistWords { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Response model for the media search API
    /// </summary>
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
}
