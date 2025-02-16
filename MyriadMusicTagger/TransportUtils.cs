using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.VisualBasic.FileIO;
using Serilog;
using ShellProgressBar;

namespace MyriadMusicTagger;

public class TransportUtils
{
    public static void ExtractTransport(string root, List<string> files)
    {
        var options = new ProgressBarOptions
        {
            ForegroundColor = ConsoleColor.Yellow,
            BackgroundColor = ConsoleColor.DarkYellow,
            ProgressCharacter = '─'
        };
        var childOptions = new ProgressBarOptions
        {
            ForegroundColor = ConsoleColor.Green,
            BackgroundColor = ConsoleColor.DarkGreen,
            ProgressCharacter = '─',
            CollapseWhenFinished = true
        };
        using var pbar = new ProgressBar(files.Count, "Extracting Files..", options);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
        {
            using var child = pbar.Spawn(100, "Extracting " + file, childOptions);
            var archive = root + "\\" + file;
            var dest = root + "\\extracted\\" + Path.GetFileNameWithoutExtension(file);
            ZipFileWithProgress.ExtractToDirectory(archive,
                dest,
                child.AsProgress<double>());
            FileSystem.DeleteFile(root + "\\" + file, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
            pbar.Tick();
        });
    }

    
    public static void CompressTransport(string root, List<string?> files, string savedir)
    {
        var options = new ProgressBarOptions
        {
            ForegroundColor = ConsoleColor.Yellow,
            BackgroundColor = ConsoleColor.DarkYellow,
            ProgressCharacter = '─'
        };
        var childOptions = new ProgressBarOptions
        {
            ForegroundColor = ConsoleColor.Green,
            BackgroundColor = ConsoleColor.DarkGreen,
            ProgressCharacter = '─',
            CollapseWhenFinished = true
        };
        using var pbar = new ProgressBar(files.Count, "Compressing Files..", options);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 10 }, file =>
        {
            using var child = pbar.Spawn(100, "Compressing " + file, childOptions);
            var archive = root + "\\extracted\\" + file;
            var dest = root  + "\\"+savedir+"\\" + Path.GetFileNameWithoutExtension(file) + ".zip";
            ZipFileWithProgress.CreateFromDirectory(archive,
                dest,
                child.AsProgress<double>());
            FileSystem.DeleteDirectory(root + "\\extracted\\" + file, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
            pbar.Tick();
        });
    }
    
    
    public static MediaItem? GetXML(string transportFile)
    {
        Log.Verbose("Finding xml file");
        var xmlfile = Directory.GetFiles(transportFile, "*.xml");
        if (xmlfile.Length == 0)
        {
            Log.Error("No xml file found in transport");
            return null;
        }
        if (new FileInfo(xmlfile[0]).Length == 0)
        {
            Log.Error("Xml file is empty");
            return null;
        }
        // Create an instance of a serializer
        var serializer = new XmlSerializer(typeof(MediaItem));
        var xmlEntry = new FileStream(xmlfile[0], FileMode.Open);
        var doc = new XmlDocument();
        doc.Load(xmlEntry);
        string xmlString = doc.OuterXml.ToString();
        XmlNodeReader reader = new XmlNodeReader(doc); 

        using (reader)
        {
            try
            {
                MediaItem? mediaItem = (MediaItem)serializer.Deserialize(reader)!;
                xmlEntry.Close();
                return mediaItem;
            }
            catch (Exception e)
            {
                Log.Error(e, "Error deserializing xml");
                xmlEntry.Close();
                return null;
            }
        }


    }

    public static string GetRawXML(string transportFile)
    {
        var xmlfile = Directory.GetFiles(transportFile, "*.xml");
        var xmlEntry = new FileStream(xmlfile[0], FileMode.Open);
        var sr = new StreamReader(xmlEntry);
        var text = sr.ReadToEnd();
        xmlEntry.Close();
        return text;
    }

    public static string GetAudio(string transportFile)
    {
        var wavfile = Directory.GetFiles(transportFile, "*.wav");
        return wavfile[0];
    }

    public static bool SaveXML(string transportFile, MediaItem metadata)
    {
        Log.Verbose("Generating new XML");
        var serialiser = new XmlSerializer(typeof(MediaItem));
        var xml = string.Empty;
        using (var sww = new StringWriter())
        {
            using (var writer = XmlWriter.Create(sww))
            {
                serialiser.Serialize(writer, metadata);
                xml = sww.ToString(); // Your XML
            }
        }

        var xmlfile = Directory.GetFiles(transportFile, "*.xml");
        Log.Verbose("Deleting entry");
        FileSystem.DeleteFile(xmlfile[0], UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
        Log.Verbose("Writing new entry");
        var xmlwriter = new StreamWriter(File.OpenWrite(xmlfile[0]), Encoding.Unicode);
        xmlwriter.Write(xml);
        xmlwriter.Flush();
        xmlwriter.Close();
        return true;
    }
}