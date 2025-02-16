using AcoustID.Audio;
using Fingerprinter.Audio;
using NAudio.Wave;

namespace MyriadMusicTagger;

/// <summary>
///     Decode using the NAudio library. Great audio library, but the MP3 decoder is kinda slow.
/// </summary>
public class NAudioDecoder : AudioDecoder
{
    private string extension;
    private WaveStream reader;


    public override void Load(string file)
    {
    }

    public void Load(MemoryStream file)
    {
        // Dispose on every new load
        Dispose(false);

        reader = new WaveFileReader(file);

        var format = reader.WaveFormat;

        sampleRate = format.SampleRate;
        channels = format.Channels;

        sourceSampleRate = format.SampleRate;
        sourceBitDepth = format.BitsPerSample;
        sourceChannels = format.Channels;
        duration = (int)reader.TotalTime.TotalSeconds;
        ready = format.BitsPerSample == 16;
    }

    public override bool Decode(IAudioConsumer consumer, int maxLength)
    {
        if (!ready) return false;

        int remaining, length, size;
        var buffer = new byte[2 * BUFFER_SIZE];
        var data = new short[BUFFER_SIZE];

        // Samples to read to get maxLength seconds of audio
        remaining = maxLength * sourceChannels * sampleRate;

        // Bytes to read
        length = 2 * Math.Min(remaining, BUFFER_SIZE);

        while ((size = reader.Read(buffer, 0, length)) > 0)
        {
            Buffer.BlockCopy(buffer, 0, data, 0, size);

            consumer.Consume(data, size / 2);

            remaining -= size / 2;
            if (remaining <= 0) break;

            length = 2 * Math.Min(remaining, BUFFER_SIZE);
        }

        return true;
    }

    #region IDisposable implementation

    private bool hasDisposed;

    public override void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Dispose(bool disposing)
    {
        if (!hasDisposed)
            if (reader != null)
            {
                reader.Close();
                reader.Dispose();
            }

        hasDisposed = disposing;
    }

    ~NAudioDecoder()
    {
        Dispose(true);
    }

    #endregion
}