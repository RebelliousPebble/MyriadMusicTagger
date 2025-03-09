using Newtonsoft.Json;

namespace MyriadMusicTagger
{
    public class AppSettings
    {
        public string AcoustIDClientKey { get; set; } = string.Empty;
        public string PlayoutWriteKey { get; set; } = string.Empty;
        public string PlayoutReadKey { get; set; } = string.Empty;
        public double DelayBetweenRequests { get; set; } = 3.0;
        public string PlayoutApiUrl { get; set; } = "http://localhost:9180/BrMyriadPlayout/v6";
    }
}