using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using AcoustID.Web;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Spectre.Console;

namespace MyriadMusicTagger;

public class ProcessingUtils
{
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
         AnsiConsole.MarkupLine("[red]Could not locate or download fpcalc. Fingerprinting is not possible.[/]");
         return new List<FingerprintMatch>();
      }

      // Get audio fingerprint
      var fingerprintData = GetAudioFingerprint(fpcalcPath, path);
      if (fingerprintData == null)
      {
         AnsiConsole.MarkupLine("[red]Failed to get audio fingerprint.[/]");
         return new List<FingerprintMatch>();
      }

      // Look up on MusicBrainz
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

      AnsiConsole.MarkupLine("[yellow]fpcalc not found. Attempting to download...[/]");

      try
      {
         // Determine the architecture and download appropriate version
         var arch = GetSystemArchitecture();
         var downloadUrl = GetDownloadUrl(arch);

         AnsiConsole.MarkupLine($"[blue]Downloading fpcalc for {arch} from {downloadUrl}[/]");

         if (DownloadAndExtractFpcalc(downloadUrl, fpcalcPath))
         {
            AnsiConsole.MarkupLine("[green]Successfully downloaded and extracted fpcalc[/]");
            return fpcalcPath;
         }

         return string.Empty;
      }
      catch (Exception ex)
      {
         AnsiConsole.MarkupLine($"[red]Error downloading fpcalc: {ex.Message}[/]");
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
            AnsiConsole.MarkupLine("[red]Could not find fpcalc.exe in the downloaded archive[/]");
            return false;
         }

         // Extract to destination
         fpcalcEntry.ExtractToFile(destinationPath, true);
         return true;
      }
      catch (Exception ex)
      {
         AnsiConsole.MarkupLine($"[red]Error during download/extraction: {ex.Message}[/]");
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
      AnsiConsole.MarkupLine("[blue]Calculating audio fingerprint...[/]");

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
         AnsiConsole.MarkupLine($"[red]Error running fpcalc: {ex.Message}[/]");
         return null;
      }
   }

   /// <summary>
   ///    Looks up multiple recordings based on fingerprint and duration
   /// </summary>
   private static List<FingerprintMatch> LookupRecordings(string fingerprint, int duration)
   {
      AnsiConsole.MarkupLine("[blue]Looking up on MusicBrainz...[/]");
      var service = new LookupService();
      var matches = new List<FingerprintMatch>();

      LookupResponse? results = null;
      try
      {
         results = service.GetAsync(fingerprint, duration, new[] { "recordingids" }).Result;

         // Retry once if the first attempt fails
         if (results == null)
         {
            AnsiConsole.MarkupLine("[yellow]First lookup attempt failed. Retrying...[/]");
            Thread.Sleep(200);
            results = service.GetAsync(fingerprint, duration, new[] { "recordingids" }).Result;

            if (results == null)
            {
               AnsiConsole.MarkupLine("[red]Lookup failed after retry.[/]");
               return matches;
            }
         }
      }
      catch (Exception ex)
      {
         AnsiConsole.MarkupLine($"[red]Error during AcoustID lookup: {ex.Message}[/]");
         return matches;
      }

      // Check for API errors
      if (!string.IsNullOrEmpty(results.ErrorMessage))
      {
         AnsiConsole.MarkupLine($"[red]AcoustId API error: {results.ErrorMessage}[/]");
         return matches;
      }

      // Get top matching recordings, ordered by score
      var topMatches = results.Results
         .Where(x => x.Recordings.Count > 0)
         .OrderByDescending(x => x.Score)
         .Take(MaxMatches)
         .ToList();

      if (topMatches.Count == 0)
      {
         AnsiConsole.MarkupLine("[yellow]No matching recordings found.[/]");
         return matches;
      }

      // Track whether we've found a high confidence match
      bool foundHighConfidenceMatch = false;
      
      // Look up details for each match
      foreach (var match in topMatches)
      {
         // If we already have a high confidence match (>90%), stop processing
         if (foundHighConfidenceMatch && match.Score < 0.9)
         {
            break;
         }

         foreach (var recording in match.Recordings.Take(MaxMatches - matches.Count))
         {
            AnsiConsole.MarkupLine($"[blue]Looking up details for match with ID: {recording.Id}[/]");
            var details = GetRecordingDetails(recording.Id);
            if (details != null)
            {
               matches.Add(new FingerprintMatch
               {
                  Score = match.Score,
                  RecordingInfo = details
               });

               // If this is a high confidence match, mark it
               if (match.Score >= 0.9)
               {
                  foundHighConfidenceMatch = true;
               }
            }

            // Pause briefly to avoid hitting rate limits
            Thread.Sleep(100);
         }

         // If we have a high confidence match, we can return early
         if (foundHighConfidenceMatch)
         {
            AnsiConsole.MarkupLine("[green]Found high confidence match, stopping further lookups.[/]");
            break;
         }
      }

      return matches;
   }

   /// <summary>
   ///    Gets detailed information about a recording using MusicBrainz API
   /// </summary>
   private static IRecording? GetRecordingDetails(string recordingId)
   {
      var query = new Query("MyriadTagger", Version.Parse("1.0"));

      try
      {
         var includeParams = Include.Aliases | Include.Artists | Include.Genres |
                             Include.Isrcs | Include.Releases | Include.Media |
                             Include.Tags | Include.Ratings;

         return query.LookupRecording(new Guid(recordingId), includeParams);
      }
      catch (Exception ex)
      {
         AnsiConsole.MarkupLine($"[red]Error looking up recording details: {ex.Message}[/]");
         return null;
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