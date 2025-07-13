// Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);

public class AudioFormat
{
   public bool Edited { get; set; }
   public bool InhibitEvents { get; set; }
   public int FormatType { get; set; }
   public int SampleRate { get; set; }
   public int BytesPerSecond { get; set; }
   public int Channels { get; set; }
   public int BitRate { get; set; }
   public bool HasBitRate { get; set; }
   public int BitsPerSample { get; set; }
   public int WavFormatTag { get; set; }
   public object? UserData { get; set; }
   public string? Description { get; set; }
   public string? LegacySerialisation { get; set; }
}

public class Copyright
{
   public string? CopyrightTitle { get; set; }
   public string? Performer { get; set; }
   public string? RecordingNumber { get; set; }
   public string? RecordLabel { get; set; }
   public string? Composer { get; set; }
   public string? Lyricist { get; set; }
   public string? Publisher { get; set; }
   public string? Promoter { get; set; }
   public string? Address { get; set; }
   public string? ISRC { get; set; }
   public string? PRS { get; set; }
   public string? License { get; set; }
   public string? TotalDuration { get; set; }
   public string? MusicDuration { get; set; }
}

public class Extro
{
   public string? End { get; set; }
   public string? Start { get; set; }
}

public class Levels
{
   public LS? LS { get; set; }
}

public class LS
{
   public double LM { get; set; }
   public double RM { get; set; }
   public double LRC { get; set; }
   public double RRC { get; set; }
}

public class MediaLength
{
   public string? End { get; set; }
}

public class Result
{
    public int MediaId { get; set; }
    public string? CutId { get; set; }
    public string? Guid { get; set; }
    public string? OriginalMediaLocation { get; set; }
    public Levels? Levels { get; set; }
    public int Type { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModDateTime { get; set; }
    public int TitleId { get; set; }
    public string? Title { get; set; }
    public string? AlbumTitle { get; set; }
    public string? TotalLength { get; set; }
    public AudioFormat? AudioFormat { get; set; }
    public Copyright? Copyright { get; set; }
    public int InformationLevel { get; set; }
    public bool FromDb { get; set; }
    public int ContentExists { get; set; }
    public int LocalContentExists { get; set; }
    public string? MediaLocation { get; set; }
    public MediaLength? MediaLength { get; set; }
    public Extro? Extro { get; set; }
    public List<ArtistInfo> Artists { get; set; } = new();
    public int FirstReleaseYear { get; set; }
}

public class ArtistInfo
{
    public int ArtistId { get; set; }
    public string? ArtistName { get; set; }
}

public class MyriadMediaItem
{
   public Result? Result { get; set; }
}

public class MyriadTitleSchema
{
   public List<string> Artists { get; set; } = new List<string>(); // Initialize to prevent null
   public string ItemTitle { get; set; } = string.Empty; // Initialize to prevent null
}