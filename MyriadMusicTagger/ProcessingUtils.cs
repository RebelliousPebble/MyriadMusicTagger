using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using AcoustID;
using AcoustID.Web;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using MyriadMusicTagger.Cache; // Added for cache models
using Serilog;

namespace MyriadMusicTagger;

public class ProcessingUtils
{
    // Static instance of CacheManager, initialized in Program.cs or similar startup location
    // For now, we assume it's globally available or passed in.
    // For simplicity in this static class, let's assume a global/static accessor for now,
    // though dependency injection would be better in a larger app.
    // This will be properly initialized in Program.cs
    internal static CacheManager? CacheManagerInstance { get; set; }

    public class ProcessingException : Exception
    {
        public ProcessingException(string message) : base(message) { }
        public ProcessingException(string message, Exception innerException) : base(message, innerException) { }
    }

    private const string FpcalcExecutableName = "fpcalc.exe";
    private const string ChomaprintVersion = "1.5.0";
    private const string ChomaprintBaseDownloadUrl = "https://github.com/acoustid/chromaprint/releases/download/v";
    private const int MaxMatches = 10; // Maximum number of matches to return

    /// <summary>
    ///    Fingerprints an audio file using fpcalc and looks up the recording information.
    /// </summary>
    /// <param name="path">Path to the audio file</param>
    /// <returns>A list of recording matches ordered by score, or empty list if none found</returns>
    public static List<FingerprintMatch> Fingerprint(string path)
    {
        if (CacheManagerInstance == null)
        {
            Log.Warning("CacheManagerInstance is not initialized in ProcessingUtils. Caching will be skipped.");
            // Optionally, proceed without caching or throw a specific configuration error
        }

        // Ensure fpcalc is available
        var fpcalcPath = EnsureFpcalcExists();
      if (string.IsNullOrEmpty(fpcalcPath))
      {
         Log.Error("fpcalc could not be located or downloaded. Fingerprinting is not possible.");
         throw new ProcessingException("fpcalc is not available. Fingerprinting cannot proceed.");
      }

      // Get audio fingerprint
      Log.Information("Getting audio fingerprint for: {Path}", path);
      var fingerprintData = GetAudioFingerprint(fpcalcPath, path);
      if (fingerprintData == null)
      {
         Log.Error("Failed to get audio fingerprint for: {Path}", path);
         throw new ProcessingException($"Failed to get audio fingerprint for: {path}");
      }

      // Look up on MusicBrainz
      Log.Information("Looking up MusicBrainz data for fingerprint of: {Path}", path);
      return LookupRecordings(fingerprintData.Fingerprint, fingerprintData.Duration);
   }

   /// <summary>
   ///    Ensures that fpcalc exists, downloading it if necessary
   /// </summary>
   /// <returns>Path to fpcalc executable</returns>
   private static string EnsureFpcalcExists()
   {
      // Check if fpcalc exists in the current directory
      var fpcalcPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FpcalcExecutableName);

      if (File.Exists(fpcalcPath)) return fpcalcPath;

       Log.Warning("fpcalc not found at {FpcalcPath}. Attempting to download...", fpcalcPath);

