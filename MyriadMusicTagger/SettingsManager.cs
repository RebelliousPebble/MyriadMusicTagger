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
                MessageBox.Query("Settings Review", "Application settings need to be reviewed or configured.", "Ok");
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
            var dialog = new Dialog("Application Settings", 70, 24); // Width, Height - increased height for RES fields
             // Deep copy for cancel functionality
            var originalSettingsJson = JsonConvert.SerializeObject(currentSettings);

            var acoustIdKeyLabel = new Label("AcoustID Client Key:") { X = 1, Y = 1 };
            var acoustIdKeyField = new TextField(currentSettings.AcoustIDClientKey ?? string.Empty) { X = Pos.Right(acoustIdKeyLabel) + 12, Y = 1, Width = Dim.Fill(5) };

            var playoutWriteKeyLabel = new Label("Playout Write Key (optional):") { X = 1, Y = Pos.Bottom(acoustIdKeyLabel) };
            var playoutWriteKeyField = new TextField(currentSettings.PlayoutWriteKey ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(playoutWriteKeyLabel), Width = Dim.Fill(5) };

            var playoutReadKeyLabel = new Label("Playout Read Key:") { X = 1, Y = Pos.Bottom(playoutWriteKeyLabel) };
            var playoutReadKeyField = new TextField(currentSettings.PlayoutReadKey ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(playoutReadKeyLabel), Width = Dim.Fill(5) };

            var resWriteKeyLabel = new Label("RES Write Key (optional):") { X = 1, Y = Pos.Bottom(playoutReadKeyLabel) };
            var resWriteKeyField = new TextField(currentSettings.RESWriteKey ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(resWriteKeyLabel), Width = Dim.Fill(5) };

            var resReadKeyLabel = new Label("RES Read Key (optional):") { X = 1, Y = Pos.Bottom(resWriteKeyLabel) };
            var resReadKeyField = new TextField(currentSettings.RESReadKey ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(resReadKeyLabel), Width = Dim.Fill(5) };

            var delayLabel = new Label("Delay MusicBrainz (s):") { X = 1, Y = Pos.Bottom(resReadKeyLabel) };
            var delayField = new TextField(currentSettings.DelayBetweenRequests.ToString("0.0##")) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(delayLabel), Width = Dim.Fill(5) }; // double to string is fine

            var apiUrlLabel = new Label("Playout API URL:") { X = 1, Y = Pos.Bottom(delayLabel) };
            var apiUrlField = new TextField(currentSettings.PlayoutApiUrl ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(apiUrlLabel), Width = Dim.Fill(5) };

            var resApiUrlLabel = new Label("RES API URL (optional):") { X = 1, Y = Pos.Bottom(apiUrlLabel) };
            var resApiUrlField = new TextField(currentSettings.RESApiUrl ?? string.Empty) { X = Pos.Left(acoustIdKeyField), Y = Pos.Top(resApiUrlLabel), Width = Dim.Fill(5) };

            var errorLabel = new Label("") { X = 1, Y = Pos.Bottom(resApiUrlLabel) + 1, Width = Dim.Fill(2), Height = 3 /* Label is multiline by default if text has \n and height allows */ };
            // Set color scheme for error label
            var errorColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Normal.Background ?? Color.Black), // Use dialog's background color
                Focus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.Focus.Background ?? Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotNormal.Background ?? Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Red, dialog.ColorScheme?.HotFocus.Background ?? Color.Black)
            };
            errorLabel.ColorScheme = errorColorScheme;


            dialog.Add(acoustIdKeyLabel, acoustIdKeyField,
                       playoutWriteKeyLabel, playoutWriteKeyField,
                       playoutReadKeyLabel, playoutReadKeyField,
                       resWriteKeyLabel, resWriteKeyField,
                       resReadKeyLabel, resReadKeyField,
                       delayLabel, delayField,
                       apiUrlLabel, apiUrlField,
                       resApiUrlLabel, resApiUrlField,
                       errorLabel);

            bool settingsSaved = false;
            var saveButton = new Button("Save") { X = Pos.Center() - 8, Y = Pos.Bottom(dialog) - 3, IsDefault = true};
            saveButton.Clicked += () => {
                var tempSettings = new AppSettings
                {
                    AcoustIDClientKey = acoustIdKeyField.Text?.ToString() ?? string.Empty,
                    PlayoutWriteKey = playoutWriteKeyField.Text?.ToString() ?? string.Empty, // Optional, can be empty
                    PlayoutReadKey = playoutReadKeyField.Text?.ToString() ?? string.Empty,
                    RESWriteKey = resWriteKeyField.Text?.ToString() ?? string.Empty, // Optional, can be empty
                    RESReadKey = resReadKeyField.Text?.ToString() ?? string.Empty, // Optional, can be empty
                    PlayoutApiUrl = apiUrlField.Text?.ToString() ?? string.Empty,
                    RESApiUrl = resApiUrlField.Text?.ToString() ?? string.Empty, // Optional, can be empty
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

                if (!string.IsNullOrWhiteSpace(tempSettings.RESApiUrl) && !UrlPattern.IsMatch(tempSettings.RESApiUrl))
                {
                    validationErrors.AppendLine("- RES API URL must be a valid HTTP/HTTPS URL if provided.");
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
                currentSettings.RESWriteKey = tempSettings.RESWriteKey;
                currentSettings.RESReadKey = tempSettings.RESReadKey;
                currentSettings.DelayBetweenRequests = tempSettings.DelayBetweenRequests;
                currentSettings.PlayoutApiUrl = tempSettings.PlayoutApiUrl;
                currentSettings.RESApiUrl = tempSettings.RESApiUrl;

                SaveSettingsToFile(currentSettings);
                settingsSaved = true;
                Application.RequestStop(dialog);
                MessageBox.Query("Settings Saved", "Settings have been saved successfully.", "Ok");
            };

            var cancelButton = new Button("Cancel") { X = Pos.Right(saveButton) + 1, Y = saveButton.Y };
            cancelButton.Clicked += () => {
                Application.RequestStop(dialog);
            };

            dialog.AddButton(saveButton);
            dialog.AddButton(cancelButton);

            acoustIdKeyField.SetFocus();
            Application.Run(dialog);

            if (settingsSaved)
            {
                return currentSettings; // Return the modified and saved settings
            }
            else
            {
                // If cancelled, or save was not successful, revert to original settings
                // The 'currentSettings' object passed to ShowSettingsDialog might have been modified
                // by data binding or direct field updates before validation failed on an attempted save.
                // So, always deserialize from the original snapshot on cancel/no save.
                var revertedSettings = JsonConvert.DeserializeObject<AppSettings>(originalSettingsJson);
                // If originalSettingsJson was somehow corrupted/null string, DeserializeObject could return null.
                // In this highly unlikely edge case, returning the 'currentSettings' as they were at the start
                // of the method is better than returning a fresh 'new AppSettings()', as 'currentSettings'
                // at least reflects a valid state the app was in.
                return revertedSettings ?? currentSettings;
            }
        }

        /// <summary>
        /// Loads current settings and then explicitly shows the settings dialog for review/edit.
        /// Saves settings if the user confirms in the dialog.
        /// </summary>
        public static void ReviewSettingsGui()
        {
            AppSettings currentSettings = LoadSettings(); // Load potentially existing or default settings
            // ShowSettingsDialog will handle saving if changes are made and confirmed by the user.
            // The returned AppSettings object from ShowSettingsDialog isn't strictly needed here
            // as LoadSettings() on next app start will pick up any saved changes.
            // However, if settings are modified, the current runtime instance of 'settings' in Program.cs
            // won't be updated by this call directly. This might be acceptable if settings are mostly
            // read at startup or before long operations. For immediate effect, Program.cs would need to
            // re-assign its 'settings' variable. For now, this just ensures settings can be reviewed and saved.
            ShowSettingsDialog(currentSettings);
        }

        /// <summary>
        /// Shows the settings dialog and returns the updated settings if they were saved.
        /// Returns null if the dialog was cancelled.
        /// </summary>
        public static AppSettings? ShowSettingsDialogAndReturn(AppSettings currentSettings)
        {
            // Deep copy for cancel functionality
            var originalSettingsJson = JsonConvert.SerializeObject(currentSettings);
            var settingsBeforeDialog = JsonConvert.DeserializeObject<AppSettings>(originalSettingsJson) ?? currentSettings;
            
            var updatedSettings = ShowSettingsDialog(currentSettings);
            
            // Compare if settings actually changed by comparing JSON representations
            var updatedJson = JsonConvert.SerializeObject(updatedSettings);
            var originalJson = JsonConvert.SerializeObject(settingsBeforeDialog);
            
            if (updatedJson != originalJson)
            {
                return updatedSettings; // Settings were changed and saved
            }
            
            return null; // No changes made or dialog was cancelled
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