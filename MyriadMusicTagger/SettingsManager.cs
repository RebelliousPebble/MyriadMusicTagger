using Newtonsoft.Json;
using Serilog; // Using Serilog
using Terminal.Gui; // Using Terminal.Gui
using System;
using System.IO;
using System.Text; // For StringBuilder
using System.Text.RegularExpressions;


namespace MyriadMusicTagger
{
    public static class SettingsManager
    {
        private const string SettingsFileName = "settings.json"; // Changed from appsettings.json to settings.json to match original
        private static readonly Regex UrlPattern = new(@"^https?:\/\/.+", RegexOptions.Compiled);

        public static AppSettings LoadSettings()
        {
            AppSettings? settings = null;
            bool settingsInvalidOrMissing = false;

            if (File.Exists(SettingsFileName))
            {
                try
                {
                    var json = File.ReadAllText(SettingsFileName);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings == null || !ValidateSettings(settings))
                    {
                        Log.Warning("Settings file {FileName} loaded but contains invalid or missing settings.", SettingsFileName);
                        settingsInvalidOrMissing = true;
                        settings ??= new AppSettings(); // Ensure settings object exists if null after deserialization
                    }
                    else
                    {
                        Log.Information("Settings loaded successfully from {FileName}.", SettingsFileName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error loading settings from {FileName}. Default settings will be used and reviewed.", SettingsFileName);
                    settingsInvalidOrMissing = true;
                    settings = new AppSettings();
                }
            }
            else
            {
                Log.Information("Settings file {FileName} not found. Default settings will be used and reviewed.", SettingsFileName);
                settingsInvalidOrMissing = true;
                settings = new AppSettings();
            }
            
            if (settingsInvalidOrMissing)
            {
                MessageBox.InfoQuery("Settings Review", "Application settings need to be reviewed or configured.", "Ok");
                settings = ShowSettingsDialog(settings); // ShowSettingsDialog will handle saving if user confirms
            }
            return settings;
        }

        private static bool ValidateSettings(AppSettings settings)
        {
            // Validate all required fields
            return !string.IsNullOrWhiteSpace(settings.AcoustIDClientKey) &&
                   !string.IsNullOrWhiteSpace(settings.PlayoutReadKey) && // WriteKey is optional, ReadKey is not
                   settings.DelayBetweenRequests >= 0.2 && // MusicBrainz asks for at least 1 req/sec, so 1000ms. Let's be safe with 200ms as a bare minimum.
                   !string.IsNullOrWhiteSpace(settings.PlayoutApiUrl) && UrlPattern.IsMatch(settings.PlayoutApiUrl);
        }

        private static AppSettings ShowSettingsDialog(AppSettings currentSettings)
        {
            var dialog = new Dialog("Application Settings", 70, 18); // Width, Height
             // Deep copy for cancel functionality
            var originalSettingsJson = JsonConvert.SerializeObject(currentSettings);

            var acoustIdKeyLabel = new Label("AcoustID Client Key:") { X = 1, Y = 1 };
            var acoustIdKeyField = new TextField(currentSettings.AcoustIDClientKey) { X = Pos.Right(acoustIdKeyLabel) + 12, Y = 1, Width = Dim.Fill(5) };

            var playoutWriteKeyLabel = new Label("Playout Write Key (optional):") { X = 1, Y = Pos.Bottom(acoustIdKeyLabel) };
            var playoutWriteKeyField = new TextField(currentSettings.PlayoutWriteKey) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(playoutWriteKeyLabel), Width = Dim.Fill(5) };
            
            var playoutReadKeyLabel = new Label("Playout Read Key:") { X = 1, Y = Pos.Bottom(playoutWriteKeyLabel) };
            var playoutReadKeyField = new TextField(currentSettings.PlayoutReadKey) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(playoutReadKeyLabel), Width = Dim.Fill(5) };

            var delayLabel = new Label("Delay MusicBrainz (s):") { X = 1, Y = Pos.Bottom(playoutReadKeyLabel) };
            var delayField = new TextField(currentSettings.DelayBetweenRequests.ToString("0.0##")) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(delayLabel), Width = Dim.Fill(5) };

