using System.Diagnostics;
using System.Globalization;
using System.Net;
using AcoustID;
using CsvHelper;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using MetaBrainz.MusicBrainz.CoverArt;
using MyriadMusicTagger;
using Serilog;
using ShellProgressBar;

var locker = new ReaderWriterLock();
var csvlocker = new ReaderWriterLock();
var artistLocker = new ReaderWriterLock();
var titleLocker = new ReaderWriterLock();
var tagLocker = new ReaderWriterLock();

using var log = new LoggerConfiguration().WriteTo.File("myriadConversionLog.log").MinimumLevel.Information()
    .CreateLogger();
Log.Logger = log;

Configuration.ClientKey = "<CLIENT_KEY>";

Query.DelayBetweenRequests = 3.0;

//Console.Write("Enter root path: ");
//var rootZipPath = Console.ReadLine();

var rootZipPath = "C:\\MyriadTransfer\\14500-98000 Songs";
Console.Write("Initialising");

List<MyriadAlbum> Albums;
var lastAlbumId = 0;
List<MyriadArtist> Artists;
var lastArtistId = 0;
List<MyriadTitle> Titles;
var lastTitleID = 0;
List<MyriadTag> Tags;
var lastTagId = 0;
List<string> completedFiles;

if (!File.Exists("albums.csv")) File.Create("albums.csv").Close();
if (!File.Exists("artists.csv")) File.Create("artists.csv").Close();
if (!File.Exists("titles.csv")) File.Create("titles.csv").Close();
if (!File.Exists("tags.csv")) File.Create("tags.csv").Close();
if (!File.Exists("completed.txt")) File.Create("completed.txt").Close();
if (!File.Exists("unknown.txt")) File.Create("unknown.txt").Close();
if (!File.Exists("known.txt")) File.Create("known.txt").Close();

using (var reader = new StreamReader("albums.csv"))
using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
{
    Albums = csv.GetRecords<MyriadAlbum>().ToList();
}

using (var reader = new StreamReader("artists.csv"))
using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
{
    Artists = csv.GetRecords<MyriadArtist>().ToList();
}

using (var reader = new StreamReader("titles.csv"))
using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
{
    Titles = csv.GetRecords<MyriadTitle>().ToList();
}

using (var reader = new StreamReader("tags.csv"))
using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
{
    Tags = csv.GetRecords<MyriadTag>().ToList();
}

completedFiles = File.ReadAllLines("completed.txt").ToList();

if (Albums.Count != 0) lastAlbumId = Albums.MaxBy(x => x.Id).Id;
if (Artists.Count != 0) lastArtistId = Artists.MaxBy(x => x.id)!.id;
if (Titles.Count != 0) lastTitleID = Titles.MaxBy(x => x.Id)!.Id;
if (Tags.Count != 0) lastTagId = Tags.MaxBy(x => x.Id)!.Id;

var backupPath = Directory.CreateDirectory(rootZipPath + "\\backupXML");

var files = Directory.GetFiles(rootZipPath, "*.zip", SearchOption.AllDirectories).Select(x => Path.GetFileName(x))
    .ToList();

//TransportUtils.ExtractTransport(rootZipPath, files);

var rootPath = rootZipPath + "\\extracted\\";

var folders = Directory.GetDirectories(rootPath).Select(x => x.Replace(rootPath, ""));
var remainingFolders = folders.Except(completedFiles);

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

var enumerable = remainingFolders as string[] ?? remainingFolders.ToArray();
using var pbar = new ProgressBar(enumerable.Count(), "Tagging Tracks...", options);

