namespace MyriadMusicTagger.Core
{
    /// <summary>
    /// Manages the collection of recently processed items
    /// </summary>
    public class RecentItemsManager
    {
        private readonly Queue<int> _recentItems = new(capacity: 10);

        /// <summary>
        /// Gets the count of recent items
        /// </summary>
        public int Count => _recentItems.Count;

        /// <summary>
        /// Gets all recent items as a list
        /// </summary>
        public List<int> GetRecentItems() => _recentItems.ToList();

        /// <summary>
        /// Adds an item to the recent items collection
        /// </summary>
        /// <param name="itemNumber">Item number to add</param>
        public void AddToRecentItems(int itemNumber)
        {
            if (!_recentItems.Contains(itemNumber))
            {
                if (_recentItems.Count >= 10)
                {
                    _recentItems.Dequeue();
                }
                _recentItems.Enqueue(itemNumber);
            }
        }

        /// <summary>
        /// Clears all recent items
        /// </summary>
        public void Clear()
        {
            _recentItems.Clear();
        }

        /// <summary>
        /// Loads recent items from persistent storage (placeholder for future implementation)
        /// </summary>
        public void LoadRecentItems()
        {
            _recentItems.Clear();
            // TODO: Implement persistence
        }
    }
}
