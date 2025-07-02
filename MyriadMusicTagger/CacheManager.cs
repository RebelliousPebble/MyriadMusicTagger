using Microsoft.Data.Sqlite;
using MyriadMusicTagger.Cache; // For CacheModels
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcoustID.Web; // For LookupResponse
using MetaBrainz.MusicBrainz.Interfaces.Entities; // For IRecording
using Serilog;

namespace MyriadMusicTagger
{
    public class CacheManager : IDisposable
    {
        private readonly SqliteConnection _connection;
        private const string DbFileName = "music_cache.sqlite";
        private static readonly string DbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyriadMusicTagger", // Application-specific folder
            DbFileName);

        // Cache TTL (Time To Live) in days. Set to 0 for no expiry, or a positive value for expiry.
        private const int CacheTTLDays = 30; // e.g., 30 days

        public CacheManager()
        {
            try
            {
                var dbDir = Path.GetDirectoryName(DbPath);
                if (dbDir != null && !Directory.Exists(dbDir))
                {
                    Directory.CreateDirectory(dbDir);
                    Log.Information("Created application data directory for cache: {Directory}", dbDir);
                }

                _connection = new SqliteConnection($"Data Source={DbPath}");
                _connection.Open();
                Log.Information("Successfully opened connection to SQLite cache at {DbPath}", DbPath);
                InitializeDatabase();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize CacheManager or open database connection at {DbPath}", DbPath);
                // If connection fails, subsequent operations will likely fail.
                // Consider how to handle this gracefully - perhaps a "no-op" cache mode.
                _connection = null!; // Ensure it's null if initialization failed.
                throw; // Re-throw to make the application aware of the critical failure.
            }
        }

        private void InitializeDatabase()
        {
            if (_connection == null) return;

            Log.Information("Initializing database schema if not exists...");
            using var command = _connection.CreateCommand();

            // AcoustID Cache Table
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS AcoustIdCache (
                    Fingerprint TEXT NOT NULL,
                    Duration INTEGER NOT NULL,
                    SerializedRecordingIdScoresJson TEXT,
                    CachedAt DATETIME NOT NULL,
                    PRIMARY KEY (Fingerprint, Duration)
                );";
            command.ExecuteNonQuery();
            Log.Debug("AcoustIdCache table checked/created.");

            // MusicBrainz Cache Table
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS MusicBrainzCache (
                    MBRecordingId TEXT PRIMARY KEY,
                    Title TEXT,
                    ArtistCreditName TEXT,
                    AllArtistNamesJson TEXT,
                    AlbumTitle TEXT,
                    ReleaseDate TEXT,
                    Disambiguation TEXT,
                    IsrcsJson TEXT,
                    UserRating REAL,
                    UserRatingCount INTEGER,
                    ArtistCreditsJson TEXT,
                    ReleasesJson TEXT,
                    CachedAt DATETIME NOT NULL
                );";
            command.ExecuteNonQuery();
            Log.Debug("MusicBrainzCache table checked/created.");

            // Create indexes for faster lookups
            command.CommandText = "CREATE INDEX IF NOT EXISTS IDX_AcoustIdCache_CachedAt ON AcoustIdCache (CachedAt);";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE INDEX IF NOT EXISTS IDX_MusicBrainzCache_CachedAt ON MusicBrainzCache (CachedAt);";
            command.ExecuteNonQuery();
            Log.Debug("Indexes for cache tables checked/created.");

            Log.Information("Database schema initialization complete.");

            // Periodically clean up old cache entries
            CleanupOldCacheEntries();
        }