Parallel.ForEach(enumerable, new ParallelOptions { MaxDegreeOfParallelism = 6 }, transport =>
{
    using var child = pbar.SpawnIndeterminate(transport, childOptions);
    Log.Information($"Processing File: {transport}");
    var metadata = TransportUtils.GetXML(rootPath + transport);
    if (metadata == null) return;
    var audio = TransportUtils.GetAudio(rootPath + transport);
    Log.Information("Fingerprinting Audio");
    var fingerprint = ProcessingUtils.Fingerprint(audio);
    if (fingerprint is null)
    {
        Log.Warning("Fingerprint not Found, Moving on.");
        try
        {
            locker.AcquireWriterLock(500000); //You might wanna change timeout value 
            File.AppendAllText("unknown.txt", transport + Environment.NewLine);
            File.AppendAllText("completed.txt", transport + Environment.NewLine);
        }
        finally
        {
            locker.ReleaseWriterLock();
        }

        return;
    }


    Log.Information("Creating Metadata Backup");
    File.WriteAllText(backupPath.FullName + $"\\{Uri.EscapeDataString(transport)}.xml",
        TransportUtils.GetRawXML(rootPath + transport));
    Log.Information("Replacing new metadata");
    if (fingerprint.Title is not null) AddTitle(ref metadata, fingerprint.Title);
    if (fingerprint.ArtistCredit is not null) AddArtists(ref metadata, fingerprint.ArtistCredit);
    if (fingerprint.Isrcs is not null && fingerprint.Isrcs.Count > 0)
        if (metadata != null)
            metadata.Copyright.Isrc = fingerprint.Isrcs[0];
    if (fingerprint.FirstReleaseDate is not null)
        if (metadata != null)
            metadata.FirstReleaseYear = fingerprint.FirstReleaseDate.Year.ToString();

    if (fingerprint.Genres is not null)
    {
        var index = 1;
        foreach (var tag in fingerprint.Genres)
        {
            var idToUse = 0;
            if (tag.Name != null)
            {
                var normalisedName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tag.Name);
                if (Tags.Any(x => x.Tag == normalisedName))
                {
                    var existingTag = Tags.First(x => x.Tag == normalisedName);
                    idToUse = existingTag.Id;
                }
                else
                {
                    lastTagId++;
                    var newTag = new MyriadTag { Tag = normalisedName, Id = lastTagId };
                    Tags.Add(newTag);
                    idToUse = lastTagId;
                }
                
                var newAttribute = new ItemAttribute
                {
                    Type = "Tag",
                    Index = Convert.ToString(index),
                    Number = Convert.ToString(idToUse),
                    Level = "0",
                    StationId = "0"
                };

                metadata.ItemAttributes.ItemAttribute.Add(newAttribute);
                index++;
            }

            
        }
    }


    TransportUtils.SaveXML(rootPath + transport, metadata);
    try
    {
        locker.AcquireWriterLock(500000); //You might wanna change timeout value 
        File.AppendAllText("known.txt", transport + Environment.NewLine);
        File.AppendAllText("completed.txt", transport + Environment.NewLine);
    }
    finally
    {
        locker.ReleaseWriterLock();
    }

    try
    {
        csvlocker.AcquireWriterLock(500000);
        UpdateFiles();
    }
    finally
    {
        csvlocker.ReleaseWriterLock();
    }

    child.Finished();
    pbar.Tick();
    
});
pbar.Dispose();


var completedFolders = Directory.GetDirectories(rootPath).Select(Path.GetFileName)
    .ToList();

TransportUtils.CompressTransport(rootZipPath, File.ReadAllLines("known.txt").ToList(), "known");

void AddArtists(ref MediaItem? metadata, IEnumerable<INameCredit> credits)
{
    try
    {
        artistLocker.AcquireWriterLock(500000);
        if (metadata.Artists is null) metadata.Artists = new Artists();
        if (metadata.Artists.Artist is null) metadata.Artists.Artist = new List<Artist>();
        metadata.Artists.Artist.Clear();
        var indexCount = 1;
        foreach (var artist in credits)
        {
            if (Artists.Any(x => x.ArtistName == artist.Name))
            {
                var existingArtist = Artists.Where(x => x.ArtistName == artist.Name).First();
                var Artist = new Artist { Id = existingArtist.id, Name = artist.Name, Index = indexCount };
                metadata.Artists.Artist.Add(Artist);
            }
            else
            {
                Log.Information($"{artist.Name} is new, generating new ID");
                lastArtistId++;
                var newArtist = new MyriadArtist { ArtistName = artist.Name, id = lastArtistId };
                var Artist = new Artist { Id = newArtist.id, Name = artist.Name, Index = indexCount };
                Artists.Add(newArtist);
                metadata.Artists.Artist.Add(Artist);
            }

            indexCount++;
        }
    }
    finally
    {
        artistLocker.ReleaseWriterLock();
    }
}

void AddTitle(ref MediaItem? metadata, string title)
{
    try
    {
        titleLocker.AcquireWriterLock(500000);
        lastAlbumId++;
        lastTitleID++;
        var newAlbum = new MyriadAlbum { Name = title, Id = lastAlbumId };
        var newTitle = new MyriadTitle { Title = title, Id = lastTitleID };
        metadata.Album ??= new Album();
        metadata.Album.Title = title;
        metadata.Album.Id = lastAlbumId;
        metadata.Title ??= new Title();
        metadata.Title.Id = lastTitleID;
        metadata.Title.trackTitle = title;
        Albums.Add(newAlbum);
        Titles.Add(newTitle);
    }
    finally
    {
        titleLocker.ReleaseWriterLock();
    }
}

void UpdateFiles()
{
    Log.Information("Updating CSVs");
    try
    {
        titleLocker.AcquireWriterLock(500000);
        using (var writer = new StreamWriter("albums.csv"))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(Albums);
        }

        using (var writer = new StreamWriter("titles.csv"))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(Titles);
        }
    }
    finally
    {
        titleLocker.ReleaseWriterLock();
    }

    try
    {
        artistLocker.AcquireWriterLock(500000);
        using (var writer = new StreamWriter("artists.csv"))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(Artists);
        }
    }
    finally
    {
        artistLocker.ReleaseWriterLock();
    }

    try
    {
        tagLocker.AcquireWriterLock(500000);
        using (var writer = new StreamWriter("tags.csv"))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(Tags);
        }
    }
    finally
    {
        tagLocker.ReleaseWriterLock();
    }
}