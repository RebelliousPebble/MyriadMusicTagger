using AcoustID.Audio;

namespace MyriadMusicTagger;

/// <summary>
///     Interface for audio decoders.
/// </summary>
public interface IAudioDecoder : IDecoder, IDisposable
{
    int SourceSampleRate { get; }
    int SourceBitDepth { get; }
    int SourceChannels { get; }

    int Duration { get; }
    bool Ready { get; }
}