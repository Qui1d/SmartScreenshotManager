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
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
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
            insert.ExecuteNonQuery();

            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id, FilePath, FileName, CreatedAt, AddedAt, OcrText,
                       Description, Category, Tags, IsFavorite, IsProcessed
                FROM Screenshots WHERE FilePath = $path;
                """;
            select.Parameters.AddWithValue("$path", filePath);
            using var reader = select.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Screenshot was not saved.");
            return new ScreenshotItem
            {
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
                IsProcessed = reader.GetBoolean(10)
            };
        }

        private static DateTime ParseDate(string value) =>
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();

        // Explicit metadata updates keep folder scans from overwriting user/AI data.
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
