using ManagedBass;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using MyriadMusicTagger.Core;
using Serilog;
using System.Numerics;

namespace MyriadMusicTagger.Utils
{
    /// <summary>
    /// Low-level audio quality analysis processor using ManagedBass and Math.NET Numerics
    /// Implements quality scoring algorithms for spectral analysis, dynamic range, clipping detection,
    /// noise floor analysis, and channel quality assessment
    /// </summary>
    public class AudioQualityProcessor : IDisposable
    {
        private readonly QualityAnalysisSettings _settings;
        private static bool _bassInitialized = false;
        private static readonly object _bassLock = new object();

        public AudioQualityProcessor(QualityAnalysisSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            InitializeBass();
        }

        /// <summary>
        /// Analyzes the audio quality of a WAV file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>Complete audio quality analysis result</returns>
        public async Task<AudioQualityResult> AnalyzeAudioQualityAsync(string filePath, int mediaId, string title, string artist)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new AudioQualityResult
            {
                MediaId = mediaId,
                Title = title,
                Artist = artist,
                FilePath = filePath
            };

            try
            {
                // Check memory before starting
                var initialMemory = GC.GetTotalMemory(false);
                Log.Debug("Starting audio quality analysis for {FilePath}, initial memory: {Memory:N0} bytes", filePath, initialMemory);

                // Check if file exists and is accessible
                if (!File.Exists(filePath))
                {
                    result.ErrorMessage = "File not found";
                    return result;
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > _settings.MaxFileSizeMB * 1024 * 1024 && _settings.SkipLargeFiles)
                {
                    result.ErrorMessage = $"File too large ({fileInfo.Length / (1024 * 1024)} MB)";
                    return result;
                }

                // Load audio data
                var audioData = await LoadAudioDataAsync(filePath);
                if (audioData == null)
                {
                    result.ErrorMessage = "Failed to load audio data";
                    return result;
                }

                Log.Debug("Loaded audio data: {Channels} channels, {SampleRate} Hz, {Samples} samples", 
                    audioData.Channels, audioData.SampleRate, audioData.LeftChannel.Length);

                // Calculate duration from audio data
                var durationSeconds = (double)audioData.LengthSamples / audioData.SampleRate;
                var duration = TimeSpan.FromSeconds(durationSeconds);
                result.Duration = duration.ToString(@"mm\:ss\.fff");

                Log.Debug("Calculated duration: {Duration} ({TotalSeconds:F2} seconds)", result.Duration, durationSeconds);

                // Perform all analyses
                result.SpectralAnalysis = await AnalyzeSpectralQualityAsync(audioData);
                result.DynamicRange = AnalyzeDynamicRange(audioData);
                result.ClippingAnalysis = AnalyzeClipping(audioData);
                result.NoiseFloor = AnalyzeNoiseFloor(audioData);
                result.ChannelQuality = AnalyzeChannelQuality(audioData);

                // Calculate overall quality score
                result.OverallQualityScore = CalculateOverallScore(result);

                // Generate quality issues and notes
                GenerateQualityAssessment(result);

                result.ProcessingSuccessful = true;
                Log.Debug("Audio quality analysis completed for {FilePath}, score: {Score:F1}", filePath, result.OverallQualityScore);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error analyzing audio quality for {FilePath}", filePath);
                result.ErrorMessage = ex.Message;
                result.ProcessingSuccessful = false;
            }
            finally
            {
                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;
                
                // Log final memory usage
                var finalMemory = GC.GetTotalMemory(false);
                Log.Debug("Completed audio analysis for {FilePath}, final memory: {Memory:N0} bytes", filePath, finalMemory);
            }

            return result;
        }