            var apiUrlLabel = new Label("Playout API URL:") { X = 1, Y = Pos.Bottom(delayLabel) };
            var apiUrlField = new TextField(currentSettings.PlayoutApiUrl) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(apiUrlLabel), Width = Dim.Fill(5) };

            var errorLabel = new Label("") { X = 1, Y = Pos.Bottom(apiUrlLabel) + 1, Width = Dim.Fill(2), Height = 3, TextColor = Application.Driver.MakeAttribute(Color.Red, Color.Black), Multiline = true };

            dialog.Add(acoustIdKeyLabel, acoustIdKeyField,
                       playoutWriteKeyLabel, playoutWriteKeyField,
                       playoutReadKeyLabel, playoutReadKeyField,
                       delayLabel, delayField,
                       apiUrlLabel, apiUrlField,
                       errorLabel);

            bool settingsSaved = false;
            var saveButton = new Button("Save") { X = Pos.Center() - 8, Y = Pos.Bottom(dialog) - 3, IsDefault = true};
            saveButton.Clicked += () => {
                var tempSettings = new AppSettings
                {
                    AcoustIDClientKey = acoustIdKeyField.Text.ToString(),
                    PlayoutWriteKey = playoutWriteKeyField.Text.ToString(), // Optional, can be empty
                    PlayoutReadKey = playoutReadKeyField.Text.ToString(),
                    PlayoutApiUrl = apiUrlField.Text.ToString(),
                };

                var validationErrors = new StringBuilder();
                if (string.IsNullOrWhiteSpace(tempSettings.AcoustIDClientKey)) validationErrors.AppendLine("- AcoustID Client Key is required.");
                if (string.IsNullOrWhiteSpace(tempSettings.PlayoutReadKey)) validationErrors.AppendLine("- Playout Read Key is required.");

                if (!double.TryParse(delayField.Text.ToString(), out double delayVal) || delayVal < 0.2)
                {
                    validationErrors.AppendLine("- Delay must be a number >= 0.2 seconds.");
                }
                else
                {
                    tempSettings.DelayBetweenRequests = delayVal;
                }

                if (string.IsNullOrWhiteSpace(tempSettings.PlayoutApiUrl) || !UrlPattern.IsMatch(tempSettings.PlayoutApiUrl))
                {
                    validationErrors.AppendLine("- Playout API URL is required and must be a valid HTTP/HTTPS URL.");
                }

                if (validationErrors.Length > 0)
                {
                    errorLabel.Text = validationErrors.ToString();
                    return;
                }

                // Update the reference to currentSettings that was passed in.
                currentSettings.AcoustIDClientKey = tempSettings.AcoustIDClientKey;
                currentSettings.PlayoutWriteKey = tempSettings.PlayoutWriteKey;
                currentSettings.PlayoutReadKey = tempSettings.PlayoutReadKey;
                currentSettings.DelayBetweenRequests = tempSettings.DelayBetweenRequests;
                currentSettings.PlayoutApiUrl = tempSettings.PlayoutApiUrl;

                SaveSettingsToFile(currentSettings);
                settingsSaved = true;
                Application.RequestStop(dialog);
                MessageBox.InfoQuery("Settings Saved", "Settings have been saved successfully.", "Ok");
            };

            var cancelButton = new Button("Cancel") { X = Pos.Right(saveButton) + 1, Y = saveButton.Y };
            cancelButton.Clicked += () => {
                Application.RequestStop(dialog);
            };

            dialog.AddButton(saveButton);
            dialog.AddButton(cancelButton);

            acoustIdKeyField.SetFocus();
            Application.Run(dialog);

            // If cancelled, revert to original settings before dialog
            return settingsSaved ? currentSettings : JsonConvert.DeserializeObject<AppSettings>(originalSettingsJson);
        }

        // Renamed to SaveSettingsToFile to make its purpose clear (private persistence)
        private static void SaveSettingsToFile(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFileName, json);
                Log.Information("Settings saved successfully to {FileName}", SettingsFileName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving settings to {FileName}", SettingsFileName);
                // This error should ideally be shown to the user if ShowSettingsDialog was the caller.
                // ShowSettingsDialog handles this with a MessageBox.
                throw; // Re-throw to allow caller (ShowSettingsDialog) to handle UI feedback
            }
        }
    }
}