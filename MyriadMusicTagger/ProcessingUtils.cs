using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using AcoustID.Web;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;

namespace MyriadMusicTagger;

public class ProcessingUtils
{
    private const string FpcalcExecutableName = "fpcalc.exe";
    private const string ChomaprintVersion = "1.5.0";
    private const string ChomaprintBaseDownloadUrl = "https://github.com/acoustid/chromaprint/releases/download/v";

    /// <summary>
    /// Fingerprints an audio file using fpcalc and looks up the recording information.
    /// </summary>
    /// <param name="path">Path to the audio file</param>
    /// <returns>The recording information if found, null otherwise</returns>
    public static IRecording? Fingerprint(string path)
    {
        // Ensure fpcalc is available
        string fpcalcPath = EnsureFpcalcExists();
        if (string.IsNullOrEmpty(fpcalcPath))
        {
            Console.WriteLine("Could not locate or download fpcalc. Fingerprinting is not possible.");
            return null;
        }

        // Get audio fingerprint
        var fingerprintData = GetAudioFingerprint(fpcalcPath, path);
        if (fingerprintData == null)
        {
            Console.WriteLine("Failed to get audio fingerprint.");
            return null;
        }

        // Look up on MusicBrainz
        return LookupRecording(fingerprintData.Fingerprint, fingerprintData.Duration);
    }

    /// <summary>
    /// Ensures that fpcalc exists, downloading it if necessary
    /// </summary>
    /// <returns>Path to fpcalc executable</returns>
    private static string EnsureFpcalcExists()
    {
        // Check if fpcalc exists in the current directory
        string fpcalcPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FpcalcExecutableName);
        
        if (File.Exists(fpcalcPath))
        {
            return fpcalcPath;
        }

        Console.WriteLine("fpcalc not found. Attempting to download...");
        
        try
        {
            // Determine the architecture and download appropriate version
            string arch = GetSystemArchitecture();
            string downloadUrl = GetDownloadUrl(arch);
            
            Console.WriteLine($"Downloading fpcalc for {arch} from {downloadUrl}");
            
            if (DownloadAndExtractFpcalc(downloadUrl, fpcalcPath))
            {
                Console.WriteLine("Successfully downloaded and extracted fpcalc");
                return fpcalcPath;
            }
            
            return string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading fpcalc: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the system architecture (x64 or x86)
    /// </summary>
    private static string GetSystemArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x86_64" // Default to x64
        };
    }

    /// <summary>
    /// Gets the download URL for the specified architecture
    /// </summary>
    private static string GetDownloadUrl(string arch)
    {
        string osPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : 
                         RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" :
                         "linux";
        
        return $"{ChomaprintBaseDownloadUrl}{ChomaprintVersion}/chromaprint-fpcalc-{ChomaprintVersion}-{osPrefix}-{arch}.zip";
    }

    /// <summary>
    /// Downloads and extracts fpcalc
    /// </summary>
    private static bool DownloadAndExtractFpcalc(string downloadUrl, string destinationPath)
    {
        using var client = new HttpClient();
        string tempZipPath = Path.Combine(Path.GetTempPath(), "fpcalc.zip");
        
        try
        {
            // Download the file
            byte[] zipData = client.GetByteArrayAsync(downloadUrl).Result;
            File.WriteAllBytes(tempZipPath, zipData);
            
            // Extract the file
            using var archive = ZipFile.OpenRead(tempZipPath);
            
            // Find the fpcalc executable in the archive
            var fpcalcEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals(FpcalcExecutableName, StringComparison.OrdinalIgnoreCase));
            
            if (fpcalcEntry == null)
            {
                Console.WriteLine("Could not find fpcalc.exe in the downloaded archive");
                return false;
            }

            // Extract to destination
            fpcalcEntry.ExtractToFile(destinationPath, true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during download/extraction: {ex.Message}");
            return false;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* ignore cleanup errors */ }
            }
        }
    }

    /// <summary>
    /// Data class to hold fingerprint information
    /// </summary>
    private class FingerprintData
    {
        public string Fingerprint { get; set; } = null!;
        public int Duration { get; set; }
    }

    /// <summary>
    /// Runs fpcalc to get the audio fingerprint
    /// </summary>
    private static FingerprintData? GetAudioFingerprint(string fpcalcPath, string audioFilePath)
    {
        Console.WriteLine("Calculating audio fingerprint...");
        
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
                {
                    duration = int.Parse(line.Replace("DURATION=", ""));
                }
                else if (line.StartsWith("FINGERPRINT="))
                {
                    fingerprint = line.Replace("FINGERPRINT=", "");
                }
            }
            
            if (fingerprint == null || duration == -1)
            {
                return null;
            }
            
            return new FingerprintData { Fingerprint = fingerprint, Duration = duration };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running fpcalc: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Looks up a recording based on fingerprint and duration
    /// </summary>
    private static IRecording? LookupRecording(string fingerprint, int duration)
    {
        Console.WriteLine("Looking up on MusicBrainz...");
        var service = new LookupService();
        
        LookupResponse? results = null;
        try
        {
            results = service.GetAsync(fingerprint, duration, new[] { "recordingids" }).Result;
            
            // Retry once if the first attempt fails
            if (results == null)
            {
                Console.WriteLine("First lookup attempt failed. Retrying...");
                Thread.Sleep(200);
                results = service.GetAsync(fingerprint, duration, new[] { "recordingids" }).Result;
                
                if (results == null)
                {
                    Console.WriteLine("Lookup failed after retry.");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during AcoustID lookup: {ex.Message}");
            return null;
        }

        // Check for API errors
        if (!string.IsNullOrEmpty(results.ErrorMessage))
        {
            Console.WriteLine($"AcoustId API error: {results.ErrorMessage}");
            return null;
        }

        // Find the best matching recording
        var bestMatch = results.Results
            .Where(x => x.Recordings.Count > 0)
            .MaxBy(x => x.Score)?.Recordings.FirstOrDefault();

        if (bestMatch == null)
        {
            Console.WriteLine("No matching recordings found.");
            return null;
        }

        // Look up the full recording details
        Console.WriteLine($"Found match with ID: {bestMatch.Id}");
        return GetRecordingDetails(bestMatch.Id);
    }

    /// <summary>
    /// Gets detailed information about a recording using MusicBrainz API
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
            Console.WriteLine($"Error looking up recording details: {ex.Message}");
            return null;
        }
    }
}