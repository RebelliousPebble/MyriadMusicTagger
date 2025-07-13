using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RestSharp;
using Serilog;

namespace MyriadMusicTagger
{
    public class DuplicateFinder
    {
        private readonly RestClient _resClient;
        private readonly AppSettings _settings;

        public DuplicateFinder(RestClient resClient, AppSettings settings)
        {
            _resClient = resClient;
            _settings = settings;
        }

        public async Task<List<List<DuplicateItem>>> FindDuplicates()
        {
            var allItems = await GetAllMediaItems();
            var duplicateGroups = allItems
                .GroupBy(item => new { Title = item.Title?.Trim().ToLower(), Artists = string.Join(",", item.Artists.Select(a => a.ArtistName?.Trim().ToLower()).OrderBy(a => a)) })
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            return duplicateGroups.Select(group =>
            {
                var duplicateItems = group.Select(item => new DuplicateItem
                {
                    MediaId = item.MediaId,
                    Title = item.Title,
                    Artists = item.Artists.Select(a => a.ArtistName).ToList(),
                    Album = item.AlbumTitle,
                    Year = item.FirstReleaseYear,
                    TotalLength = item.TotalLength,
                    BitRate = item.AudioFormat?.BitRate ?? 0
                }).ToList();

                MarkBestToKeep(duplicateItems);
                return duplicateItems;

            }).ToList();
        }

        private async Task<List<Result>> GetAllMediaItems()
        {
            var allItems = new List<Result>();
            var request = new RestRequest("/api/Media/Search")
                .AddQueryParameter("returnInfo", "Full");

            if (!string.IsNullOrEmpty(_settings.RESApiKey))
            {
                request.AddHeader("X-API-Key", _settings.RESApiKey);
            }

            try
            {
                var response = await _resClient.GetAsync<SearchMediaResults>(request);
                if (response != null && response.Items != null)
                {
                    allItems.AddRange(response.Items);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error fetching all media items from RES API");
            }

            return allItems;
        }

        private void MarkBestToKeep(List<DuplicateItem> group)
        {
            if (group == null || group.Count == 0) return;

            DuplicateItem bestItem = group
                .OrderByDescending(i => i.BitRate)
                .ThenByDescending(i => i.Year)
                .ThenByDescending(i => !string.IsNullOrEmpty(i.Album))
                .ThenBy(i => i.MediaId)
                .First();

            foreach (var item in group)
            {
                item.Keep = item == bestItem;
            }
        }

        public async Task<bool> DeleteItems(IEnumerable<int> mediaIds)
        {
            bool allSucceeded = true;
            foreach (var mediaId in mediaIds)
            {
                var request = new RestRequest($"/api/Media/DeleteMediaItem")
                    .AddQueryParameter("mediaId", mediaId.ToString());

                if (!string.IsNullOrEmpty(_settings.RESApiKey))
                {
                    request.AddHeader("X-API-Key", _settings.RESApiKey);
                }

                try
                {
                    var response = await _resClient.PostAsync(request);
                    if (!response.IsSuccessful)
                    {
                        allSucceeded = false;
                        Log.Error($"Failed to delete media item {mediaId}. Status: {response.StatusCode}, Content: {response.Content}");
                    }
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    Log.Error(ex, $"Exception while deleting media item {mediaId}");
                }
            }
            return allSucceeded;
        }
    }

    public class SearchMediaResults
    {
        public List<Result> Items { get; set; } = new();
    }
}
