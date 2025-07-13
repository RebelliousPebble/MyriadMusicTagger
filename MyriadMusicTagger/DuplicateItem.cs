using System.Collections.Generic;

namespace MyriadMusicTagger
{
    public class DuplicateItem
    {
        public int MediaId { get; set; }
        public string? Title { get; set; }
        public List<string> Artists { get; set; } = new();
        public bool Keep { get; set; }
        public string? Album { get; set; }
        public int Year { get; set; }
        public string? TotalLength { get; set; }
        public int BitRate { get; set; }
    }
}