      try
      {
         // Determine the architecture and download appropriate version
         var arch = GetSystemArchitecture();
         var downloadUrl = GetDownloadUrl(arch);

          Log.Information("Downloading fpcalc for {Architecture} from {Url}", arch, downloadUrl);

         if (DownloadAndExtractFpcalc(downloadUrl, fpcalcPath))
         {
             Log.Information("Successfully downloaded and extracted fpcalc to {FpcalcPath}", fpcalcPath);
            return fpcalcPath;
         }
          Log.Error("DownloadAndExtractFpcalc returned false. fpcalc not available.");
         return string.Empty;
      }
      catch (Exception ex)
      {
          Log.Error(ex, "Error downloading fpcalc");
         return string.Empty;
      }
   }

   /// <summary>
   ///    Gets the system architecture (x64 or x86)
   /// </summary>
   private static string GetSystemArchitecture()
   {
      return RuntimeInformation.ProcessArchitecture switch
      {
         Architecture.X64 => "x86_64",
         Architecture.X86 => "i686",
         Architecture.Arm64 => "arm64",
         _ => "x86_64" // Default to x64
      };
   }

   /// <summary>
   ///    Gets the download URL for the specified architecture
   /// </summary>
   private static string GetDownloadUrl(string arch)
   {
      var osPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
         RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" :
         "linux";

      return
         $"{ChomaprintBaseDownloadUrl}{ChomaprintVersion}/chromaprint-fpcalc-{ChomaprintVersion}-{osPrefix}-{arch}.zip";
   }

   /// <summary>
   ///    Downloads and extracts fpcalc
   /// </summary>
   private static bool DownloadAndExtractFpcalc(string downloadUrl, string destinationPath)
   {
      using var client = new HttpClient();
      var tempZipPath = Path.Combine(Path.GetTempPath(), "fpcalc.zip");

      try
      {
         // Download the file
         var zipData = client.GetByteArrayAsync(downloadUrl).Result;
         File.WriteAllBytes(tempZipPath, zipData);

         // Extract the file
         using var archive = ZipFile.OpenRead(tempZipPath);

         // Find the fpcalc executable in the archive
         var fpcalcEntry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals(FpcalcExecutableName, StringComparison.OrdinalIgnoreCase));

         if (fpcalcEntry == null)
         {
            Log.Error("Could not find {FpcalcExecutableName} in the downloaded archive from {DownloadUrl}", FpcalcExecutableName, downloadUrl);
            return false;
         }

         // Extract to destination
         fpcalcEntry.ExtractToFile(destinationPath, true);
         return true;
      }
      catch (Exception ex)
      {
         Log.Error(ex, "Error during download/extraction of fpcalc from {DownloadUrl}", downloadUrl);
         return false;
      }
      finally
      {
         if (File.Exists(tempZipPath))
            try
            {
               File.Delete(tempZipPath);
            }
            catch
            {
               /* ignore cleanup errors */
            }
      }
   }

   /// <summary>
   ///    Runs fpcalc to get the audio fingerprint
   /// </summary>
   private static FingerprintData? GetAudioFingerprint(string fpcalcPath, string audioFilePath)
   {
      Log.Information("Calculating audio fingerprint for {AudioFilePath} using {FpcalcPath}...", audioFilePath, fpcalcPath);

      var proc = new Process
      {
         StartInfo = new ProcessStartInfo
         {
            FileName = fpcalcPath,
            Arguments = $"\"{audioFilePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
         }
      };

      string? fingerprint = null;
      var duration = -1;

      try
      {
         proc.Start();
         proc.WaitForExit();

         while (!proc.StandardOutput.EndOfStream)
         {
            var line = proc.StandardOutput.ReadLine();
            if (line == null) continue;

            if (line.StartsWith("DURATION="))
               duration = int.Parse(line.Replace("DURATION=", ""));
            else if (line.StartsWith("FINGERPRINT=")) fingerprint = line.Replace("FINGERPRINT=", "");
         }

         if (fingerprint == null || duration == -1) return null;

         return new FingerprintData { Fingerprint = fingerprint, Duration = duration };
      }
      catch (Exception ex)
      {
         Log.Error(ex, "Error running fpcalc for {AudioFilePath}", audioFilePath);
         // Return null, the caller (Fingerprint method) will throw a ProcessingException.
         return null;
      }
   }

   /// <summary>
   ///    Looks up multiple recordings based on fingerprint and duration
   /// </summary>
   private static List<FingerprintMatch> LookupRecordings(string fingerprint, int duration)
   {
      Log.Information("Looking up fingerprint on AcoustID/MusicBrainz (Duration: {Duration}s)...", duration);
      Log.Debug("Fingerprint length: {FingerprintLength} characters", fingerprint.Length);
      var matches = new List<FingerprintMatch>();

      // 1. Check Cache for AcoustID result first
      var cachedAcoustId = CacheManagerInstance?.GetCachedAcoustIdResult(fingerprint, duration);
      if (cachedAcoustId?.RecordingIdScores?.Any() == true)
      {
            Log.Information("AcoustID cache hit for fingerprint. Processing {Count} cached MBIDs.", cachedAcoustId.RecordingIdScores.Count);
            // We have cached AcoustID results (list of MBID + score).
            // Now, for each of these, try to get MusicBrainz details (from cache or API).
            bool foundHighConfidence = false;
            foreach (var idScore in cachedAcoustId.RecordingIdScores.OrderByDescending(s => s.Score).Take(MaxMatches))
            {
                if (foundHighConfidence && idScore.Score < 0.9) break;

                var mbDetails = GetRecordingDetails(idScore.MBRecordingId); // This will use MB cache
                if (mbDetails != null)
                {
                    matches.Add(new FingerprintMatch { Score = idScore.Score, RecordingInfo = mbDetails });
                    if (idScore.Score >= 0.9) foundHighConfidence = true;
                }
                if (matches.Count >= MaxMatches) break;
            }
            Log.Information("Finished processing cached AcoustID results. Found {MatchCount} matches.", matches.Count);
            return matches;
      }
      Log.Information("AcoustID cache miss or empty result for fingerprint. Proceeding with API lookup.");

      // --- If not in AcoustID Cache, proceed with API lookup ---
      if (string.IsNullOrEmpty(Configuration.ClientKey))
      {
         Log.Error("AcoustID Configuration.ClientKey is not set! This will cause API calls to fail or hang.");
         throw new ProcessingException("AcoustID client key is not configured. Please check your settings.");
      }
      Log.Information("AcoustID Configuration.ClientKey is configured (length: {KeyLength})", Configuration.ClientKey.Length);
      Log.Debug("AcoustID ClientKey starts with: {KeyStart}...", Configuration.ClientKey.Length > 4 ? Configuration.ClientKey.Substring(0, 4) : Configuration.ClientKey);
      
      var service = new LookupService();
      LookupResponse? acoustIdApiResponse = null;
      try
      {
         Log.Information("Calling AcoustID service.GetAsync() with 30 second timeout...");
         var lookupTask = Task.Run(async () => await service.GetAsync(fingerprint, duration, new[] { "recordingids" }));

         if (lookupTask.Wait(TimeSpan.FromSeconds(30)))
         {
            acoustIdApiResponse = lookupTask.Result;
            Log.Information("AcoustID service.GetAsync() completed successfully.");
         }
         else
         {
            Log.Error("AcoustID service.GetAsync() timed out after 30 seconds.");
            throw new ProcessingException("AcoustID lookup timed out. Service may be unavailable.");
         }

         if (acoustIdApiResponse == null) // Retry once
         {
            Log.Warning("AcoustID lookup returned null. Retrying with 30 second timeout...");
            Thread.Sleep(200); // Brief pause before retry
            var retryTask = Task.Run(async () => await service.GetAsync(fingerprint, duration, new[] { "recordingids" }));
            if (retryTask.Wait(TimeSpan.FromSeconds(30)))
            {
               acoustIdApiResponse = retryTask.Result;
               if (acoustIdApiResponse != null) Log.Information("AcoustID retry succeeded.");
               else throw new ProcessingException("AcoustID lookup failed after retry (service returned null).");
            }
            else
            {
               Log.Error("AcoustID retry timed out.");
               throw new ProcessingException("AcoustID retry timed out.");
            }
         }
      }
      catch (Exception ex)
      {
         Log.Error(ex, "Exception during AcoustID lookup for fingerprint.");
         throw new ProcessingException($"Error during AcoustID lookup: {ex.Message}", ex);
      }

      if (!string.IsNullOrEmpty(acoustIdApiResponse.ErrorMessage))
      {
         Log.Error("AcoustID API error: {ErrorMessage}", acoustIdApiResponse.ErrorMessage);
         throw new ProcessingException($"AcoustID API error: {acoustIdApiResponse.ErrorMessage}");
      }

      if (acoustIdApiResponse.Results == null || !acoustIdApiResponse.Results.Any())
      {
        Log.Information("No results found in AcoustID API response for fingerprint.");
        // Cache this empty result? For now, no. So next time it will try API again.
        return matches;
      }

      // 2. Cache the AcoustID API Response
      CacheManagerInstance?.CacheAcoustIdResult(fingerprint, duration, acoustIdApiResponse);
      Log.Information("AcoustID API returned {ResultCount} raw results.", acoustIdApiResponse.Results.Count());

      var topAcoustIdMatches = acoustIdApiResponse.Results
         .Where(x => x.Recordings != null && x.Recordings.Any())
         .OrderByDescending(x => x.Score)
         .Take(MaxMatches)
         .ToList();

      if (!topAcoustIdMatches.Any())
      {
         Log.Information("No matching recordings found after filtering AcoustID API results.");
         return matches;
      }
      Log.Information("Found {Count} potential AcoustID matches from API. Starting MusicBrainz lookups...", topAcoustIdMatches.Count);

      bool foundHighConfidenceMatch = false;
      foreach (var apiMatch in topAcoustIdMatches)
      {
         Log.Information("Processing AcoustID API match with score {Score} ({RecordingCount} recordings)...", apiMatch.Score, apiMatch.Recordings?.Count ?? 0);
         if (foundHighConfidenceMatch && apiMatch.Score < 0.9)
         {
            Log.Information("Skipping lower confidence API match (Score: {Score}) as a high confidence one was already found.", apiMatch.Score);
            break;
         }

         if (apiMatch.Recordings == null)
         {
            Log.Warning("Match has null recordings, skipping...");
            continue;
         }

         foreach (var acoustIdRecording in apiMatch.Recordings.Take(MaxMatches - matches.Count)) // Ensure we don't exceed MaxMatches overall
         {
            Log.Information("About to lookup MusicBrainz details for AcoustID API match (Score: {Score}), MBID: {RecordingId}", apiMatch.Score, acoustIdRecording.Id);
            // GetRecordingDetails will first check cache, then API, then cache the API result.
            var mbDetails = GetRecordingDetails(acoustIdRecording.Id.ToString());
            Log.Information("MusicBrainz lookup completed for MBID: {RecordingId}. Result: {HasDetails}", acoustIdRecording.Id, mbDetails != null ? "Success" : "Failed/Null");
            
            if (mbDetails != null)
            {
               matches.Add(new FingerprintMatch
               {
                  Score = apiMatch.Score, // Use the score from AcoustID
                  RecordingInfo = mbDetails
               });
               Log.Information("Added match to results. Total matches so far: {MatchCount}", matches.Count);

               if (apiMatch.Score >= 0.9)
               {
                  Log.Information("High confidence match (Score: {Score}) found for MBID: {RecordingId}", apiMatch.Score, acoustIdRecording.Id);
                  foundHighConfidenceMatch = true;
               }
            }
            // Rate limiting is handled by MusicBrainz.NET library and our CacheManager helps reduce calls
         }

         if (foundHighConfidenceMatch)
         {
            Log.Information("High confidence match found, stopping further lookups for this fingerprint batch.");
            break;
         }
      }
      Log.Information("Finished processing {Count} matches from MusicBrainz (via API route).", matches.Count);
      return matches;
   }

    /// <summary>
    /// Gets detailed information about a recording.
    /// It first checks the cache. If not found, it queries the MusicBrainz API,
    /// then caches the result.
    /// </summary>
    private static IRecording? GetRecordingDetails(string recordingId)
    {
        if (!Guid.TryParse(recordingId, out Guid parsedGuid))
        {
            Log.Warning("Invalid GUID format for recordingId: {RecordingId}", recordingId);
            return null;
        }
        string canonicalMbId = parsedGuid.ToString(); // Use canonical form for lookups

        // 1. Check Cache for MusicBrainz recording
        var cachedMbRecording = CacheManagerInstance?.GetCachedMusicBrainzRecording(canonicalMbId);
        if (cachedMbRecording != null)
        {
            Log.Information("MusicBrainz cache hit for Recording MBID: {MBRecordingId}", canonicalMbId);
            // Convert CachedMusicBrainzRecording back to an IRecording. This is tricky as IRecording is an interface.
            // We will need to create a simple implementation or map to a suitable existing one if possible.
            // For now, let's create a simple adapter.
            return new CachedMbRecordingAdapter(cachedMbRecording);
        }
        Log.Information("MusicBrainz cache miss for Recording MBID: {MBRecordingId}. Querying API.", canonicalMbId);

        // 2. If not in cache, query MusicBrainz API
        var query = new Query("MyriadTagger", Version.Parse("1.0")); // App name and version
        Log.Debug("Created MusicBrainz Query instance. Current DelayBetweenRequests: {Delay}s", Query.DelayBetweenRequests);
        IRecording? apiRecording = null;
        try
        {
            var includeParams = Include.Artists | Include.Releases | Include.Media | Include.Isrcs | Include.Tags | Include.Ratings | Include.Aliases | Include.Genres;
            apiRecording = query.LookupRecording(parsedGuid, includeParams);
            Log.Information("MusicBrainz API lookup completed for MBID: {ParsedGuid}. Result: {HasResult}", parsedGuid, apiRecording != null ? "Success" : "Null");

            // 3. Cache the API response
            if (apiRecording != null)
            {
                CacheManagerInstance?.CacheMusicBrainzRecording(apiRecording);
            }
            else
            {
                // Optionally, cache a "not found" result for a short period to avoid hammering the API for known misses.
                // For now, we only cache successful lookups.
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error looking up MusicBrainz recording details via API for ID: {RecordingId}", canonicalMbId);
            return null; // Return null, don't throw, allow processing of other matches
        }
        return apiRecording;
    }

   /// <summary>
   ///    Represents a fingerprint match result with score and recording information
   /// </summary>
   public class FingerprintMatch
   {
      public double Score { get; set; }
      public IRecording? RecordingInfo { get; set; }
   }

   /// <summary>
   ///    Data class to hold fingerprint information
   /// </summary>
   private class FingerprintData
   {
      public string Fingerprint { get; set; } = null!;
      public int Duration { get; set; }
   }
}

