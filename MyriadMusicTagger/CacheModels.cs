namespace MyriadMusicTagger.Cache
{
    public class CachedAcoustIdResult
    {
        public string Fingerprint { get; set; } = string.Empty;
        public int Duration { get; set; }
        public List<RecordingIdScore> RecordingIdScores { get; set; } = new List<RecordingIdScore>();
        public DateTime CachedAt { get; set; } // To allow for future cache expiry logic
    }

    public class RecordingIdScore
    {
        public string MBRecordingId { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    public class CachedMusicBrainzRecording
    {
        public string MBRecordingId { get; set; } = string.Empty; // Primary Key
        public string Title { get; set; } = string.Empty;
        public string ArtistCreditName { get; set; } = string.Empty; // Primary artist name
        public List<string>? AllArtistNames { get; set; } // For "Artist1, Artist2 feat. Artist3"
        public string? AlbumTitle { get; set; }
        public string? ReleaseDate { get; set; } // Store as string for simplicity, e.g., "YYYY-MM-DD" or "YYYY"
        public string? Disambiguation { get; set; }
        public List<string>? Isrcs { get; set; }
        public double? UserRating { get; set; }
        public uint? UserRatingCount { get; set; }

        // Store the essential ArtistCredit details
        public List<CachedArtistCredit>? ArtistCredits { get; set; }
        // Store essential Release details (simplified)
        public List<CachedReleaseInfo>? Releases { get; set; }


        public DateTime CachedAt { get; set; } // To allow for future cache expiry logic
    }

    public class CachedArtistCredit
    {
        public string Name { get; set; } = string.Empty;
        public string JoinPhrase { get; set; } = string.Empty;
        // We might not need MBID for artist here, simplifying.
    }

    public class CachedReleaseInfo
    {
        public string MBId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? ReleaseDate { get; set; } // e.g. "YYYY-MM-DD"
        public string? Status { get; set; } // e.g. "Official"
        public string? Barcode { get; set; }
        public string? CountryCode { get; set; }
        // Simplified media structure, perhaps just track counts or basic format
        public List<CachedMedium>? Media { get; set; }
    }

    public class CachedMedium
    {
        public string? Format { get; set; }
        public int TrackCount { get; set; }
        public List<CachedTrack>? Tracks { get; set; }
    }

    public class CachedTrack
    {
        public string MBId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty; // Track number as string e.g. "A1"
        public int Length { get; set; } // in milliseconds
        // Optionally, the recording MBID this track points to, if different from the main one
        public string? RecordingMBId { get; set; }
    }
}
