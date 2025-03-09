using AcoustID;
using MetaBrainz.MusicBrainz;
using MyriadMusicTagger;
using Newtonsoft.Json;
using RestSharp;
using Serilog;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

public class Program
{
    private static List<ProcessingUtils.FingerprintMatch> _lastFingerprints = new();
    private static List<BatchProcessItem> _batchProcessItems = new();
    private static readonly Queue<int> _recentItems = new(capacity: 10);

    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = (Exception)e.ExceptionObject;
            Log.Error(exception, "Unhandled exception occurred");
            AnsiConsole.MarkupLine("[red]An unexpected error occurred. Check the log file for details.[/]");
            if (!e.IsTerminating)
            {
                AnsiConsole.MarkupLine("[yellow]Press any key to continue...[/]");
                Console.ReadKey(true);
            }
        };

        using var log = new LoggerConfiguration()
            .WriteTo.File("log.txt")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .MinimumLevel.Information()
            .CreateLogger();
        Log.Logger = log;
        
        Console.OutputEncoding = Encoding.UTF8;
        
        var settings = SettingsManager.LoadSettings();
        
        Configuration.ClientKey = settings.AcoustIDClientKey;
        Query.DelayBetweenRequests = settings.DelayBetweenRequests;
        
        var options = new RestClientOptions
        {
            BaseUrl = new Uri(settings.PlayoutApiUrl.TrimEnd('/'))
        };
        var Playoutv6Client = new RestClient(options);
        
        LoadRecentItems();
        
        bool continueRunning = true;
        while (continueRunning)
        {
            try
            {
                DisplayHeader();
                
                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[green]Choose an option:[/]")
                        .PageSize(10)
                        .AddChoices(new[]
                        {
                            "Process single item",
                            "Process batch of items",
                            "Recent items",
                            "Help",
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
                    case "Recent items":
                        ProcessRecentItems(Playoutv6Client, settings);
                        break;
                    case "Help":
                        ShowHelp();
                        break;
                    case "Exit":
                        continueRunning = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred in the main loop");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                AnsiConsole.MarkupLine("[yellow]Press any key to continue...[/]");
                Console.ReadKey(true);
            }
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.Clear();
        var helpPanel = CreatePanel(
            new Rows(
                new Text("• Use arrow keys to navigate menus"),
                new Text("• Press Escape to go back in most menus"),
                new Text("• Tab completion is available when entering paths"),
                new Text("• Use Ctrl+C to cancel long-running operations"),
                new Text(""),
                new Text("For more information, visit:", new Style(Color.Yellow)),
                new Text("https://github.com/RebelliousPebble/MyriadMusicTagger")
            ),
            "Help & Information",
            Color.Blue
        );
        
        AnsiConsole.Write(helpPanel);
        WaitForKeyPress("Press any key to return to the main menu...");
    }

    private static void ProcessRecentItems(RestClient client, AppSettings settings)
    {
        if (_recentItems.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No recent items found[/]");
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
            return;
        }

        var recentItemsList = _recentItems.Select(i => $"Item {i}").ToList();
        recentItemsList.Add("Back to main menu");

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select a recent item to process:[/]")
                .PageSize(15)
                .AddChoices(recentItemsList));

        if (selection != "Back to main menu")
        {
            var itemNumber = int.Parse(selection.Split(' ')[1]);
            ProcessItem(itemNumber, client, settings);
        }
    }

    private static void LoadRecentItems()
    {
        _recentItems.Clear();
    }

    private static void AddToRecentItems(int itemNumber)
    {
        if (!_recentItems.Contains(itemNumber))
        {
            if (_recentItems.Count >= 10)
            {
                _recentItems.Dequeue();
            }
            _recentItems.Enqueue(itemNumber);
        }
    }

    static void ProcessSingleItem(RestClient client, AppSettings settings)
    {
        var item = GetItemNumberFromUser();
        if (!item.HasValue)
        {
            return;
        }
    
        ProcessItem(item.Value, client, settings);
        
        AnsiConsole.MarkupLine("[grey]Press any key to return to the main menu...[/]");
        Console.ReadKey(true);
    }

    static void DisplayHeader()
    {
        Console.Clear();
        var rule = new Rule("[bold cyan]MYRIAD MUSIC TAGGER[/]");
        rule.Justification = Justify.Center;
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
        
        var table = CreateTable(noBorder: true);
        table.AddColumn(new TableColumn("Description").Centered());
        table.AddRow("[yellow]Audio fingerprinting and metadata tagging tool[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }
    
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
            
            AnsiConsole.MarkupLine("[red]Invalid input. Please enter a valid number.[/]");
        }
    }
    
    static void ProcessItem(int itemNumber, RestClient client, AppSettings settings)
    {
        AnsiConsole.WriteLine();
        
        var request = CreateReadItemRequest(itemNumber, settings.PlayoutReadKey);
        var response = client.Get(request);
        var readItemResponse = ProcessApiResponse(response, "Failed to parse API response");
        
        if (readItemResponse == null) return;
        
        DisplayItemDetails(readItemResponse);
        
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"Processing item {itemNumber}...", ctx => 
            {
                ctx.Status("Fingerprinting audio file...");
                var pathToFile = readItemResponse.MediaLocation;
                var matches = ProcessingUtils.Fingerprint(pathToFile);
    
                if (matches.Count == 0)
                {
                    DisplayError("No audio fingerprint matches found");
                    return;
                }
                
                var (parsedArtist, parsedTitle) = ParseArtistAndTitle(
                    readItemResponse.Title ?? "",
                    readItemResponse.Copyright?.Performer ?? "");
                
                var scoredMatches = ScoreMatches(matches, parsedTitle, parsedArtist);
                var bestMatch = scoredMatches.FirstOrDefault();
                
                if (bestMatch.Score > 0.9)
                {
                    _lastFingerprints = new List<ProcessingUtils.FingerprintMatch> { bestMatch.Match };
                    ctx.Status($"Found high confidence match: {bestMatch.Match.RecordingInfo?.Title} by {bestMatch.Match.RecordingInfo?.ArtistCredit?.FirstOrDefault()?.Name}");
                    return;
                }
                
                _lastFingerprints = scoredMatches.Select(m => m.Match).ToList();
                ctx.Status("Multiple potential matches found");
            });
    
        var matchResult = _lastFingerprints.Count == 1 
            ? _lastFingerprints[0].RecordingInfo 
            : SelectFromMatches();
        
        if (matchResult != null && AskToSaveChanges())
        {
            SaveChangesToMyriad(itemNumber, matchResult, client, settings);
        }
    }
    
    private static (string Artist, string Title) ParseArtistAndTitle(string title, string fallbackArtist = "")
    {
        var titleParts = title?.Split(new[] { " - " }, StringSplitOptions.None) ?? Array.Empty<string>();
        if (titleParts.Length == 2)
        {
            return (titleParts[0].Trim(), titleParts[1].Trim());
        }
        return (fallbackArtist, title ?? string.Empty);
    }

    private static void WaitForKeyPress(string message = "Press any key to continue...")
    {
        AnsiConsole.MarkupLine($"[grey]{message}[/]");
        Console.ReadKey(true);
    }

    private static void DisplayError(string message, Exception? ex = null)
    {
        AnsiConsole.MarkupLine($"[red]ERROR: {Markup.Escape(message)}[/]");
        if (ex != null)
        {
            Log.Error(ex, message);
        }
    }

    private static RestRequest CreateReadItemRequest(int itemNumber, string readKey)
    {
        return new RestRequest("/api/Media/ReadItem")
            .AddQueryParameter("mediaId", itemNumber.ToString())
            .AddQueryParameter("attributesStationId", "-1")
            .AddQueryParameter("additionalInfo", "Full")
            .AddHeader("X-API-Key", readKey);
    }

    private static RestRequest CreateUpdateItemRequest(int itemNumber, string writeKey, MyriadTitleSchema titleUpdate)
    {
        var request = new RestRequest("/api/Media/SetItemTitling");
        request.AddQueryParameter("mediaId", itemNumber.ToString());
        request.AddHeader("X-API-Key", writeKey);
        request.AddHeader("Content-Type", "application/json");
        request.AddHeader("Accept", "application/json");
        request.AddBody(JsonConvert.SerializeObject(titleUpdate, Formatting.None));
        return request;
    }

    private static Result? ProcessApiResponse(RestResponse response, string errorMessage)
    {
        if (response.Content == null)
        {
            DisplayError("API response was null");
            return null;
        }

        var result = JsonConvert.DeserializeObject<MyriadMediaItem>(response.Content)?.Result;
        if (result == null)
        {
            DisplayError(errorMessage);
            return null;
        }

        return result;
    }

    private static void HandleApiError(RestResponse response, string operation)
    {
        DisplayError($"Failed to {operation}");
        AnsiConsole.MarkupLine($"[red]Error: {response.ErrorMessage ?? "Unknown error"}[/]");
        if (!string.IsNullOrEmpty(response.Content))
        {
            AnsiConsole.MarkupLine("[red]Response Content:[/]");
            AnsiConsole.WriteLine(response.Content);
        }
    }
    
    private static double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0;
        
        int distance = ComputeLevenshteinDistance(str1, str2);
        int maxLength = Math.Max(str1.Length, str2.Length);
        
        return 1.0 - ((double)distance / maxLength);
    }
    
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
    
        var matchRule = new Rule($"[yellow]{scoredMatches.Count} possible matches found[/]");
        matchRule.Style = new Style(Color.Yellow);
        AnsiConsole.Write(matchRule);
        AnsiConsole.WriteLine();
    
        var selectionPrompt = new SelectionPrompt<string>()
            .Title("[green]Select the best match:[/]")
            .PageSize(15)
            .MoreChoicesText("[grey](Move up and down to see more matches)[/]");
    
        selectionPrompt.AddChoice("[red]None of these matches[/]");
    
        var matchDictionary = new Dictionary<string, ProcessingUtils.FingerprintMatch>();
    
        foreach (var match in scoredMatches)
        {
            var recording = match.Match.RecordingInfo;
            if (recording == null) continue;
    
            string artistNames = recording.ArtistCredit?.Any() == true 
                ? String.Join(", ", recording.ArtistCredit.Select(a => Markup.Escape(a.Name ?? string.Empty)))
                : "[grey]no artist[/]";
            
            string releaseTitle = recording.Releases?.FirstOrDefault()?.Title ?? "[grey]no album[/]";
            string confidence = (match.Score * 100).ToString("F0") + "%";
    
            string displayText = $"{confidence} - {Markup.Escape(recording.Title ?? string.Empty)} - {artistNames} - {Markup.Escape(releaseTitle)}";
            selectionPrompt.AddChoice(displayText);
            matchDictionary[displayText] = match.Match;
        }
    
        var selection = AnsiConsole.Prompt(selectionPrompt);
    
        if (selection == "[red]None of these matches[/]")
        {
            AnsiConsole.MarkupLine("[yellow]No match was selected.[/]");
            return null;
        }
        else
        {
            var selectedMatch = matchDictionary[selection];
            
            if (selectedMatch.RecordingInfo != null)
            {
                DisplayNewMetadata(selectedMatch.RecordingInfo);
                return selectedMatch.RecordingInfo;
            }
            return null;
        }
    }
    
    static void DisplayItemDetails(Result item)
    {
        var table = CreateTable();
        table.AddColumn("Field")
            .AddColumn("Value")
            .AddRow("Title", Markup.Escape(item.Title ?? "[not set]"))
            .AddRow("Media ID", item.MediaId.ToString())
            .AddRow("Duration", Markup.Escape(item.TotalLength ?? "[unknown]"))
            .AddRow("Artist", Markup.Escape(item.Copyright?.Performer ?? "[not set]"));
        
        var panel = CreatePanel(table, "Current Item Details");
        panel.Expand = true;
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
    
    static void DisplayNewMetadata(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
    {
        var metadataTable = new Table()
            .AddColumn("Field")
            .AddColumn("Value")
            .AddRow("Title", Markup.Escape(recordingInfo.Title ?? "[not found]"));
    
        if (recordingInfo.ArtistCredit?.Any() == true)
        {
            metadataTable.AddRow("Artist", Markup.Escape(recordingInfo.ArtistCredit.First().Name ?? "[unnamed artist]"));
            
            if (recordingInfo.ArtistCredit.Count > 1)
            {
                for (int i = 1; i < recordingInfo.ArtistCredit.Count; i++)
                {
                    metadataTable.AddRow("Additional Artist", Markup.Escape(recordingInfo.ArtistCredit[i].Name ?? "[unnamed artist]"));
                }
            }
        }
        else
        {
            metadataTable.AddRow("Artist", "[grey]no artist credit found[/]");
        }
        
        if (!string.IsNullOrEmpty(recordingInfo.Disambiguation))
        {
            metadataTable.AddRow("Info", Markup.Escape(recordingInfo.Disambiguation));
        }
        
        if (recordingInfo.Releases?.Any() == true)
        {
            var firstRelease = recordingInfo.Releases.First();
            metadataTable.AddRow("Album", Markup.Escape(firstRelease.Title ?? "[unknown album]"));
            
            if (firstRelease.Date != null)
            {
                metadataTable.AddRow("Release Date", Markup.Escape(firstRelease.Date.ToString()));
            }
        }
    
        var panel = new Panel(metadataTable);
        panel.Header = new PanelHeader("[yellow]Selected Metadata[/]");
        panel.Border = BoxBorder.Rounded;
        panel.BorderColor(Color.Green);
        panel.Expand = true;
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
    
    static bool AskToSaveChanges()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Do you want to save these changes to Myriad?[/]")
                .AddChoices(new[] { "Yes", "No" }))
                .Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }
    
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

                var request = CreateUpdateItemRequest(itemNumber, settings.PlayoutWriteKey, titleUpdate);
                var result = client.Execute(request, Method.Post);
    
                if (result.IsSuccessful)
                {
                    AnsiConsole.MarkupLine("[green]✓ Metadata successfully updated in Myriad system![/]");
                    if (!string.IsNullOrEmpty(result.Content))
                    {
                        AnsiConsole.MarkupLine("[grey]Response:[/]");
                        AnsiConsole.WriteLine(result.Content);
                    }
                }
                else
                {
                    HandleApiError(result, "update metadata in Myriad system");
                }
            });
    }
    
    private static BatchProcessItem CreateBatchItem(int itemNumber, Result response)
    {
        return new BatchProcessItem
        {
            ItemNumber = itemNumber,
            OldTitle = response.Title ?? string.Empty,
            OldArtist = response.Copyright?.Performer ?? string.Empty,
            MediaLocation = response.MediaLocation
        };
    }

    private static void ProcessBatchItem(BatchProcessItem item, ProgressTask currentTask)
    {
        if (string.IsNullOrEmpty(item.MediaLocation) || !File.Exists(item.MediaLocation))
        {
            item.Error = "Media file not found";
            item.IsSelected = false;
            return;
        }
        
        currentTask.Description = $"[yellow]Fingerprinting item {item.ItemNumber}[/]";
        currentTask.Increment(0.2);
        
        var matches = ProcessingUtils.Fingerprint(item.MediaLocation);
        
        if (matches.Count == 0)
        {
            item.Error = "No fingerprint matches found";
            item.IsSelected = false;
            item.AvailableMatches = new List<ProcessingUtils.FingerprintMatch>();
            return;
        }
        
        currentTask.Description = $"[yellow]Finding matches for item {item.ItemNumber}[/]";
        currentTask.Increment(0.2);
        
        var bestMatch = ScoreMatches(matches, item.OldTitle, item.OldArtist).FirstOrDefault();
        
        if (bestMatch.Score > 0.8 && bestMatch.Match.RecordingInfo != null)
        {
            var recordingInfo = bestMatch.Match.RecordingInfo;
            item.NewTitle = recordingInfo.Title ?? string.Empty;
            item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
            item.IsSelected = true;
            item.ConfidenceScore = bestMatch.Score;
            item.RecordingInfo = recordingInfo;
        }
        else
        {
            item.IsSelected = false;
            item.ConfidenceScore = bestMatch.Score;
            item.AvailableMatches = matches;
        }
    }

    static void ProcessBatchItems(RestClient client, AppSettings settings)
    {
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
        
        _batchProcessItems.Clear();
        
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
            .Start(progressContext =>
            {
                var overallTask = progressContext.AddTask($"[green]Processing items {startItem} to {endItem}[/]", 
                    maxValue: endItem - startItem + 1);
                var currentTask = progressContext.AddTask("[yellow]Current operation[/]", maxValue: 1);
                
                for (int itemNumber = startItem; itemNumber <= endItem; itemNumber++)
                {
                    try
                    {
                        overallTask.Description = $"[green]Processing item {itemNumber} of {endItem}[/]";
                        currentTask.Description = $"[yellow]Reading item {itemNumber}[/]";
                        currentTask.Value = 0;
                        
                        var request = CreateReadItemRequest(itemNumber, settings.PlayoutReadKey);
                        var response = client.Get(request);
                        var readItemResponse = ProcessApiResponse(response, "Failed to parse API response");
                        currentTask.Increment(0.2);
                        
                        if (readItemResponse == null)
                        {
                            _batchProcessItems.Add(new BatchProcessItem 
                            { 
                                ItemNumber = itemNumber,
                                Error = "API response was null",
                                IsSelected = false
                            });
                            continue;
                        }
                        
                        var batchItem = CreateBatchItem(itemNumber, readItemResponse);
                        ProcessBatchItem(batchItem, currentTask);
                        _batchProcessItems.Add(batchItem);
                        currentTask.Value = 1;
                        
                        AddToRecentItems(itemNumber);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error processing item {itemNumber}");
                        _batchProcessItems.Add(new BatchProcessItem 
                        { 
                            ItemNumber = itemNumber,
                            Error = $"Error: {ex.Message}",
                            IsSelected = false
                        });
                    }
                    
                    overallTask.Increment(1);
                }
            });
            
        ShowBatchResults();
        ShowBatchEditTable(client, settings);
    }

    private static List<(ProcessingUtils.FingerprintMatch Match, double Score)> ScoreMatches(
        List<ProcessingUtils.FingerprintMatch> matches, string existingTitle, string existingArtist)
    {
        var parsedArtist = "";
        var parsedTitle = "";
        
        var titleParts = existingTitle?.Split(new[] { " - " }, StringSplitOptions.None) ?? Array.Empty<string>();
        if (titleParts.Length == 2)
        {
            parsedArtist = titleParts[0].Trim();
            parsedTitle = titleParts[1].Trim();
        }
        else
        {
            parsedTitle = existingTitle ?? string.Empty;
            parsedArtist = existingArtist ?? string.Empty;
        }
        
        return matches.Select(m => {
            if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
            
            var matchTitle = m.RecordingInfo.Title ?? "";
            var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
            
            double score = m.Score;
            
            double titleSimilarity = CalculateStringSimilarity(matchTitle, parsedTitle);
            double artistSimilarity = CalculateStringSimilarity(matchArtist, parsedArtist);
            
            score = (score * 0.5) + (titleSimilarity * 0.3) + (artistSimilarity * 0.2);
            
            return (Match: m, Score: score);
        })
        .OrderByDescending(m => m.Score)
        .ToList();
    }

    static void ShowBatchResults()
    {
        var successCount = _batchProcessItems.Count(i => i.IsSelected && string.IsNullOrEmpty(i.Error));
        var errorCount = _batchProcessItems.Count(i => !string.IsNullOrEmpty(i.Error));
        var needsReviewCount = _batchProcessItems.Count(i => string.IsNullOrEmpty(i.Error) && !i.IsSelected);
        
        var resultsPanel = CreatePanel(
            new Rows(
                new Markup($"[green]Successfully processed:[/] {successCount}"),
                new Markup($"[yellow]Needs review:[/] {needsReviewCount}"),
                new Markup($"[red]Errors:[/] {errorCount}")
            ),
            "Batch Processing Results",
            Color.Blue
        );
        
        AnsiConsole.Clear();
        AnsiConsole.Write(resultsPanel);
        AnsiConsole.WriteLine();
        
        if (errorCount > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Some items encountered errors. You can review them in the batch edit table.[/]");
        }
        
        if (needsReviewCount > 0)
        {
            AnsiConsole.MarkupLine("[blue]Some items need review. Use the batch edit table to select matches or edit metadata.[/]");
        }
        
        WaitForKeyPress("Press any key to continue to the batch edit table...");
    }

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
            
            int totalItems = _batchProcessItems.Count;
            int selectedItems = _batchProcessItems.Count(i => i.IsSelected);
            int itemsWithErrors = _batchProcessItems.Count(i => !string.IsNullOrEmpty(i.Error));
            
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
                
            foreach (var item in _batchProcessItems)
            {
                string selectedMark = item.IsSelected ? "[green]✓[/]" : "[grey]□[/]";
                string confidenceStr = item.ConfidenceScore > 0 ? $"{item.ConfidenceScore:P0}" : "-";
                string actions;
                
                if (!string.IsNullOrEmpty(item.Error))
                {
                    actions = $"[red]Error: {Markup.Escape(item.Error)}[/]";
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
                    Markup.Escape(item.OldTitle),
                    Markup.Escape(item.OldArtist),
                    Markup.Escape(item.NewTitle ?? string.Empty),
                    Markup.Escape(item.NewArtist ?? string.Empty),
                    confidenceStr,
                    actions
                );
            }
            
            AnsiConsole.Write(table);
            
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
    
    static void EditBatchItem(RestClient client, AppSettings settings)
    {
        var itemNumbers = _batchProcessItems.Select(i => i.ItemNumber).ToList();
        var itemNumber = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[yellow]Select an item number to edit:[/]")
                .PageSize(15)
                .AddChoices(itemNumbers));
                
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        AnsiConsole.Clear();
        
        var detailsPanel = new Panel(new Rows(
            new Markup($"[bold]Item Number:[/] {item.ItemNumber}"),
            new Markup($"[bold]Current Title:[/] {Markup.Escape(item.OldTitle)}"),
            new Markup($"[bold]Current Artist:[/] {Markup.Escape(item.OldArtist)}")
        ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Blue),
            Header = new PanelHeader("[yellow]Item Details[/]")
        };
        
        AnsiConsole.Write(detailsPanel);
        AnsiConsole.WriteLine();
        
        if (!string.IsNullOrEmpty(item.Error))
        {
            AnsiConsole.MarkupLine($"[red]This item has an error: {Markup.Escape(item.Error)}[/]");
            AnsiConsole.MarkupLine("[yellow]You can manually enter metadata for this item.[/]");
            
            ManuallyEditItem(item);
            return;
        }
        
        if ((string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist)) && 
            item.AvailableMatches?.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]No match has been automatically selected. Choose from available matches:[/]");
            
            _lastFingerprints = item.AvailableMatches;
            
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
        
        ManuallyEditItem(item);
    }
    
    static void ManuallyEditItem(BatchProcessItem item)
    {
        item.NewTitle = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter title:[/]")
                .DefaultValue(item.NewTitle ?? item.OldTitle)
                .AllowEmpty());
                
        item.NewArtist = AnsiConsole.Prompt(
            new TextPrompt<string>("[yellow]Enter artist:[/]")
                .DefaultValue(item.NewArtist ?? item.OldArtist)
                .AllowEmpty());
                
        item.IsSelected = !string.IsNullOrEmpty(item.NewTitle) && !string.IsNullOrEmpty(item.NewArtist);
        
        if (item.IsSelected)
        {
            AnsiConsole.MarkupLine("[green]Item updated and selected for saving![/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Item needs both title and artist to be selected for saving.[/]");
        }
        
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
    
    static void ToggleItemSelection()
    {
        var itemNumbers = _batchProcessItems.Select(i => i.ItemNumber).ToList();
        var itemNumber = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[yellow]Select an item number to toggle selection:[/]")
                .PageSize(15)
                .AddChoices(itemNumbers));
                
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        if (!string.IsNullOrEmpty(item.Error) || string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist))
        {
            AnsiConsole.MarkupLine("[red]This item cannot be selected because it has errors or missing metadata.[/]");
            AnsiConsole.MarkupLine("[yellow]Edit the item first to provide valid metadata.[/]");
        }
        else
        {
            item.IsSelected = !item.IsSelected;
            AnsiConsole.MarkupLine(item.IsSelected 
                ? $"[green]Item {item.ItemNumber} is now selected.[/]" 
                : $"[yellow]Item {item.ItemNumber} is now deselected.[/]");
        }
        
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
    
    static void SaveBatchChanges(RestClient client, AppSettings settings)
    {
        var selectedItems = _batchProcessItems.Where(i => i.IsSelected).ToList();
        var itemCount = selectedItems.Count;
        
        if (itemCount == 0)
        {
            AnsiConsole.MarkupLine("[red]No items are selected for saving.[/]");
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
            return;
        }
        
        var confirmSave = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[yellow]Save changes to {itemCount} items in the Myriad system?[/]")
                .AddChoices(new[] { "Yes", "No" })) == "Yes";
                
        if (!confirmSave) return;
        
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
                        var titleUpdate = new MyriadTitleSchema
                        {
                            ItemTitle = item.NewTitle,
                            Artists = item.NewArtist.Split(',').Select(a => a.Trim()).ToList()
                        };
                        
                        var request = CreateUpdateItemRequest(item.ItemNumber, settings.PlayoutWriteKey, titleUpdate);
                        
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
    
    private static double CalculateStringSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0;
        
        string normalized1 = RemoveCommonExtras(str1.ToLower());
        string normalized2 = RemoveCommonExtras(str2.ToLower());
        
        if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase))
            return 1.0;
            
        if (normalized1.Contains(normalized2) || normalized2.Contains(normalized1))
            return 0.8;
            
        return 1.0 - ((double)ComputeLevenshteinDistance(normalized1, normalized2) / Math.Max(normalized1.Length, normalized2.Length));
    }

    private static Table CreateTable(string title = "", bool noBorder = false)
    {
        var table = new Table();
        if (noBorder)
        {
            table.Border(TableBorder.None);
        }
        else
        {
            table.Border(TableBorder.Rounded)
                .BorderColor(Color.Blue);
        }
        
        if (!string.IsNullOrEmpty(title))
        {
            table.Title($"[yellow]{title}[/]");
        }
        
        return table;
    }

    private static Panel CreatePanel(IRenderable content, string header = "", Color? borderColor = null)
    {
        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded,
            Header = string.IsNullOrEmpty(header) ? null : new PanelHeader($"[yellow]{header}[/]")
        };
        
        if (borderColor.HasValue)
        {
            panel.BorderStyle = new Style(borderColor.Value);
        }
        
        return panel;
    }
}

