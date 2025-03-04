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
    
    // Store batch processing items
    private static List<BatchProcessItem> _batchProcessItems = new();

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
        
        // Main application loop
        bool continueRunning = true;
        while (continueRunning)
        {
            // Display application header
            DisplayHeader();
            
            // Display main menu with options
            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Choose an option:[/]")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "Process single item",
                        "Process batch of items",
                        "Exit"
                    }));

            switch (selection)
            {
                case "Process single item":
                    ProcessSingleItem(Playoutv6Client, settings);
                    break;
                case "Process batch of items":
                    ProcessBatchItems(Playoutv6Client, settings);
                    break;
                case "Exit":
                    continueRunning = false;
                    break;
            }
        }
        
        AnsiConsole.MarkupLine("[green]Thank you for using Myriad Music Tagger![/]");
        AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
        Console.ReadKey();
    }

    // Process a single item (original functionality)
    static void ProcessSingleItem(RestClient client, AppSettings settings)
    {
        // Get item number to process
        var item = GetItemNumberFromUser();
        if (!item.HasValue)
        {
            return;
        }
    
        // Process the item
        ProcessItem(item.Value, client, settings);
        
        // Wait for user to press a key before returning to menu
        AnsiConsole.MarkupLine("[grey]Press any key to return to the main menu...[/]");
        Console.ReadKey(true);
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
    
    // New method for batch processing of items
    static void ProcessBatchItems(RestClient client, AppSettings settings)
    {
        // Get start and end item numbers for the batch
        var startItem = AnsiConsole.Prompt(
            new TextPrompt<int>("[yellow]Enter the starting item number:[/]")
                .PromptStyle("green")
                .ValidationErrorMessage("[red]Please enter a valid number[/]")
                .Validate(n => n > 0 ? ValidationResult.Success() : ValidationResult.Error("Item number must be positive")));
        
        var endItem = AnsiConsole.Prompt(
            new TextPrompt<int>("[yellow]Enter the ending item number:[/]")
                .PromptStyle("green")
                .ValidationErrorMessage("[red]Please enter a valid number[/]")
                .Validate(n => {
                    if (n < startItem) return ValidationResult.Error("End number must be greater than or equal to start number");
                    if (n - startItem > 100) return ValidationResult.Error("Maximum batch size is 100 items");
                    return ValidationResult.Success();
                }));
        
        // Clear previous batch items
        _batchProcessItems.Clear();
        
        // Process each item in the range
        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            })
            .Start(ctx =>
            {
                // Create a task for the overall progress
                var mainTask = ctx.AddTask($"[green]Processing items {startItem} to {endItem}[/]", maxValue: endItem - startItem + 1);
                
                for (int itemNumber = startItem; itemNumber <= endItem; itemNumber++)
                {
                    // Update the task description to show current item
                    mainTask.Description = $"[green]Processing item {itemNumber} of {endItem}[/]";
                    
                    // Process the current item
                    ProcessBatchItem(itemNumber, client, settings);
                    
                    // Increment the progress
                    mainTask.Increment(1);
                }
            });
            
        // Display the batch editing table
        ShowBatchEditTable(client, settings);
    }
    
    // Process a single item in batch mode
    static void ProcessBatchItem(int itemNumber, RestClient client, AppSettings settings)
    {
        try
        {
            // Step 1: Fetch item details from API
            var request = new RestRequest("/api/Media/ReadItem")
                .AddQueryParameter("mediaId", itemNumber.ToString())
                .AddQueryParameter("attributesStationId", "-1")
                .AddQueryParameter("additionalInfo", "Full")
                .AddHeader("X-API-Key", settings.PlayoutReadKey);
        
            var ReadItemRestResponse = client.Get(request);
        
            if (ReadItemRestResponse.Content == null)
            {
                _batchProcessItems.Add(new BatchProcessItem 
                { 
                    ItemNumber = itemNumber,
                    Error = "API response was null",
                    IsSelected = false
                });
                return;
            }
        
            var ReadItemResponse = JsonConvert.DeserializeObject<MyriadMediaItem>(ReadItemRestResponse.Content)?.Result;
        
            if (ReadItemResponse == null)
            {
                _batchProcessItems.Add(new BatchProcessItem 
                { 
                    ItemNumber = itemNumber,
                    Error = "Failed to parse API response",
                    IsSelected = false
                });
                return;
            }
        
            // Create batch process item
            var batchItem = new BatchProcessItem
            {
                ItemNumber = itemNumber,
                OldTitle = ReadItemResponse.Title ?? string.Empty,
                OldArtist = ReadItemResponse.Copyright?.Performer ?? string.Empty,
                MediaLocation = ReadItemResponse.MediaLocation
            };
            
            // Check if file exists
            if (string.IsNullOrEmpty(batchItem.MediaLocation) || !File.Exists(batchItem.MediaLocation))
            {
                batchItem.Error = "Media file not found";
                batchItem.IsSelected = false;
                _batchProcessItems.Add(batchItem);
                return;
            }
            
            // Fingerprint the file
            var matches = ProcessingUtils.Fingerprint(batchItem.MediaLocation);
            
            if (matches.Count == 0)
            {
                batchItem.Error = "No fingerprint matches found";
                batchItem.IsSelected = false;
                batchItem.AvailableMatches = new List<ProcessingUtils.FingerprintMatch>();
                _batchProcessItems.Add(batchItem);
                return;
            }
            
            // Parse existing metadata for better matching
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
            
            // Score each match
            var scoredMatches = matches.Select(m => {
                if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
                
                var matchTitle = m.RecordingInfo.Title ?? "";
                var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
                
                // Start with acoustic fingerprint score as base
                double score = m.Score;
                
                // Calculate similarity for title and artist
                double titleSimilarity = CalculateStringSimilarity(matchTitle, parsedTitle);
                double artistSimilarity = CalculateStringSimilarity(matchArtist, parsedArtist);
                
                // Weighted scoring
                score = (score * 0.5) + (titleSimilarity * 0.3) + (artistSimilarity * 0.2);
                
                return (Match: m, Score: score);
            })
            .OrderByDescending(m => m.Score)
            .ToList();
            
            // Use the best match if score is above threshold
            var bestMatch = scoredMatches.FirstOrDefault();
            if (bestMatch.Score > 0.8 && bestMatch.Match.RecordingInfo != null)
            {
                var recordingInfo = bestMatch.Match.RecordingInfo;
                batchItem.NewTitle = recordingInfo.Title ?? string.Empty;
                batchItem.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
                batchItem.IsSelected = true;
                batchItem.ConfidenceScore = bestMatch.Score;
                batchItem.RecordingInfo = recordingInfo;
            }
            else
            {
                batchItem.IsSelected = false;
                batchItem.ConfidenceScore = bestMatch.Score;
                batchItem.AvailableMatches = matches;
            }
            
            _batchProcessItems.Add(batchItem);
        }
        catch (Exception ex)
        {
            _batchProcessItems.Add(new BatchProcessItem 
            { 
                ItemNumber = itemNumber,
                Error = $"Error processing: {ex.Message}",
                IsSelected = false
            });
        }
    }
    
    // Helper method for string similarity calculation in batch processing
    static double CalculateStringSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0;
        
        // Normalize strings for comparison
        string normalized1 = RemoveCommonExtras(str1.ToLower());
        string normalized2 = RemoveCommonExtras(str2.ToLower());
        
        // Check for exact match
        if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase))
            return 1.0;
            
        // Check for substring match
        if (normalized1.Contains(normalized2) || normalized2.Contains(normalized1))
            return 0.8;
            
        // Use Levenshtein distance for fuzzy matching
        return 1.0 - ((double)ComputeLevenshteinDistance(normalized1, normalized2) / Math.Max(normalized1.Length, normalized2.Length));
    }
    
    // Show batch edit table with all items
    static void ShowBatchEditTable(RestClient client, AppSettings settings)
    {
        if (_batchProcessItems.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No items were processed in the batch[/]");
            return;
        }
        
        bool exitTable = false;
        
        while (!exitTable)
        {
            AnsiConsole.Clear();
            DisplayHeader();
            
            // Show summary counts
            int totalItems = _batchProcessItems.Count;
            int selectedItems = _batchProcessItems.Count(i => i.IsSelected);
            int itemsWithErrors = _batchProcessItems.Count(i => !string.IsNullOrEmpty(i.Error));
            
            // Create a summary panel
            var summaryPanel = new Panel(
                Align.Center(
                    new Markup($"[bold]Total Items:[/] {totalItems}  [bold]Selected:[/] [green]{selectedItems}[/]  [bold]Errors:[/] [red]{itemsWithErrors}[/]")
                ))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow)
            };
            
            AnsiConsole.Write(summaryPanel);
            AnsiConsole.WriteLine();
            
            // Create the batch items table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .Title("[yellow]Batch Processing Results[/]")
                .AddColumn(new TableColumn("Selected").Centered())
                .AddColumn(new TableColumn("Item #").Centered())
                .AddColumn(new TableColumn("Old Title").Width(25))
                .AddColumn(new TableColumn("Old Artist").Width(20))
                .AddColumn(new TableColumn("New Title").Width(25))
                .AddColumn(new TableColumn("New Artist").Width(20))
                .AddColumn(new TableColumn("Confidence").Centered())
                .AddColumn(new TableColumn("Actions").Centered());
                
            // Populate the table
            foreach (var item in _batchProcessItems)
            {
                string selectedMark = item.IsSelected ? "[green]✓[/]" : "[grey]□[/]";
                string confidenceStr = item.ConfidenceScore > 0 ? $"{item.ConfidenceScore:P0}" : "-";
                string actions;
                
                if (!string.IsNullOrEmpty(item.Error))
                {
                    actions = $"[red]Error: {item.Error}[/]";
                }
                else if (item.AvailableMatches?.Count > 0 && string.IsNullOrEmpty(item.NewTitle))
                {
                    actions = "[yellow]Needs Selection[/]";
                }
                else
                {
                    actions = "[blue]Edit[/]";
                }
                
                table.AddRow(
                    selectedMark,
                    item.ItemNumber.ToString(),
                    item.OldTitle,
                    item.OldArtist,
                    item.NewTitle ?? string.Empty,
                    item.NewArtist ?? string.Empty,
                    confidenceStr,
                    actions
                );
            }
            
            AnsiConsole.Write(table);
            
            // Display options
            AnsiConsole.WriteLine();
            var options = new List<string>
            {
                "Edit an item",
                "Select/Deselect an item",
                "Save all selected items",
                "Exit batch processing"
            };
            
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Choose an action:[/]")
                    .PageSize(10)
                    .AddChoices(options));
                    
            switch (choice)
            {
                case "Edit an item":
                    EditBatchItem(client, settings);
                    break;
                case "Select/Deselect an item":
                    ToggleItemSelection();
                    break;
                case "Save all selected items":
                    SaveBatchChanges(client, settings);
                    break;
                case "Exit batch processing":
                    exitTable = true;
                    break;
            }
        }
    }
    
    // Edit a specific item in the batch
    static void EditBatchItem(RestClient client, AppSettings settings)
    {
        // Prompt for item number
        var itemNumbers = _batchProcessItems.Select(i => i.ItemNumber).ToList();
        var itemNumber = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[yellow]Select an item number to edit:[/]")
                .PageSize(15)
                .AddChoices(itemNumbers));
                
        // Find the selected item
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        // Display item details
        AnsiConsole.Clear();
        
        var detailsPanel = new Panel(new Rows(
            new Markup($"[bold]Item Number:[/] {item.ItemNumber}"),
            new Markup($"[bold]Current Title:[/] {item.OldTitle}"),
            new Markup($"[bold]Current Artist:[/] {item.OldArtist}")
        ))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Item Details")
        };
        
        AnsiConsole.Write(detailsPanel);
        AnsiConsole.WriteLine();
        
        // If there was an error with this item
        if (!string.IsNullOrEmpty(item.Error))
        {
            AnsiConsole.MarkupLine($"[red]This item has an error: {item.Error}[/]");
            AnsiConsole.MarkupLine("[yellow]You can manually enter metadata for this item.[/]");
            
            // Allow manual entry
            ManuallyEditItem(item);
            return;
        }
        
        // If we have available matches but none selected
        if ((string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist)) && 
            item.AvailableMatches?.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]No match has been automatically selected. Choose from available matches:[/]");
            
            // Temporary store the fingerprint matches for the selection function
            _lastFingerprints = item.AvailableMatches;
            
            // Let the user select a match
            var recordingInfo = SelectFromMatches();
            if (recordingInfo != null)
            {
                item.NewTitle = recordingInfo.Title ?? string.Empty;
                item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
                item.RecordingInfo = recordingInfo;
                item.IsSelected = true;
                AnsiConsole.MarkupLine("[green]Match selected![/]");
                AnsiConsole.WriteLine();
            }
        }
        
        // Always allow manual edits
        ManuallyEditItem(item);
    }
    
    // Manually edit item metadata
    static void ManuallyEditItem(BatchProcessItem item)
    {
        // Show current values and allow editing
        item.NewTitle = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter title:[/]")
                .DefaultValue(item.NewTitle ?? item.OldTitle)
                .AllowEmpty());
                
        item.NewArtist = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter artist:[/]")
                .DefaultValue(item.NewArtist ?? item.OldArtist)
                .AllowEmpty());
                
        // Update selection based on whether we have data
        item.IsSelected = !string.IsNullOrEmpty(item.NewTitle) && !string.IsNullOrEmpty(item.NewArtist);
        
        if (item.IsSelected)
        {
            AnsiConsole.MarkupLine("[green]Item updated and selected for saving![/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Item needs both title and artist to be selected for saving.[/]");
        }
        
        // Wait for keypress
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
    
    // Toggle selection status for an item
    static void ToggleItemSelection()
    {
        // Prompt for item number
        var itemNumbers = _batchProcessItems.Select(i => i.ItemNumber).ToList();
        var itemNumber = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[yellow]Select an item number to toggle selection:[/]")
                .PageSize(15)
                .AddChoices(itemNumbers));
                
        // Find the selected item
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        // Cannot select items with errors or missing metadata
        if (!string.IsNullOrEmpty(item.Error) || string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist))
        {
            AnsiConsole.MarkupLine("[red]This item cannot be selected because it has errors or missing metadata.[/]");
            AnsiConsole.MarkupLine("[yellow]Edit the item first to provide valid metadata.[/]");
        }
        else
        {
            // Toggle selection
            item.IsSelected = !item.IsSelected;
            AnsiConsole.MarkupLine(item.IsSelected 
                ? $"[green]Item {item.ItemNumber} is now selected.[/]" 
                : $"[yellow]Item {item.ItemNumber} is now deselected.[/]");
        }
        
        // Wait for keypress
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
    
    // Save all selected batch changes
    static void SaveBatchChanges(RestClient client, AppSettings settings)
    {
        // Count selected items
        var selectedItems = _batchProcessItems.Where(i => i.IsSelected).ToList();
        var itemCount = selectedItems.Count;
        
        if (itemCount == 0)
        {
            AnsiConsole.MarkupLine("[red]No items are selected for saving.[/]");
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
            return;
        }
        
        // Confirm save
        var confirmSave = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[yellow]Save changes to {itemCount} items in the Myriad system?[/]")
                .AddChoices(new[] { "Yes", "No" })) == "Yes";
                
        if (!confirmSave) return;
        
        // Process updates with progress bar
        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .Start(ctx =>
            {
                var mainTask = ctx.AddTask("[green]Saving changes to Myriad system[/]", maxValue: itemCount);
                int successCount = 0;
                int errorCount = 0;
                
                foreach (var item in selectedItems)
                {
                    mainTask.Description = $"[green]Saving item {item.ItemNumber} ({successCount + errorCount + 1} of {itemCount})[/]";
                    
                    try
                    {
                        // Create title update data
                        var titleUpdate = new MyriadTitleSchema
                        {
                            ItemTitle = item.NewTitle,
                            Artists = item.NewArtist.Split(',').Select(a => a.Trim()).ToList()
                        };
                        
                        var jsonData = JsonConvert.SerializeObject(titleUpdate, Formatting.None);
                        
                        // Create request
                        var request = new RestRequest("/api/Media/SetItemTitling");
                        request.AddQueryParameter("mediaId", item.ItemNumber.ToString());
                        request.AddHeader("X-API-Key", settings.PlayoutWriteKey);
                        request.AddHeader("Content-Type", "application/json");
                        request.AddHeader("Accept", "application/json");
                        request.AddBody(jsonData);
                        
                        // Execute the request
                        var result = client.Execute(request, Method.Post);
                        
                        if (result.IsSuccessful)
                        {
                            successCount++;
                        }
                        else
                        {
                            errorCount++;
                            item.Error = $"API error: {result.ErrorMessage ?? "Unknown error"}";
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        item.Error = $"Exception: {ex.Message}";
                    }
                    
                    mainTask.Increment(1);
                }
                
                mainTask.Description = $"[green]Completed: {successCount} successful, {errorCount} failed[/]";
            });
            
        // Display summary and wait
        if (_batchProcessItems.Any(i => !string.IsNullOrEmpty(i.Error)))
        {
            AnsiConsole.MarkupLine("[yellow]Some items had errors during saving. Review the batch table for details.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]All selected items were successfully saved![/]");
        }
        
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
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

// Class to represent an item in batch processing
public class BatchProcessItem
{
    // Item identification
    public int ItemNumber { get; set; }
    public string MediaLocation { get; set; } = string.Empty;
    
    // Current metadata
    public string OldTitle { get; set; } = string.Empty;
    public string OldArtist { get; set; } = string.Empty;
    
    // New metadata
    public string NewTitle { get; set; } = string.Empty;
    public string NewArtist { get; set; } = string.Empty;
    
    // Processing state
    public bool IsSelected { get; set; }
    public double ConfidenceScore { get; set; }
    public string Error { get; set; } = string.Empty;
    
    // Reference to the original recording info
    public MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording RecordingInfo { get; set; }
    
    // Available matches when no best match could be automatically selected
    public List<ProcessingUtils.FingerprintMatch> AvailableMatches { get; set; } = new List<ProcessingUtils.FingerprintMatch>();
}