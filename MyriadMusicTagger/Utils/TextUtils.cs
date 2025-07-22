using System.Text;

namespace MyriadMusicTagger.Utils
{
    /// <summary>
    /// Utility methods for common string and text operations
    /// </summary>
    public static class TextUtils
    {
        /// <summary>
        /// Truncates text to a maximum length, adding ellipsis if needed
        /// </summary>
        /// <param name="text">Text to truncate</param>
        /// <param name="maxLength">Maximum length including ellipsis</param>
        /// <returns>Truncated text</returns>
        public static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Creates a formatted summary string from batch processing results
        /// </summary>
        /// <param name="successCount">Number of successful items</param>
        /// <param name="errorCount">Number of error items</param>
        /// <param name="needsReviewCount">Number of items needing review</param>
        /// <param name="noMatchCount">Number of items with no matches</param>
        /// <returns>Formatted summary string</returns>
        public static string CreateBatchSummary(int successCount, int errorCount, int needsReviewCount, int noMatchCount)
        {
            var message = new StringBuilder();
            message.AppendLine("Batch Processing Complete!");
            message.AppendLine($"Successfully processed (high confidence): {successCount}");
            message.AppendLine($"Needs review (matches found): {needsReviewCount}");
            message.AppendLine($"No automatic match / No matches found: {noMatchCount}");
            message.AppendLine($"Errors: {errorCount}");
            message.AppendLine("\nProceed to the batch edit table to review and save changes.");
            return message.ToString();
        }

        /// <summary>
        /// Creates a formatted save summary string
        /// </summary>
        /// <param name="successCount">Number of successfully saved items</param>
        /// <param name="failedCount">Number of failed saves</param>
        /// <returns>Formatted summary string</returns>
        public static string CreateSaveSummary(int successCount, int failedCount)
        {
            var summaryMessage = new StringBuilder();
            summaryMessage.AppendLine($"Successfully saved: {successCount} item(s).");
            summaryMessage.AppendLine($"Failed to save: {failedCount} item(s).");
            if (failedCount > 0)
            {
                summaryMessage.AppendLine("Check the table for error details on failed items.");
            }
            return summaryMessage.ToString();
        }

        /// <summary>
        /// Formats item details for display
        /// </summary>
        /// <param name="item">Item result to format</param>
        /// <returns>Formatted item details string</returns>
        public static string FormatItemDetails(Result item)
        {
            var lines = new List<string>
            {
                $"Title: {item.Title ?? "[not set]"}",
                $"Media ID: {item.MediaId}",
                $"Duration: {item.TotalLength ?? "[unknown]"}",
                $"Artist: {item.Copyright?.Performer ?? "[not set]"}"
            };
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats metadata information for display
        /// </summary>
        /// <param name="recordingInfo">Recording information to format</param>
        /// <returns>Formatted metadata string</returns>
        public static string FormatMetadata(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
        {
            var lines = new List<string> { $"Title: {recordingInfo.Title ?? "[not found]"}" };
            
            if (recordingInfo.ArtistCredit?.Any() == true)
            {
                lines.Add($"Artist: {recordingInfo.ArtistCredit.First().Name ?? "[unnamed artist]"}");
                if (recordingInfo.ArtistCredit.Count > 1)
                {
                    for (int i = 1; i < recordingInfo.ArtistCredit.Count; i++)
                    {
                        lines.Add($"Additional Artist: {recordingInfo.ArtistCredit[i].Name ?? "[unnamed artist]"}");
                    }
                }
            }
            else
            {
                lines.Add("Artist: [no artist credit found]");
            }

            if (!string.IsNullOrEmpty(recordingInfo.Disambiguation))
            {
                lines.Add($"Info: {recordingInfo.Disambiguation}");
            }

            if (recordingInfo.Releases?.Any() == true)
            {
                var firstRelease = recordingInfo.Releases.First();
                lines.Add($"Album: {firstRelease.Title ?? "[unknown album]"}");
                if (firstRelease.Date != null)
                {
                    lines.Add($"Release Date: {firstRelease.Date.ToString()}");
                }
            }

            return string.Join("\n", lines);
        }
    }
}
