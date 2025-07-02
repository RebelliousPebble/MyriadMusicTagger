# Myriad Music Tagger

A tool for automatically identifying music tracks and updates their metadata using acoustic fingerprinting technology.

## Features

- **Single Item Processing**: Process individual cart numbers one at a time
- **Batch Range Processing**: Process a sequential range of cart numbers  
- **CSV File Processing**: Process cart numbers from a comma-separated values file
- **Recent Items**: Quick access to recently processed items
- **Interactive Table Editor**: Review and edit batch results before saving
- **Acoustic Fingerprinting**: Uses AcoustID/MusicBrainz for accurate music identification
- **Confidence Scoring**: Automatic matching with configurable confidence thresholds

## Processing Options

### Single Item
Process an individual cart number by entering it manually.

### Batch of Items  
Process a sequential range of cart numbers (e.g., items 1000-1050).

### CSV File
Process cart numbers from a CSV file. The file can contain:
- Comma-separated values: `12345,12346,12347`
- Semicolon-separated values: `12345;12346;12347`
- One number per line
- Mix of formats

Example CSV file content:
```
12345,12346,12347
12348
12349;12350
12351,12352,12353
```

### Recent Items
Quickly reprocess recently accessed cart numbers.

## Prerequisites

- .NET 9.0 or higher
- Windows operating system
- Access to a Myriad Playout v6 system (Tested with 6.2+, must be run on the same system as Playout)
- AcoustID API key (get one from [acoustid.org](https://acoustid.org/))

## Installation

1. Clone the repository:
```bash
git clone https://github.com/RebelliousPebble/MyriadMusicTagger.git
```

2. Navigate to the project directory:
```bash
cd MyriadMusicTagger
```

3. Build the project:
```bash
dotnet build
```

4. Run the application:
```bash
dotnet run
```

## Configuration

On first run, you'll be prompted to enter the following settings:

- **AcoustID Client Key**: Your AcoustID API key
- **Playout Write Key**: API key for writing to Myriad Playout
- **Playout Read Key**: API key for reading from Myriad Playout
- **Delay Between Requests**: Time to wait between API requests (in seconds)
- **Playout API URL**: URL of your Myriad Playout v6 API

These settings are saved in `settings.json` and can be modified later if needed.

## Usage

1. **Launch the application** - The main menu will appear with options for File, Process, and Help
2. **Choose processing method**:
   - Press `Ctrl+S` for Single Item
   - Press `Ctrl+B` for Batch of Items  
   - Press `Ctrl+C` for CSV File
   - Press `Ctrl+R` for Recent Items
3. **Review results** - For batch and CSV processing, use the interactive table to review matches
4. **Save changes** - Select items to save and confirm the updates to your Myriad system

### Keyboard Shortcuts
- `Ctrl+Q`: Quit application
- `Ctrl+S`: Process Single Item
- `Ctrl+B`: Process Batch of Items
- `Ctrl+C`: Process CSV File
- `Ctrl+R`: Process Recent Items
- `F1`: View Help
- `Space`: Toggle selection in batch table
- `Enter`: Edit item in batch table
- `Tab`: Navigate between interface elements
- `Esc`: Return focus to table from buttons

### Batch Table Filters
When reviewing batch results, use the filter buttons to focus on specific items:
- **All**: Show all processed items
- **Unselected**: Show only items that are not selected for saving
- **Selected**: Show only items that are selected for saving  
- **Errors**: Show only items that had processing errors

This is especially useful for large batches where you want to focus on items that need attention.

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- [AcoustID](https://acoustid.org/)
- [MusicBrainz](https://musicbrainz.org/)
- [Spectre.Console](https://spectreconsole.net/) 
- [Myriad](https://www.broadcastradio.com/)
