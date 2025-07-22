using MetaBrainz.MusicBrainz.Interfaces.Entities;

namespace MyriadMusicTagger.Core
{
    /// <summary>
    /// Provides functionality to score and rank fingerprint matches based on various criteria
    /// </summary>
    public static class FingerprintMatchScorer
    {
        /// <summary>
        /// Scores fingerprint matches based on fingerprint confidence and metadata similarity
        /// </summary>
        /// <param name="matches">List of fingerprint matches to score</param>
        /// <param name="existingTitle">Current title to compare against</param>
        /// <param name="existingArtist">Current artist to compare against</param>
        /// <returns>List of scored matches ordered by score descending</returns>
        public static List<(ProcessingUtils.FingerprintMatch Match, double Score)> ScoreMatches(
            List<ProcessingUtils.FingerprintMatch> matches, 
            string existingTitle, 
            string existingArtist)
        {
            var (parsedArtist, parsedTitle) = ParseArtistAndTitle(existingTitle, existingArtist);
            
            return matches.Select(m => {
                if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
                
                var matchTitle = m.RecordingInfo.Title ?? "";
                var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
                
                double score = m.Score;
                double titleSimilarity = CalculateStringSimilarity(matchTitle, parsedTitle);
                double artistSimilarity = CalculateStringSimilarity(matchArtist, parsedArtist);
                
                // Give much more weight to fingerprint (85%), use title/artist as tiebreakers (15% total)
                score = (score * 0.85) + (titleSimilarity * 0.10) + (artistSimilarity * 0.05);
                
                return (Match: m, Score: score);
            }).OrderByDescending(m => m.Score).ToList();
        }

        /// <summary>
        /// Parses a combined title string into separate artist and title components
        /// </summary>
        /// <param name="title">Title string that may contain artist - title format</param>
        /// <param name="fallbackArtist">Artist to use if not found in title</param>
        /// <returns>Tuple of (Artist, Title)</returns>
        public static (string Artist, string Title) ParseArtistAndTitle(string title, string fallbackArtist = "")
        {
            var titleParts = title?.Split(new[] { " - " }, StringSplitOptions.None) ?? Array.Empty<string>();
            return titleParts.Length == 2 
                ? (titleParts[0].Trim(), titleParts[1].Trim()) 
                : (fallbackArtist, title ?? string.Empty);
        }

        /// <summary>
        /// Calculates string similarity between two strings using normalized Levenshtein distance
        /// </summary>
        private static double CalculateStringSimilarity(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) && string.IsNullOrEmpty(str2)) return 1.0; // Both empty is a perfect match
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0; // One empty is no match
            
            string normalized1 = RemoveCommonExtras(str1);
            string normalized2 = RemoveCommonExtras(str2);
            
            if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (normalized1.Contains(normalized2, StringComparison.OrdinalIgnoreCase) || 
                normalized2.Contains(normalized1, StringComparison.OrdinalIgnoreCase)) return 0.8;
                
            return 1.0 - ((double)ComputeLevenshteinDistance(normalized1, normalized2) / 
                         Math.Max(normalized1.Length, normalized2.Length));
        }

        /// <summary>
        /// Removes common extra text from music titles for better comparison
        /// </summary>
        private static string RemoveCommonExtras(string title)
        {
            string[] commonExtras = new[] { 
                "(original version)", "(instrumental)", "(radio edit)", "(album version)", 
                "(official video)", "(official audio)", "(lyric video)", "(official music video)", 
                "(clean)", "(explicit)" 
            };
            
            var result = title.ToLowerInvariant();
            foreach (var extra in commonExtras) 
            { 
                result = result.Replace(extra, ""); 
            }
            return result.Trim();
        }

        /// <summary>
        /// Computes the Levenshtein distance between two strings
        /// </summary>
        private static int ComputeLevenshteinDistance(string str1, string str2)
        {
            int[,] matrix = new int[str1.Length + 1, str2.Length + 1];
            
            for (int i = 0; i <= str1.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= str2.Length; j++) matrix[0, j] = j;
            
            for (int i = 1; i <= str1.Length; i++) 
            { 
                for (int j = 1; j <= str2.Length; j++) 
                { 
                    int cost = (str1[i - 1] == str2[j - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), 
                                          matrix[i - 1, j - 1] + cost);
                } 
            }
            
            return matrix[str1.Length, str2.Length];
        }
    }
}
