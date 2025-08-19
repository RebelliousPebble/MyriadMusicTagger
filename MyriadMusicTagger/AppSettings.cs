using Newtonsoft.Json;

namespace MyriadMusicTagger
{
    /// <summary>
    /// Application configuration settings for MyriadMusicTagger
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// AcoustID Client Key for audio fingerprinting (Required)
        /// </summary>
        public string AcoustIDClientKey { get; set; } = string.Empty;
        
        /// <summary>
        /// Playout system write key (Optional)
        /// </summary>
        public string PlayoutWriteKey { get; set; } = string.Empty;
        
        /// <summary>
        /// Playout system read key (Required)
        /// </summary>
        public string PlayoutReadKey { get; set; } = string.Empty;
        
        /// <summary>
        /// RES system write key (Optional)
        /// </summary>
        public string RESWriteKey { get; set; } = string.Empty;
        
        /// <summary>
        /// RES system read key (Optional)
        /// </summary>
        public string RESReadKey { get; set; } = string.Empty;
        
        /// <summary>
        /// Delay between MusicBrainz requests in seconds (Minimum: 0.2)
        /// </summary>
        public double DelayBetweenRequests { get; set; } = 1.0; // Default to 1 second for safety
        
        /// <summary>
        /// Playout API base URL (Required)
        /// </summary>
        public string PlayoutApiUrl { get; set; } = "http://localhost:9180/BrMyriadPlayout/v6";
        
        /// <summary>
        /// RES API base URL (Optional)
        /// </summary>
        public string RESApiUrl { get; set; } = "http://localhost:6941/BrMyriadRES/v6";

        /// <summary>
        /// Validates that all required settings are properly configured
        /// </summary>
        /// <returns>True if all required settings are valid</returns>
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(AcoustIDClientKey) &&
                   !string.IsNullOrWhiteSpace(PlayoutReadKey) &&
                   DelayBetweenRequests >= 0.2 &&
                   !string.IsNullOrWhiteSpace(PlayoutApiUrl) &&
                   Uri.TryCreate(PlayoutApiUrl, UriKind.Absolute, out _);
        }

        /// <summary>
        /// Gets a list of validation errors for the current settings
        /// </summary>
        /// <returns>List of validation error messages</returns>
        public List<string> GetValidationErrors()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(AcoustIDClientKey))
                errors.Add("AcoustID Client Key is required");

            if (string.IsNullOrWhiteSpace(PlayoutReadKey))
                errors.Add("Playout Read Key is required");

            if (DelayBetweenRequests < 0.2)
                errors.Add("Delay between requests must be at least 0.2 seconds");

            if (string.IsNullOrWhiteSpace(PlayoutApiUrl))
                errors.Add("Playout API URL is required");
            else if (!Uri.TryCreate(PlayoutApiUrl, UriKind.Absolute, out _))
                errors.Add("Playout API URL must be a valid URL");

            if (!string.IsNullOrWhiteSpace(RESApiUrl) && !Uri.TryCreate(RESApiUrl, UriKind.Absolute, out _))
                errors.Add("RES API URL must be a valid URL if provided");

            return errors;
        }
    }
}