// Adapter class to make CachedMusicBrainzRecording usable as IRecording
// This will be a simplified adapter, implementing only the properties
// actually used by MyriadMusicTagger from IRecording.
// Other properties will return default or null values.
internal class CachedMbRecordingAdapter : IRecording
{
    private readonly CachedMusicBrainzRecording _cached;

    public CachedMbRecordingAdapter(CachedMusicBrainzRecording cachedData)
    {
        _cached = cachedData ?? throw new ArgumentNullException(nameof(cachedData));
    }

    public Guid Id => Guid.TryParse(_cached.MBRecordingId, out var guid) ? guid : Guid.Empty;
    public string? Title => _cached.Title;
    public int? Length => null; // Not explicitly stored in CachedMusicBrainzRecording in a directly usable format for IRecording.Length (TimeSpan?)
                                // If needed, this would require conversion or storing it differently. For now, default.
    public string? Disambiguation => _cached.Disambiguation;
    public bool? Video => null; // Not stored

    public IReadOnlyList<INameCredit>? ArtistCredit => _cached.ArtistCredits?.Select(ac => new CachedNameCreditAdapter(ac) as INameCredit).ToList()?.AsReadOnly();
    public IReadOnlyList<IRelease>? Releases => _cached.Releases?.Select(r => new CachedReleaseAdapter(r) as IRelease).ToList()?.AsReadOnly();
    public IReadOnlyList<IAlias>? Aliases => null; // Not stored in detail, could be if necessary
    public IReadOnlyList<IGenre>? Genres => null; // Not stored
    public IReadOnlyList<IIsrc>? Isrcs => _cached.Isrcs?.Select(isrc => new CachedIsrcAdapter(isrc) as IIsrc).ToList()?.AsReadOnly();
    public IReadOnlyList<IRelationship>? Relationships => null; // Not stored
    public IReadOnlyList<ITag>? Tags => null; // Not stored
    public IRating? Rating => _cached.UserRating.HasValue ? new CachedRatingAdapter(_cached.UserRating.Value, _cached.UserRatingCount ?? 0) : null;
    public IUserRating? UserRating => _cached.UserRating.HasValue ? new CachedUserRatingAdapter(_cached.UserRating.Value) : null; // Assuming UserRating is what's stored
    public IReadOnlyList<IUserTag>? UserTags => null; // Not stored