        private void CleanupOldCacheEntries()
        {
            if (_connection == null || CacheTTLDays <= 0) return;

            Log.Information("Cleaning up expired cache entries older than {CacheTTLDays} days...", CacheTTLDays);
            var expiryDate = DateTime.UtcNow.AddDays(-CacheTTLDays);

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM AcoustIdCache WHERE CachedAt < @ExpiryDate;";
                command.Parameters.AddWithValue("@ExpiryDate", expiryDate);
                int acoustIdRowsDeleted = command.ExecuteNonQuery();
                Log.Information("Deleted {Count} expired entries from AcoustIdCache.", acoustIdRowsDeleted);

                command.CommandText = "DELETE FROM MusicBrainzCache WHERE CachedAt < @ExpiryDate;";
                command.Parameters.Clear(); // Clear previous parameters
                command.Parameters.AddWithValue("@ExpiryDate", expiryDate);
                int musicBrainzRowsDeleted = command.ExecuteNonQuery();
                Log.Information("Deleted {Count} expired entries from MusicBrainzCache.", musicBrainzRowsDeleted);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during cache cleanup.");
            }
        }


        public CachedAcoustIdResult? GetCachedAcoustIdResult(string fingerprint, int duration)
        {
            if (_connection == null) return null;
            Log.Debug("Attempting to retrieve AcoustID result from cache for Fingerprint: {Fingerprint}, Duration: {Duration}", fingerprint, duration);

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = @"
                    SELECT SerializedRecordingIdScoresJson, CachedAt
                    FROM AcoustIdCache
                    WHERE Fingerprint = @Fingerprint AND Duration = @Duration;";
                command.Parameters.AddWithValue("@Fingerprint", fingerprint);
                command.Parameters.AddWithValue("@Duration", duration);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var cachedAt = reader.GetDateTime(reader.GetOrdinal("CachedAt"));
                    if (CacheTTLDays > 0 && cachedAt < DateTime.UtcNow.AddDays(-CacheTTLDays))
                    {
                        Log.Information("Found stale AcoustID cache entry for Fingerprint: {Fingerprint}. Deleting.", fingerprint);
                        // Asynchronously delete the stale entry to not slow down the current request.
                        Task.Run(() => DeleteAcoustIdCacheEntry(fingerprint, duration));
                        return null; // Treat as cache miss
                    }

                    var json = reader.GetString(reader.GetOrdinal("SerializedRecordingIdScoresJson"));
                    var scores = JsonConvert.DeserializeObject<List<RecordingIdScore>>(json);
                    Log.Information("Cache hit for AcoustID result (Fingerprint: {Fingerprint})", fingerprint);
                    return new CachedAcoustIdResult
                    {
                        Fingerprint = fingerprint,
                        Duration = duration,
                        RecordingIdScores = scores ?? new List<RecordingIdScore>(),
                        CachedAt = cachedAt
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving AcoustID result from cache for Fingerprint: {Fingerprint}", fingerprint);
            }
            Log.Debug("Cache miss for AcoustID result (Fingerprint: {Fingerprint})", fingerprint);
            return null;
        }

        public void CacheAcoustIdResult(string fingerprint, int duration, LookupResponse acoustIdResponse)
        {
            if (_connection == null || acoustIdResponse?.Results == null) return;
            Log.Debug("Attempting to cache AcoustID result for Fingerprint: {Fingerprint}", fingerprint);

            var recordingIdScores = acoustIdResponse.Results
                .Where(r => r.Recordings != null && r.Recordings.Any())
                .SelectMany(r => r.Recordings.Select(rec => new RecordingIdScore { MBRecordingId = rec.Id.ToString(), Score = r.Score }))
                .ToList();

            if (!recordingIdScores.Any())
            {
                Log.Debug("No valid recording IDs to cache for AcoustID result (Fingerprint: {Fingerprint})", fingerprint);
                // Optionally, cache an "empty" result to prevent repeated lookups for known misses,
                // but be careful with expiry for these. For now, we only cache actual results.
                // return;
            }

            var cachedResult = new CachedAcoustIdResult
            {
                Fingerprint = fingerprint,
                Duration = duration,
                RecordingIdScores = recordingIdScores,
                CachedAt = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(cachedResult.RecordingIdScores);

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR REPLACE INTO AcoustIdCache (Fingerprint, Duration, SerializedRecordingIdScoresJson, CachedAt)
                    VALUES (@Fingerprint, @Duration, @Json, @CachedAt);";
                command.Parameters.AddWithValue("@Fingerprint", cachedResult.Fingerprint);
                command.Parameters.AddWithValue("@Duration", cachedResult.Duration);
                command.Parameters.AddWithValue("@Json", json);
                command.Parameters.AddWithValue("@CachedAt", cachedResult.CachedAt);
                command.ExecuteNonQuery();
                Log.Information("Successfully cached AcoustID result for Fingerprint: {Fingerprint}", fingerprint);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error caching AcoustID result for Fingerprint: {Fingerprint}", fingerprint);
            }
        }

        private void DeleteAcoustIdCacheEntry(string fingerprint, int duration)
        {
            if (_connection == null) return;
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM AcoustIdCache WHERE Fingerprint = @Fingerprint AND Duration = @Duration;";
                command.Parameters.AddWithValue("@Fingerprint", fingerprint);
                command.Parameters.AddWithValue("@Duration", duration);
                command.ExecuteNonQuery();
                Log.Information("Deleted AcoustID cache entry for Fingerprint: {Fingerprint}, Duration: {Duration}", fingerprint, duration);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting AcoustID cache entry for Fingerprint: {Fingerprint}", fingerprint);
            }
        }

        public CachedMusicBrainzRecording? GetCachedMusicBrainzRecording(string mbRecordingId)
        {
            if (_connection == null) return null;
            Guid guid;
            if (!Guid.TryParse(mbRecordingId, out guid))
            {
                Log.Warning("Attempted to get cached MusicBrainz recording with invalid MBID: {MBRecordingId}", mbRecordingId);
                return null;
            }
            string canonicalMbId = guid.ToString(); // Ensure consistent format
            Log.Debug("Attempting to retrieve MusicBrainz recording from cache for MBID: {MBRecordingId}", canonicalMbId);

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = @"
                    SELECT Title, ArtistCreditName, AllArtistNamesJson, AlbumTitle, ReleaseDate,
                           Disambiguation, IsrcsJson, UserRating, UserRatingCount,
                           ArtistCreditsJson, ReleasesJson, CachedAt
                    FROM MusicBrainzCache
                    WHERE MBRecordingId = @MBRecordingId;";
                command.Parameters.AddWithValue("@MBRecordingId", canonicalMbId);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var cachedAt = reader.GetDateTime(reader.GetOrdinal("CachedAt"));
                     if (CacheTTLDays > 0 && cachedAt < DateTime.UtcNow.AddDays(-CacheTTLDays))
                    {
                        Log.Information("Found stale MusicBrainz cache entry for MBID: {MBRecordingId}. Deleting.", canonicalMbId);
                        Task.Run(() => DeleteMusicBrainzCacheEntry(canonicalMbId));
                        return null; // Treat as cache miss
                    }

                    var cachedRecording = new CachedMusicBrainzRecording
                    {
                        MBRecordingId = canonicalMbId,
                        Title = reader.IsDBNull(reader.GetOrdinal("Title")) ? string.Empty : reader.GetString(reader.GetOrdinal("Title")),
                        ArtistCreditName = reader.IsDBNull(reader.GetOrdinal("ArtistCreditName")) ? string.Empty : reader.GetString(reader.GetOrdinal("ArtistCreditName")),
                        AllArtistNames = reader.IsDBNull(reader.GetOrdinal("AllArtistNamesJson")) ? null : JsonConvert.DeserializeObject<List<string>>(reader.GetString(reader.GetOrdinal("AllArtistNamesJson"))),
                        AlbumTitle = reader.IsDBNull(reader.GetOrdinal("AlbumTitle")) ? null : reader.GetString(reader.GetOrdinal("AlbumTitle")),
                        ReleaseDate = reader.IsDBNull(reader.GetOrdinal("ReleaseDate")) ? null : reader.GetString(reader.GetOrdinal("ReleaseDate")),
                        Disambiguation = reader.IsDBNull(reader.GetOrdinal("Disambiguation")) ? null : reader.GetString(reader.GetOrdinal("Disambiguation")),
                        Isrcs = reader.IsDBNull(reader.GetOrdinal("IsrcsJson")) ? null : JsonConvert.DeserializeObject<List<string>>(reader.GetString(reader.GetOrdinal("IsrcsJson"))),
                        UserRating = reader.IsDBNull(reader.GetOrdinal("UserRating")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("UserRating")),
                        UserRatingCount = reader.IsDBNull(reader.GetOrdinal("UserRatingCount")) ? (uint?)null : (uint)reader.GetInt64(reader.GetOrdinal("UserRatingCount")),
                        ArtistCredits = reader.IsDBNull(reader.GetOrdinal("ArtistCreditsJson")) ? null : JsonConvert.DeserializeObject<List<CachedArtistCredit>>(reader.GetString(reader.GetOrdinal("ArtistCreditsJson"))),
                        Releases = reader.IsDBNull(reader.GetOrdinal("ReleasesJson")) ? null : JsonConvert.DeserializeObject<List<CachedReleaseInfo>>(reader.GetString(reader.GetOrdinal("ReleasesJson"))),
                        CachedAt = cachedAt
                    };
                    Log.Information("Cache hit for MusicBrainz recording (MBID: {MBRecordingId})", canonicalMbId);
                    return cachedRecording;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving MusicBrainz recording from cache for MBID: {MBRecordingId}", canonicalMbId);
            }
            Log.Debug("Cache miss for MusicBrainz recording (MBID: {MBRecordingId})", canonicalMbId);
            return null;
        }

        public void CacheMusicBrainzRecording(IRecording musicBrainzRecording)
        {
            if (_connection == null || musicBrainzRecording == null) return;
            string canonicalMbId = musicBrainzRecording.Id.ToString(); // Ensure consistent format
            Log.Debug("Attempting to cache MusicBrainz recording for MBID: {MBRecordingId}", canonicalMbId);

            var cachedRecording = new CachedMusicBrainzRecording
            {
                MBRecordingId = canonicalMbId,
                Title = musicBrainzRecording.Title ?? string.Empty,
                ArtistCreditName = musicBrainzRecording.ArtistCredit?.FirstOrDefault()?.Name ?? string.Empty,
                AllArtistNames = musicBrainzRecording.ArtistCredit?.Select(ac => ac.ToString() ?? string.Empty).ToList(), // Captures full credit string like "Artist A feat. Artist B"
                AlbumTitle = musicBrainzRecording.Releases?.FirstOrDefault()?.Title, // Simplified: first release's title
                ReleaseDate = musicBrainzRecording.Releases?.FirstOrDefault()?.Date?.ToString(), // Simplified
                Disambiguation = musicBrainzRecording.Disambiguation,
                Isrcs = musicBrainzRecording.Isrcs?.Select(i => i.Id).ToList(),
                UserRating = musicBrainzRecording.Rating?.Value,
                UserRatingCount = musicBrainzRecording.Rating?.VoteCount,
                CachedAt = DateTime.UtcNow
            };

            // Populate CachedArtistCredit
            if (musicBrainzRecording.ArtistCredit != null)
            {
                cachedRecording.ArtistCredits = musicBrainzRecording.ArtistCredit.Select(ac => new CachedArtistCredit
                {
                    Name = ac.Name ?? string.Empty,
                    JoinPhrase = ac.JoinPhrase ?? string.Empty
                }).ToList();
            }

            // Populate CachedReleaseInfo (simplified)
            if (musicBrainzRecording.Releases != null)
            {
                cachedRecording.Releases = musicBrainzRecording.Releases.Select(r => new CachedReleaseInfo
                {
                    MBId = r.Id.ToString(),
                    Title = r.Title ?? string.Empty,
                    ReleaseDate = r.Date?.ToString(),
                    Status = r.Status,
                    Barcode = r.Barcode,
                    CountryCode = r.Country,
                    Media = r.Media?.Select(m => new CachedMedium
                    {
                        Format = m.Format,
                        TrackCount = m.TrackCount,
                        Tracks = m.Tracks?.Select(t => new CachedTrack
                        {
                            MBId = t.Id.ToString(),
                            Title = t.Title ?? string.Empty,
                            Number = t.Number ?? string.Empty,
                            Length = (int)(t.Length?.TotalMilliseconds ?? 0),
                            RecordingMBId = t.Recording?.Id.ToString()
                        }).ToList()
                    }).ToList()
                }).ToList();
            }


            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR REPLACE INTO MusicBrainzCache (
                        MBRecordingId, Title, ArtistCreditName, AllArtistNamesJson, AlbumTitle, ReleaseDate,
                        Disambiguation, IsrcsJson, UserRating, UserRatingCount,
                        ArtistCreditsJson, ReleasesJson, CachedAt
                    ) VALUES (
                        @MBRecordingId, @Title, @ArtistCreditName, @AllArtistNamesJson, @AlbumTitle, @ReleaseDate,
                        @Disambiguation, @IsrcsJson, @UserRating, @UserRatingCount,
                        @ArtistCreditsJson, @ReleasesJson, @CachedAt
                    );";
                command.Parameters.AddWithValue("@MBRecordingId", cachedRecording.MBRecordingId);
                command.Parameters.AddWithValue("@Title", cachedRecording.Title);
                command.Parameters.AddWithValue("@ArtistCreditName", cachedRecording.ArtistCreditName);
                command.Parameters.AddWithValue("@AllArtistNamesJson", JsonConvert.SerializeObject(cachedRecording.AllArtistNames));
                command.Parameters.AddWithValue("@AlbumTitle", (object)cachedRecording.AlbumTitle ?? DBNull.Value);
                command.Parameters.AddWithValue("@ReleaseDate", (object)cachedRecording.ReleaseDate ?? DBNull.Value);
                command.Parameters.AddWithValue("@Disambiguation", (object)cachedRecording.Disambiguation ?? DBNull.Value);
                command.Parameters.AddWithValue("@IsrcsJson", JsonConvert.SerializeObject(cachedRecording.Isrcs));
                command.Parameters.AddWithValue("@UserRating", (object)cachedRecording.UserRating ?? DBNull.Value);
                command.Parameters.AddWithValue("@UserRatingCount", (object)cachedRecording.UserRatingCount ?? DBNull.Value);
                command.Parameters.AddWithValue("@ArtistCreditsJson", JsonConvert.SerializeObject(cachedRecording.ArtistCredits));
                command.Parameters.AddWithValue("@ReleasesJson", JsonConvert.SerializeObject(cachedRecording.Releases));
                command.Parameters.AddWithValue("@CachedAt", cachedRecording.CachedAt);

                command.ExecuteNonQuery();
                Log.Information("Successfully cached MusicBrainz recording for MBID: {MBRecordingId}", canonicalMbId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error caching MusicBrainz recording for MBID: {MBRecordingId}", canonicalMbId);
            }
        }

        private void DeleteMusicBrainzCacheEntry(string mbRecordingId)
        {
            if (_connection == null) return;
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM MusicBrainzCache WHERE MBRecordingId = @MBRecordingId;";
                command.Parameters.AddWithValue("@MBRecordingId", mbRecordingId);
                command.ExecuteNonQuery();
                Log.Information("Deleted MusicBrainz cache entry for MBID: {MBRecordingId}", mbRecordingId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error deleting MusicBrainz cache entry for MBID: {MBRecordingId}", mbRecordingId);
            }
        }

        public void Dispose()
        {
            Log.Information("Disposing CacheManager and closing SQLite connection.");
            _connection?.Close();
            _connection?.Dispose();
            // SqliteConnection.ClearAllPools(); // Might be useful if issues with file locking persist
        }
    }
}
