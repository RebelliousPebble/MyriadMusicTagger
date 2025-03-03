using AcoustID;
using MetaBrainz.MusicBrainz;
using MyriadMusicTagger;
using Newtonsoft.Json;
using RestSharp;
using Serilog;
using System.Text;
using Spectre.Console;

// Create a Program class to hold the static fields and methods
public class Program
{
    // Store the last retrieved fingerprint matches to avoid redundant processing
    private static List<ProcessingUtils.FingerprintMatch> _lastFingerprints = new();

    public static void Main(string[] args)
    {
        using var log = new LoggerConfiguration().WriteTo.File("myriadConversionLog.log").MinimumLevel.Information()
           .CreateLogger();
        Log.Logger = log;
        
        // Set console encoding to UTF-8 for better display of special characters
        Console.OutputEncoding = Encoding.UTF8;
        
        // Load settings from settings.json or create if it doesn't exist
        var settings = SettingsManager.LoadSettings();
        
        // Apply settings
        Configuration.ClientKey = settings.AcoustIDClientKey;
        Query.DelayBetweenRequests = settings.DelayBetweenRequests;
        
        // Create the REST client with proper base URL formatting
        var options = new RestClientOptions
        {
            BaseUrl = new Uri(settings.PlayoutApiUrl.TrimEnd('/'))
        };
        var Playoutv6Client = new RestClient(options);
        
        // Display application header
        DisplayHeader();
        
        // Main application loop
        bool continueRunning = true;
        while (continueRunning)
        {
            // Get item number to process
            var item = GetItemNumberFromUser();
            if (!item.HasValue)
            {
                continueRunning = false;
                continue;
            }
        
            // Process the item
            ProcessItem(item.Value, Playoutv6Client, settings);
            
            // Ask if user wants to process another item
            continueRunning = AskToContinue();
        }
        
        AnsiConsole.MarkupLine("[green]Thank you for using Myriad Music Tagger![/]");
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey();
    }

