namespace MyriadMusicTagger.Core
{
    /// <summary>
    /// Complete audio quality analysis result for a track
    /// </summary>
    public class AudioQualityResult
    {
        public int MediaId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Duration { get; set; } = "";
        
        // Overall quality score (0-100)
        public float OverallQualityScore { get; set; }
        
        // Individual analysis results
        public SpectralAnalysisResult SpectralAnalysis { get; set; } = new();
        public DynamicRangeResult DynamicRange { get; set; } = new();
        public ClippingAnalysisResult ClippingAnalysis { get; set; } = new();
        public NoiseFloorResult NoiseFloor { get; set; } = new();
        public ChannelQualityResult ChannelQuality { get; set; } = new();
        
        // Processing metadata
        public DateTime AnalyzedDateTime { get; set; } = DateTime.Now;
        public bool ProcessingSuccessful { get; set; }
        public string ErrorMessage { get; set; } = "";
        public TimeSpan ProcessingTime { get; set; }
        
        // Recommendations (now dynamic based on analysis settings threshold)
        public bool RecommendReRip { get; set; }
        public List<string> QualityIssues { get; set; } = new List<string>();
        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// Spectral analysis results from FFT processing (40% weight)
    /// </summary>
    public class SpectralAnalysisResult
    {
        // Score components
        public float SpectralScore { get; set; } // 0-100
        
        // Frequency analysis
        public float HighFrequencyContent { get; set; } // % of content above 15kHz
        public float FrequencyRolloffPoint { get; set; } // Hz where significant rolloff begins
        public float SpectralCentroid { get; set; } // Average frequency weighted by magnitude
        public float SpectralBandwidth { get; set; } // Spread of frequency content
        
        // Artifact detection
        public bool HasMp3Artifacts { get; set; }
        public float Mp3ArtifactConfidence { get; set; } // 0-1, confidence in MP3 artifacts
        public List<float> SuspiciousFrequencies { get; set; } = new List<float>(); // Frequencies with artifacts
        
        // Quality indicators
        public float FrequencyResponseSmoothness { get; set; } // How smooth the frequency response is
        public float HighFrequencyNoise { get; set; } // Amount of noise in high frequencies
        
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Dynamic range analysis results (20% weight)
    /// </summary>
    public class DynamicRangeResult
    {
        // Score components
        public float DynamicRangeScore { get; set; } // 0-100
        
        // Level measurements (in dB)
        public float PeakLevel { get; set; }
        public float RmsLevel { get; set; }
        public float DynamicRange { get; set; } // Peak - RMS
        
        // Compression indicators
        public float CompressionRatio { get; set; } // Estimated compression ratio
        public bool IsOverCompressed { get; set; }
        public float LoudnessRange { get; set; } // EBU R128 loudness range
        
        // Temporal analysis
        public float QuietPassageCount { get; set; } // Number of quiet sections
        public float LoudPassageCount { get; set; } // Number of loud sections
        public float DynamicVariation { get; set; } // How much the levels vary
        
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Digital clipping detection results (20% weight)
    /// </summary>
    public class ClippingAnalysisResult
    {
        // Score components (negative penalties)
        public float ClippingPenalty { get; set; } // 0 to -100 (penalty points)
        
        // Clipping detection
        public int ClippedSamplesCount { get; set; }
        public float ClippingPercentage { get; set; } // % of samples that are clipped
        public int ClippingEventsCount { get; set; } // Number of separate clipping events
        
        // Clipping characteristics
        public float MaxConsecutiveClippedSamples { get; set; }
        public float AverageClippingDuration { get; set; } // Average length of clipping events
        public bool HasSustainedClipping { get; set; } // Clipping lasting > 10ms
        
        // Inter-sample peaks (true peak detection)
        public float TruePeakLevel { get; set; }
        public bool HasIntersamplePeaks { get; set; }
        
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Noise floor and signal-to-noise ratio analysis (10% weight)
    /// </summary>
    public class NoiseFloorResult
    {
        // Score components
        public float NoiseFloorScore { get; set; } // 0-100
        
        // Noise measurements (in dB)
        public float NoiseFloorLevel { get; set; } // Average noise floor level
        public float SignalToNoiseRatio { get; set; } // SNR in dB
        public float NoiseVariation { get; set; } // How consistent the noise floor is
        
        // Noise characteristics
        public bool HasTapeHiss { get; set; }
        public bool HasHum { get; set; } // 50/60Hz hum
        public bool HasDigitalNoise { get; set; }
        public float HumLevel { get; set; } // Level of AC hum
        
        // Quiet passage analysis
        public int QuietPassagesAnalyzed { get; set; }
        public float AverageQuietLevel { get; set; }
        public float QuietestLevel { get; set; }
        
