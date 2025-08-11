using RestSharp;
using Newtonsoft.Json;
using Serilog;
using System.Text.RegularExpressions;
using MyriadMusicTagger.Utils;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service for detecting duplicate songs in the Myriad database
    /// </summary>
    public class DuplicateDetectionService
    {
        private readonly RestClient _resClient;
        private readonly AppSettings _settings;
        private readonly MyriadDatabaseSearcher _databaseSearcher;

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
            _databaseSearcher = new MyriadDatabaseSearcher(settings);
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
            
            // Use the shared database searcher
            var basicSongs = await _databaseSearcher.GetAllSongsBasicAsync(apiProgressCallback);
            Log.Information("Found {Count} songs in database", basicSongs.Count);
            
            // Convert to DuplicateCandidate format for compatibility
            var allSongs = basicSongs.Select(s => new DuplicateCandidate
            {
                MediaId = s.MediaId,
                Title = s.Title,
                Artist = s.Artist,
                Duration = s.Duration,
                Categories = s.Categories
            }).ToList();
            
            var duplicateGroups = GroupDuplicates(allSongs, analysisProgressCallback);
            Log.Information("Found {Count} groups with potential duplicates", duplicateGroups.Count);
            
            return duplicateGroups;
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
}
