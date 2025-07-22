using RestSharp;
using Newtonsoft.Json;
using Serilog;
using MyriadMusicTagger.Core;

namespace MyriadMusicTagger.Services
{
    /// <summary>
    /// Service for interacting with the Myriad API
    /// </summary>
    public class MyriadApiService
    {
        private readonly RestClient _playoutClient;
        private readonly RestClient _resClient;
        private readonly AppSettings _settings;

        public MyriadApiService(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            var playoutOptions = new RestClientOptions
            {
                BaseUrl = new Uri(settings.PlayoutApiUrl.TrimEnd('/'))
            };
            _playoutClient = new RestClient(playoutOptions);

            var resOptions = new RestClientOptions
            {
                BaseUrl = new Uri(settings.RESApiUrl.TrimEnd('/'))
            };
            _resClient = new RestClient(resOptions);
        }

        /// <summary>
        /// Reads an item from the Myriad system
        /// </summary>
        /// <param name="itemNumber">Item number to read</param>
        /// <returns>The item result or null if failed</returns>
        /// <exception cref="ApiException">Thrown when API call fails</exception>
        public Result? ReadItem(int itemNumber)
        {
            var request = CreateReadItemRequest(itemNumber, _settings.PlayoutReadKey);
            RestResponse response;
            
            try 
            { 
                response = _playoutClient.Get(request); 
            }
            catch (Exception ex) 
            { 
                Log.Error(ex, "Myriad API connection error on Get for item {ItemNumber}", itemNumber);
                throw new ApiException($"Failed to connect to Myriad API: {ex.Message}", ex);
            }

            return ProcessApiResponse(response, "Failed to parse API response for ReadItem");
        }

        /// <summary>
        /// Updates an item's metadata in the Myriad system
        /// </summary>
        /// <param name="itemNumber">Item number to update</param>
        /// <param name="titleUpdate">New title and artist information</param>
        /// <returns>True if successful, false otherwise</returns>
        /// <exception cref="ApiException">Thrown when API call fails</exception>
        public bool UpdateItemMetadata(int itemNumber, MyriadTitleSchema titleUpdate)
        {
            var request = CreateUpdateItemRequest(itemNumber, _settings.PlayoutWriteKey, titleUpdate);
            RestResponse result;
            
            try 
            { 
                result = _playoutClient.Execute(request, Method.Post); 
            }
            catch (Exception ex) 
            { 
                Log.Error(ex, "Myriad API connection error on Post for item {ItemNumber}", itemNumber);
                throw new ApiException($"Failed to save to Myriad API: {ex.Message}", ex);
            }

            if (result.IsSuccessful)
            {
                return true;
            }
            else
            {
                string errorDetails = $"Failed to update metadata in Myriad system.\nError: {result.ErrorMessage ?? "Unknown error"}";
                if (!string.IsNullOrEmpty(result.Content)) 
                { 
                    errorDetails += $"\nResponse Content:\n{result.Content}"; 
                }
                Log.Error($"API Error during update metadata. Status: {result.StatusCode}. Response: {result.Content}", result.ErrorException);
                throw new ApiException($"API Error ({result.StatusCode}): {errorDetails}");
            }
        }

        /// <summary>
        /// Creates a REST request for reading an item
        /// </summary>
        private RestRequest CreateReadItemRequest(int itemNumber, string readKey)
        {
            return new RestRequest("/api/Media/ReadItem")
                .AddQueryParameter("mediaId", itemNumber.ToString())
                .AddQueryParameter("attributesStationId", "-1")
                .AddQueryParameter("additionalInfo", "Full")
                .AddHeader("X-API-Key", readKey);
        }

        /// <summary>
        /// Creates a REST request for updating an item
        /// </summary>
        private RestRequest CreateUpdateItemRequest(int itemNumber, string writeKey, MyriadTitleSchema titleUpdate)
        {
            var request = new RestRequest("/api/Media/SetItemTitling");
            request.AddQueryParameter("mediaId", itemNumber.ToString());
            request.AddHeader("X-API-Key", writeKey);
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Accept", "application/json");
            request.AddBody(JsonConvert.SerializeObject(titleUpdate, Formatting.None));
            return request;
        }

        /// <summary>
        /// Processes an API response and returns the parsed result
        /// </summary>
        private Result? ProcessApiResponse(RestResponse response, string errorMessageContext)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content != null)
            {
                var myriadMediaItem = JsonConvert.DeserializeObject<MyriadMediaItem>(response.Content);
                if (myriadMediaItem?.Result != null) 
                    return myriadMediaItem.Result;

                Log.Error("Failed to deserialize {Context} response content: {Content}", errorMessageContext, response.Content);
                throw new ApiException($"Failed to parse API response for {errorMessageContext}. Content might be invalid.");
            }

            string errorMsg = $"API request for {errorMessageContext} failed. Status: {response.StatusCode}.";
            if (!string.IsNullOrEmpty(response.ErrorMessage)) 
                errorMsg += $" Error: {response.ErrorMessage}";
            if (!string.IsNullOrEmpty(response.Content)) 
                errorMsg += $"\nContent: {response.Content}";
            
            Log.Error($"API Error during {errorMessageContext}. Status: {response.StatusCode}. Response: {response.Content}", response.ErrorException);
            throw new ApiException(errorMsg);
        }
    }

    /// <summary>
    /// Exception thrown by API service operations
    /// </summary>
    public class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
        public ApiException(string message, Exception innerException) : base(message, innerException) { }
    }
}
