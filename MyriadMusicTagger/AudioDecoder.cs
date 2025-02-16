using AcoustID.Audio;
using MyriadMusicTagger;

namespace Fingerprinter.Audio;

/// <summary>
///     Abstract base class for audio decoders
/// </summary>
public abstract class AudioDecoder : IAudioDecoder
{
    protected static readonly int BUFFER_SIZE = 2 * 192000;
    protected int channels;
    protected int duration;

    protected bool ready;

    protected int sampleRate;
    protected int sourceBitDepth;
    protected int sourceChannels;

    protected int sourceSampleRate;

    public int SourceSampleRate => sourceSampleRate;

    public int SourceBitDepth => sourceBitDepth;

    public int SourceChannels => sourceChannels;

    public int Duration => duration;

    public bool Ready => ready;

    public int SampleRate => sampleRate;

    public int Channels => channels;

    public abstract bool Decode(IAudioConsumer consumer, int maxLength);

    public virtual void Dispose()
    {
    }

    public abstract void Load(string file);
}