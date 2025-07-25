﻿using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using AcoustID;
using AcoustID.Web;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Serilog; // Added Serilog for logging

namespace MyriadMusicTagger;

public class ProcessingUtils
{
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
      
      // Check if AcoustID is properly configured
      if (string.IsNullOrEmpty(Configuration.ClientKey))
      {
         Log.Error("AcoustID Configuration.ClientKey is not set! This will cause API calls to fail or hang.");
         throw new ProcessingException("AcoustID client key is not configured. Please check your settings.");
      }
      Log.Information("AcoustID Configuration.ClientKey is configured (length: {KeyLength})", Configuration.ClientKey.Length);
      Log.Debug("AcoustID ClientKey starts with: {KeyStart}...", Configuration.ClientKey.Length > 4 ? Configuration.ClientKey.Substring(0, 4) : Configuration.ClientKey);
      
      Log.Information("Creating AcoustID LookupService...");
      var service = new LookupService();
      Log.Information("LookupService created successfully.");
      var matches = new List<FingerprintMatch>();

      LookupResponse? results = null;
      try
      {
         Log.Information("Calling AcoustID service.GetAsync() with 30 second timeout...");
         
         // Use Task.Run with timeout to prevent indefinite hanging
         var lookupTask = Task.Run(async () => 
         {
            return await service.GetAsync(fingerprint, duration, new[] { "recordingids" });
         });

         // Wait for the task to complete with a 30-second timeout
         if (lookupTask.Wait(TimeSpan.FromSeconds(30)))
         {
            results = lookupTask.Result;
            Log.Information("AcoustID service.GetAsync() completed successfully.");
         }
         else
         {
            Log.Error("AcoustID service.GetAsync() timed out after 30 seconds.");
            throw new ProcessingException("AcoustID lookup timed out after 30 seconds. This may indicate a network issue or service unavailability.");
         }

         // Retry once if the first attempt fails (e.g. service unavailable temporarily)
         if (results == null)
         {
            Log.Warning("AcoustID lookup returned null on first attempt. Retrying with 30 second timeout...");
            Thread.Sleep(200);
            
            var retryTask = Task.Run(async () => 
            {
               return await service.GetAsync(fingerprint, duration, new[] { "recordingids" });
            });

            if (retryTask.Wait(TimeSpan.FromSeconds(30)))
            {
               results = retryTask.Result;
               if (results != null)
               {
                  Log.Information("AcoustID retry succeeded.");
               }
               else
               {
                  Log.Error("AcoustID lookup returned null after retry. Cannot proceed with this fingerprint.");
                  throw new ProcessingException("AcoustID lookup failed after retry (service returned null).");
               }
            }
            else
            {
               Log.Error("AcoustID retry timed out after 30 seconds.");
               throw new ProcessingException("AcoustID retry timed out after 30 seconds.");
            }
         }
      }
      catch (Exception ex)
      {
         Log.Error(ex, "Exception during AcoustID lookup for fingerprint. Exception type: {ExceptionType}", ex.GetType().Name);
         Log.Error("Exception message: {Message}", ex.Message);
         if (ex.InnerException != null)
         {
            Log.Error("Inner exception: {InnerExceptionType} - {InnerMessage}", ex.InnerException.GetType().Name, ex.InnerException.Message);
         }
         throw new ProcessingException($"Error during AcoustID lookup: {ex.Message}", ex);
      }

      Log.Information("AcoustID lookup completed. Checking response for errors...");

      Log.Information("AcoustID lookup completed. Checking response for errors...");
      // Check for API errors in the response object itself
      if (!string.IsNullOrEmpty(results.ErrorMessage))
      {
         Log.Error("AcoustID API error: {ErrorMessage}", results.ErrorMessage);
         throw new ProcessingException($"AcoustID API error: {results.ErrorMessage}");
      }

      Log.Information("No API errors found. Checking for results...");
      if (results.Results == null || !results.Results.Any())
      {
        Log.Information("No results found in AcoustID response for fingerprint.");
        return matches; // No results, not an error
      }

      Log.Information("AcoustID returned {ResultCount} raw results.", results.Results.Count());

      Log.Information("AcoustID returned {ResultCount} raw results.", results.Results.Count());
      // Get top matching recordings, ordered by score
      var topMatches = results.Results
         .Where(x => x.Recordings != null && x.Recordings.Count > 0)
         .OrderByDescending(x => x.Score)
         .Take(MaxMatches)
         .ToList();

