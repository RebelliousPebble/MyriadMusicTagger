using System.Diagnostics;
using AcoustID;
using AcoustID.Web;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Serilog;

namespace MyriadMusicTagger;

public class ProcessingUtils
{
    public static IRecording? Fingerprint(string path)
    {
        if (string.IsNullOrEmpty(Configuration.ClientKey)) Configuration.ClientKey = "1WNNNhvhYO";

        var service = new LookupService();
        var recs = new List<Recording>();
        Log.Debug("Starting AcoustID call");
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "fpcalc.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = false
            }
        };

        string fingerprint = null;
        var duration = -1;
        proc.Start();
        proc.WaitForExit();
        while (!proc.StandardOutput.EndOfStream)
        {
            var line = proc.StandardOutput.ReadLine();
            if (line.StartsWith("DURATION="))
                duration = int.Parse(line.Replace("DURATION=", ""));
            else if (line.StartsWith("FINGERPRINT="))
                fingerprint = line.Replace("FINGERPRINT=", "");
            else
                return null;
        }

        if (fingerprint is null || duration == -1) return null;
        LookupResponse? results = null;
        try
        {
            results = service.GetAsync(fingerprint, duration,
                new[]
                {
                    "recordings", "recordingids", "releases", "releaseids", "releasegroups", "releasegroupids",
                    "tracks", "compress", "usermeta", "sources"
                }).Result;
            if (results is null)
            {
                Thread.Sleep(200);
                results = service.GetAsync(fingerprint, duration,
                    new[]
                    {
                        "recordings", "recordingids", "releases", "releaseids", "releasegroups", "releasegroupids",
                        "tracks", "compress", "usermeta", "sources"
                    }).Result;
                if (results is null) return null;
            }
        }
        catch
        {
            return null;
        }


        if (!string.IsNullOrEmpty(results.ErrorMessage)) Log.Error("AcoustId API call failed: " + results.ErrorMessage);

        var allRecordings = results.Results.Where(x => x.Recordings.Count > 0).MaxBy(x => x.Score)?.Recordings;

        if (allRecordings is not null && allRecordings.Any())
        {
            var q = new Query("MyriadTagger", Version.Parse("1.0"));
            try
            {
                var maxValue = allRecordings.MaxBy(y => y.Sources)?.Id;
                if (string.IsNullOrEmpty(maxValue)) return null;
                var result = q.LookupRecording(new Guid(maxValue),
                    Include.Aliases | Include.Artists | Include.Genres | Include.Isrcs | Include.Releases | Include.Media | Include.Tags | Include.Ratings);
                return result;
            }
            catch (NullReferenceException)
            {
                return null;
            }
            catch (QueryException)
            {
                return null;
            }
        }

        return null;
    }
}