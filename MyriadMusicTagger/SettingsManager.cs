using Newtonsoft.Json;
using Spectre.Console;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MyriadMusicTagger
{
    public static class SettingsManager
    {
        private const string SettingsFileName = "settings.json";
        private static readonly Regex UrlPattern = new(@"^https?:\/\/.+", RegexOptions.Compiled);

        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFileName))
                {
                    var json = File.ReadAllText(SettingsFileName);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && ValidateSettings(settings))
                    {
                        return settings;
                    }
                    
                    if (settings != null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Some settings appear to be invalid or missing. Let's review them.[/]");
                        return UpdateSettings(settings);
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading settings: {ex.Message}[/]");
            }
            
            return CreateNewSettings();
        }

        private static bool ValidateSettings(AppSettings settings)
        {
            return !string.IsNullOrWhiteSpace(settings.AcoustIDClientKey) &&
                   !string.IsNullOrWhiteSpace(settings.PlayoutWriteKey) &&
                   !string.IsNullOrWhiteSpace(settings.PlayoutReadKey) &&
                   settings.DelayBetweenRequests >= 0 &&
                   UrlPattern.IsMatch(settings.PlayoutApiUrl);
        }

        private static AppSettings CreateNewSettings()
        {
            var rule = new Rule("[yellow]Settings Configuration[/]");
            rule.Style = Style.Parse("yellow");
            rule.Title = "Settings Configuration";
            AnsiConsole.Write(rule);
            AnsiConsole.WriteLine();
            
            var settings = new AppSettings();
            UpdateSettings(settings);
            return settings;
        }

        private static AppSettings UpdateSettings(AppSettings settings)
        {
            var settingsTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .AddColumn(new TableColumn("Setting").LeftAligned())
                .AddColumn(new TableColumn("Current Value").LeftAligned());

            // Helper function to display current value
            string GetDisplayValue(string value) => string.IsNullOrEmpty(value) ? "[grey]<not set>[/]" : value;

            settingsTable.AddRow("AcoustID Client Key", GetDisplayValue(settings.AcoustIDClientKey));
            settingsTable.AddRow("Playout Write Key", GetDisplayValue(settings.PlayoutWriteKey));
            settingsTable.AddRow("Playout Read Key", GetDisplayValue(settings.PlayoutReadKey));
            settingsTable.AddRow("Delay Between Requests", settings.DelayBetweenRequests.ToString());
            settingsTable.AddRow("Playout API URL", GetDisplayValue(settings.PlayoutApiUrl));

            AnsiConsole.Write(settingsTable);
            AnsiConsole.WriteLine();

            if (!string.IsNullOrWhiteSpace(settings.AcoustIDClientKey))
            {
                if (!AnsiConsole.Confirm("Would you like to update any settings?"))
                {
                    return settings;
                }
            }

            settings.AcoustIDClientKey = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]AcoustID Client Key:[/]")
                    .PromptStyle("green")
                    .DefaultValue(settings.AcoustIDClientKey)
                    .Validate(key =>
                    {
                        return !string.IsNullOrWhiteSpace(key) 
                            ? ValidationResult.Success() 
                            : ValidationResult.Error("Client key cannot be empty");
                    }));

            settings.PlayoutWriteKey = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Playout Write Key:[/]")
                    .PromptStyle("green")
                    .DefaultValue(settings.PlayoutWriteKey)
                    .Validate(key =>
                    {
                        return !string.IsNullOrWhiteSpace(key) 
                            ? ValidationResult.Success() 
                            : ValidationResult.Error("Write key cannot be empty");
                    }));

            settings.PlayoutReadKey = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Playout Read Key:[/]")
                    .PromptStyle("green")
                    .DefaultValue(settings.PlayoutReadKey)
                    .Validate(key =>
                    {
                        return !string.IsNullOrWhiteSpace(key) 
                            ? ValidationResult.Success() 
                            : ValidationResult.Error("Read key cannot be empty");
                    }));

            settings.DelayBetweenRequests = AnsiConsole.Prompt(
                new TextPrompt<double>("[yellow]Delay Between Requests (seconds):[/]")
                    .PromptStyle("green")
                    .DefaultValue(settings.DelayBetweenRequests)
                    .Validate(delay =>
                    {
                        return delay >= 0 
                            ? ValidationResult.Success() 
                            : ValidationResult.Error("Delay must be non-negative");
                    }));

            settings.PlayoutApiUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Playout API URL:[/]")
                    .PromptStyle("green")
                    .DefaultValue(settings.PlayoutApiUrl ?? "http://localhost:9180/BrMyriadPlayout/v6")
                    .Validate(url =>
                    {
                        if (string.IsNullOrWhiteSpace(url))
                            return ValidationResult.Error("URL cannot be empty");
                        if (!UrlPattern.IsMatch(url))
                            return ValidationResult.Error("Invalid URL format");
                        return ValidationResult.Success();
                    }));

            try
            {
                SaveSettings(settings);
                AnsiConsole.MarkupLine("[green]Settings saved successfully![/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error saving settings: {ex.Message}[/]");
                AnsiConsole.MarkupLine("[yellow]You can continue with the current settings, but they won't be saved for next time.[/]");
            }

            return settings;
        }

        private static void SaveSettings(AppSettings settings)
        {
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(SettingsFileName, json);
        }
    }
}