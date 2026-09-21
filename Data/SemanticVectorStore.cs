using Microsoft.Data.Sqlite;
using SmartScreenshotManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SmartScreenshotManager.Data
{
    // Separate, rebuildable database. A missing extension cannot break the screenshot library.
    public sealed class SemanticVectorStore
    {
        public const int Dimensions = 512;
        public const string Model = "text-embedding-3-small";
        private readonly string _connectionString;
        private readonly object _gate = new();
        public SemanticVectorStore(string path) => _connectionString = new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString();

        private SqliteConnection Open()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("Semantic search currently requires an x64 build.");
            string extension = Path.Combine(AppContext.BaseDirectory, "vec0.dll");
            if (!File.Exists(extension))
                extension = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll");
            if (!File.Exists(extension))
                throw new InvalidOperationException("Vector extension not found. Restore NuGet packages and rebuild for x64.");
            var connection = new SqliteConnection(_connectionString);
            try
            {
                connection.Open();
                connection.LoadExtension(extension, "sqlite3_vec_init");
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS Documents(Id INTEGER PRIMARY KEY, Fingerprint TEXT NOT NULL);
                    CREATE VIRTUAL TABLE IF NOT EXISTS Vectors USING vec0(
                        Id INTEGER PRIMARY KEY, Embedding float[512] distance_metric=cosine);
                    """;
                command.ExecuteNonQuery();
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        public HashSet<int> GetCurrentIds(IReadOnlyList<SemanticDocument> documents)
        {
            lock (_gate)
            {
                using var connection = Open();
                var result = new HashSet<int>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Fingerprint FROM Documents WHERE Id=$id";
                var id = command.Parameters.Add("$id", SqliteType.Integer);
                foreach (var document in documents)
                {
                    id.Value = document.Id;
                    if (command.ExecuteScalar() is string hash && hash == document.Fingerprint) result.Add(document.Id);
                }
                return result;
            }
        }

        public void Save(SemanticDocument document, float[] vector)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM Vectors WHERE Id=$id;
                    INSERT INTO Vectors(Id, Embedding) VALUES($id, $vector);
                    INSERT INTO Documents(Id, Fingerprint) VALUES($id, $hash)
                        ON CONFLICT(Id) DO UPDATE SET Fingerprint=excluded.Fingerprint;
                    """;
                command.Parameters.AddWithValue("$id", document.Id);
                command.Parameters.AddWithValue("$vector", ToBytes(vector));
                command.Parameters.AddWithValue("$hash", document.Fingerprint);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        public Dictionary<int, double> Search(float[] vector, IReadOnlyList<SemanticDocument> eligible)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TEMP TABLE Eligible(Id INTEGER PRIMARY KEY, Fingerprint TEXT NOT NULL)";
                command.ExecuteNonQuery();
                using (var transaction = connection.BeginTransaction())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO Eligible VALUES($id, $hash)";
                    var id = command.Parameters.Add("$id", SqliteType.Integer);
                    var hash = command.Parameters.Add("$hash", SqliteType.Text);
                    foreach (var item in eligible)
                    {
                        id.Value = item.Id; hash.Value = item.Fingerprint;
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                command.Transaction = null;
                command.Parameters.Clear();
                // Exact vector search within the active folder/filter, excluding stale metadata.
                command.CommandText = """
                    SELECT v.Id, vec_distance_cosine(v.Embedding, $vector) AS Distance
                    FROM Vectors v JOIN Documents d ON d.Id=v.Id
                    JOIN Eligible e ON e.Id=v.Id AND e.Fingerprint=d.Fingerprint
                    ORDER BY Distance, v.Id LIMIT 20;
                    """;
                command.Parameters.AddWithValue("$vector", ToBytes(vector));
                var results = new Dictionary<int, double>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) results.Add(reader.GetInt32(0), reader.GetDouble(1));
                return results;
            }
        }

        private static byte[] ToBytes(float[] vector)
        {
            if (vector.Length != Dimensions) throw new InvalidOperationException("Unexpected embedding dimensions.");
            var bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
