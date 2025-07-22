using Terminal.Gui;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Core;
using MyriadMusicTagger.Utils;
using Serilog;

namespace MyriadMusicTagger.UI.Controllers
{
    /// <summary>
    /// Controller for batch processing UI operations
    /// </summary>
    public class BatchProcessingController
    {
        private readonly BatchProcessingService _batchProcessingService;
        private readonly RecentItemsManager _recentItemsManager;

        public BatchProcessingController(BatchProcessingService batchProcessingService, RecentItemsManager recentItemsManager)
        {
            _batchProcessingService = batchProcessingService ?? throw new ArgumentNullException(nameof(batchProcessingService));
            _recentItemsManager = recentItemsManager ?? throw new ArgumentNullException(nameof(recentItemsManager));
        }

        /// <summary>
        /// Shows the batch processing dialog for item ranges
        /// </summary>
        public void ShowBatchProcessingDialog()
        {
            var dialog = new Dialog("Process Batch of Items", 60, 12);
            var startLabel = new Label("Start Item Number:") { X = 1, Y = 1 };
            var startField = new TextField("") { X = Pos.Right(startLabel) + 1, Y = 1, Width = 10 };
            var endLabel = new Label("End Item Number:") { X = 1, Y = Pos.Bottom(startLabel) + 1 };
            var endField = new TextField("") { X = Pos.Right(endLabel) + 1, Y = Pos.Top(endLabel), Width = 10 };
            var errorLabel = new Label("") { X = 1, Y = Pos.Bottom(endLabel) + 1, Width = Dim.Fill() - 2 };
            
            var errorColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Normal.Background ?? Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Focus.Background ?? Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotNormal.Background ?? Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotFocus.Background ?? Color.Black)
            };
            errorLabel.ColorScheme = errorColorScheme;
            dialog.Add(startLabel, startField, endLabel, endField, errorLabel);

            bool inputValid = false;
            int startItem = 0, endItem = 0;
            
            var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
            processButton.Clicked += () => {
                errorLabel.Text = "";
                if (!int.TryParse(startField.Text.ToString(), out startItem) || startItem <= 0) 
                { 
                    errorLabel.Text = "Start item number must be a positive integer."; 
                    startField.SetFocus(); 
                    return; 
                }
                if (!int.TryParse(endField.Text.ToString(), out endItem)) 
                { 
                    errorLabel.Text = "End item number must be a valid integer."; 
                    endField.SetFocus(); 
                    return; 
                }
                if (endItem < startItem) 
                { 
                    errorLabel.Text = "End item number must be >= start item number."; 
                    endField.SetFocus(); 
                    return; 
                }
                inputValid = true; 
                Application.RequestStop(dialog);
            };
            