        // Spectral noise analysis
        public float HighFrequencyNoise { get; set; } // Noise above 10kHz
        public float LowFrequencyNoise { get; set; } // Noise below 100Hz
        
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Stereo field and channel quality analysis (10% weight)
    /// </summary>
    public class ChannelQualityResult
    {
        // Score components
        public float ChannelScore { get; set; } // 0-100
        
        // Channel analysis
        public bool IsMono { get; set; }
        public bool IsPseudoStereo { get; set; } // Mono content panned to stereo
        public float StereoWidth { get; set; } // How wide the stereo image is
        public float ChannelCorrelation { get; set; } // -1 to 1, correlation between channels
        
        // Balance and positioning
        public float LeftRightBalance { get; set; } // -1 (left) to 1 (right), 0 = centered
        public float PhaseCoherence { get; set; } // How in-phase the channels are
        public bool HasPhaseIssues { get; set; }
        
        // Channel-specific issues
        public bool HasChannelImbalance { get; set; }
        public bool HasChannelPolarity { get; set; } // One channel inverted
        public float ChannelLevelDifference { get; set; } // dB difference between channels
        
        public List<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>
    /// Collection of quality analysis results for reporting and export
    /// </summary>
    public class QualityReport
    {
        public DateTime GeneratedDateTime { get; set; } = DateTime.Now;
        public string DatabaseName { get; set; } = "";
        public int TotalTracksAnalyzed { get; set; }
        public int SuccessfulAnalyses { get; set; }
        public int FailedAnalyses { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        
        // Summary statistics
        public float AverageQualityScore { get; set; }
        public float MedianQualityScore { get; set; }
        public int TracksNeedingReRip { get; set; }
        public float PercentageNeedingReRip { get; set; }
        
        // Quality distribution
        public Dictionary<string, int> QualityDistribution { get; set; } = new();
        // e.g., "Excellent (90-100)": 150, "Good (70-89)": 300, etc.
        
        // Common issues found
        public Dictionary<string, int> CommonIssues { get; set; } = new();
        
        // Individual results
        public List<AudioQualityResult> Results { get; set; } = new List<AudioQualityResult>();
        
        // Export settings
        public bool IncludeOnlyProblematic { get; set; } = false;
        public float MinimumScoreForExport { get; set; } = 0.0f;
    }

    /// <summary>
    /// Progress information for long-running quality analysis operations
    /// </summary>
    public class QualityAnalysisProgress
    {
        public int TotalTracks { get; set; }
        public int ProcessedTracks { get; set; }
        
        // Use fields for thread-safe counters
        private int _successfulAnalyses;
        private int _failedAnalyses;
        
        public int SuccessfulAnalyses 
        { 
            get => _successfulAnalyses; 
            set => _successfulAnalyses = value; 
        }
        
        public int FailedAnalyses 
        { 
            get => _failedAnalyses; 
            set => _failedAnalyses = value; 
        }
        
        // Access to the underlying fields for Interlocked operations
        internal ref int SuccessfulAnalysesRef => ref _successfulAnalyses;
        internal ref int FailedAnalysesRef => ref _failedAnalyses;
        
        public float OverallProgress => TotalTracks > 0 ? (float)ProcessedTracks / TotalTracks : 0.0f;
        public string CurrentTrack { get; set; } = "";
        public string CurrentPhase { get; set; } = "";
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public float TracksPerSecond { get; set; }
    }

    /// <summary>
    /// Configuration for audio quality analysis
    /// </summary>
    public class QualityAnalysisSettings
    {
        // Analysis scope
        public bool AnalyzeAllTracks { get; set; } = true;
        public List<int> SpecificMediaIds { get; set; } = new List<int>();
        public List<string> IncludeCategories { get; set; } = new List<string>();
        public List<string> ExcludeCategories { get; set; } = new List<string>();
        
        // Processing options
        public int MaxConcurrentAnalyses { get; set; } = Environment.ProcessorCount;
        public int MaxAudioMemoryMB { get; set; } = 512; // Max memory for audio processing
        public bool SkipLargeFiles { get; set; } = true;
        public long MaxFileSizeMB { get; set; } = 500;
        
        // Analysis thresholds
        public float ReRipThreshold { get; set; } = 60.0f;
        public float ClippingThreshold { get; set; } = 0.95f; // Sample value threshold for clipping
        public float NoiseFloorThresholdDb { get; set; } = -40.0f; // dB threshold for acceptable noise floor (adjusted for realistic expectations)
        
        // Scoring weights (must sum to 100)
        public float SpectralWeight { get; set; } = 40.0f;
        public float DynamicRangeWeight { get; set; } = 20.0f;
        public float ClippingWeight { get; set; } = 20.0f;
        public float NoiseFloorWeight { get; set; } = 10.0f;
        public float ChannelWeight { get; set; } = 10.0f;
        
        // Export options
        public bool ExportOnlyProblematic { get; set; } = false;
        public string ExportFilePath { get; set; } = "";
    }
}