      if (topMatches.Count == 0)
      {
         Log.Information("No matching recordings found after filtering AcoustID results.");
         return matches;
      }
      Log.Information("Found {Count} potential AcoustID matches after filtering. Starting MusicBrainz lookups...", topMatches.Count);

      // Track whether we've found a high confidence match
      bool foundHighConfidenceMatch = false;
      
      Log.Information("Starting to process {MatchCount} AcoustID matches...", topMatches.Count);
      // Look up details for each match
      foreach (var match in topMatches)
      {
         Log.Information("Processing AcoustID match with score {Score} ({RecordingCount} recordings)...", match.Score, match.Recordings?.Count ?? 0);
         
         // If we already have a high confidence match (>90%), stop processing further potential matches
         if (foundHighConfidenceMatch && match.Score < 0.9)
         {
            Log.Information("Skipping lower confidence match (Score: {Score}) as a high confidence one was already found.", match.Score);
            break;
         }

         if (match.Recordings == null) 
         {
            Log.Warning("Match has null recordings, skipping...");
            continue;
         }

         foreach (var recording in match.Recordings.Take(MaxMatches - matches.Count)) // Ensure we don't exceed MaxMatches overall
         {
            Log.Information("About to lookup MusicBrainz details for AcoustID match (Score: {Score}), MBID: {RecordingId}", match.Score, recording.Id);
            var details = GetRecordingDetails(recording.Id); // This method handles its own logging for errors
            Log.Information("MusicBrainz lookup completed for MBID: {RecordingId}. Result: {HasDetails}", recording.Id, details != null ? "Success" : "Failed/Null");
            
            if (details != null)
            {
               matches.Add(new FingerprintMatch
               {
                  Score = match.Score,
                  RecordingInfo = details
               });
               Log.Information("Added match to results. Total matches so far: {MatchCount}", matches.Count);

               // If this is a high confidence match, mark it
               if (match.Score >= 0.9)
               {
                  Log.Information("High confidence match (Score: {Score}) found for MBID: {RecordingId}", match.Score, recording.Id);
                  foundHighConfidenceMatch = true;
               }
            }

            // The MusicBrainz library now handles rate limiting automatically via Query.DelayBetweenRequests
         }

         // If we have a high confidence match, we can break from the outer loop too
         if (foundHighConfidenceMatch)
         {
            Log.Information("High confidence match found, stopping further lookups for this fingerprint batch.");
            break;
         }
      }
      Log.Information("Finished processing {Count} matches from MusicBrainz.", matches.Count);
      return matches;
   }

   /// <summary>
   ///    Gets detailed information about a recording using MusicBrainz API
   /// </summary>
   private static IRecording? GetRecordingDetails(string recordingId)
   {
      Log.Information("Starting MusicBrainz lookup for recording ID: {RecordingId}", recordingId);
      
      // Create a query instance but don't override the global DelayBetweenRequests
      var query = new Query("MyriadTagger", Version.Parse("1.0"));
      Log.Debug("Created MusicBrainz Query instance. Current DelayBetweenRequests: {Delay}s", Query.DelayBetweenRequests);

      try
      {
         // Ensure recordingId is a valid GUID
         if (!Guid.TryParse(recordingId, out Guid parsedGuid))
         {
            Log.Warning("Invalid GUID format for recordingId: {RecordingId}", recordingId);
            return null;
         }

         Log.Information("About to call query.LookupRecording for MBID: {ParsedGuid}", parsedGuid);
         var includeParams = Include.Aliases | Include.Artists | Include.Genres |
                             Include.Isrcs | Include.Releases | Include.Media |
                             Include.Tags | Include.Ratings;

         var result = query.LookupRecording(parsedGuid, includeParams);
         Log.Information("query.LookupRecording completed for MBID: {ParsedGuid}. Result: {HasResult}", parsedGuid, result != null ? "Success" : "Null");
         return result;
      }
      catch (Exception ex)
      {
         // Specific logging for MusicBrainz lookup failure
         Log.Error(ex, "Error looking up MusicBrainz recording details for ID: {RecordingId}. Exception type: {ExceptionType}", recordingId, ex.GetType().Name);
         Log.Error("MusicBrainz exception message: {Message}", ex.Message);
         if (ex.InnerException != null)
         {
            Log.Error("MusicBrainz inner exception: {InnerExceptionType} - {InnerMessage}", ex.InnerException.GetType().Name, ex.InnerException.Message);
         }
         return null; // Return null, don't throw, allow processing of other matches
      }
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