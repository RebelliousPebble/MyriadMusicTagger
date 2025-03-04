using AcoustID;
using MetaBrainz.MusicBrainz;
using MyriadMusicTagger;
using Newtonsoft.Json;
using RestSharp;
using Serilog;
using System.Text;
using Terminal.Gui;

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
        Application.Init();
        var top = Application.Top;
        var mainWindow = new Window("Myriad Music Tagger")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        top.Add(mainWindow);

        var menu = new MenuBar(new MenuBarItem[]
        {
            new MenuBarItem("_File", new MenuItem[]
            {
                new MenuItem("_Process Single Item", "", () => ProcessSingleItem(Playoutv6Client, settings)),
                new MenuItem("_Process Batch of Items", "", () => ProcessBatchItems(Playoutv6Client, settings)),
                new MenuItem("_Exit", "", () => Application.RequestStop())
            })
        });
        top.Add(menu);

        Application.Run();
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
        MessageBox.Query("Info", "Press any key to return to the main menu...", "OK");
    }

    // Display application header with title and version
    static void DisplayHeader()
    {
        // No longer needed as Terminal.Gui handles the header
    }
    
    // Get item number from user with validation
    static int? GetItemNumberFromUser()
    {
        var dialog = new Dialog("Enter Item Number", 60, 7);
        var itemNumberField = new TextField("")
        {
            X = 1,
            Y = 1,
            Width = 40
        };
        dialog.Add(new Label("Item Number:") { X = 1, Y = 0 });
        dialog.Add(itemNumberField);
        var okButton = new Button("OK", is_default: true);
        var cancelButton = new Button("Cancel");
        dialog.AddButton(okButton);
        dialog.AddButton(cancelButton);

        int? itemNumber = null;
        okButton.Clicked += () =>
        {
            if (int.TryParse(itemNumberField.Text.ToString(), out int result))
            {
                itemNumber = result;
                Application.RequestStop();
            }
            else
            {
                MessageBox.ErrorQuery("Error", "Invalid input. Please enter a valid number.", "OK");
            }
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
        return itemNumber;
    }
    
    // Process the media item
    static void ProcessItem(int itemNumber, RestClient client, AppSettings settings)
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
            MessageBox.ErrorQuery("Error", "API response was null", "OK");
            return;
        }
    
        var ReadItemResponse = JsonConvert.DeserializeObject<MyriadMediaItem>(ReadItemRestResponse.Content)?.Result;
    
        if (ReadItemResponse == null)
        {
            MessageBox.ErrorQuery("Error", "Failed to parse API response", "OK");
            return;
        }
    
        // Save response to file
        File.WriteAllText("item.json", JsonConvert.SerializeObject(ReadItemResponse, Formatting.Indented));
    
        // Display current item details
        DisplayItemDetails(ReadItemResponse);
    
        // Step 2: Fingerprint the file and find matches
        var progress = new ProgressDialog("Processing item...", "Cancel", false);
        progress.Pulse();
        Application.Run(progress);

        var pathToFile = ReadItemResponse.MediaLocation;
        var matches = ProcessingUtils.Fingerprint(pathToFile);

        if (matches.Count == 0)
        {
            MessageBox.ErrorQuery("Error", "No audio fingerprint matches found", "OK");
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
            MessageBox.Query("Info", $"Found high confidence match: {bestMatch.Match.RecordingInfo?.Title} by {bestMatch.Match.RecordingInfo?.ArtistCredit?.FirstOrDefault()?.Name}", "OK");
            return;
        }
        
        // Otherwise store all matches sorted by score
        _lastFingerprints = scoredMatches.Select(m => m.Match).ToList();
        MessageBox.Query("Info", "Multiple potential matches found", "OK");

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
        var dialog = new Dialog("Select Match", 60, 20);
        var listView = new ListView(_lastFingerprints.Select(m => $"{m.Score:P0} - {m.RecordingInfo?.Title} - {m.RecordingInfo?.ArtistCredit?.FirstOrDefault()?.Name}").ToList())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        dialog.Add(listView);
        var okButton = new Button("OK", is_default: true);
        var cancelButton = new Button("Cancel");
        dialog.AddButton(okButton);
        dialog.AddButton(cancelButton);

        MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? selectedMatch = null;
        okButton.Clicked += () =>
        {
            var selectedIndex = listView.SelectedItem;
            if (selectedIndex >= 0 && selectedIndex < _lastFingerprints.Count)
            {
                selectedMatch = _lastFingerprints[selectedIndex].RecordingInfo;
                Application.RequestStop();
            }
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
        return selectedMatch;
    }
    
    // Display current item details
    static void DisplayItemDetails(Result item)
    {
        var dialog = new Dialog("Current Item Details", 60, 10);
        var details = new ListView(new[]
        {
            $"Title: {item.Title ?? "[not set]"}",
            $"Media ID: {item.MediaId}",
            $"Duration: {item.TotalLength ?? "[unknown]"}",
            $"Artist: {item.Copyright?.Performer ?? "[not set]"}"
        })
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        dialog.Add(details);
        var okButton = new Button("OK", is_default: true);
        dialog.AddButton(okButton);
        okButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
    }
    
    // Display new metadata found
    static void DisplayNewMetadata(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
    {
        var dialog = new Dialog("Selected Metadata", 60, 15);
        var metadata = new ListView(new List<string>
        {
            $"Title: {recordingInfo.Title ?? "[not found]"}",
            $"Artist: {recordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "[unnamed artist]"}",
            $"Album: {recordingInfo.Releases?.FirstOrDefault()?.Title ?? "[unknown album]"}",
            $"Release Date: {recordingInfo.Releases?.FirstOrDefault()?.Date?.ToString() ?? "[unknown]"}",
            $"Info: {recordingInfo.Disambiguation ?? "[none]"}"
        })
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        dialog.Add(metadata);
        var okButton = new Button("OK", is_default: true);
        dialog.AddButton(okButton);
        okButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
    }
    
    // Ask if user wants to save changes
    static bool AskToSaveChanges()
    {
        return MessageBox.Query("Save Changes", "Do you want to save these changes to Myriad?", "Yes", "No") == 0;
    }
    
    // Save changes to Myriad
    static void SaveChangesToMyriad(int itemNumber, MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo, RestClient client, AppSettings settings)
    {
        var progress = new ProgressDialog("Saving changes...", "Cancel", false);
        progress.Pulse();
        Application.Run(progress);

        var titleUpdate = new MyriadTitleSchema
        {
            ItemTitle = recordingInfo.Title ?? string.Empty,
            Artists = recordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()
        };

        // Log the actual JSON being sent
        var jsonData = JsonConvert.SerializeObject(titleUpdate, Formatting.None);
        
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
            MessageBox.Query("Success", "Metadata successfully updated in Myriad system!", "OK");
        }
        else
        {
            MessageBox.ErrorQuery("Error", $"Failed to update metadata in Myriad system. Error: {result.ErrorMessage ?? "Unknown error"}", "OK");
        }
    }
    
    // New method for batch processing of items
    static void ProcessBatchItems(RestClient client, AppSettings settings)
    {
        // Get start and end item numbers for the batch
        var startItem = GetItemNumberFromUser("Enter the starting item number:");
        if (!startItem.HasValue) return;
        
        var endItem = GetItemNumberFromUser("Enter the ending item number:");
        if (!endItem.HasValue || endItem < startItem)
        {
            MessageBox.ErrorQuery("Error", "Invalid end item number", "OK");
            return;
        }
        
        // Clear previous batch items
        _batchProcessItems.Clear();
        
        // Process each item in the range
        var progress = new ProgressDialog("Processing items...", "Cancel", false);
        progress.Pulse();
        Application.Run(progress);

        for (int itemNumber = startItem.Value; itemNumber <= endItem.Value; itemNumber++)
        {
            ProcessBatchItem(itemNumber, client, settings);
        }
        
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
            MessageBox.ErrorQuery("Error", "No items were processed in the batch", "OK");
            return;
        }
        
        bool exitTable = false;
        
        while (!exitTable)
        {
            var dialog = new Dialog("Batch Processing Results", 80, 20);
            var table = new ListView(_batchProcessItems.Select(item => 
                $"{(item.IsSelected ? "[X]" : "[ ]")} {item.ItemNumber} - {item.OldTitle} - {item.OldArtist} -> {item.NewTitle} - {item.NewArtist} ({item.ConfidenceScore:P0})").ToList())
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            dialog.Add(table);
            var editButton = new Button("Edit");
            var selectButton = new Button("Select/Deselect");
            var saveButton = new Button("Save All");
            var exitButton = new Button("Exit");
            dialog.AddButton(editButton);
            dialog.AddButton(selectButton);
            dialog.AddButton(saveButton);
            dialog.AddButton(exitButton);

            editButton.Clicked += () => EditBatchItem(client, settings);
            selectButton.Clicked += ToggleItemSelection;
            saveButton.Clicked += () => SaveBatchChanges(client, settings);
            exitButton.Clicked += () => { exitTable = true; Application.RequestStop(); };

            Application.Run(dialog);
        }
    }
    
    // Edit a specific item in the batch
    static void EditBatchItem(RestClient client, AppSettings settings)
    {
        // Prompt for item number
        var itemNumbers = _batchProcessItems.Select(i => i.ItemNumber).ToList();
        var itemNumber = GetItemNumberFromUser("Select an item number to edit:");
        if (!itemNumber.HasValue) return;
                
        // Find the selected item
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        // Display item details
        var dialog = new Dialog("Item Details", 60, 10);
        var details = new ListView(new[]
        {
            $"Item Number: {item.ItemNumber}",
            $"Current Title: {item.OldTitle}",
            $"Current Artist: {item.OldArtist}"
        })
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        dialog.Add(details);
        var okButton = new Button("OK", is_default: true);
        dialog.AddButton(okButton);
        okButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
        
        // If there was an error with this item
        if (!string.IsNullOrEmpty(item.Error))
        {
            MessageBox.ErrorQuery("Error", $"This item has an error: {item.Error}\nYou can manually enter metadata for this item.", "OK");
            
            // Allow manual entry
            ManuallyEditItem(item);
            return;
        }
        
        // If we have available matches but none selected
        if ((string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist)) && 
            item.AvailableMatches?.Count > 0)
        {
            MessageBox.Query("Info", "No match has been automatically selected. Choose from available matches:", "OK");
            
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
                MessageBox.Query("Info", "Match selected!", "OK");
            }
        }
        
        // Always allow manual edits
        ManuallyEditItem(item);
    }
    
    // Manually edit item metadata
    static void ManuallyEditItem(BatchProcessItem item)
    {
        var dialog = new Dialog("Edit Item", 60, 10);
        var titleField = new TextField(item.NewTitle ?? item.OldTitle)
        {
            X = 1,
            Y = 1,
            Width = 40
        };
        var artistField = new TextField(item.NewArtist ?? item.OldArtist)
        {
            X = 1,
            Y = 3,
            Width = 40
        };
        dialog.Add(new Label("Title:") { X = 1, Y = 0 });
        dialog.Add(titleField);
        dialog.Add(new Label("Artist:") { X = 1, Y = 2 });
        dialog.Add(artistField);
        var okButton = new Button("OK", is_default: true);
        var cancelButton = new Button("Cancel");
        dialog.AddButton(okButton);
        dialog.AddButton(cancelButton);

        okButton.Clicked += () =>
        {
            item.NewTitle = titleField.Text.ToString();
            item.NewArtist = artistField.Text.ToString();
            item.IsSelected = !string.IsNullOrEmpty(item.NewTitle) && !string.IsNullOrEmpty(item.NewArtist);
            Application.RequestStop();
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);

        if (item.IsSelected)
        {
            MessageBox.Query("Info", "Item updated and selected for saving!", "OK");
        }
        else
        {
            MessageBox.Query("Info", "Item needs both title and artist to be selected for saving.", "OK");
        }
    }
    
    // Toggle selection status for an item
    static void ToggleItemSelection()
    {
        var itemNumber = GetItemNumberFromUser("Select an item number to toggle selection:");
        if (!itemNumber.HasValue) return;
                
        // Find the selected item
        var item = _batchProcessItems.FirstOrDefault(i => i.ItemNumber == itemNumber);
        if (item == null) return;
        
        // Cannot select items with errors or missing metadata
        if (!string.IsNullOrEmpty(item.Error) || string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist))
        {
            MessageBox.ErrorQuery("Error", "This item cannot be selected because it has errors or missing metadata.\nEdit the item first to provide valid metadata.", "OK");
        }
        else
        {
            // Toggle selection
            item.IsSelected = !item.IsSelected;
            MessageBox.Query("Info", item.IsSelected 
                ? $"Item {item.ItemNumber} is now selected." 
                : $"Item {item.ItemNumber} is now deselected.", "OK");
        }
    }
    
    // Save all selected batch changes
    static void SaveBatchChanges(RestClient client, AppSettings settings)
    {
        // Count selected items
        var selectedItems = _batchProcessItems.Where(i => i.IsSelected).ToList();
        var itemCount = selectedItems.Count;
        
        if (itemCount == 0)
        {
            MessageBox.ErrorQuery("Error", "No items are selected for saving.", "OK");
            return;
        }
        
        // Confirm save
        if (MessageBox.Query("Save Changes", $"Save changes to {itemCount} items in the Myriad system?", "Yes", "No") != 0)
        {
            return;
        }
        
        // Process updates with progress bar
        var progress = new ProgressDialog("Saving changes...", "Cancel", false);
        progress.Pulse();
        Application.Run(progress);

        int successCount = 0;
        int errorCount = 0;
        
        foreach (var item in selectedItems)
        {
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
        }
        
        if (errorCount > 0)
        {
            MessageBox.ErrorQuery("Error", $"Completed: {successCount} successful, {errorCount} failed", "OK");
        }
        else
        {
            MessageBox.Query("Success", "All selected items were successfully saved!", "OK");
        }
    }
    
    // Ask if user wants to continue processing more items
    static bool AskToContinue()
    {
        return MessageBox.Query("Continue", "Would you like to process another item?", "Yes", "No") == 0;
    }

    // Helper method to get item number from user with validation
    static int? GetItemNumberFromUser(string prompt)
    {
        var dialog = new Dialog(prompt, 60, 7);
        var itemNumberField = new TextField("")
        {
            X = 1,
            Y = 1,
            Width = 40
        };
        dialog.Add(new Label("Item Number:") { X = 1, Y = 0 });
        dialog.Add(itemNumberField);
        var okButton = new Button("OK", is_default: true);
        var cancelButton = new Button("Cancel");
        dialog.AddButton(okButton);
        dialog.AddButton(cancelButton);

        int? itemNumber = null;
        okButton.Clicked += () =>
        {
            if (int.TryParse(itemNumberField.Text.ToString(), out int result))
            {
                itemNumber = result;
                Application.RequestStop();
            }
            else
            {
                MessageBox.ErrorQuery("Error", "Invalid input. Please enter a valid number.", "OK");
            }
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);
        return itemNumber;
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