        /// <summary>
        /// Loads audio data from a file using ManagedBass
        /// </summary>
        private async Task<AudioData?> LoadAudioDataAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Create a stream from the file
                    var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Mono);
                    if (stream == 0)
                    {
                        Log.Warning("Failed to create Bass stream for {FilePath}: {Error}", filePath, Bass.LastError);
                        return null;
                    }

                    try
                    {
                        // Get channel info
                        var info = Bass.ChannelGetInfo(stream);
                        if (info.Channels == 0)
                        {
                            Log.Warning("Invalid channel info for {FilePath}", filePath);
                            return null;
                        }

                        // Get length in samples
                        var lengthBytes = Bass.ChannelGetLength(stream);
                        var lengthSamples = (int)(lengthBytes / (sizeof(float) * info.Channels));

                        Log.Debug("Audio file info: {Channels} channels, {SampleRate} Hz, {Samples} samples", 
                            info.Channels, info.Frequency, lengthSamples);

                        // Read audio data in chunks to avoid memory issues
                        const int maxSamplesPerChunk = 44100 * 30; // 30 seconds at 44.1kHz
                        var totalSamples = lengthSamples;
                        
                        // Limit total samples for very long files to avoid memory issues
                        const int maxTotalSamples = 44100 * 300; // 5 minutes max for analysis
                        if (totalSamples > maxTotalSamples)
                        {
                            Log.Information("Large audio file detected ({TotalMinutes:F1} min), limiting analysis to first 5 minutes for memory efficiency", 
                                (double)totalSamples / info.Frequency / 60);
                            totalSamples = maxTotalSamples;
                        }
                        
                        var leftChannel = new List<float>();
                        var rightChannel = new List<float>();
                        
                        // Pre-allocate capacity to reduce memory allocations
                        leftChannel.Capacity = totalSamples;
                        rightChannel.Capacity = totalSamples;
                        
                        // Process audio in chunks
                        var remainingSamples = totalSamples;
                        var currentPosition = 0;
                        
                        try
                        {
                            while (remainingSamples > 0)
                            {
                                var samplesThisChunk = Math.Min(remainingSamples, maxSamplesPerChunk);
                                var bufferSize = samplesThisChunk * info.Channels;
                                var chunkBuffer = new float[bufferSize];
                                
                                // Set position for this chunk
                                Bass.ChannelSetPosition(stream, currentPosition * info.Channels * sizeof(float));
                                
                                var bytesRead = Bass.ChannelGetData(stream, chunkBuffer, bufferSize * sizeof(float));
                                if (bytesRead <= 0) 
                                {
                                    Log.Debug("Reached end of audio data at position {Position}", currentPosition);
                                    break;
                                }
                                
                                var actualSamplesThisChunk = bytesRead / (sizeof(float) * info.Channels);
                                
                                // Extract channels from interleaved data
                                for (int i = 0; i < actualSamplesThisChunk; i++)
                                {
                                    leftChannel.Add(chunkBuffer[i * info.Channels]);
                                    if (info.Channels > 1)
                                    {
                                        rightChannel.Add(chunkBuffer[i * info.Channels + 1]);
                                    }
                                    else
                                    {
                                        rightChannel.Add(chunkBuffer[i * info.Channels]); // Mono
                                    }
                                }
                                
                                currentPosition += actualSamplesThisChunk;
                                remainingSamples -= actualSamplesThisChunk;
                                
                                // Log progress for very large files
                                if (totalSamples > maxSamplesPerChunk * 2)
                                {
                                    var progress = (double)(totalSamples - remainingSamples) / totalSamples * 100;
                                    if (progress > 0 && (int)progress % 25 == 0)
                                    {
                                        Log.Debug("Audio loading progress: {Progress:F0}%", progress);
                                    }
                                }
                                
                                // For very large processing, suggest garbage collection periodically
                                if (leftChannel.Count > maxSamplesPerChunk * 2 && leftChannel.Count % maxSamplesPerChunk == 0)
                                {
                                    GC.Collect();
                                    GC.WaitForPendingFinalizers();
                                }
                            }
                        }
                        catch (OutOfMemoryException ex)
                        {
                            Log.Error(ex, "Out of memory while processing audio file {FilePath}. Consider reducing file size or increasing available memory.", filePath);
                            return null;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error while reading audio data in chunks from {FilePath}", filePath);
                            return null;
                        }

                        var actualTotalSamples = leftChannel.Count;
                        if (actualTotalSamples == 0)
                        {
                            Log.Warning("No audio samples read from {FilePath}", filePath);
                            return null;
                        }

                        Log.Debug("Successfully loaded {Samples} samples in chunks", actualTotalSamples);

                        return new AudioData
                        {
                            LeftChannel = leftChannel.ToArray(),
                            RightChannel = rightChannel.ToArray(),
                            SampleRate = info.Frequency,
                            Channels = info.Channels,
                            BitsPerSample = 32, // We're using float data
                            LengthSamples = actualTotalSamples
                        };
                    }
                    finally
                    {
                        Bass.StreamFree(stream);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception loading audio data from {FilePath}", filePath);
                    return null;
                }
            });
        }

        /// <summary>
        /// Performs spectral analysis using FFT (40% weight)
        /// </summary>
        private async Task<SpectralAnalysisResult> AnalyzeSpectralQualityAsync(AudioData audioData)
        {
            return await Task.Run(() =>
            {
                var result = new SpectralAnalysisResult();

                try
                {
                    // Use a 8192-sample FFT for good frequency resolution
                    int fftSize = 8192;
                    int hopSize = fftSize / 4;
                    var sampleRate = audioData.SampleRate;
                    
                    // Combine channels for analysis
                    var monoData = new float[audioData.LeftChannel.Length];
                    for (int i = 0; i < monoData.Length; i++)
                    {
                        monoData[i] = (audioData.LeftChannel[i] + audioData.RightChannel[i]) * 0.5f;
                    }

                    var spectrums = new List<double[]>();
                    var window = MathNet.Numerics.Window.Hann(fftSize);

                    // Process overlapping windows
                    for (int pos = 0; pos + fftSize < monoData.Length; pos += hopSize)
                    {
                        var fftData = new Complex[fftSize];
                        
                        // Apply window and copy data
                        for (int i = 0; i < fftSize; i++)
                        {
                            fftData[i] = new Complex(monoData[pos + i] * window[i], 0);
                        }

                        // Perform FFT
                        Fourier.Forward(fftData, FourierOptions.Matlab);

                        // Calculate magnitude spectrum (only need first half due to symmetry)
                        var spectrum = new double[fftSize / 2];
                        for (int i = 0; i < spectrum.Length; i++)
                        {
                            spectrum[i] = fftData[i].Magnitude;
                        }

                        spectrums.Add(spectrum);
                    }

                    if (spectrums.Count == 0)
                    {
                        result.Issues.Add("Unable to perform spectral analysis - file too short");
                        return result;
                    }

                    // Average all spectrums
                    var avgSpectrum = new double[fftSize / 2];
                    foreach (var spectrum in spectrums)
                    {
                        for (int i = 0; i < avgSpectrum.Length; i++)
                        {
                            avgSpectrum[i] += spectrum[i];
                        }
                    }

                    for (int i = 0; i < avgSpectrum.Length; i++)
                    {
                        avgSpectrum[i] /= spectrums.Count;
                    }

                    // Analyze frequency content
                    AnalyzeFrequencyContent(result, avgSpectrum, sampleRate);
                    DetectMp3Artifacts(result, avgSpectrum, sampleRate);
                    CalculateSpectralMetrics(result, avgSpectrum, sampleRate);

                    Log.Debug("Spectral analysis completed: rolloff={0:F0}Hz, centroid={1:F0}Hz, HF content={2:F1}%", 
                        result.FrequencyRolloffPoint, result.SpectralCentroid, result.HighFrequencyContent);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in spectral analysis");
                    result.Issues.Add($"Spectral analysis error: {ex.Message}");
                }

                return result;
            });
        }

        /// <summary>
        /// Analyzes frequency content and rolloff characteristics
        /// </summary>
        private void AnalyzeFrequencyContent(SpectralAnalysisResult result, double[] spectrum, int sampleRate)
        {
            var nyquist = sampleRate / 2.0;
            var freqPerBin = nyquist / spectrum.Length;

            // Find frequency rolloff point (where energy drops to -3dB of peak in high frequencies)
            var peakEnergy = spectrum.Skip(spectrum.Length / 4).Max(); // Start from 1/4 of spectrum
            var rolloffThreshold = peakEnergy * 0.707; // -3dB

            result.FrequencyRolloffPoint = (float)nyquist; // Default to Nyquist if no rolloff found
            for (int i = spectrum.Length - 1; i >= spectrum.Length / 4; i--)
            {
                if (spectrum[i] > rolloffThreshold)
                {
                    result.FrequencyRolloffPoint = (float)(i * freqPerBin);
                    break;
                }
            }

            // Calculate high frequency content (15kHz and above)
            int hfStartBin = (int)(15000 / freqPerBin);
            if (hfStartBin < spectrum.Length)
            {
                var totalEnergy = spectrum.Sum();
                var hfEnergy = spectrum.Skip(hfStartBin).Sum();
                result.HighFrequencyContent = totalEnergy > 0 ? (float)(hfEnergy / totalEnergy * 100) : 0;
            }

            // Score based on frequency content - adjusted for modern mastering practices
            // Many well-mastered tracks naturally roll off around 8-12kHz due to mastering choices
            float rolloffScore = Math.Min(100, Math.Max(0, (result.FrequencyRolloffPoint - 4000) / 100)); // Score starts at 4kHz, peaks at 14kHz
            float hfScore = Math.Min(100, result.HighFrequencyContent * 20); // Adjusted sensitivity for HF content
            result.SpectralScore = (rolloffScore + hfScore) / 2;
        }

        /// <summary>
        /// Detects artifacts typical of MP3 encoding
        /// </summary>
        private void DetectMp3Artifacts(SpectralAnalysisResult result, double[] spectrum, int sampleRate)
        {
            var freqPerBin = (sampleRate / 2.0) / spectrum.Length;

            // Look for typical MP3 artifacts
            // 1. Sharp cutoff around 16kHz (128kbps) or 20kHz (higher bitrates)
            var cutoff16k = (int)(16000 / freqPerBin);
            var cutoff20k = (int)(20000 / freqPerBin);

            if (cutoff16k < spectrum.Length && cutoff20k < spectrum.Length)
            {
                var energy16k = spectrum.Skip(cutoff16k).Take(100).Average();
                var energy14k = spectrum.Skip((int)(14000 / freqPerBin)).Take(100).Average();
                var energy20k = spectrum.Skip(cutoff20k).Take(50).Average();

                // Check for sharp cutoff characteristic of MP3
                if (energy14k > 0 && energy16k / energy14k < 0.1)
                {
                    result.HasMp3Artifacts = true;
                    result.Mp3ArtifactConfidence = 0.8f;
                    result.Issues.Add("Sharp frequency cutoff at ~16kHz suggests MP3 encoding");
                    result.SuspiciousFrequencies.Add(16000);
                }
                else if (energy14k > 0 && energy20k / energy14k < 0.05)
                {
                    result.HasMp3Artifacts = true;
                    result.Mp3ArtifactConfidence = 0.6f;
                    result.Issues.Add("High frequency rolloff suggests compressed source");
                }
            }

            // 2. Look for pre-echo artifacts (energy before transients)
            // This is more complex and would require temporal analysis

            // 3. Check for quantization noise patterns - only flag extreme cases
            // Look for elevated noise floor in specific frequency bands
            var lowFreqNoise = spectrum.Take(spectrum.Length / 8).Where(x => x > 0).Average();
            var midFreqNoise = spectrum.Skip(spectrum.Length / 4).Take(spectrum.Length / 4).Where(x => x > 0).Average();
            
            // Only flag very unusual noise distributions that strongly suggest compression artifacts
            if (midFreqNoise > 0 && lowFreqNoise / midFreqNoise > 10.0) // Very conservative threshold - only extreme cases
            {
                result.Issues.Add("Unusual noise distribution suggests digital compression artifacts");
                result.Mp3ArtifactConfidence = Math.Max(result.Mp3ArtifactConfidence, 0.4f);
            }
        }

        /// <summary>
        /// Calculates spectral metrics like centroid and bandwidth
        /// </summary>
        private void CalculateSpectralMetrics(SpectralAnalysisResult result, double[] spectrum, int sampleRate)
        {
            var freqPerBin = (sampleRate / 2.0) / spectrum.Length;
            var totalEnergy = spectrum.Sum();

            if (totalEnergy > 0)
            {
                // Spectral centroid (weighted average frequency)
                double weightedSum = 0;
                for (int i = 0; i < spectrum.Length; i++)
                {
                    weightedSum += i * freqPerBin * spectrum[i];
                }
                result.SpectralCentroid = (float)(weightedSum / totalEnergy);

                // Spectral bandwidth (spread around centroid)
                double variance = 0;
                for (int i = 0; i < spectrum.Length; i++)
                {
                    var freq = i * freqPerBin;
                    variance += Math.Pow(freq - result.SpectralCentroid, 2) * spectrum[i];
                }
                result.SpectralBandwidth = (float)Math.Sqrt(variance / totalEnergy);

                // Frequency response smoothness (standard deviation of spectrum)
                var mean = spectrum.Average();
                var variance2 = spectrum.Select(x => Math.Pow(x - mean, 2)).Average();
                result.FrequencyResponseSmoothness = 100.0f - (float)Math.Min(100, Math.Sqrt(variance2) / mean * 100);

                // High frequency noise analysis
                int hfStart = (int)(10000 / freqPerBin); // Above 10kHz
                if (hfStart < spectrum.Length)
                {
                    var hfSpectrum = spectrum.Skip(hfStart).ToArray();
                    var hfMean = hfSpectrum.Average();
                    var hfStdDev = Math.Sqrt(hfSpectrum.Select(x => Math.Pow(x - hfMean, 2)).Average());
                    result.HighFrequencyNoise = hfMean > 0 ? (float)(hfStdDev / hfMean) : 0;
                }
            }

            // Adjust spectral score based on artifacts
            if (result.HasMp3Artifacts)
            {
                result.SpectralScore *= (1.0f - result.Mp3ArtifactConfidence * 0.5f);
            }

            result.SpectralScore = Math.Max(0, Math.Min(100, result.SpectralScore));
        }

        /// <summary>
        /// Analyzes dynamic range and compression (20% weight)
        /// </summary>
        private DynamicRangeResult AnalyzeDynamicRange(AudioData audioData)
        {
            var result = new DynamicRangeResult();

            try
            {
                // Combine channels for analysis
                var monoData = new float[audioData.LeftChannel.Length];
                for (int i = 0; i < monoData.Length; i++)
                {
                    monoData[i] = (Math.Abs(audioData.LeftChannel[i]) + Math.Abs(audioData.RightChannel[i])) * 0.5f;
                }

                // Calculate peak and RMS levels
                result.PeakLevel = 20.0f * (float)Math.Log10(monoData.Max() + 1e-10);
                
                var rms = Math.Sqrt(monoData.Select(x => x * x).Average());
                result.RmsLevel = 20.0f * (float)Math.Log10(rms + 1e-10);
                
                result.DynamicRange = result.PeakLevel - result.RmsLevel;

                // Analyze level distribution for compression detection
                AnalyzeLevelDistribution(result, monoData);
                
                // Calculate loudness range (simplified EBU R128-style)
                CalculateLoudnessRange(result, monoData, audioData.SampleRate);

                // Score based on dynamic range - adjusted for modern mastering practices
                result.DynamicRangeScore = Math.Min(100, Math.Max(0, (float)((result.DynamicRange - 3) * 8.33))); // 3dB = 0 points, 15dB = 100 points

                // Penalties for extreme over-compression (adjusted thresholds)
                if (result.DynamicRange < 4)
                {
                    result.IsOverCompressed = true;
                    result.Issues.Add($"Very low dynamic range ({result.DynamicRange:F1} dB) suggests extreme compression");
                }
                else if (result.DynamicRange < 6)
                {
                    result.Issues.Add($"Low dynamic range ({result.DynamicRange:F1} dB) - heavily compressed but within acceptable range");
                }

                if (result.CompressionRatio > 10)
                {
                    result.Issues.Add($"High compression ratio ({result.CompressionRatio:F1}:1) detected");
                }

                Log.Debug("Dynamic range analysis: Peak={0:F1}dB, RMS={1:F1}dB, DR={2:F1}dB", 
                    result.PeakLevel, result.RmsLevel, result.DynamicRange);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in dynamic range analysis");
                result.Issues.Add($"Dynamic range analysis error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Analyzes level distribution to detect compression
        /// </summary>
        private void AnalyzeLevelDistribution(DynamicRangeResult result, float[] audioData)
        {
            // Create histogram of levels
            const int bins = 100;
            var histogram = new int[bins];
            var maxLevel = audioData.Max();

            if (maxLevel > 0)
            {
                foreach (var sample in audioData)
                {
                    var bin = Math.Min(bins - 1, (int)(sample / maxLevel * bins));
                    histogram[bin]++;
                }

                // Find quiet and loud passages
                var threshold = audioData.Length / 1000; // 0.1% threshold
                result.QuietPassageCount = histogram.Take(bins / 4).Sum();
                result.LoudPassageCount = histogram.Skip(3 * bins / 4).Sum();

                // Calculate dynamic variation
                var nonZeroBins = histogram.Count(h => h > threshold);
                result.DynamicVariation = (float)nonZeroBins / bins * 100;

                // Estimate compression ratio based on level distribution
                var peakBin = Array.LastIndexOf(histogram, histogram.Max());
                var quietBins = histogram.Take(bins / 4).Sum();
                var loudBins = histogram.Skip(3 * bins / 4).Sum();
                
                if (quietBins > 0)
                {
                    result.CompressionRatio = (float)loudBins / quietBins;
                }
            }
        }

        /// <summary>
        /// Calculates loudness range (simplified EBU R128 approach)
        /// </summary>
        private void CalculateLoudnessRange(DynamicRangeResult result, float[] audioData, int sampleRate)
        {
            // Simplified loudness range calculation
            // In a full implementation, this would use proper EBU R128 filtering and gating
            
            int windowSize = sampleRate / 4; // 250ms windows
            var windowLoudness = new List<float>();

            for (int i = 0; i + windowSize < audioData.Length; i += windowSize / 2)
            {
                var windowRms = Math.Sqrt(audioData.Skip(i).Take(windowSize).Select(x => x * x).Average());
                var loudness = -0.691f + 10.0f * (float)Math.Log10(windowRms + 1e-10);
                windowLoudness.Add(loudness);
            }

            if (windowLoudness.Count > 0)
            {
                windowLoudness.Sort();
                
                // LRA is difference between 95th and 10th percentiles
                var p10Index = (int)(windowLoudness.Count * 0.1);
                var p95Index = (int)(windowLoudness.Count * 0.95);
                
                result.LoudnessRange = windowLoudness[p95Index] - windowLoudness[p10Index];
            }
        }

        /// <summary>
        /// Detects digital clipping (20% weight)
        /// </summary>
        private ClippingAnalysisResult AnalyzeClipping(AudioData audioData)
        {
            var result = new ClippingAnalysisResult();

            try
            {
                var leftClipping = DetectClippingInChannel(audioData.LeftChannel);
                var rightClipping = DetectClippingInChannel(audioData.RightChannel);

                // Combine results from both channels
                result.ClippedSamplesCount = leftClipping.ClippedSamples + rightClipping.ClippedSamples;
                result.ClippingEventsCount = leftClipping.ClippingEvents + rightClipping.ClippingEvents;
                result.MaxConsecutiveClippedSamples = Math.Max(leftClipping.MaxConsecutive, rightClipping.MaxConsecutive);
                
                var totalSamples = audioData.LeftChannel.Length * 2; // Both channels
                result.ClippingPercentage = (float)result.ClippedSamplesCount / totalSamples * 100;

                // Calculate average clipping duration
                if (result.ClippingEventsCount > 0)
                {
                    result.AverageClippingDuration = result.ClippedSamplesCount / (float)result.ClippingEventsCount;
                    result.AverageClippingDuration = result.AverageClippingDuration / audioData.SampleRate * 1000; // Convert to ms
                }

                // Check for sustained clipping (> 10ms)
                var sustainedThreshold = audioData.SampleRate * 0.01f; // 10ms in samples
                result.HasSustainedClipping = result.MaxConsecutiveClippedSamples > sustainedThreshold;

                // Calculate true peak using interpolation (simplified)
                result.TruePeakLevel = CalculateTruePeak(audioData);
                result.HasIntersamplePeaks = result.TruePeakLevel > 0.0f; // Above 0 dBFS

                // Calculate clipping penalty - only flag significant clipping that indicates damage
                if (result.ClippingPercentage > 5.0) // Only penalize clipping above 5% (significant damage)
                {
                    result.ClippingPenalty = -Math.Min(50, result.ClippingPercentage * 8); // Penalty for significant clipping
                    result.Issues.Add($"Significant digital clipping detected: {result.ClippingPercentage:F3}% of samples");
                }
                else if (result.ClippingPercentage > 3.0) // Flag only more significant clipping
                {
                    result.ClippingPenalty = -Math.Min(15, result.ClippingPercentage * 3); // Very minor penalty
                    result.Issues.Add($"Moderate clipping detected: {result.ClippingPercentage:F3}% of samples - likely from mastering");
                }
                // Note: Clipping below 3% is considered normal for modern mastered audio and not flagged

                if (result.HasSustainedClipping)
                {
                    result.ClippingPenalty -= 30; // More significant penalty for sustained clipping
                    result.Issues.Add($"Sustained clipping detected: {result.MaxConsecutiveClippedSamples} consecutive samples");
                }

                // Intersample peaks are normal in modern mastering, only flag extreme cases
                if (result.TruePeakLevel > 1.0f) // Only flag peaks above +1 dBFS
                {
                    result.ClippingPenalty -= 5; // Minor penalty for extreme intersample peaks
                    result.Issues.Add($"Extreme intersample peaks detected: {result.TruePeakLevel:F1} dBFS");
                }

                Log.Debug("Clipping analysis: {0:F3}% clipped, {1} events, penalty: {2:F1}", 
                    result.ClippingPercentage, result.ClippingEventsCount, result.ClippingPenalty);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in clipping analysis");
                result.Issues.Add($"Clipping analysis error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Detects clipping in a single channel
        /// </summary>
        private (int ClippedSamples, int ClippingEvents, int MaxConsecutive) DetectClippingInChannel(float[] channel)
        {
            var clippedSamples = 0;
            var clippingEvents = 0;
            var maxConsecutive = 0;
            var currentConsecutive = 0;
            var inClippingEvent = false;

            for (int i = 0; i < channel.Length; i++)
            {
                var sample = Math.Abs(channel[i]);
                var isClipped = sample >= _settings.ClippingThreshold;

                if (isClipped)
                {
                    clippedSamples++;
                    currentConsecutive++;
                    
                    if (!inClippingEvent)
                    {
                        clippingEvents++;
                        inClippingEvent = true;
                    }
                }
                else
                {
                    if (inClippingEvent)
                    {
                        maxConsecutive = Math.Max(maxConsecutive, currentConsecutive);
                        currentConsecutive = 0;
                        inClippingEvent = false;
                    }
                }
            }

            // Handle case where file ends with clipping
            if (inClippingEvent)
            {
                maxConsecutive = Math.Max(maxConsecutive, currentConsecutive);
            }

            return (clippedSamples, clippingEvents, maxConsecutive);
        }

        /// <summary>
        /// Calculates true peak level using interpolation
        /// </summary>
        private float CalculateTruePeak(AudioData audioData)
        {
            // Simplified true peak calculation
            // In a full implementation, this would use proper oversampling and interpolation
            
            var leftPeak = audioData.LeftChannel.Max(Math.Abs);
            var rightPeak = audioData.RightChannel.Max(Math.Abs);
            var peak = Math.Max(leftPeak, rightPeak);
            
            // Apply simple interpolation factor
            var truePeak = peak * 1.05f; // Rough approximation
            
            return 20.0f * (float)Math.Log10(truePeak + 1e-10);
        }

        /// <summary>
        /// Analyzes noise floor and signal-to-noise ratio (10% weight)
        /// </summary>
        private NoiseFloorResult AnalyzeNoiseFloor(AudioData audioData)
        {
            var result = new NoiseFloorResult();

            try
            {
                // Find quiet passages for noise floor analysis
                var quietPassages = FindQuietPassages(audioData);
                result.QuietPassagesAnalyzed = quietPassages.Count;

                if (quietPassages.Count > 0)
                {
                    // Analyze noise in quiet passages
                    AnalyzeQuietPassageNoise(result, quietPassages, audioData);
                }

                // Analyze overall signal-to-noise ratio
                CalculateSignalToNoiseRatio(result, audioData);

                // Detect specific types of noise
                DetectNoiseTypes(result, audioData);

                // Score based on noise floor quality
                CalculateNoiseFloorScore(result);

                Log.Debug("Noise floor analysis: SNR={0:F1}dB, floor={1:F1}dB, {2} quiet passages", 
                    result.SignalToNoiseRatio, result.NoiseFloorLevel, result.QuietPassagesAnalyzed);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in noise floor analysis");
                result.Issues.Add($"Noise floor analysis error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Finds quiet passages in the audio for noise analysis
        /// </summary>
        private List<(int Start, int Length)> FindQuietPassages(AudioData audioData)
        {
            var quietPassages = new List<(int Start, int Length)>();
            var windowSize = audioData.SampleRate / 10; // 100ms windows
            var quietThreshold = 0.01f; // -40dB roughly
            
            var currentQuietStart = -1;
            
            for (int i = 0; i + windowSize < audioData.LeftChannel.Length; i += windowSize / 2)
            {
                // Calculate RMS for this window
                var windowRms = 0.0;
                for (int j = i; j < i + windowSize; j++)
                {
                    var sample = (Math.Abs(audioData.LeftChannel[j]) + Math.Abs(audioData.RightChannel[j])) * 0.5;
                    windowRms += sample * sample;
                }
                windowRms = Math.Sqrt(windowRms / windowSize);
                
                if (windowRms < quietThreshold)
                {
                    if (currentQuietStart == -1)
                    {
                        currentQuietStart = i;
                    }
                }
                else
                {
                    if (currentQuietStart != -1)
                    {
                        var length = i - currentQuietStart;
                        if (length > audioData.SampleRate / 4) // At least 250ms
                        {
                            quietPassages.Add((currentQuietStart, length));
                        }
                        currentQuietStart = -1;
                    }
                }
            }
            
            // Handle quiet passage at end
            if (currentQuietStart != -1)
            {
                var length = audioData.LeftChannel.Length - currentQuietStart;
                if (length > audioData.SampleRate / 4)
                {
                    quietPassages.Add((currentQuietStart, length));
                }
            }
            
            return quietPassages;
        }

        /// <summary>
        /// Analyzes noise characteristics in quiet passages
        /// </summary>
        private void AnalyzeQuietPassageNoise(NoiseFloorResult result, List<(int Start, int Length)> quietPassages, AudioData audioData)
        {
            var noiseLevels = new List<float>();
            
            foreach (var passage in quietPassages)
            {
                var noiseRms = 0.0;
                var sampleCount = 0;
                
                for (int i = passage.Start; i < passage.Start + passage.Length && i < audioData.LeftChannel.Length; i++)
                {
                    var sample = (Math.Abs(audioData.LeftChannel[i]) + Math.Abs(audioData.RightChannel[i])) * 0.5;
                    noiseRms += sample * sample;
                    sampleCount++;
                }
                
                if (sampleCount > 0)
                {
                    noiseRms = Math.Sqrt(noiseRms / sampleCount);
                    var noiseLevel = 20.0f * (float)Math.Log10(noiseRms + 1e-10);
                    noiseLevels.Add(noiseLevel);
                }
            }
            
            if (noiseLevels.Count > 0)
            {
                result.NoiseFloorLevel = noiseLevels.Average();
                result.QuietestLevel = noiseLevels.Min();
                result.AverageQuietLevel = noiseLevels.Average();
                
                // Calculate noise variation
                if (noiseLevels.Count > 1)
                {
                    var variance = noiseLevels.Select(x => Math.Pow(x - result.NoiseFloorLevel, 2)).Average();
                    result.NoiseVariation = (float)Math.Sqrt(variance);
                }
            }
        }

        /// <summary>
        /// Calculates overall signal-to-noise ratio
        /// </summary>
        private void CalculateSignalToNoiseRatio(NoiseFloorResult result, AudioData audioData)
        {
            // Calculate signal level (RMS of entire track)
            var signalRms = 0.0;
            for (int i = 0; i < audioData.LeftChannel.Length; i++)
            {
                var sample = (Math.Abs(audioData.LeftChannel[i]) + Math.Abs(audioData.RightChannel[i])) * 0.5;
                signalRms += sample * sample;
            }
            signalRms = Math.Sqrt(signalRms / audioData.LeftChannel.Length);
            var signalLevel = 20.0f * (float)Math.Log10(signalRms + 1e-10);
            
            // SNR is difference between signal level and noise floor
            if (result.NoiseFloorLevel < -120) // If we couldn't measure noise floor
            {
                result.NoiseFloorLevel = -80; // Assume reasonable noise floor
            }
            
            result.SignalToNoiseRatio = signalLevel - result.NoiseFloorLevel;
        }

        /// <summary>
        /// Detects specific types of noise (hum, hiss, digital artifacts)
        /// </summary>
        private void DetectNoiseTypes(NoiseFloorResult result, AudioData audioData)
        {
            // This would require spectral analysis of quiet passages
            // For now, implement basic heuristics
            
            // Detect hum (50/60Hz components)
            // This would require FFT analysis of quiet passages
            
            // Detect tape hiss (high frequency noise)
            // This would require high-frequency spectral analysis
            
            // Detect digital noise
            // This would require analysis of noise patterns
            
            // Simplified implementation based on noise floor level - only flag truly poor noise floors
            if (result.NoiseFloorLevel > -30) // Only flag really poor noise floors above -30 dB
            {
                result.Issues.Add("High noise floor suggests poor recording conditions or equipment");
            }
            
            if (result.NoiseVariation > 10)
            {
                result.Issues.Add("Variable noise floor suggests inconsistent recording conditions");
            }
        }

        /// <summary>
        /// Calculates noise floor quality score - adjusted for realistic noise floor expectations
        /// </summary>
        private void CalculateNoiseFloorScore(NoiseFloorResult result)
        {
            // Score based on signal-to-noise ratio - adjusted for modern digital content
            result.NoiseFloorScore = Math.Max(0, Math.Min(100, (float)((result.SignalToNoiseRatio + 20) * 2.5))); // 12dB SNR = 80 points, 32dB SNR = 100 points
            
            // Only penalize truly poor noise floors - adjusted for realistic music content expectations
            if (result.NoiseFloorLevel > -30.0f)
            {
                result.NoiseFloorScore *= 0.6f;
                result.Issues.Add($"Very poor noise floor ({result.NoiseFloorLevel:F1} dB)");
            }
            else if (result.NoiseFloorLevel > -35.0f)
            {
                result.NoiseFloorScore *= 0.8f;
                result.Issues.Add($"Poor noise floor ({result.NoiseFloorLevel:F1} dB)");
            }
            
            if (result.HasTapeHiss)
            {
                result.NoiseFloorScore *= 0.8f;
                result.Issues.Add("Tape hiss detected");
            }
            
            if (result.HasHum)
            {
                result.NoiseFloorScore *= 0.9f;
                result.Issues.Add("AC hum detected");
            }
            
            if (result.HasDigitalNoise)
            {
                result.NoiseFloorScore *= 0.8f;
                result.Issues.Add("Digital artifacts in noise floor");
            }
        }

        /// <summary>
        /// Analyzes channel quality and stereo characteristics (10% weight)
        /// </summary>
        private ChannelQualityResult AnalyzeChannelQuality(AudioData audioData)
        {
            var result = new ChannelQualityResult();

            try
            {
                // Check if audio is actually mono
                result.IsMono = CheckIfMono(audioData);
                
                if (!result.IsMono)
                {
                    // Analyze stereo characteristics
                    AnalyzeStereoCharacteristics(result, audioData);
                }
                else
                {
                    result.Issues.Add("Audio is mono");
                    result.ChannelScore = 50; // Neutral score for mono
                    return result;
                }

                // Calculate channel quality score
                CalculateChannelQualityScore(result);

                Log.Debug("Channel analysis: Mono={0}, Width={1:F2}, Correlation={2:F2}, Balance={3:F2}", 
                    result.IsMono, result.StereoWidth, result.ChannelCorrelation, result.LeftRightBalance);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in channel quality analysis");
                result.Issues.Add($"Channel analysis error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Checks if the audio is effectively mono
        /// </summary>
        private bool CheckIfMono(AudioData audioData)
        {
            var differences = 0;
            var threshold = 0.001f; // Very small threshold for differences

            for (int i = 0; i < Math.Min(audioData.LeftChannel.Length, audioData.RightChannel.Length); i++)
            {
                if (Math.Abs(audioData.LeftChannel[i] - audioData.RightChannel[i]) > threshold)
                {
                    differences++;
                }
            }

            var differencePercentage = (float)differences / audioData.LeftChannel.Length;
            return differencePercentage < 0.01f; // Less than 1% differences = mono
        }

        /// <summary>
        /// Analyzes stereo field characteristics
        /// </summary>
        private void AnalyzeStereoCharacteristics(ChannelQualityResult result, AudioData audioData)
        {
            var correlationSum = 0.0;
            var leftSum = 0.0;
            var rightSum = 0.0;
            var leftSqSum = 0.0;
            var rightSqSum = 0.0;
            var sampleCount = Math.Min(audioData.LeftChannel.Length, audioData.RightChannel.Length);

            // Calculate correlation and levels
            for (int i = 0; i < sampleCount; i++)
            {
                var left = audioData.LeftChannel[i];
                var right = audioData.RightChannel[i];

                correlationSum += left * right;
                leftSum += Math.Abs(left);
                rightSum += Math.Abs(right);
                leftSqSum += left * left;
                rightSqSum += right * right;
            }

            // Channel correlation
            var leftRms = Math.Sqrt(leftSqSum / sampleCount);
            var rightRms = Math.Sqrt(rightSqSum / sampleCount);
            if (leftRms > 0 && rightRms > 0)
            {
                result.ChannelCorrelation = (float)(correlationSum / sampleCount / (leftRms * rightRms));
            }

            // Left/right balance
            var leftLevel = leftSum / sampleCount;
            var rightLevel = rightSum / sampleCount;
            if (leftLevel + rightLevel > 0)
            {
                result.LeftRightBalance = (float)((rightLevel - leftLevel) / (leftLevel + rightLevel));
            }

            // Stereo width (simplified)
            result.StereoWidth = (float)Math.Max(0, 1.0f - Math.Abs(result.ChannelCorrelation));

            // Check for pseudo-stereo (high correlation but not identical)
            result.IsPseudoStereo = result.ChannelCorrelation > 0.95f && result.ChannelCorrelation < 0.999f;

            // Check for phase issues
            result.HasPhaseIssues = result.ChannelCorrelation < -0.5f;
            result.PhaseCoherence = Math.Abs(result.ChannelCorrelation);

            // Check for channel imbalance
            result.HasChannelImbalance = Math.Abs(result.LeftRightBalance) > 0.2f; // More than 20% imbalance
            
            // Calculate level difference in dB
            if (leftLevel > 0 && rightLevel > 0)
            {
                result.ChannelLevelDifference = 20.0f * (float)Math.Log10(Math.Max(leftLevel, rightLevel) / Math.Min(leftLevel, rightLevel));
            }

            // Check for polarity issues
            result.HasChannelPolarity = result.ChannelCorrelation < -0.8f;
        }

        /// <summary>
        /// Calculates channel quality score
        /// </summary>
        private void CalculateChannelQualityScore(ChannelQualityResult result)
        {
            result.ChannelScore = 100; // Start with perfect score

            if (result.IsPseudoStereo)
            {
                result.ChannelScore -= 20;
                result.Issues.Add("Pseudo-stereo detected (mono content panned to stereo)");
            }

            if (result.HasPhaseIssues)
            {
                result.ChannelScore -= 30;
                result.Issues.Add($"Phase issues detected (correlation: {result.ChannelCorrelation:F2})");
            }

            if (result.HasChannelImbalance)
            {
                result.ChannelScore -= 15;
                result.Issues.Add($"Channel imbalance detected ({result.LeftRightBalance * 100:F1}% bias)");
            }

            if (result.HasChannelPolarity)
            {
                result.ChannelScore -= 25;
                result.Issues.Add("Channel polarity inversion detected");
            }

            if (result.ChannelLevelDifference > 3.0f)
            {
                result.ChannelScore -= 10;
                result.Issues.Add($"Significant level difference between channels ({result.ChannelLevelDifference:F1} dB)");
            }

            // Only flag extremely narrow stereo images that suggest mono content or technical issues
            if (result.StereoWidth < 0.05f)
            {
                result.ChannelScore -= 10;
                result.Issues.Add("Extremely narrow stereo image - possible mono content");
            }

            result.ChannelScore = Math.Max(0, result.ChannelScore);
        }

        /// <summary>
        /// Calculates the overall quality score from individual component scores
        /// </summary>
        private float CalculateOverallScore(AudioQualityResult result)
        {
            var weightedScore = 
                result.SpectralAnalysis.SpectralScore * _settings.SpectralWeight / 100.0f +
                result.DynamicRange.DynamicRangeScore * _settings.DynamicRangeWeight / 100.0f +
                Math.Max(0, 100 + result.ClippingAnalysis.ClippingPenalty) * _settings.ClippingWeight / 100.0f +
                result.NoiseFloor.NoiseFloorScore * _settings.NoiseFloorWeight / 100.0f +
                result.ChannelQuality.ChannelScore * _settings.ChannelWeight / 100.0f;

            return Math.Max(0, Math.Min(100, weightedScore));
        }

        /// <summary>
        /// Generates quality assessment notes and issues
        /// </summary>
        private void GenerateQualityAssessment(AudioQualityResult result)
        {
            // Collect all issues
            result.QualityIssues.AddRange(result.SpectralAnalysis.Issues);
            result.QualityIssues.AddRange(result.DynamicRange.Issues);
            result.QualityIssues.AddRange(result.ClippingAnalysis.Issues);
            result.QualityIssues.AddRange(result.NoiseFloor.Issues);
            result.QualityIssues.AddRange(result.ChannelQuality.Issues);

            // Generate summary notes - adjusted thresholds for more accurate quality assessment
            var notes = new List<string>();

            if (result.OverallQualityScore >= 85)
            {
                notes.Add("Excellent quality audio - no significant issues detected");
            }
            else if (result.OverallQualityScore >= 70)
            {
                notes.Add("Good quality audio - minor issues may be present");
            }
            else if (result.OverallQualityScore >= 55)
            {
                notes.Add("Acceptable quality audio - some degradation detected");
            }
            else if (result.OverallQualityScore >= 35)
            {
                notes.Add("Poor quality audio - significant issues detected");
            }
            else
            {
                notes.Add("Very poor quality audio - re-rip strongly recommended");
            }

            // Add specific recommendations
            if (result.SpectralAnalysis.HasMp3Artifacts)
            {
                notes.Add("Source appears to be compressed audio (MP3/AAC)");
            }

            if (result.DynamicRange.IsOverCompressed)
            {
                notes.Add("Audio shows signs of heavy dynamic range compression");
            }

            if (result.ClippingAnalysis.ClippingPercentage > 5.0f)
            {
                notes.Add("Significant digital clipping detected - original source may be damaged");
            }
            else if (result.ClippingAnalysis.ClippingPercentage > 3.0f)
            {
                notes.Add("Moderate clipping detected - likely from mastering process");
            }
            // Note: Clipping below 3% is not mentioned as it's normal for modern mastered audio

            result.Notes = string.Join("; ", notes);
        }

        /// <summary>
        /// Initializes the BASS audio library
        /// </summary>
        private void InitializeBass()
        {
            lock (_bassLock)
            {
                if (!_bassInitialized)
                {
                    if (Bass.Init())
                    {
                        _bassInitialized = true;
                        Log.Debug("BASS audio library initialized successfully");
                    }
                    else
                    {
                        Log.Error("Failed to initialize BASS audio library: {Error}", Bass.LastError);
                        throw new InvalidOperationException($"Failed to initialize BASS: {Bass.LastError}");
                    }
                }
            }
        }

        /// <summary>
        /// Audio data container
        /// </summary>
        internal class AudioData
        {
            public float[] LeftChannel { get; set; } = Array.Empty<float>();
            public float[] RightChannel { get; set; } = Array.Empty<float>();
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
            public int LengthSamples { get; set; }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            // BASS will be freed when the application exits
        }
    }
}
