using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.MySqlImport
{
    public static class MySqlImportPreflightScanner
    {
        private const int MaxBatchBytes = 32 * 1024 * 1024; // 32 MiB
        private static readonly Regex ManifestTableRegex = new Regex(@"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestTablesRegex = new Regex(@"^--\s*Tables:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestRowsRegex = new Regex(@"^--\s*TotalRows:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestSha256Regex = new Regex(@"^--\s*Manifest-SHA256:\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static MySqlImportPreflight Scan(string filePath, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required", nameof(filePath));
            if (!File.Exists(filePath))
                return new MySqlImportPreflight(false, 0, 0, false, 0, 0, null, new[] { "File not found: " + filePath });

            long fileBytes = new FileInfo(filePath).Length;
            var issues = new List<string>();
            int statementCount = 0;
            bool hasManifest = false;
            int manifestTables = 0;
            long manifestRows = 0;
            string manifestSha = null;

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, new UTF8Encoding(false, true), true))
                {
                    foreach (var statement in MySqlScriptReader.Read(reader, cancellation, comment =>
                    {
                        if (comment.Contains("mySQLPunk Native Manifest")) hasManifest = true;
                        if (!hasManifest) return;
                        var tables = ManifestTablesRegex.Match(comment);
                        if (tables.Success && int.TryParse(tables.Groups[1].Value, out int mt)) manifestTables = mt;
                        var rows = ManifestRowsRegex.Match(comment);
                        if (rows.Success && long.TryParse(rows.Groups[1].Value, out long mr)) manifestRows = mr;
                        var sha = ManifestSha256Regex.Match(comment);
                        if (sha.Success) manifestSha = sha.Groups[1].Value;
                    })) statementCount++;
                    if (manifestSha != null && !VerifyHash(filePath, manifestSha, cancellation))
                        issues.Add("Native manifest SHA-256 does not match the SQL file contents.");
                }
            }
            catch (OperationCanceledException)
            {
                return new MySqlImportPreflight(false, fileBytes, statementCount, hasManifest, manifestTables, manifestRows, manifestSha, new[] { "Scan cancelled by user." });
            }
            catch (Exception ex)
            {
                issues.Add("Error during preflight scan: " + ex.Message);
            }

            bool passed = issues.Count == 0;
            return new MySqlImportPreflight(
                passed,
                fileBytes,
                statementCount,
                hasManifest,
                manifestTables,
                manifestRows,
                manifestSha,
                issues);
        }
        private static bool VerifyHash(string path, string expected, CancellationToken cancellation)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                int tailSize = (int)Math.Min(1024, stream.Length);
                stream.Position = stream.Length - tailSize;
                var tail = new byte[tailSize];
                int read = stream.Read(tail, 0, tail.Length);
                byte[] marker = Encoding.ASCII.GetBytes("-- Manifest-SHA256: " + expected);
                int index = -1;
                for (int i = read - marker.Length; i >= 0; i--)
                {
                    bool match = true;
                    for (int j = 0; j < marker.Length; j++)
                        if (char.ToUpperInvariant((char)tail[i + j]) != char.ToUpperInvariant((char)marker[j])) { match = false; break; }
                    if (match) { index = i; break; }
                }
                if (index < 0 || (index > 0 && tail[index - 1] != 10 && tail[index - 1] != 13)) return false;
                for (int i = index + marker.Length; i < read; i++)
                    if (tail[i] != 10 && tail[i] != 13 && tail[i] != 32 && tail[i] != 9) return false;
                long remaining = stream.Length - tailSize + index;
                stream.Position = 0;
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) return false;
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    remaining -= count;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Equals(BitConverter.ToString(sha.Hash).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

    }
}
