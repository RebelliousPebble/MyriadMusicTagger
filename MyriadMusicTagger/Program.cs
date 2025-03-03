using AcoustID;
using MetaBrainz.MusicBrainz;
using MyriadMusicTagger;
using Newtonsoft.Json;
using RestSharp;
using Serilog;

using var log = new LoggerConfiguration().WriteTo.File("myriadConversionLog.log").MinimumLevel.Information()
   .CreateLogger();
Log.Logger = log;

// Load settings from settings.json or create if it doesn't exist
var settings = SettingsManager.LoadSettings();

// Apply settings
Configuration.ClientKey = settings.AcoustIDClientKey;
Query.DelayBetweenRequests = settings.DelayBetweenRequests;

var Playoutv6Client = new RestClient(new RestClientOptions(settings.PlayoutApiUrl));

Console.Write("Enter the item number to auto-tag: ");
var value = Console.ReadLine();
if (value == null) return;
var item = Convert.ToInt32(value);
Console.WriteLine($"Looking up {item}");


var request = new RestRequest("/api/Media/ReadItem")
   .AddParameter("mediaId", item)
   .AddParameter("attributesStationId", -1)
   .AddParameter("additionalInfo", "Full")
   .AddHeader("X-API-Key", settings.PlayoutReadKey);

Console.WriteLine(request.Parameters.First().Value);
var ReadItemRestResponse = Playoutv6Client.Get(request);

if (ReadItemRestResponse.Content == null)
{
   Console.WriteLine("API response was null");
   Console.ReadLine();
   return;
}

var ReadItemResponse = JsonConvert.DeserializeObject<MyriadMediaItem>(ReadItemRestResponse.Content)?.Result;

if (ReadItemResponse == null)
{
   Console.WriteLine("Call failed :(");
   Console.ReadLine();
   return;
}

Console.WriteLine(ReadItemResponse);
//Save the wholw response to a file
File.WriteAllText("item.json", JsonConvert.SerializeObject(ReadItemResponse, Formatting.Indented));

Console.WriteLine($"Title: {ReadItemResponse.Title}");

var pathToFile = ReadItemResponse.MediaLocation;
var recordingInfo = ProcessingUtils.Fingerprint(pathToFile);

if (recordingInfo is null)
{
   Console.WriteLine("Fingerprint not Found");
   Console.ReadLine();
   return;
}

Console.WriteLine($"New Title: {recordingInfo.Title}");
if (recordingInfo.ArtistCredit?.Any() == true)
{
   Console.WriteLine($"New Artist: {recordingInfo.ArtistCredit.First().Name}");
}
else
{
   Console.WriteLine("No artist credit found");
}

Console.WriteLine("Do you want to save to Myriad? (Y/N)");
var proceedInput = Console.ReadLine();
var proceed = proceedInput?.ToLower() ?? "n";
if (proceed != "y") return;

var titleUpdate = new MyriadTitleSchema
{
   ItemTitle = recordingInfo.Title ?? string.Empty,
   Artists = recordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()
};

Console.WriteLine("Setting item titling");

var titleUpdateJson = JsonConvert.SerializeObject(titleUpdate);
var titleSetRequest = new RestRequest($"/api/Media/SetItemTitling?mediaId={item}")
   .AddJsonBody(titleUpdate)
   .AddHeader("X-API-Key", settings.PlayoutWriteKey)
   .AddHeader("Accept", "application/json");

var result = Playoutv6Client.Post(titleSetRequest);

if (!result.IsSuccessful) Console.WriteLine("Setting metadata failed :(");