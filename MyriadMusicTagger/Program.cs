using AcoustID;
using MetaBrainz.MusicBrainz;
using MyriadMusicTagger;
using Newtonsoft.Json;
using RestSharp;
using Serilog;
using System.Text;
// Removed Spectre.Console using directives
using Terminal.Gui;

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
            if (Application.Driver != null)
            {
                 Application.Shutdown();
            }
            Console.Error.WriteLine($"An unexpected error occurred: {exception.Message}");
            Console.Error.WriteLine("Check the log.txt file for more details.");
        };

        using var log = new LoggerConfiguration()
            .WriteTo.File("log.txt")
            // Removed direct console sink for logs to avoid conflict with Terminal.Gui
            .MinimumLevel.Information()
            .CreateLogger();
        Log.Logger = log;
        
        Console.OutputEncoding = Encoding.UTF8; // Keep for general console output if any leaks
        
        var settings = SettingsManager.LoadSettings(); // This might trigger GUI for settings
        
        Configuration.ClientKey = settings.AcoustIDClientKey;
        // Query.DelayBetweenRequests is a double in seconds for MusicBrainz.NET
        // AppSettings.DelayBetweenRequests is also double in seconds.
        Query.DelayBetweenRequests = settings.DelayBetweenRequests;
        
        var options = new RestClientOptions
        {
            BaseUrl = new Uri(settings.PlayoutApiUrl.TrimEnd('/'))
        };
        var Playoutv6Client = new RestClient(options);
        
        LoadRecentItems();
        
        Application.Init();

        var top = Application.Top;

        var mainWindow = new Window("Myriad Music Tagger")
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill()
        };

        var menu = new MenuBar(new MenuBarItem[]
        {
            new MenuBarItem("_File", new MenuItem[]
            {
                new MenuItem("_Settings", "", () => SettingsManager.ReviewSettingsGui()),
                new MenuItem("_Exit", "", () => Application.RequestStop(), null, null, Key.Q | Key.CtrlMask)
            }),
            new MenuBarItem("_Process", new MenuItem[]
            {
                new MenuItem("_Single Item", "", () => ProcessSingleItemGui(Playoutv6Client, settings), null, null, Key.S | Key.CtrlMask),
                new MenuItem("_Batch of Items", "", () => ProcessBatchItemsGui(Playoutv6Client, settings), null, null, Key.B | Key.CtrlMask),
                new MenuItem("_Recent Items", "", () => ProcessRecentItemsGui(Playoutv6Client, settings), null, null, Key.R | Key.CtrlMask)
            }),
            new MenuBarItem("_Help", new MenuItem[]
            {
                new MenuItem("_View Help", "", () => ShowHelpGui(), null, null, Key.F1)
            })
        });
        
        top.Add(menu, mainWindow);
        Application.Run();
        Application.Shutdown();
    }

    private static void ProcessSingleItemGui(RestClient client, AppSettings settings)
    {
        var dialog = new Dialog("Process Single Item", 60, 10);

        var itemNumberLabel = new Label("Enter Item Number:") { X = 1, Y = 1 };
        var itemNumberField = new TextField("") { X = Pos.Right(itemNumberLabel) + 1, Y = 1, Width = 10 };

        var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 5, IsDefault = true,};
        processButton.Clicked += () =>
        {
            if (int.TryParse(itemNumberField.Text.ToString(), out int itemNumber) && itemNumber > 0)
            {
                Application.RequestStop(dialog);
                try { ProcessItemGui(itemNumber, client, settings); }
                catch (ProcessingUtils.ProcessingException pex) { MessageBox.ErrorQuery("Processing Error", pex.Message, "Ok");}
                catch (Exception ex) { MessageBox.ErrorQuery("Unexpected Error", $"An error occurred: {ex.Message}", "Ok"); Log.Error(ex, "Error in ProcessSingleItemGui after ProcessItemGui call for item {ItemNumber}", itemNumber);}
            }
            else
            {
                MessageBox.ErrorQuery("Invalid Input", "Please enter a valid positive number.", "Ok");
            }
        };

        var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y,};
        cancelButton.Clicked += () => { Application.RequestStop(dialog); };

        dialog.Add(itemNumberLabel, itemNumberField, processButton, cancelButton);
        itemNumberField.SetFocus();
        Application.Run(dialog);
    }

    private static void ProcessItemGui(int itemNumber, RestClient client, AppSettings settings)
    {
        var loadingDialog = new Dialog("Processing...", 50, 7) { Title = $"Item {itemNumber}"};
        var statusLabel = new Label($"Fetching details for item {itemNumber}...") { X = 1, Y = 1, Width = Dim.Fill(2) };
        loadingDialog.Add(statusLabel);
        var loadingToken = Application.Begin(loadingDialog);

        var request = CreateReadItemRequest(itemNumber, settings.PlayoutReadKey);
        RestResponse response;
        try { response = client.Get(request); } // Synchronous
        catch (Exception ex) { Application.End(loadingToken); MessageBox.ErrorQuery("API Connection Error", $"Failed to connect to Myriad API: {ex.Message}", "Ok"); Log.Error(ex, "Myriad API connection error on Get for item {ItemNumber}",itemNumber); return;}

        var readItemResponse = ProcessApiResponse(response, "Failed to parse API response for ReadItem");
        if (readItemResponse == null) { Application.End(loadingToken); return; }

        Application.End(loadingToken);
        DisplayItemDetailsGui(readItemResponse);

        var processingDialog = new Dialog("Processing...", 50, 7) { Title = $"Item {itemNumber}"};
        var fingerprintStatusLabel = new Label($"Fingerprinting item {itemNumber}...") { X = 1, Y = 1, Width = Dim.Fill(2) };
        processingDialog.Add(fingerprintStatusLabel);
        var processingToken = Application.Begin(processingDialog);

        var pathToFile = readItemResponse.MediaLocation;
        if (string.IsNullOrEmpty(pathToFile) || !File.Exists(pathToFile))
        {
            Application.End(processingToken);
            MessageBox.ErrorQuery("File Error", $"Media file not found or path is invalid:\n{pathToFile}", "Ok");
            return;
        }
        
        List<ProcessingUtils.FingerprintMatch> matches;
        try
        {
            matches = ProcessingUtils.Fingerprint(pathToFile);
        }
        catch (ProcessingUtils.ProcessingException pex)
        {
            Application.End(processingToken);
            MessageBox.ErrorQuery("Fingerprint Error", pex.Message, "Ok");
            return;
        }
        catch (Exception ex)
        {
            Application.End(processingToken);
            MessageBox.ErrorQuery("Fingerprint Error", $"An unexpected error occurred during fingerprinting: {ex.Message}", "Ok");
            Log.Error(ex, "Unexpected error in Fingerprint call for {PathToFile}", pathToFile);
            return;
        }

        if (matches.Count == 0)
        {
            Application.End(processingToken);
            MessageBox.Query("No Matches", "No audio fingerprint matches found.", "Ok");
            return;
        }

        var (parsedArtist, parsedTitle) = ParseArtistAndTitle(readItemResponse.Title ?? "", readItemResponse.Copyright?.Performer ?? "");
        var scoredMatches = ScoreMatches(matches, parsedTitle, parsedArtist);
        var bestMatch = scoredMatches.FirstOrDefault();

        _lastFingerprints.Clear();

        if (bestMatch.Score > 0.9 && bestMatch.Match.RecordingInfo != null)
        {
            _lastFingerprints = new List<ProcessingUtils.FingerprintMatch> { bestMatch.Match };
            fingerprintStatusLabel.Text = $"Found high confidence match: {bestMatch.Match.RecordingInfo?.Title} by {bestMatch.Match.RecordingInfo?.ArtistCredit?.FirstOrDefault()?.Name}";
            Application.Refresh();
            System.Threading.Thread.Sleep(1000);
        }
        else
        {
            _lastFingerprints = scoredMatches.Select(m => m.Match).ToList();
            fingerprintStatusLabel.Text = "Multiple potential matches found.";
            Application.Refresh();
            System.Threading.Thread.Sleep(500);
        }

        Application.End(processingToken);

        MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? matchResult = null;
        if (_lastFingerprints.Count == 1 && _lastFingerprints[0].RecordingInfo != null)
        {
            matchResult = _lastFingerprints[0].RecordingInfo;
            DisplayNewMetadataGui(matchResult);
        }
        else if (_lastFingerprints.Any())
        {
            matchResult = SelectFromMatchesGui();
        }
        else
        {
             MessageBox.Query("No Matches", "No valid fingerprint matches to select from after scoring.", "Ok");
             return;
        }
        
        if (matchResult != null)
        {
            if (AskToSaveChangesGui(matchResult))
            {
                SaveChangesToMyriadGui(itemNumber, matchResult, client, settings);
            }
            else
            {
                MessageBox.Query("Save Cancelled", "Changes were not saved.", "Ok");
            }
        }
        else
        {
             MessageBox.Query("No Match Selected", "No match was selected or confirmed. No changes made.", "Ok");
        }
        AddToRecentItems(itemNumber);
    }

    private static void DisplayItemDetailsGui(Result item)
    {
        var lines = new List<string>
        {
            $"Title: {item.Title ?? "[not set]"}",
            $"Media ID: {item.MediaId}",
            $"Duration: {item.TotalLength ?? "[unknown]"}",
            $"Artist: {item.Copyright?.Performer ?? "[not set]"}"
        };
        MessageBox.Query("Current Item Details", string.Join("\n", lines), "Ok");
    }

    private static MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? SelectFromMatchesGui()
    {
        if (_lastFingerprints == null || !_lastFingerprints.Any())
        {
            MessageBox.ErrorQuery("Selection Error", "No fingerprint matches available to select from.", "Ok");
            return null;
        }

        var dialog = new Dialog("Select Best Match", 70, 20);
        var matchItems = new List<string>();
        var matchMap = new Dictionary<string, MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording>();

        var scoredMatchesForDisplay = _lastFingerprints.Select(m => {
            if (m.RecordingInfo == null) return (Match: m, Score: 0.0, Recording: (MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording?)null);
            return (Match: m, Score: m.Score, Recording: m.RecordingInfo);
        }).OrderByDescending(m => m.Score).ToList();

        foreach (var matchTuple in scoredMatchesForDisplay)
        {
            var recording = matchTuple.Recording;
            if (recording == null) continue;

            string artistNames = recording.ArtistCredit?.Any() == true ? string.Join(", ", recording.ArtistCredit.Select(a => a.Name ?? string.Empty)) : "[no artist]";
            string releaseTitle = recording.Releases?.FirstOrDefault()?.Title ?? "[no album]";
            string confidence = (matchTuple.Score * 100).ToString("F0") + "%";
            string displayText = $"{confidence} - {recording.Title ?? ""} - {artistNames} - {releaseTitle}";
            
            matchItems.Add(displayText);
            if (!matchMap.ContainsKey(displayText)) { matchMap.Add(displayText, recording); }
        }

        if (!matchItems.Any()) { MessageBox.ErrorQuery("Selection Error", "No valid matches could be displayed.", "Ok"); return null; }

        var listView = new ListView(matchItems) { X = 1, Y = 1, Width = Dim.Fill() - 2, Height = Dim.Fill() - 4 };
        MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording? selectedRecording = null;
        var selectButton = new Button("Select") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
        selectButton.Clicked += () => {
            if (listView.SelectedItem >= 0 && listView.SelectedItem < matchItems.Count)
            {
                selectedRecording = matchMap[matchItems[listView.SelectedItem]];
                if (selectedRecording != null) // Add null check here
                {
                    DisplayNewMetadataGui(selectedRecording);
                }
            }
            Application.RequestStop(dialog);
        };
        var noneButton = new Button("None") { X = Pos.Right(selectButton) + 1, Y = selectButton.Y };
        noneButton.Clicked += () => { selectedRecording = null; Application.RequestStop(dialog); };
        
        dialog.Add(listView, selectButton, noneButton);
        listView.SetFocus();
        Application.Run(dialog);
        return selectedRecording;
    }

    private static void DisplayNewMetadataGui(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
    {
        var lines = new List<string> { $"Title: {recordingInfo.Title ?? "[not found]"}" };
        if (recordingInfo.ArtistCredit?.Any() == true)
        {
            lines.Add($"Artist: {recordingInfo.ArtistCredit.First().Name ?? "[unnamed artist]"}");
            if (recordingInfo.ArtistCredit.Count > 1) { for (int i = 1; i < recordingInfo.ArtistCredit.Count; i++) { lines.Add($"Additional Artist: {recordingInfo.ArtistCredit[i].Name ?? "[unnamed artist]"}"); } }
        } else { lines.Add("Artist: [no artist credit found]"); }
        if (!string.IsNullOrEmpty(recordingInfo.Disambiguation)) { lines.Add($"Info: {recordingInfo.Disambiguation}"); }
        if (recordingInfo.Releases?.Any() == true)
        {
            var firstRelease = recordingInfo.Releases.First();
            lines.Add($"Album: {firstRelease.Title ?? "[unknown album]"}");
            if (firstRelease.Date != null) { lines.Add($"Release Date: {firstRelease.Date.ToString()}"); }
        }
        MessageBox.Query("Selected Metadata", string.Join("\n", lines), "Ok");
    }

    private static bool AskToSaveChangesGui(MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo)
    {
        int result = MessageBox.Query("Save Changes", "Do you want to save these changes to Myriad?", "Yes", "No");
        return result == 0;
    }

    private static void SaveChangesToMyriadGui(int itemNumber, MetaBrainz.MusicBrainz.Interfaces.Entities.IRecording recordingInfo, RestClient client, AppSettings settings)
    {
        var savingDialog = new Dialog("Saving...", 40, 7) {Title = $"Item {itemNumber}"};
        savingDialog.Add(new Label($"Saving changes for item {itemNumber}...") { X = 1, Y = 1 });
        var savingToken = Application.Begin(savingDialog);

        var titleUpdate = new MyriadTitleSchema { ItemTitle = recordingInfo.Title ?? string.Empty, Artists = recordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>() };
        var request = CreateUpdateItemRequest(itemNumber, settings.PlayoutWriteKey, titleUpdate);
        RestResponse result;
        try { result = client.Execute(request, Method.Post); } // Synchronous
        catch(Exception ex) { Application.End(savingToken); MessageBox.ErrorQuery("API Connection Error", $"Failed to save to Myriad API: {ex.Message}", "Ok"); Log.Error(ex, "Myriad API connection error on Post for item {ItemNumber}", itemNumber); return;}

        Application.End(savingToken);
        if (result.IsSuccessful) { MessageBox.Query("Success", "Metadata successfully updated in Myriad system!", "Ok");}
        else { HandleApiErrorGui(result, "update metadata in Myriad system"); }
    }
    
    private static void DisplayErrorGui(string message, Exception? ex = null)
    {
        if (ex != null) { Log.Error(ex, message); }
        MessageBox.ErrorQuery("Error", message, "Ok");
    }

     private static void HandleApiErrorGui(RestResponse response, string operation)
    {
        string errorDetails = $"Failed to {operation}.\nError: {response.ErrorMessage ?? "Unknown error"}";
        if (!string.IsNullOrEmpty(response.Content)) { errorDetails += $"\nResponse Content:\n{response.Content}"; }
        Log.Error($"API Error during {operation}. Status: {response.StatusCode}. Response: {response.Content}", response.ErrorException);
        MessageBox.ErrorQuery($"API Error ({response.StatusCode})", errorDetails, "Ok");
    }

    private static void ProcessBatchItemsGui(RestClient client, AppSettings settings)
    {
        var dialog = new Dialog("Process Batch of Items", 60, 12);
        var startLabel = new Label("Start Item Number:") { X = 1, Y = 1 };
        var startField = new TextField("") { X = Pos.Right(startLabel) + 1, Y = 1, Width = 10 };
        var endLabel = new Label("End Item Number:") { X = 1, Y = Pos.Bottom(startLabel) + 1 };
        var endField = new TextField("") { X = Pos.Right(endLabel) + 1, Y = Pos.Top(endLabel), Width = 10 };
        var errorLabel = new Label("") { X = 1, Y = Pos.Bottom(endLabel) + 1, Width = Dim.Fill() -2 /* Removed TextColor, will use ColorScheme */ };
        var errorColorScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Normal.Background ?? Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Focus.Background ?? Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotNormal.Background ?? Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotFocus.Background ?? Color.Black)
        };
        errorLabel.ColorScheme = errorColorScheme;
        dialog.Add(startLabel, startField, endLabel, endField, errorLabel);

        bool inputValid = false; int startItem = 0, endItem = 0;
        var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
        processButton.Clicked += () => {
            errorLabel.Text = "";
            if (!int.TryParse(startField.Text.ToString(), out startItem) || startItem <= 0) { errorLabel.Text = "Start item number must be a positive integer."; startField.SetFocus(); return; }
            if (!int.TryParse(endField.Text.ToString(), out endItem)) { errorLabel.Text = "End item number must be a valid integer."; endField.SetFocus(); return; }
            if (endItem < startItem) { errorLabel.Text = "End item number must be >= start item number."; endField.SetFocus(); return; }
            if (endItem - startItem + 1 > 100) { errorLabel.Text = "Maximum batch size is 100 items."; endField.SetFocus(); return; }
            inputValid = true; Application.RequestStop(dialog);
        };
        var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y };
        cancelButton.Clicked += () => { inputValid = false; Application.RequestStop(dialog); };
        dialog.AddButton(processButton); dialog.AddButton(cancelButton);
        startField.SetFocus(); Application.Run(dialog);

        if (!inputValid) return;
        _batchProcessItems.Clear();

        var progressDialog = new Dialog("Batch Processing...", 50, 7);
        var progressLabel = new Label($"Processing items {startItem} to {endItem}...") { X = 1, Y = 1, Width = Dim.Fill()-2 };
        var currentItemLabel = new Label("") { X = 1, Y = 2, Width = Dim.Fill()-2 };
        progressDialog.Add(progressLabel, currentItemLabel);
        var batchProgressToken = Application.Begin(progressDialog);

        for (int itemNumber = startItem; itemNumber <= endItem; itemNumber++)
        {
            currentItemLabel.Text = $"Reading item {itemNumber}..."; Application.Refresh();
            try
            {
                var request = CreateReadItemRequest(itemNumber, settings.PlayoutReadKey);
                var apiResponse = client.Get(request);
                var readItemResponse = ProcessApiResponse(apiResponse, "Failed to parse API response for batch ReadItem");
                if (readItemResponse == null) { _batchProcessItems.Add(new BatchProcessItem { ItemNumber = itemNumber, Error = "API response was null or failed to parse.", IsSelected = false }); continue; }

                var batchItem = CreateBatchItem(itemNumber, readItemResponse);
                currentItemLabel.Text = $"Fingerprinting item {itemNumber}..."; Application.Refresh();
                try
                {
                    ProcessBatchItemGui(batchItem); // This can throw ProcessingException
                }
                catch (ProcessingUtils.ProcessingException pex)
                {
                    batchItem.Error = pex.Message; // Store the error message
                    Log.Warning(pex, "ProcessingException for batch item {ItemNumber} during fingerprinting/lookup.", itemNumber);
                }
                catch (Exception ex) // Catch any other unexpected errors from ProcessBatchItemGui
                {
                    batchItem.Error = $"Unexpected error: {ex.Message}";
                    Log.Error(ex, "Unexpected error in ProcessBatchItemGui for item {ItemNumber}", itemNumber);
                }
                _batchProcessItems.Add(batchItem); AddToRecentItems(itemNumber);
            }
            catch (Exception ex) // Catch errors from Get/ProcessApiResponse for an item, or other general errors in the loop
            {
                Log.Error(ex, $"Outer loop error processing item {itemNumber} in batch");
                _batchProcessItems.Add(new BatchProcessItem { ItemNumber = itemNumber, Error = $"Failed to process: {ex.Message}", IsSelected = false });
            }
        }
        Application.End(batchProgressToken);
        ShowBatchResultsGui();
        ShowBatchEditTableGui(client, settings);
    }

    private static void ProcessBatchItemGui(BatchProcessItem item) // No RestClient/AppSettings needed here, just processes the data
    {
        if (string.IsNullOrEmpty(item.MediaLocation) || !File.Exists(item.MediaLocation))
        { item.Error = "Media file not found"; item.IsSelected = false; return; }
        
        // Fingerprint method now throws ProcessingException on failure
        var matches = ProcessingUtils.Fingerprint(item.MediaLocation); // This line can throw
        
        if (matches.Count == 0) { item.Error = "No fingerprint matches found"; item.IsSelected = false; item.AvailableMatches = new List<ProcessingUtils.FingerprintMatch>(); return; }
        
        var bestMatch = ScoreMatches(matches, item.OldTitle, item.OldArtist).FirstOrDefault();
        if (bestMatch.Score > 0.8 && bestMatch.Match.RecordingInfo != null)
        {
            var recordingInfo = bestMatch.Match.RecordingInfo;
            item.NewTitle = recordingInfo.Title ?? string.Empty;
            item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
            item.IsSelected = true; item.ConfidenceScore = bestMatch.Score; item.RecordingInfo = recordingInfo;
        } else { item.IsSelected = false; item.ConfidenceScore = bestMatch.Score; item.AvailableMatches = matches; }
    }

    private static void ShowBatchResultsGui()
    {
        var successCount = _batchProcessItems.Count(i => i.IsSelected && string.IsNullOrEmpty(i.Error));
        var errorCount = _batchProcessItems.Count(i => !string.IsNullOrEmpty(i.Error));
        var needsReviewCount = _batchProcessItems.Count(i => string.IsNullOrEmpty(i.Error) && !i.IsSelected && (i.AvailableMatches?.Any() ?? false));
        var noMatchCount = _batchProcessItems.Count(i => string.IsNullOrEmpty(i.Error) && !i.IsSelected && !(i.AvailableMatches?.Any() ?? false));

        var message = new StringBuilder();
        message.AppendLine("Batch Processing Complete!");
        message.AppendLine($"Successfully processed (high confidence): {successCount}");
        message.AppendLine($"Needs review (matches found): {needsReviewCount}");
        message.AppendLine($"No automatic match / No matches found: {noMatchCount}");
        message.AppendLine($"Errors: {errorCount}");
        message.AppendLine("\nProceed to the batch edit table to review and save changes.");
        MessageBox.Query("Batch Processing Results", message.ToString(), "Ok");
    }

    private static void ShowBatchEditTableGui(RestClient client, AppSettings settings)
    {
        if (_batchProcessItems.Count == 0) { MessageBox.Query("Batch Edit", "No items were processed in the batch.", "Ok"); return; }
        var editDialog = new Dialog("Batch Edit Table", 120, 30);
        var tableView = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 3, FullRowSelect = true, };
        var table = new System.Data.DataTable(); // This is the DataTable instance
        table.Columns.Add("✓", typeof(string)); table.Columns.Add("Item #", typeof(int)); table.Columns.Add("Old Title", typeof(string)); table.Columns.Add("Old Artist", typeof(string)); table.Columns.Add("New Title", typeof(string)); table.Columns.Add("New Artist", typeof(string)); table.Columns.Add("Conf.", typeof(string)); table.Columns.Add("Status", typeof(string));

        tableView.Table = table; // Assign the DataTable to the TableView
        RefreshBatchTableView(tableView, table); // Initial population using the same DataTable instance

        tableView.Style.AlwaysShowHeaders = true;
        editDialog.Add(tableView);

        var editButton = new Button("Edit") { X = 1, Y = Pos.Bottom(tableView) +1 };
        var toggleSelectButton = new Button("Toggle Select") { X = Pos.Right(editButton) + 1, Y = editButton.Y };
        var saveButton = new Button("Save Selected") { X = Pos.Right(toggleSelectButton) + 1, Y = editButton.Y };
        var exitButton = new Button("Exit") { X = Pos.Right(saveButton) + 5, Y = editButton.Y };

        editButton.Clicked += () => {
            if (tableView.SelectedRow < 0 || tableView.SelectedRow >= _batchProcessItems.Count) return;
            var selectedItem = _batchProcessItems[tableView.SelectedRow];
            EditBatchItemGui(selectedItem, client, settings);
            RefreshBatchTableView(tableView, table);
        };
        toggleSelectButton.Clicked += () => {
            if (tableView.SelectedRow < 0 || tableView.SelectedRow >= _batchProcessItems.Count) return;
            ToggleItemSelectionGui(_batchProcessItems[tableView.SelectedRow]);
            RefreshBatchTableView(tableView, table);
        };
        saveButton.Clicked += () => { SaveBatchChangesGui(client, settings); RefreshBatchTableView(tableView, table); };
        exitButton.Clicked += () => { Application.RequestStop(editDialog); };
        editDialog.Add(editButton, toggleSelectButton, saveButton, exitButton);
        Application.Run(editDialog);
    }

    private static void RefreshBatchTableView(TableView tableView, System.Data.DataTable table)
    {
        table.Rows.Clear();
        foreach (var item in _batchProcessItems)
        {
            string selectedMark = item.IsSelected ? "✓" : " ";
            string confidenceStr = item.ConfidenceScore > 0 ? $"{item.ConfidenceScore:P0}" : "-";
            string status;
            if (!string.IsNullOrEmpty(item.Error)) status = $"Error: {item.Error.Split('\n')[0]}";
            else if (item.AvailableMatches?.Any() ?? false && string.IsNullOrEmpty(item.NewTitle)) status = "Needs Selection";
            else if (!item.IsSelected && !string.IsNullOrEmpty(item.NewTitle)) status = "Review & Select";
            else if (item.IsSelected) status = "Selected"; else status = "Needs Review";
            table.Rows.Add(selectedMark, item.ItemNumber, item.OldTitle, item.OldArtist, item.NewTitle ?? "", item.NewArtist ?? "", confidenceStr, status);
        }
        tableView.SetNeedsDisplay();
    }

    private static void EditBatchItemGui(BatchProcessItem item, RestClient client, AppSettings settings)
    {
        var editDialog = new Dialog($"Edit Item: {item.ItemNumber}", 70, 20);
        var details = new StringBuilder();
        details.AppendLine($"Item Number: {item.ItemNumber}"); details.AppendLine($"Old Title: {item.OldTitle}"); details.AppendLine($"Old Artist: {item.OldArtist}");
        if (!string.IsNullOrEmpty(item.Error)) { details.AppendLine($"Error: {item.Error}"); }
        var detailsLabel = new Label(details.ToString()) { X = 1, Y = 1, Width = Dim.Fill() -2 };
        var newTitleLabel = new Label("New Title:") { X = 1, Y = Pos.Bottom(detailsLabel) + 1 };
        var newTitleField = new TextField(item.NewTitle ?? "") { X = Pos.Right(newTitleLabel) + 1, Y = Pos.Top(newTitleLabel), Width = Dim.Fill() - 20 };
        var newArtistLabel = new Label("New Artist:") { X = 1, Y = Pos.Bottom(newTitleLabel) };
        var newArtistField = new TextField(item.NewArtist ?? "") { X = Pos.Right(newArtistLabel) + 1, Y = Pos.Top(newArtistLabel), Width = Dim.Fill() - 20 };
        editDialog.Add(detailsLabel, newTitleLabel, newTitleField, newArtistLabel, newArtistField);

        if (string.IsNullOrEmpty(item.NewTitle) && (item.AvailableMatches?.Any() ?? false) && string.IsNullOrEmpty(item.Error))
        {
            var selectMatchButton = new Button("Choose from Matches") { X = Pos.Center(), Y = Pos.Bottom(newArtistLabel) + 1};
            selectMatchButton.Clicked += () => {
                _lastFingerprints = item.AvailableMatches;
                var recordingInfo = SelectFromMatchesGui();
                if (recordingInfo != null)
                {
                    item.NewTitle = recordingInfo.Title ?? string.Empty;
                    item.NewArtist = string.Join(", ", recordingInfo.ArtistCredit?.Select(a => a.Name) ?? Array.Empty<string>());
                    item.RecordingInfo = recordingInfo;
                    item.ConfidenceScore = _lastFingerprints.FirstOrDefault(f => f.RecordingInfo == recordingInfo)?.Score ?? item.ConfidenceScore;
                    newTitleField.Text = item.NewTitle; newArtistField.Text = item.NewArtist; item.Error = null;
                    MessageBox.Query("Match Selected", "Metadata populated from selection.", "Ok");
                }
            };
            editDialog.Add(selectMatchButton);
        }

        var okButton = new Button("Ok") { X = Pos.Center() - 8, Y = Pos.Bottom(editDialog) - 3, IsDefault = true };
        okButton.Clicked += () => {
            item.NewTitle = newTitleField.Text?.ToString() ?? string.Empty;
            item.NewArtist = newArtistField.Text?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(item.NewTitle) && !string.IsNullOrWhiteSpace(item.NewArtist)) { item.IsSelected = true; item.Error = null; }
            else { item.IsSelected = false; MessageBox.ErrorQuery("Missing Info", "Title and Artist cannot be empty if item is to be saved.", "Ok");}
            Application.RequestStop(editDialog);
        };
        var cancelButton = new Button("Cancel") { X = Pos.Right(okButton) + 1, Y = okButton.Y };
        cancelButton.Clicked += () => { Application.RequestStop(editDialog); };
        editDialog.AddButton(okButton); editDialog.AddButton(cancelButton);
        newTitleField.SetFocus(); Application.Run(editDialog);
    }

    private static void ToggleItemSelectionGui(BatchProcessItem item)
    {
         if (!string.IsNullOrEmpty(item.Error) || string.IsNullOrEmpty(item.NewTitle) || string.IsNullOrEmpty(item.NewArtist))
            { MessageBox.ErrorQuery("Selection Error", "This item cannot be selected due to errors or missing metadata. Edit the item first.", "Ok"); }
        else { item.IsSelected = !item.IsSelected; }
    }

    private static void SaveBatchChangesGui(RestClient client, AppSettings settings)
    {
        var selectedItems = _batchProcessItems.Where(i => i.IsSelected && string.IsNullOrEmpty(i.Error)).ToList();
        if (!selectedItems.Any()) { MessageBox.Query("Save Changes", "No items are currently selected for saving or all selected items have errors.", "Ok"); return; }
        if (MessageBox.Query("Confirm Save", $"Are you sure you want to save changes for {selectedItems.Count} item(s)?", "Yes", "No") == 1) return;

        var progressDialog = new Dialog("Saving Changes...", 50, 7);
        var progressLabel = new Label($"Saving {selectedItems.Count} items...") { X = 1, Y = 1, Width = Dim.Fill() -2 };
        var currentItemLabel = new Label("") { X = 1, Y = 2, Width = Dim.Fill() -2 };
        progressDialog.Add(progressLabel, currentItemLabel);
        var saveProgressToken = Application.Begin(progressDialog);
        int successCount = 0, errorCount = 0;

        for (int i = 0; i < selectedItems.Count; i++)
        {
            var item = selectedItems[i];
            currentItemLabel.Text = $"Saving item {item.ItemNumber} ({i + 1}/{selectedItems.Count})..."; Application.Refresh();
            try
            {
                MyriadTitleSchema titleUpdate;
                if (item.RecordingInfo != null && item.RecordingInfo.Title == item.NewTitle && (string.Join(", ", item.RecordingInfo.ArtistCredit?.Select(a => a.Name ?? string.Empty) ?? Array.Empty<string>()) == item.NewArtist || item.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name == item.NewArtist) )
                {
                    titleUpdate = new MyriadTitleSchema { ItemTitle = item.RecordingInfo.Title ?? string.Empty, Artists = item.RecordingInfo.ArtistCredit?.Select(x => x.Name ?? string.Empty).ToList() ?? new List<string>()};
                }
                else
                {
                    List<string> artistsList = new List<string>();
                    if (item.NewArtist != null)
                    {
                        artistsList = item.NewArtist.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
                    }
                    titleUpdate = new MyriadTitleSchema { ItemTitle = item.NewTitle, Artists = artistsList };
                }
                
                if (string.IsNullOrWhiteSpace(titleUpdate.ItemTitle) || !titleUpdate.Artists.Any())
                { item.Error = "Title or Artist is empty, cannot save."; item.IsSelected = false; errorCount++; Log.Warning($"Skipping save for item {item.ItemNumber} due to missing title/artist after final check."); continue; }

                var request = CreateUpdateItemRequest(item.ItemNumber, settings.PlayoutWriteKey, titleUpdate);
                var result = client.Execute(request, Method.Post);
                if (result.IsSuccessful) { successCount++; item.Error = null; }
                else { errorCount++; item.Error = $"API Error: {result.ErrorMessage ?? result.Content ?? "Unknown"}"; item.IsSelected = false; Log.Error($"Failed to save item {item.ItemNumber}: {item.Error}", result.ErrorException); }
            } catch (Exception ex) { errorCount++; item.Error = $"Exception: {ex.Message}"; item.IsSelected = false; Log.Error(ex, $"Exception while saving item {item.ItemNumber}"); }
        }
        Application.End(saveProgressToken);
        var summaryMessage = new StringBuilder();
        summaryMessage.AppendLine($"Successfully saved: {successCount} item(s)."); summaryMessage.AppendLine($"Failed to save: {errorCount} item(s).");
        if (errorCount > 0) { summaryMessage.AppendLine("Check the table for error details on failed items."); }
        MessageBox.Query("Save Complete", summaryMessage.ToString(), "Ok");
    }

    private static void ProcessRecentItemsGui(RestClient client, AppSettings settings)
    {
        if (_recentItems.Count == 0) { MessageBox.Query("Recent Items", "No recent items found.", "Ok"); return; }
        var dialog = new Dialog("Recent Items", 50, 15 + Math.Min(_recentItems.Count, 10));
        var recentItemsList = _recentItems.Select(i => $"Item {i}").ToList();
        var listView = new ListView(recentItemsList) { X = 1, Y = 1, Width = Dim.Fill() - 2, Height = Dim.Fill() - 4, AllowsMarking = false, AllowsMultipleSelection = false };
        dialog.Add(listView); int selectedItemNumber = -1;
        var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) -3, IsDefault = true};
        processButton.Clicked += () => {
            if (listView.SelectedItem >= 0 && listView.SelectedItem < recentItemsList.Count)
            { if (int.TryParse(recentItemsList[listView.SelectedItem].Split(' ')[1], out int itemNumber)) { selectedItemNumber = itemNumber; Application.RequestStop(dialog); }
              else { MessageBox.ErrorQuery("Error", "Could not parse selected item number.", "Ok"); }
            } else { MessageBox.Query("Selection", "Please select an item to process.", "Ok"); }
        };
        var cancelButton = new Button("Cancel") {X = Pos.Right(processButton) + 1, Y = processButton.Y};
        cancelButton.Clicked += () => { selectedItemNumber = -1; Application.RequestStop(dialog); };
        dialog.AddButton(processButton); dialog.AddButton(cancelButton);
        listView.SetFocus(); Application.Run(dialog);
        if (selectedItemNumber != -1)
        {
            try { ProcessItemGui(selectedItemNumber, client, settings); }
            catch (ProcessingUtils.ProcessingException pex) { MessageBox.ErrorQuery("Processing Error", pex.Message, "Ok");}
            catch (Exception ex) { MessageBox.ErrorQuery("Unexpected Error", $"An error occurred: {ex.Message}", "Ok"); Log.Error(ex, "Error in ProcessRecentItemsGui after ProcessItemGui call for item {ItemNumber}", selectedItemNumber);}
        }
    }

    private static void ShowHelpGui()
    {
        var helpDialog = new Dialog("Help & Information", 60, 15);
        var helpText = @"• Use arrow keys and Enter to navigate menus.
• Alt + highlighted letter activates menu items.
• Tab/Shift+Tab to move between UI elements.
• Esc to close dialogs/windows.
• Ctrl+Q to quit the application.

For more information, visit:
https://github.com/RebelliousPebble/MyriadMusicTagger";
        var helpTextView = new TextView() { X = 1, Y = 1, Width = Dim.Fill() - 2, Height = Dim.Fill() - 2, Text = helpText, ReadOnly = true };
        var okButton = new Button("Ok") { X = Pos.Center(), Y = Pos.Bottom(helpDialog) - 3, IsDefault = true};
        okButton.Clicked += () => Application.RequestStop(helpDialog);
        helpDialog.Add(helpTextView); helpDialog.AddButton(okButton);
        Application.Run(helpDialog);
    }

    private static void LoadRecentItems() { _recentItems.Clear(); /* TODO: Persist/Load recent items */ }
    private static void AddToRecentItems(int itemNumber) { if (!_recentItems.Contains(itemNumber)) { if (_recentItems.Count >= 10) { _recentItems.Dequeue(); } _recentItems.Enqueue(itemNumber); } }
    private static (string Artist, string Title) ParseArtistAndTitle(string title, string fallbackArtist = "")
    {
        var titleParts = title?.Split(new[] { " - " }, StringSplitOptions.None) ?? Array.Empty<string>();
        return titleParts.Length == 2 ? (titleParts[0].Trim(), titleParts[1].Trim()) : (fallbackArtist, title ?? string.Empty);
    }

    private static RestRequest CreateReadItemRequest(int itemNumber, string readKey) => new RestRequest("/api/Media/ReadItem").AddQueryParameter("mediaId", itemNumber.ToString()).AddQueryParameter("attributesStationId", "-1").AddQueryParameter("additionalInfo", "Full").AddHeader("X-API-Key", readKey);
    private static RestRequest CreateUpdateItemRequest(int itemNumber, string writeKey, MyriadTitleSchema titleUpdate)
    {
        var request = new RestRequest("/api/Media/SetItemTitling");
        request.AddQueryParameter("mediaId", itemNumber.ToString());
        request.AddHeader("X-API-Key", writeKey); request.AddHeader("Content-Type", "application/json"); request.AddHeader("Accept", "application/json");
        request.AddBody(JsonConvert.SerializeObject(titleUpdate, Formatting.None)); return request;
    }

    private static Result? ProcessApiResponse(RestResponse response, string errorMessageContext)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content != null)
        {
            var myriadMediaItem = JsonConvert.DeserializeObject<MyriadMediaItem>(response.Content);
            if (myriadMediaItem?.Result != null) return myriadMediaItem.Result;

            Log.Error("Failed to deserialize {Context} response content: {Content}", errorMessageContext, response.Content);
            DisplayErrorGui($"Failed to parse API response for {errorMessageContext}. Content might be invalid.");
            return null;
        }

        string errorMsg = $"API request for {errorMessageContext} failed. Status: {response.StatusCode}.";
        if (!string.IsNullOrEmpty(response.ErrorMessage)) errorMsg += $" Error: {response.ErrorMessage}";
        if (!string.IsNullOrEmpty(response.Content)) errorMsg += $"\nContent: {response.Content}";
        
        DisplayErrorGui(errorMsg); // DisplayErrorGui will also log
        return null;
    }
    
    private static double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0;
        int distance = ComputeLevenshteinDistance(str1, str2); int maxLength = Math.Max(str1.Length, str2.Length);
        return maxLength == 0 ? 1.0 : (1.0 - ((double)distance / maxLength)); // Avoid division by zero
    }
    
    private static int ComputeLevenshteinDistance(string str1, string str2)
    {
        int[,] matrix = new int[str1.Length + 1, str2.Length + 1];
        for (int i = 0; i <= str1.Length; i++) matrix[i, 0] = i; for (int j = 0; j <= str2.Length; j++) matrix[0, j] = j;
        for (int i = 1; i <= str1.Length; i++) { for (int j = 1; j <= str2.Length; j++) { int cost = (str1[i - 1] == str2[j - 1]) ? 0 : 1; matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost); } }
        return matrix[str1.Length, str2.Length];
    }
    
    private static string RemoveCommonExtras(string title)
    {
        string[] commonExtras = new[] { "(original version)", "(instrumental)", "(radio edit)", "(album version)", "(official video)", "(official audio)", "(lyric video)", "(official music video)", "(clean)", "(explicit)" };
        var result = title.ToLowerInvariant();
        foreach (var extra in commonExtras) { result = result.Replace(extra, ""); }
        return result.Trim();
    }

    private static BatchProcessItem CreateBatchItem(int itemNumber, Result response) => new BatchProcessItem { ItemNumber = itemNumber, OldTitle = response.Title ?? string.Empty, OldArtist = response.Copyright?.Performer ?? string.Empty, MediaLocation = response.MediaLocation };

    private static List<(ProcessingUtils.FingerprintMatch Match, double Score)> ScoreMatches(List<ProcessingUtils.FingerprintMatch> matches, string existingTitle, string existingArtist)
    {
        var (parsedArtist, parsedTitle) = ParseArtistAndTitle(existingTitle, existingArtist);
        return matches.Select(m => {
            if (m.RecordingInfo == null) return (Match: m, Score: 0.0);
            var matchTitle = m.RecordingInfo.Title ?? ""; var matchArtist = m.RecordingInfo.ArtistCredit?.FirstOrDefault()?.Name ?? "";
            double score = m.Score;
            double titleSimilarity = CalculateStringSimilarity(matchTitle, parsedTitle);
            double artistSimilarity = CalculateStringSimilarity(matchArtist, parsedArtist);
            score = (score * 0.5) + (titleSimilarity * 0.3) + (artistSimilarity * 0.2);
            return (Match: m, Score: score);
        }).OrderByDescending(m => m.Score).ToList();
    }

    private static double CalculateStringSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) && string.IsNullOrEmpty(str2)) return 1.0; // Both empty is a perfect match
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0.0; // One empty is no match
        string normalized1 = RemoveCommonExtras(str1); string normalized2 = RemoveCommonExtras(str2);
        if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (normalized1.Contains(normalized2, StringComparison.OrdinalIgnoreCase) || normalized2.Contains(normalized1, StringComparison.OrdinalIgnoreCase)) return 0.8; // Adjusted for case-insensitivity
        return 1.0 - ((double)ComputeLevenshteinDistance(normalized1, normalized2) / Math.Max(normalized1.Length, normalized2.Length));
    }
}