    // Display application header with title and version
    static void DisplayHeader()
    {
        Console.Clear();
        var rule = new Rule("[bold cyan]MYRIAD MUSIC TAGGER[/]");
        rule.Justification = Justify.Center;
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
        
        var table = new Table().Border(TableBorder.None);
        table.AddColumn(new TableColumn("Description").Centered());
        table.AddRow("[yellow]Audio fingerprinting and metadata tagging tool[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }
    
    // Get item number from user with validation
    static int? GetItemNumberFromUser()
    {
        while (true)
        {
            var itemNumber = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter the item number to auto-tag (or 'q' to quit):[/]")
                    .PromptStyle("green")
                    .AllowEmpty());
            
            if (string.IsNullOrWhiteSpace(itemNumber) || itemNumber.ToLower() == "q")
            {
                return null;
            }
    
            if (int.TryParse(itemNumber, out int result))
            {
                return result;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Invalid input. Please enter a valid number.[/]");
            }
        }
    }
    
    // Process the media item
    static void ProcessItem(int itemNumber, RestClient client, AppSettings settings)
    {
        AnsiConsole.WriteLine();
        
        // Step 1: Fetch item details from API
        var request = new RestRequest("/api/Media/ReadItem")
            .AddQueryParameter("mediaId", itemNumber.ToString())
            .AddQueryParameter("attributesStationId", "-1")
            .AddQueryParameter("additionalInfo", "Full")
            .AddHeader("X-API-Key", settings.PlayoutReadKey);
    
        var ReadItemRestResponse = client.Get(request);
    
        if (ReadItemRestResponse.Content == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR: API response was null[/]");
            return;
        }
    
        var ReadItemResponse = JsonConvert.DeserializeObject<MyriadMediaItem>(ReadItemRestResponse.Content)?.Result;
    
        if (ReadItemResponse == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR: Failed to parse API response[/]");
            return;
        }
    
        // Save response to file
        File.WriteAllText("item.json", JsonConvert.SerializeObject(ReadItemResponse, Formatting.Indented));
    
        // Display current item details
        DisplayItemDetails(ReadItemResponse);
    
        // Step 2: Fingerprint the file and find matches
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"Processing item {itemNumber}...", ctx => 
            {
                ctx.Status("Fingerprinting audio file...");
                var pathToFile = ReadItemResponse.MediaLocation;
                var matches = ProcessingUtils.Fingerprint(pathToFile);
    
                if (matches.Count == 0)
                {
                    AnsiConsole.MarkupLine("[red]ERROR: No audio fingerprint matches found[/]");
                    return;
                }
                
                // Parse existing metadata
                var existingTitle = ReadItemResponse.Title ?? "";
                var existingArtist = ReadItemResponse.Copyright?.Performer ?? "";
                var parsedArtist = "";
                var parsedTitle = "";
                
                // Check if title is in "Artist - Title" format
                var titleParts = existingTitle.Split(new[] { " - " }, StringSplitOptions.None);
                if (titleParts.Length == 2)
                {
                    parsedArtist = titleParts[0].Trim();
                    parsedTitle = titleParts[1].Trim();
                }
                else
                {
                    parsedTitle = existingTitle;
                    parsedArtist = existingArtist;
                }
                
                // Score each match based on similarity
                var scoredMatches = matches.Select(m => {
                    if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
                    
                    var matchTitle = m.RecordingInfo.Title ?? "";
                    var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
                    
                    // Start with acoustic fingerprint score as base
                    double score = m.Score;
                    
                    // Calculate Levenshtein distance for title and artist
                    double titleSimilarity = 0.0;
                    double artistSimilarity = 0.0;
                    
                    // Compare titles (case insensitive)
                    if (string.Equals(matchTitle, parsedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        titleSimilarity = 1.0;
                    }
                    else
                    {
                        var normalizedMatchTitle = matchTitle.ToLower();
                        var normalizedParsedTitle = parsedTitle.ToLower();
                        
                        // Remove common extras like "(Original Version)", "(instrumental)", etc.
                        normalizedMatchTitle = RemoveCommonExtras(normalizedMatchTitle);
                        normalizedParsedTitle = RemoveCommonExtras(normalizedParsedTitle);
                        
                        if (string.Equals(normalizedMatchTitle, normalizedParsedTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            titleSimilarity = 0.9; // Almost perfect match
                        }
                        else if (normalizedMatchTitle.Contains(normalizedParsedTitle) || 
                                normalizedParsedTitle.Contains(normalizedMatchTitle))
                        {
                            titleSimilarity = 0.7; // Partial match
                        }
                        else
                        {
                            titleSimilarity = CalculateSimilarity(normalizedMatchTitle, normalizedParsedTitle);
                        }
                    }
                    
                    // Compare artists if we have artist information
                    if (!string.IsNullOrEmpty(parsedArtist))
                    {
                        if (string.Equals(matchArtist, parsedArtist, StringComparison.OrdinalIgnoreCase))
                        {
                            artistSimilarity = 1.0;
                        }
                        else
                        {
                            var normalizedMatchArtist = matchArtist.ToLower();
                            var normalizedParsedArtist = parsedArtist.ToLower();
                            
                            if (normalizedMatchArtist.Contains(normalizedParsedArtist) || 
                                normalizedParsedArtist.Contains(normalizedMatchArtist))
                            {
                                artistSimilarity = 0.8;
                            }
                            else
                            {
                                artistSimilarity = CalculateSimilarity(normalizedMatchArtist, normalizedParsedArtist);
                            }
                        }
                    }
                    
                    // Weighted scoring: 
                    // - Acoustic fingerprint: 50%
                    // - Title match: 30%
                    // - Artist match: 20%
                    score = (score * 0.5) + (titleSimilarity * 0.3) + (artistSimilarity * 0.2);
                    
                    return (Match: m, Score: score);
                })
                .OrderByDescending(m => m.Score)
                .ToList();

                // If we have a very good match (>90% confidence), use it directly
                var bestMatch = scoredMatches.FirstOrDefault();
                if (bestMatch.Score > 0.9)
                {
                    _lastFingerprints = new List<ProcessingUtils.FingerprintMatch> { bestMatch.Match };
                    ctx.Status($"Found high confidence match: {bestMatch.Match.RecordingInfo?.Title} by {bestMatch.Match.RecordingInfo?.ArtistCredit?.FirstOrDefault()?.Name}");
                    return;
                }
                
                // Otherwise store all matches sorted by score
                _lastFingerprints = scoredMatches.Select(m => m.Match).ToList();
                ctx.Status("Multiple potential matches found");
            });
    
        // Step 3: Let user select from the matches (only if we have multiple potential matches)
        var matchResult = _lastFingerprints.Count == 1 
            ? _lastFingerprints[0].RecordingInfo 
            : SelectFromMatches();
        
        if (matchResult != null && AskToSaveChanges())
        {
            SaveChangesToMyriad(itemNumber, matchResult, client, settings);
        }
    }
    
    // Helper method to calculate string similarity
    private static double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0;
        
        int distance = ComputeLevenshteinDistance(str1, str2);
        int maxLength = Math.Max(str1.Length, str2.Length);
        
        return 1.0 - ((double)distance / maxLength);
    }
    
    // Compute Levenshtein distance between two strings
    private static int ComputeLevenshteinDistance(string str1, string str2)
    {
        int[,] matrix = new int[str1.Length + 1, str2.Length + 1];

        for (int i = 0; i <= str1.Length; i++)
            matrix[i, 0] = i;
        for (int j = 0; j <= str2.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= str1.Length; i++)
        {
            for (int j = 1; j <= str2.Length; j++)
            {
                int cost = (str1[i - 1] == str2[j - 1]) ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[str1.Length, str2.Length];
    }
    
    // Remove common extras from titles for better matching
    private static string RemoveCommonExtras(string title)
    {
        string[] commonExtras = new[] {
            "(original version)",
            "(instrumental)",
            "(radio edit)",
            "(album version)",
            "(official video)",
            "(official audio)",
            "(lyric video)",
            "(official music video)",
            "(clean)",
            "(explicit)"
        };
        
        var result = title.ToLower();
        foreach (var extra in commonExtras)
        {
            result = result.Replace(extra, "");
        }
        
        return result.Trim();
    }
    
    // Let the user select from the list of matches
    static MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? SelectFromMatches()
    {
        AnsiConsole.WriteLine();
        
        // Get the matches and their scores
        var scoredMatches = _lastFingerprints.Select(m => {
            if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
            
            var matchTitle = m.RecordingInfo.Title ?? "";
            var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
            
            // Recalculate score using the same logic as in ProcessItem
            double titleSimilarity = 0.0;
            double artistSimilarity = 0.0;
            
            // Get the existing title and artist from the file name or metadata
            var existingTitle = m.RecordingInfo.Title ?? "";
            var titleParts = existingTitle.Split(new[] { " - " }, StringSplitOptions.None);
            var parsedTitle = titleParts.Length == 2 ? titleParts[1].Trim() : existingTitle;
            var parsedArtist = titleParts.Length == 2 ? titleParts[0].Trim() : "";
            
            // Compare titles (case insensitive)
            if (string.Equals(matchTitle, parsedTitle, StringComparison.OrdinalIgnoreCase))
            {
                titleSimilarity = 1.0;
            }
            else
            {
                var normalizedMatchTitle = RemoveCommonExtras(matchTitle.ToLower());
                var normalizedParsedTitle = RemoveCommonExtras(parsedTitle.ToLower());
                
                if (string.Equals(normalizedMatchTitle, normalizedParsedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    titleSimilarity = 0.9;
                }
                else if (normalizedMatchTitle.Contains(normalizedParsedTitle) || 
                         normalizedParsedTitle.Contains(normalizedMatchTitle))
                {
                    titleSimilarity = 0.7;
                }
                else
                {
                    titleSimilarity = CalculateSimilarity(normalizedMatchTitle, normalizedParsedTitle);
                }
            }
            
            // Compare artists if we have artist information
            if (!string.IsNullOrEmpty(parsedArtist))
            {
                if (string.Equals(matchArtist, parsedArtist, StringComparison.OrdinalIgnoreCase))
                {
                    artistSimilarity = 1.0;
                }
                else
                {
                    var normalizedMatchArtist = matchArtist.ToLower();
                    var normalizedParsedArtist = parsedArtist.ToLower();
                    
                    if (normalizedMatchArtist.Contains(normalizedParsedArtist) || 
                        normalizedParsedArtist.Contains(normalizedMatchArtist))
                    {
                        artistSimilarity = 0.8;
                    }
                    else
                    {
                        artistSimilarity = CalculateSimilarity(normalizedMatchArtist, normalizedParsedArtist);
                    }
                }
            }
            
            // Calculate final score using the same weights as in ProcessItem
            double score = (m.Score * 0.5) + (titleSimilarity * 0.3) + (artistSimilarity * 0.2);
            return (Match: m, Score: score);
        })
        .OrderByDescending(m => m.Score)
        .ToList();

        if (scoredMatches.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No fingerprint matches found.[/]");
            return null;
        }
    
        // Display match count with pretty formatting
        var matchRule = new Rule($"[yellow]{scoredMatches.Count} possible matches found[/]");
        matchRule.Style = new Style(Color.Yellow);
        AnsiConsole.Write(matchRule);
        AnsiConsole.WriteLine();
    
        // Create prompt for selection
        var selectionPrompt = new SelectionPrompt<string>()
            .Title("[green]Select the best match:[/]")
            .PageSize(15)
            .MoreChoicesText("[grey](Move up and down to see more matches)[/]");
    
        // Add "none of these" option
        selectionPrompt.AddChoice("[red]None of these matches[/]");
    
        // Create a dictionary to map selection text to match object
        var matchDictionary = new Dictionary<string, ProcessingUtils.FingerprintMatch>();
    
        // Add all matches to the prompt
        foreach (var match in scoredMatches)
        {
            var recording = match.Match.RecordingInfo;
            if (recording == null) continue;
    
            string artistNames = recording.ArtistCredit?.Any() == true 
                ? String.Join(", ", recording.ArtistCredit.Select(a => a.Name))
                : "[no artist]";
            
            string releaseTitle = recording.Releases?.FirstOrDefault()?.Title ?? "[no album]";
            string confidence = (match.Score * 100).ToString("F0") + "%";
    
            // Escape any [] characters in the strings to prevent markup parsing errors
            string safeTitle = recording.Title?.Replace("[", "[[").Replace("]", "]]") ?? string.Empty;
            string safeArtistNames = artistNames.Replace("[", "[[").Replace("]", "]]");
            string safeReleaseTitle = releaseTitle.Replace("[", "[[").Replace("]", "]]");
    
            string displayText = $"{confidence} - {safeTitle} - {safeArtistNames} - {safeReleaseTitle}";
            selectionPrompt.AddChoice(displayText);
            matchDictionary[displayText] = match.Match;
        }
    
        // Show the prompt and get user selection
        var selection = AnsiConsole.Prompt(selectionPrompt);
    
        // Handle the selection
        if (selection == "[red]None of these matches[/]")
        {
            AnsiConsole.MarkupLine("[yellow]No match was selected.[/]");
            return null;
        }
        else
        {
            var selectedMatch = matchDictionary[selection];
            
            // Display detailed information about the selected match
            if (selectedMatch.RecordingInfo != null)
            {
                DisplayNewMetadata(selectedMatch.RecordingInfo);
                return selectedMatch.RecordingInfo;
            }
            return null;
        }
    }
    
    // Display current item details
    static void DisplayItemDetails(Result item)
    {
        var panel = new Panel(new Table()
            .AddColumn("Field")
            .AddColumn("Value")
            .AddRow("Title", item.Title ?? "[not set]")
            .AddRow("Media ID", item.MediaId.ToString())
            .AddRow("Duration", item.TotalLength ?? "[unknown]")
            .AddRow("Artist", item.Copyright?.Performer ?? "[not set]")
            .BorderColor(Color.Blue));
        
        panel.Header = new PanelHeader("Current Item Details");
        panel.Border = BoxBorder.Rounded;
        panel.Expand = true;
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
    
    // Display new metadata found
    static void DisplayNewMetadata(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
    {
        var metadataTable = new Table()
            .AddColumn("Field")
            .AddColumn("Value")
            .AddRow("Title", recordingInfo.Title ?? "[not found]");
    
        // Add artist information
        if (recordingInfo.ArtistCredit?.Any() == true)
        {
            metadataTable.AddRow("Artist", recordingInfo.ArtistCredit.First().Name ?? "[unnamed artist]");
            
            if (recordingInfo.ArtistCredit.Count > 1)
            {
                for (int i = 1; i < recordingInfo.ArtistCredit.Count; i++)
                {
                    metadataTable.AddRow("Additional Artist", recordingInfo.ArtistCredit[i].Name ?? "[unnamed artist]");
                }
            }
        }
        else
        {
            metadataTable.AddRow("Artist", "[no artist credit found]");
        }
        
        // Add disambiguation if available
        if (!string.IsNullOrEmpty(recordingInfo.Disambiguation))
        {
            metadataTable.AddRow("Info", recordingInfo.Disambiguation);
        }
        
        // Add release information
        if (recordingInfo.Releases?.Any() == true)
        {
            var firstRelease = recordingInfo.Releases.First();
            metadataTable.AddRow("Album", firstRelease.Title ?? "[unknown album]");
            
            // Fix for the PartialDate issue - check if date exists and isn't null
            if (firstRelease.Date != null)
            {
                // Use ToString() without format as PartialDate might not be a complete date
                metadataTable.AddRow("Release Date", firstRelease.Date.ToString());
            }
        }
    
        var panel = new Panel(metadataTable);
        panel.Header = new PanelHeader("Selected Metadata");
        panel.Border = BoxBorder.Rounded;
        panel.BorderColor(Color.Green);
        panel.Expand = true;
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
    
    // Ask if user wants to save changes
    static bool AskToSaveChanges()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Do you want to save these changes to Myriad?[/]")
                .AddChoices(new[] { "Yes", "No" }))
                .Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }
    
    // Save changes to Myriad
    static void SaveChangesToMyriad(int itemNumber, MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo, RestClient client, AppSettings settings)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .Start("Setting item titling...", ctx => 
            {
                var titleUpdate = new MyriadTitleSchema
                {
                    ItemTitle = recordingInfo.Title ?? string.Empty,
                    Artists = recordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()
                };

                // Display the data being sent
                AnsiConsole.WriteLine();
                var dataTable = new Table()
                    .AddColumn("Field")
                    .AddColumn("Value")
                    .BorderColor(Color.Blue);
                
                dataTable.AddRow("Title", titleUpdate.ItemTitle);
                dataTable.AddRow("Artists", string.Join(", ", titleUpdate.Artists));
                
                AnsiConsole.MarkupLine("[blue]Data Being Sent to Myriad:[/]");
                AnsiConsole.Write(dataTable);
                AnsiConsole.WriteLine();

                // Log the actual JSON being sent
                var jsonData = JsonConvert.SerializeObject(titleUpdate, Formatting.None);
                AnsiConsole.MarkupLine("[grey]Request JSON:[/]");
                AnsiConsole.WriteLine(jsonData);
                AnsiConsole.WriteLine();
                
                // Create request to match CURL structure
                var request = new RestRequest("/api/Media/SetItemTitling");
                request.AddQueryParameter("mediaId", itemNumber.ToString());
                request.AddHeader("X-API-Key", settings.PlayoutWriteKey);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("Accept", "application/json");
                request.AddBody(jsonData);
                

                // Execute the request with POST method explicitly specified
                var result = client.Execute(request, Method.Post);
    
                if (result.IsSuccessful)
                {
                    AnsiConsole.MarkupLine("[green]✓ Metadata successfully updated in Myriad system![/]");
                    
                    // Log the response if available
                    if (!string.IsNullOrEmpty(result.Content))
                    {
                        AnsiConsole.MarkupLine("[grey]Response:[/]");
                        AnsiConsole.WriteLine(result.Content);
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]✗ Failed to update metadata in Myriad system.[/]");
                    AnsiConsole.MarkupLine($"[red]Error: {result.ErrorMessage ?? "Unknown error"}[/]");
                    if (!string.IsNullOrEmpty(result.Content))
                    {
                        AnsiConsole.MarkupLine("[red]Response Content:[/]");
                        AnsiConsole.WriteLine(result.Content);
                        AnsiConsole.WriteLine($"[red]Request URL: {client.BuildUri(request)}[/]");
                    }
                }
            });
    }
    
    // Ask if user wants to continue processing more items
    static bool AskToContinue()
    {
        AnsiConsole.WriteLine();
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Would you like to process another item?[/]")
                .AddChoices(new[] { "Yes", "No" }))
                .Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }
}