    // IAnnotatedEntity
    public string? Annotation => null; // Not stored
    // IMbEntity
    public MbEntityType EntityType => MbEntityType.Recording;
    public string? EntityId => _cached.MBRecordingId;
    // INamedEntity
    public string? Name => Title; // For INamedEntity, Name is often Title for recordings
    public string? SortName => null; // Not stored
    // IRelatableEntity - no members to implement from here directly
    // ITaggableEntity - no members to implement from here directly
    // IWork 예술 작품 - no members?
    public IWork? Work => null; // Not stored
    public IReadOnlyList<IArtist>? Artists => ArtistCredit?.Select(ac => ac.Artist).ToList().AsReadOnly(); // Simplified
    public IReadOnlyList<IArtistCredit>? ArtistCredits => ArtistCredit; // Already IReadOnlyList<INameCredit>
}

internal class CachedNameCreditAdapter : INameCredit
{
    private readonly CachedArtistCredit _cached;
    public CachedNameCreditAdapter(CachedArtistCredit cached) { _cached = cached; }
    public IArtist? Artist => new CachedArtistAdapter(_cached.Name); // Simplified
    public string? Name => _cached.Name;
    public string? JoinPhrase => _cached.JoinPhrase;
}

internal class CachedArtistAdapter : IArtist
{
    public CachedArtistAdapter(string name) { Name = name; }
    public string? Name { get; }
    public Guid Id => Guid.Empty; // Not stored for basic artist reference
    public string? SortName => null;
    public string? Disambiguation => null;
    public string? Type => null; // Person, Group, etc.
    public string? TypeId => null;
    public string? Gender => null;
    public string? GenderId => null;
    public string? Country => null; // Country code
    public IArea? Area => null;
    public IArea? BeginArea => null;
    public IArea? EndArea => null;
    public ILifeSpan? LifeSpan => null;
    public IReadOnlyList<IAlias>? Aliases => null;
    public IReadOnlyList<IGenre>? Genres => null;
    public IReadOnlyList<IIpiCode>? Ipis => null;
    public IReadOnlyList<IIsniCode>? Isnis => null;
    public IReadOnlyList<IRecording>? Recordings => null;
    public IReadOnlyList<IRelease>? Releases => null;
    public IReadOnlyList<IReleaseGroup>? ReleaseGroups => null;
    public IReadOnlyList<IRelationship>? Relationships => null;
    public IReadOnlyList<ITag>? Tags => null;
    public IRating? Rating => null;
    public IUserRating? UserRating => null;
    public IReadOnlyList<IUserTag>? UserTags => null;
    public string? Annotation => null;
    public MbEntityType EntityType => MbEntityType.Artist;
    public string? EntityId => null;
}