            var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y };
            cancelButton.Clicked += () => { inputValid = false; Application.RequestStop(dialog); };
            
            dialog.AddButton(processButton);
            dialog.AddButton(cancelButton);
            startField.SetFocus();
            Application.Run(dialog);

            if (!inputValid) return;

            ProcessBatchRange(startItem, endItem);
        }

        /// <summary>
        /// Shows the CSV file processing dialog
        /// </summary>
        public void ShowCsvProcessingDialog()
        {
            var dialog = new Dialog("Process Items from CSV File", 70, 15);
            
            var filePathLabel = new Label("CSV File Path:") { X = 1, Y = 1 };
            var filePathField = new TextField("") { X = Pos.Right(filePathLabel) + 1, Y = 1, Width = Dim.Fill() - 15 };
            var browseButton = new Button("Browse") { X = Pos.Right(filePathField) + 1, Y = 1 };
            
            var instructionLabel = new Label("File should contain cart numbers separated by commas, semicolons, or newlines.") 
            { 
                X = 1, Y = Pos.Bottom(filePathLabel) + 1, Width = Dim.Fill() - 2 
            };
            
            var exampleLabel = new Label("Example: 12345,12346,12347 or one number per line") 
            { 
                X = 1, Y = Pos.Bottom(instructionLabel) + 1, Width = Dim.Fill() - 2 
            };
            
            var errorLabel = new Label("") { X = 1, Y = Pos.Bottom(exampleLabel) + 1, Width = Dim.Fill() - 2 };
            var errorColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Normal.Background ?? Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Focus.Background ?? Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotNormal.Background ?? Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotFocus.Background ?? Color.Black)
            };
            errorLabel.ColorScheme = errorColorScheme;

            browseButton.Clicked += () => {
                var openDialog = new OpenDialog("Select CSV File", "Select a CSV file containing cart numbers");
                openDialog.AllowedFileTypes = new string[] { ".csv", ".txt" };
                Application.Run(openDialog);
                if (!openDialog.Canceled && !string.IsNullOrEmpty(openDialog.FilePath.ToString()))
                {
                    filePathField.Text = openDialog.FilePath.ToString();
                }
            };

            dialog.Add(filePathLabel, filePathField, browseButton, instructionLabel, exampleLabel, errorLabel);

            bool inputValid = false;
            List<int> cartNumbers = new List<int>();
            
            var processButton = new Button("Process") { X = Pos.Center() - 10, Y = Pos.Bottom(dialog) - 3, IsDefault = true };
            processButton.Clicked += () => {
                errorLabel.Text = "";
                var filePath = filePathField.Text.ToString();
                
                if (string.IsNullOrEmpty(filePath))
                {
                    errorLabel.Text = "Please specify a file path.";
                    filePathField.SetFocus();
                    return;
                }
                
                if (!File.Exists(filePath))
                {
                    errorLabel.Text = "File does not exist.";
                    filePathField.SetFocus();
                    return;
                }
                
                try
                {
                    cartNumbers = CsvParser.ParseCartNumbersFromFile(filePath);
                    if (cartNumbers.Count == 0)
                    {
                        errorLabel.Text = "No valid cart numbers found in file.";
                        return;
                    }
                    
                    inputValid = true;
                    Application.RequestStop(dialog);
                }
                catch (Exception ex)
                {
                    errorLabel.Text = $"Error reading file: {ex.Message}";
                    Log.Error(ex, "Error reading CSV file: {FilePath}", filePath);
                }
            };
            
            var cancelButton = new Button("Cancel") { X = Pos.Right(processButton) + 1, Y = processButton.Y };
            cancelButton.Clicked += () => { inputValid = false; Application.RequestStop(dialog); };
            
            dialog.AddButton(processButton);
            dialog.AddButton(cancelButton);
            filePathField.SetFocus();
            Application.Run(dialog);

            if (!inputValid) return;

            ProcessCsvCartNumbers(cartNumbers);
        }

        /// <summary>
        /// Processes a batch of items by range
        /// </summary>
        private void ProcessBatchRange(int startItem, int endItem)
        {
            _batchProcessingService.ClearBatch();

            var progressDialog = new Dialog("Batch Processing...", 50, 7);
            var progressLabel = new Label($"Processing items {startItem} to {endItem}...") { X = 1, Y = 1, Width = Dim.Fill() - 2 };
            var currentItemLabel = new Label("") { X = 1, Y = 2, Width = Dim.Fill() - 2 };
            progressDialog.Add(progressLabel, currentItemLabel);
            var batchProgressToken = Application.Begin(progressDialog);

            var result = _batchProcessingService.ProcessItemRange(startItem, endItem, 
                (current, total, status) => {
                    currentItemLabel.Text = status;
                    Application.Refresh();
                    // Add items to recent items as they're processed
                    _recentItemsManager.AddToRecentItems(current);
                });

            Application.End(batchProgressToken);
            ShowBatchResults(result);
            ShowBatchEditTable();
        }

        /// <summary>
        /// Processes cart numbers from CSV
        /// </summary>
        private void ProcessCsvCartNumbers(List<int> cartNumbers)
        {
            _batchProcessingService.ClearBatch();

            var progressDialog = new Dialog("CSV Batch Processing...", 50, 7);
            var progressLabel = new Label($"Processing {cartNumbers.Count} items from CSV...") { X = 1, Y = 1, Width = Dim.Fill() - 2 };
            var currentItemLabel = new Label("") { X = 1, Y = 2, Width = Dim.Fill() - 2 };
            progressDialog.Add(progressLabel, currentItemLabel);
            var batchProgressToken = Application.Begin(progressDialog);

            var result = _batchProcessingService.ProcessCartNumbers(cartNumbers, 
                (current, total, status) => {
                    currentItemLabel.Text = status;
                    Application.Refresh();
                    // Add items to recent items as they're processed
                    if (current <= cartNumbers.Count)
                    {
                        _recentItemsManager.AddToRecentItems(cartNumbers[current - 1]);
                    }
                });

            Application.End(batchProgressToken);
            ShowBatchResults(result);
            ShowBatchEditTable();
        }

        /// <summary>
        /// Shows batch processing results
        /// </summary>
        private void ShowBatchResults(BatchProcessingResult result)
        {
            var message = TextUtils.CreateBatchSummary(result.SuccessCount, result.ErrorCount, 
                result.NeedsReviewCount, result.NoMatchCount);
            MessageBox.Query("Batch Processing Results", message, "Ok");
        }

        /// <summary>
        /// Shows the batch edit table
        /// </summary>
        private void ShowBatchEditTable()
        {
            var batchEditController = new BatchEditController(_batchProcessingService);
            batchEditController.ShowBatchEditTable();
        }
    }
}
