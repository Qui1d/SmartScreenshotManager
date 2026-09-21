using Microsoft.Data.Sqlite;
using SmartScreenshotManager.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SmartScreenshotManager.Data
{
    // Synchronous SQLite operations; callers run them on a worker thread.
    public sealed class ScreenshotRepository
    {
        private readonly string _connectionString;
        private readonly object _gate = new();
        private bool _initialized;

        public ScreenshotRepository(string databasePath)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                DefaultTimeout = 5
            }.ToString();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            try
            {
                connection.Open();
                // Match Windows paths, including non-ASCII names, consistently.
                connection.CreateCollation("PATH", (a, b) =>
                    StringComparer.OrdinalIgnoreCase.Compare(a, b));
                if (_initialized) return connection;
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS Screenshots (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FilePath TEXT NOT NULL COLLATE PATH UNIQUE,
                        FolderPath TEXT NOT NULL COLLATE PATH,
                        FileName TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        AddedAt TEXT NOT NULL,
                        OcrText TEXT NULL,
                        Description TEXT NULL,
                        Category TEXT NULL,
                        Tags TEXT NULL,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsProcessed INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS IX_Screenshots_FolderPath
                        ON Screenshots(FolderPath);
                    """;
                command.ExecuteNonQuery();
                using var migration = connection.BeginTransaction();
                command.Transaction = migration;
                command.CommandText = "PRAGMA user_version";
                long version = Convert.ToInt64(command.ExecuteScalar());
                if (version > 5) throw new InvalidOperationException("This database requires a newer app version.");
                if (version < 2)
                {
                    command.CommandText = """
                        ALTER TABLE Screenshots ADD COLUMN OcrStatus TEXT NOT NULL DEFAULT 'Pending';
                        ALTER TABLE Screenshots ADD COLUMN OcrError TEXT NULL;
                        UPDATE Screenshots SET OcrStatus = 'Processed' WHERE IsProcessed = 1;
                        PRAGMA user_version = 2;
                        """;
                    command.ExecuteNonQuery();
                }
                if (version < 3)
                {
                    command.CommandText = """
                        ALTER TABLE Screenshots ADD COLUMN CategoryIsManual INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE Screenshots ADD COLUMN AiStatus TEXT NOT NULL DEFAULT 'NotProcessed';
                        ALTER TABLE Screenshots ADD COLUMN AiError TEXT NULL;
                        UPDATE Screenshots SET CategoryIsManual = 1
                            WHERE Category IS NOT NULL AND trim(Category) <> '';
                        PRAGMA user_version = 3;
                        """;
                    command.ExecuteNonQuery();
                }
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS AiRequests (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Day TEXT NOT NULL,
                        Model TEXT NOT NULL,
                        InputTokens INTEGER NULL,
                        OutputTokens INTEGER NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_AiRequests_Day ON AiRequests(Day);

                    """;
                command.ExecuteNonQuery();
                if (version < 5)
                {
                    command.CommandText = """
                        ALTER TABLE Screenshots ADD COLUMN OcrDurationMs INTEGER NULL;
                        ALTER TABLE Screenshots ADD COLUMN AiDurationMs INTEGER NULL;
                        ALTER TABLE Screenshots ADD COLUMN OcrFinishedAt TEXT NULL;
                        ALTER TABLE Screenshots ADD COLUMN AiFinishedAt TEXT NULL;
                        PRAGMA user_version = 5;
                        """;
                    command.ExecuteNonQuery();
                }
                // Interrupted paid requests are not resent automatically on restart.
                command.CommandText = """
                    UPDATE Screenshots SET AiStatus = 'Cancelled',
                        AiError = 'Previous analysis was interrupted. Run AI analysis again if needed.'
                    WHERE AiStatus IN ('Pending', 'Processing');
                    """;
                command.ExecuteNonQuery();
                migration.Commit();
                _initialized = true;
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        // Reserve atomically before sending. Failed/unknown requests also consume the local limit.
        public void SaveAttemptTiming(int id, bool ai, long durationMs)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = ai
                    ? "UPDATE Screenshots SET AiDurationMs=$ms,AiFinishedAt=$at WHERE Id=$id"
                    : "UPDATE Screenshots SET OcrDurationMs=$ms,OcrFinishedAt=$at WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$ms", Math.Max(0, durationMs));
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }

        public ActivitySnapshot GetActivity(string folder, bool failuresOnly)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*),
                        COALESCE(SUM(OcrStatus='Processed'),0), COALESCE(SUM(OcrStatus='Failed'),0),
                        COALESCE(SUM(AiStatus='Processed'),0), COALESCE(SUM(AiStatus='Failed'),0),
                        COALESCE(SUM(AiStatus='Cancelled'),0)
                    FROM Screenshots WHERE FolderPath=$folder;
                    """;
                command.Parameters.AddWithValue("$folder", folder);
                string summary;
                long total;
                using (var reader = command.ExecuteReader())
                {
                    reader.Read();
                    total = reader.GetInt64(0);
                    summary = $"Saved results in this folder: OCR {reader.GetInt64(1)} processed / {reader.GetInt64(2)} failed · AI {reader.GetInt64(3)} processed / {reader.GetInt64(4)} failed / {reader.GetInt64(5)} cancelled";
                }
                command.CommandText = """
                    SELECT Id,FileName,FilePath,OcrStatus,AiStatus,OcrError,AiError,
                        OcrDurationMs,AiDurationMs,OcrFinishedAt,AiFinishedAt
                    FROM Screenshots WHERE FolderPath=$folder
                        AND ($failures=0 OR OcrStatus='Failed' OR AiStatus IN ('Failed','Cancelled'))
                    ORDER BY CASE
                        WHEN OcrStatus='Processing' OR AiStatus='Processing' THEN 0
                        WHEN OcrStatus='Failed' OR AiStatus IN ('Failed','Cancelled') THEN 1
                        WHEN OcrStatus='Pending' THEN 2 ELSE 3 END,
                        AddedAt DESC, Id DESC LIMIT 200;
                    """;
                command.Parameters.AddWithValue("$failures", failuresOnly ? 1 : 0);
                var items = new List<ActivityItem>();
                using (var reader = command.ExecuteReader())
                {
                    string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                    long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                    while (reader.Read()) items.Add(new ActivityItem(reader.GetInt32(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3), reader.GetString(4), Text(5), Text(6),
                        Number(7), Number(8), Text(9), Text(10)));
                }
                return new ActivitySnapshot(items, summary, total);
            }
        }

        public long ReserveAiRequest(int limit, string model)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO AiRequests(Day, Model)
                    SELECT $day, $model
                    WHERE (SELECT COUNT(*) FROM AiRequests WHERE Day = $day) < $limit;
                    """;
                command.Parameters.AddWithValue("$day", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$model", model);
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 0, 10000));
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Daily AI request limit reached. Change the limit in Settings or wait until 00:00 UTC.");
                command.CommandText = "SELECT last_insert_rowid()";
                long id = Convert.ToInt64(command.ExecuteScalar());
                transaction.Commit();
                return id;
            }
        }

        public void RecordAiUsage(long requestId, long input, long output)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE AiRequests SET InputTokens=$input, OutputTokens=$output WHERE Id=$id";
                command.Parameters.AddWithValue("$id", requestId);
                command.Parameters.AddWithValue("$input", input);
                command.Parameters.AddWithValue("$output", output);
                command.ExecuteNonQuery();
            }
        }

        public string GetAiUsageSummary()
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*), COALESCE(SUM(InputTokens),0), COALESCE(SUM(OutputTokens),0),
                        COALESCE(SUM(CASE WHEN InputTokens IS NULL THEN 1 ELSE 0 END),0)
                    FROM AiRequests WHERE Day=$day;
                    """;
                command.Parameters.AddWithValue("$day", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                using var reader = command.ExecuteReader();
                reader.Read();
                return $"Today (UTC): {reader.GetInt64(0)} requests\nInput: {reader.GetInt64(1):N0} tokens · Output: {reader.GetInt64(2):N0} tokens\nRequests without usage data: {reader.GetInt64(3)}";
            }
        }

        public static bool IsSupportedImage(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        public List<ScreenshotItem> SynchronizeFolder(string folderPath)
        {
            lock (_gate)
            {
                folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
                // Enumerate completely before changing the database. An inaccessible folder
                // must not be interpreted as an empty folder and erase its metadata.
                var files = Directory.GetFiles(folderPath)
                    .Where(IsSupportedImage).ToArray();
                var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var result = new List<ScreenshotItem>();
                foreach (string file in files)
                {
                    if (!File.Exists(file)) continue;
                    // For the first import, use creation time to preserve the old gallery order.
                    result.Add(GetOrAdd(connection, transaction, file, File.GetCreationTimeUtc(file)));
                }

                using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT FilePath FROM Screenshots WHERE FolderPath = $folder";
                select.Parameters.AddWithValue("$folder", folderPath);
                var stale = new List<string>();
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                        if (!present.Contains(reader.GetString(0))) stale.Add(reader.GetString(0));
                }
                foreach (string file in stale)
                    Delete(connection, transaction, file);
                transaction.Commit();
                return result;
            }
        }

        public ScreenshotItem GetOrAdd(string filePath)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var result = GetOrAdd(connection, transaction, Path.GetFullPath(filePath), DateTime.UtcNow);
                transaction.Commit();
                return result;
            }
        }

        private static ScreenshotItem GetOrAdd(SqliteConnection connection,
            SqliteTransaction transaction, string filePath, DateTime addedAt)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Screenshots (FilePath, FolderPath, FileName, CreatedAt, AddedAt)
                VALUES ($path, $folder, $name, $created, $added)
                ON CONFLICT(FilePath) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$path", filePath);
            insert.Parameters.AddWithValue("$folder", Path.GetDirectoryName(filePath)!);
            insert.Parameters.AddWithValue("$name", Path.GetFileName(filePath));
            insert.Parameters.AddWithValue("$created", File.GetCreationTimeUtc(filePath).ToString("O"));
            insert.Parameters.AddWithValue("$added", addedAt.ToUniversalTime().ToString("O"));
            bool newlyImported = insert.ExecuteNonQuery() == 1;

            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id, FilePath, FileName, CreatedAt, AddedAt, OcrText,
                       Description, Category, Tags, IsFavorite, IsProcessed, OcrStatus, OcrError, CategoryIsManual, AiStatus, AiError
                FROM Screenshots WHERE FilePath = $path;
                """;
            select.Parameters.AddWithValue("$path", filePath);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Screenshot was not saved.");
            return new ScreenshotItem
            {
                WasAddedToLibrary = newlyImported,
                Id = reader.GetInt32(0),
                FilePath = reader.GetString(1),
                FileName = reader.GetString(2),
                CreatedAt = ParseDate(reader.GetString(3)),
                AddedAt = ParseDate(reader.GetString(4)),
                OcrText = reader.IsDBNull(5) ? null : reader.GetString(5),
                Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                Category = reader.IsDBNull(7) ? null : reader.GetString(7),
                Tags = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsFavorite = reader.GetBoolean(9),
                IsProcessed = reader.GetBoolean(10),
                OcrStatus = reader.GetString(11),
                OcrError = reader.IsDBNull(12) ? null : reader.GetString(12),
                CategoryIsManual = reader.GetBoolean(13),
                AiStatus = reader.GetString(14),
                AiError = reader.IsDBNull(15) ? null : reader.GetString(15)
            };
        }

        private static DateTime ParseDate(string value) =>
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();

        public OcrJobState? GetOcrState(int id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, FilePath, OcrStatus, OcrText, OcrError FROM Screenshots WHERE Id = $id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() ? new OcrJobState(reader.GetInt32(0), reader.GetString(1),
                    reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
            }
        }

        public bool SaveOcrState(int id, string expectedPath, string status, string? text, string? error)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE Screenshots SET OcrStatus = $status, OcrText = $text,
                        OcrError = $error, IsProcessed = $processed
                    WHERE Id = $id AND FilePath = $path;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", expectedPath);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
                command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                command.Parameters.AddWithValue("$processed", status == "Processed");
                return command.ExecuteNonQuery() == 1;
            }
        }

        // Explicit metadata updates keep folder scans from overwriting user/AI data.
        public void SetCategory(int id, string? category)
        {
            if (category != null && category != "Gaming" && category != "Programming"
                && category != "Documents" && category != "Other")
                throw new ArgumentException("Unknown screenshot category.", nameof(category));
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Screenshots SET Category = $category, CategoryIsManual = 1 WHERE Id = $id";
                command.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Screenshot no longer exists in the library.");
            }
        }

        public void AllowAiCategory(int id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Screenshots SET CategoryIsManual = 0 WHERE Id = $id";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Screenshot no longer exists in the library.");
            }
        }

        public SemanticDocument? GetSemanticDocument(int id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id,FilePath,FileName,Category,Description,Tags,OcrText FROM Screenshots WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                string? Value(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                return SemanticDocument.Create(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    Value(3), Value(4), Value(5), Value(6));
            }
        }

        public AiJobState? GetAiState(int id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id, FilePath, AiStatus, AiError, Description, Tags, Category, CategoryIsManual, OcrText
                    FROM Screenshots WHERE Id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() ? new AiJobState(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)) : null;
            }
        }

        public bool SaveAiStatus(int id, string path, string status, string? error)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Screenshots SET AiStatus = $status, AiError = $error WHERE Id = $id AND FilePath = $path";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public bool SaveAiResult(int id, string path, AiAnalysis result)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE Screenshots SET Description = $description, Tags = $tags,
                        Category = CASE WHEN CategoryIsManual = 1 THEN Category ELSE $category END,
                        AiStatus = 'Processed', AiError = NULL
                    WHERE Id = $id AND FilePath = $path;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$description", result.Description);
                command.Parameters.AddWithValue("$tags", string.Join(", ", result.Tags));
                command.Parameters.AddWithValue("$category", result.Category);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public void SetFavorite(int id, bool isFavorite)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Screenshots SET IsFavorite = $favorite WHERE Id = $id";
                command.Parameters.AddWithValue("$favorite", isFavorite);
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Screenshot no longer exists in the library.");
            }
        }

        public void UpdateMetadata(ScreenshotItem item)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE Screenshots SET OcrText = $ocr, Description = $description,
                        Category = $category, Tags = $tags, IsFavorite = $favorite,
                        IsProcessed = $processed
                    WHERE Id = $id;
                    """;
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$ocr", (object?)item.OcrText ?? DBNull.Value);
                command.Parameters.AddWithValue("$description", (object?)item.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("$category", (object?)item.Category ?? DBNull.Value);
                command.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
                command.Parameters.AddWithValue("$favorite", item.IsFavorite);
                command.Parameters.AddWithValue("$processed", item.IsProcessed);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Screenshot no longer exists in the library.");
            }
        }

        public void Delete(string filePath)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Delete(connection, transaction, Path.GetFullPath(filePath));
                transaction.Commit();
            }
        }

        private static void Delete(SqliteConnection connection, SqliteTransaction transaction, string path)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Screenshots WHERE FilePath = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        public void Rename(string oldPath, string newPath)
        {
            lock (_gate)
            {
                oldPath = Path.GetFullPath(oldPath);
                newPath = Path.GetFullPath(newPath);
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // A destination may already have been observed by a duplicate watcher event.
                // Only replace it when there is an original record whose metadata we can retain.
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM Screenshots WHERE FilePath = $path";
                exists.Parameters.AddWithValue("$path", oldPath);
                if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(oldPath, newPath))
                        Delete(connection, transaction, newPath);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        UPDATE Screenshots SET FilePath = $new, FolderPath = $folder, FileName = $name
                        WHERE FilePath = $old;
                        """;
                    command.Parameters.AddWithValue("$old", oldPath);
                    command.Parameters.AddWithValue("$new", newPath);
                    command.Parameters.AddWithValue("$folder", Path.GetDirectoryName(newPath)!);
                    command.Parameters.AddWithValue("$name", Path.GetFileName(newPath));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
    }
}