internal class CachedReleaseAdapter : IRelease
{
    private readonly CachedReleaseInfo _cached;
    public CachedReleaseAdapter(CachedReleaseInfo cached) { _cached = cached; }
    public Guid Id => Guid.TryParse(_cached.MBId, out var g) ? g : Guid.Empty;
    public string? Title => _cached.Title;
    public string? Disambiguation => null;
    public string? Packaging => null;
    public string? PackagingId => null;
    public string? Status => _cached.Status;
    public string? StatusId => null;
    public string? TextRepresentationLanguage => null;
    public string? TextRepresentationScript => null;
    public PartialDate? Date => PartialDate.TryParse(_cached.ReleaseDate, out var pd) ? pd : null;
    public string? Country => _cached.CountryCode;
    public string? Barcode => _cached.Barcode;
    public string? Asin => null; // Not stored
    public ICoverArtArchive? CoverArtArchive => null; // Not stored
    public IReadOnlyList<INameCredit>? ArtistCredit => null; // Not directly on CachedReleaseInfo, but could be added if needed
    public IReadOnlyList<ILabelInfo>? LabelInfo => null; // Not stored
    public IReadOnlyList<IMedium>? Media => _cached.Media?.Select(m => new CachedMediumAdapter(m) as IMedium).ToList()?.AsReadOnly();
    public IReleaseGroup? ReleaseGroup => null; // Not stored
    public IReadOnlyList<IAlias>? Aliases => null;
    public IReadOnlyList<IGenre>? Genres => null;
    public IReadOnlyList<IRelationship>? Relationships => null;
    public IReadOnlyList<ITag>? Tags => null;
    public IRating? Rating => null;
    public IUserRating? UserRating => null;
    public IReadOnlyList<IUserTag>? UserTags => null;
    public string? Annotation => null;
    public MbEntityType EntityType => MbEntityType.Release;
    public string? EntityId => _cached.MBId;
    public string? Name => Title;
    public string? SortName => null;
    public Quality Quality => Quality.Normal; // Default
}

