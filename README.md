# Myriad Music Tagger

A tool for automatically identifying music tracks and updates their metadata using acoustic fingerprinting technology.

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

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- [AcoustID](https://acoustid.org/)
- [MusicBrainz](https://musicbrainz.org/)
- [Spectre.Console](https://spectreconsole.net/) 
- [Myriad](https://www.broadcastradio.com/)
