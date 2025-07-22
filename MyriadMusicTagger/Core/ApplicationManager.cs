using AcoustID;
using MetaBrainz.MusicBrainz;
using RestSharp;
using MyriadMusicTagger.Services;
using MyriadMusicTagger.Core;

namespace MyriadMusicTagger.Core
{
    /// <summary>
    /// Manages application-wide settings and configuration
    /// </summary>
    public class ApplicationManager
    {
        private AppSettings _currentSettings;
        private MyriadApiService? _apiService;
        private ItemProcessingService? _itemProcessingService;
        private BatchProcessingService? _batchProcessingService;
        private readonly RecentItemsManager _recentItemsManager;

        public ApplicationManager()
        {
            _currentSettings = new AppSettings();
            _recentItemsManager = new RecentItemsManager();
        }

        /// <summary>
        /// Gets the current application settings
        /// </summary>
        public AppSettings CurrentSettings => _currentSettings;

        /// <summary>
        /// Gets the recent items manager
        /// </summary>
        public RecentItemsManager RecentItemsManager => _recentItemsManager;

        /// <summary>
        /// Gets the API service instance
        /// </summary>
        public MyriadApiService ApiService => _apiService ?? throw new InvalidOperationException("Settings must be applied first");

        /// <summary>
        /// Gets the item processing service instance
        /// </summary>
        public ItemProcessingService ItemProcessingService => _itemProcessingService ?? throw new InvalidOperationException("Settings must be applied first");

        /// <summary>
        /// Gets the batch processing service instance
        /// </summary>
        public BatchProcessingService BatchProcessingService => _batchProcessingService ?? throw new InvalidOperationException("Settings must be applied first");

        /// <summary>
        /// Initializes the application with settings
        /// </summary>
        /// <param name="settings">Application settings to apply</param>
        public void Initialize(AppSettings settings)
        {
            _currentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
            ApplySettings(_currentSettings);
            _recentItemsManager.LoadRecentItems();
        }

        /// <summary>
        /// Updates the current settings and reinitializes services
        /// </summary>
        /// <param name="newSettings">New settings to apply</param>
        public void UpdateSettings(AppSettings newSettings)
        {
            if (newSettings != null)
            {
                _currentSettings = newSettings;
                ApplySettings(_currentSettings);
            }
        }

        /// <summary>
        /// Applies settings to the application and services
        /// </summary>
        private void ApplySettings(AppSettings settings)
        {
            // Configure AcoustID
            Configuration.ClientKey = settings.AcoustIDClientKey;
            
            // Configure MusicBrainz
            Query.DelayBetweenRequests = settings.DelayBetweenRequests;

            // Initialize services
            _apiService = new MyriadApiService(settings);
            _itemProcessingService = new ItemProcessingService(_apiService);
            _batchProcessingService = new BatchProcessingService(_itemProcessingService);
        }
    }
}