internal class CachedMediumAdapter : IMedium
{
    private readonly CachedMedium _cached;
    public CachedMediumAdapter(CachedMedium cached) { _cached = cached; }
    public string? Format => _cached.Format;
    public string? FormatId => null;
    public IDisc? Disc => null; // Not stored
    public IReadOnlyList<ITrack>? Tracks => _cached.Tracks?.Select(t => new CachedTrackAdapter(t) as ITrack).ToList()?.AsReadOnly();
    public int? Position => null; // Not stored
    public string? Title => null; // Medium title not stored
    public int TrackCount => _cached.TrackCount;
    public int? TrackOffset => null; // Not stored
}

internal class CachedTrackAdapter : ITrack
{
    private readonly CachedTrack _cached;
    // We need a reference to the parent recording adapter to avoid recursion if a track's recording is the same.
    // However, to keep it simple for now, and because the primary IRecording is already the main object,
    // we will return null for track-level recording to prevent cycles during cache reconstruction.
    public CachedTrackAdapter(CachedTrack cached) { _cached = cached; }
    public Guid Id => Guid.TryParse(_cached.MBId, out var g) ? g : Guid.Empty;
    public IRecording? Recording => null; // Simplified to avoid recursive lookups from cache. The main recording is already available.
    public IReadOnlyList<INameCredit>? ArtistCredit => null; // Not on track level in cache
    public TimeSpan? Length => TimeSpan.FromMilliseconds(_cached.Length);
    public string? Number => _cached.Number;
    public int Position => int.TryParse(_cached.Number, out var p) ? p : 0; // Best effort
    public string? Title => _cached.Title;
    public string? Name => Title;
    public string? SortName => null;
    public string? Disambiguation => null;
    public MbEntityType EntityType => MbEntityType.Track;
    public string? EntityId => _cached.MBId;
    public IReadOnlyList<IAlias>? Aliases => null;
    public IRating? Rating => null;
    public IUserRating? UserRating => null;
    public IReadOnlyList<IGenre>? Genres => null;
    public IReadOnlyList<IRelationship>? Relationships => null;
    public IReadOnlyList<ITag>? Tags => null;
    public IReadOnlyList<IUserTag>? UserTags => null;
    public string? Annotation => null;
}


internal class CachedIsrcAdapter : IIsrc
{
    private readonly string _isrcValue;
    public CachedIsrcAdapter(string isrc) { _isrcValue = isrc; }
    public string Id => _isrcValue;
    public IReadOnlyList<IRecording>? Recordings => null; // Not linking back from cache
    public MbEntityType EntityType => MbEntityType.Isrc;
    public string? EntityId => _isrcValue;
}

internal class CachedRatingAdapter : IRating
{
    public CachedRatingAdapter(double value, uint count) { Value = value; VoteCount = count; }
    public double Value { get; }
    public uint VoteCount { get; }
}

internal class CachedUserRatingAdapter : IUserRating
{
    public CachedUserRatingAdapter(double value) { Value = value; }
    public double Value { get; }
    public DateTimeOffset? SubmissionTime => null; // Not stored
}