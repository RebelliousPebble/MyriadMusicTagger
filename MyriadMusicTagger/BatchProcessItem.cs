namespace MyriadMusicTagger;

public class BatchProcessItem
{
    public int ItemNumber { get; set; }
    public string MediaLocation { get; set; } = string.Empty;
    
    public string OldTitle { get; set; } = string.Empty;
    public string OldArtist { get; set; } = string.Empty;
    
    public string NewTitle { get; set; } = string.Empty;
    public string NewArtist { get; set; } = string.Empty;
    
    public bool IsSelected { get; set; }
    public double ConfidenceScore { get; set; }
    public string Error { get; set; } = string.Empty;
    public MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? RecordingInfo { get; set; } // Made nullable
    public List<ProcessingUtils.FingerprintMatch> AvailableMatches { get; set; } = new List<ProcessingUtils.FingerprintMatch>();
}