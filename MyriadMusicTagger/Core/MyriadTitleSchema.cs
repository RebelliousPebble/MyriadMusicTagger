namespace MyriadMusicTagger.Core
{
    /// <summary>
    /// Represents a request to update item metadata in the Myriad system
    /// </summary>
    public class MyriadTitleSchema
    {
        public string ItemTitle { get; set; } = string.Empty;
        public List<string> Artists { get; set; } = new List<string>();
    }
}
