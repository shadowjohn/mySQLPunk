using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.SqliteImport
{
    // Preflight and execution use the same bounded lexer. It retains the original
    // characters (including literal CR/LF), and treats a trigger body as one statement.
    internal sealed class SqliteStatementReader : IDisposable
    {
        internal const int MaxStatementBytes = 32 * 1024 * 1024;
        private readonly StreamReader reader;
        private readonly CancellationToken cancellation;
        private readonly Action<string> comment;
        private int line = 1;
        private bool first = true;
        internal int StatementLine { get; private set; }
        internal SqliteStatementReader(string path, CancellationToken cancellation, Action<string> comment = null)
        {
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536),
                new UTF8Encoding(false, true), false, 65536);
            this.cancellation = cancellation;
            this.comment = comment;
        }
        internal string Read()
        {
            var sql = new StringBuilder();
            var word = new StringBuilder();
            var lineComment = new StringBuilder();
            int quote = 0, bytes = 0, caseDepth = 0, prefixCount = 0;
            bool block = false, inline = false, meaningful = false, create = false, trigger = false, body = false, end = false;
            StatementLine = line;
            Action flushWord = () =>
            {
                if (word.Length == 0) return;
                string token = word.ToString().ToUpperInvariant();
                word.Clear();
                if (prefixCount == 0) create = token == "CREATE";
                if (create && prefixCount < 4 && token == "TRIGGER") trigger = true;
                prefixCount++;
                if (trigger)
                {
                    if (token == "BEGIN" && !body) body = true;
                    else if (body && token == "CASE") { caseDepth++; end = false; }
                    else if (body && token == "END")
                    {
                        if (caseDepth > 0) { caseDepth--; end = false; }
                        else end = true;
                    }
                    else end = false;
                }
            };
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int next = reader.Read();
                if (next < 0)
                {
                    flushWord();
                    if (bytes > MaxStatementBytes) throw new InvalidDataException("Single SQL statement exceeds safe limit of 32 MiB.");
                    if (inline && comment != null) comment(lineComment.ToString());
                    if (quote != 0 || block || (trigger && !end))
                        throw new InvalidDataException("Unterminated SQLite quote, comment or trigger at line " + StatementLine + ".");
                    return meaningful ? sql.ToString() : null;
                }
                char c = (char)next;
                if (first) { first = false; if (c == '\uFEFF') continue; }
                if (c == '\0') throw new InvalidDataException("Line " + line + ": Contains Embedded NUL character; use CAST(X'...' AS TEXT).");
                // UTF-8 decoding is strict. Count surrogate pairs as four bytes.
                bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsHighSurrogate(c) ? 2 : char.IsLowSurrogate(c) ? 2 : 3;
                if (bytes > MaxStatementBytes) throw new InvalidDataException("Line " + StatementLine + ": Single SQL statement exceeds safe limit of 32 MiB.");
                sql.Append(c);
                if (c == '\n') line++;
                if (inline)
                {
                    if (c == '\r' || c == '\n')
                    {
                        inline = false;
                        if (comment != null) comment(lineComment.ToString());
                        lineComment.Clear();
                    }
                    else if (lineComment.Length < 1024 * 1024) lineComment.Append(c);
                    else throw new InvalidDataException("Native comment metadata exceeds the safe 1 MiB limit.");
                    continue;
                }
                if (block)
                {
                    if (c == '*' && reader.Peek() == '/') { sql.Append((char)reader.Read()); bytes++; block = false; }
                    continue;
                }
                if (quote != 0)
                {
                    char closer = quote == '[' ? ']' : (char)quote;
                    if (c == closer)
                    {
                        if (quote != '[' && reader.Peek() == closer) { sql.Append((char)reader.Read()); bytes += closer < 128 ? 1 : 3; }
                        else quote = 0;
                    }
                    continue;
                }
                if (c == '-' && reader.Peek() == '-')
                {
                    flushWord(); sql.Append((char)reader.Read()); bytes++; inline = true; lineComment.Append("--"); continue;
                }
                if (c == '/' && reader.Peek() == '*')
                {
                    flushWord(); sql.Append((char)reader.Read()); bytes++; block = true; continue;
                }
                if (c == '\'' || c == '"' || c == '`' || c == '[')
                {
                    flushWord(); quote = c; meaningful = true; end = false; continue;
                }
                if (char.IsLetterOrDigit(c) || c == '_') { if (word.Length < 64) word.Append(c); meaningful = true; continue; }
                flushWord();
                if (c == ';')
                {
                    if (meaningful && (!trigger || (body && end))) { if (bytes > MaxStatementBytes) throw new InvalidDataException("Single SQL statement exceeds safe limit of 32 MiB."); return sql.ToString(); }
                }
                else if (!char.IsWhiteSpace(c)) { meaningful = true; end = false; }
            }
        }
        internal static string ExecutableSql(string sql)
        {
            int i = 0;
            while (i < sql.Length)
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
                {
                    i += 2; while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++; continue;
                }
                if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                {
                    int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return "";
                    i = end + 2; continue;
                }
                break;
            }
            return sql.Substring(i).Trim();
        }
        internal static string PolicySql(string sql)
        {
            var result = new StringBuilder(sql.Length);
            int quote = 0;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (quote != 0)
                {
                    result.Append(c);
                    char closer = quote == '[' ? ']' : (char)quote;
                    if (c == closer)
                    {
                        if (quote != '[' && i + 1 < sql.Length && sql[i + 1] == closer) result.Append(sql[++i]);
                        else quote = 0;
                    }
                }
                else if (c == '\'' || c == '"' || c == '`' || c == '[') { quote = c; result.Append(c); }
                else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    result.Append(' '); i += 2;
                    while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++;
                    if (i < sql.Length) result.Append(sql[i]);
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    result.Append(' '); int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 1;
                }
                else result.Append(c);
            }
            return result.ToString().Trim();
        }
        public void Dispose() { reader.Dispose(); }
    }

    public static class SqliteImportPreflightScanner
    {
        internal static readonly Regex ManifestTableRegex = new Regex(@"^--\s*Manifest-Table(?<base64>64)?:\s*(?<name>.*?)\s*\|\s*Rows:\s*(?<rows>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ManifestShaRegex = new Regex(@"^--\s*Manifest-(?:Payload-)?SHA256:\s*([a-fA-F0-9]{64})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] SpatialKeywords = { "InitSpatialMetaData", "AddGeometryColumn", "CreateSpatialIndex", "mod_spatialite", "spatial_ref_sys", "geometry_columns", "gpkg_contents", "GeomFromText", "ST_GeomFromText" };

        internal static Tuple<string, long> ParseTableManifest(string comment)
        {
            var match = ManifestTableRegex.Match(comment);
            if (!match.Success) return null;
            string name = match.Groups["name"].Value;
            if (match.Groups["base64"].Success) name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(name));
            return Tuple.Create(name, long.Parse(match.Groups["rows"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        public static SqliteImportPreflight Scan(string filePath, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required", nameof(filePath));
            if (!File.Exists(filePath)) return new SqliteImportPreflight(false, 0, 0, false, 0, 0, null, false, new[] { "File not found: " + filePath });
            long bytes = new FileInfo(filePath).Length, rows = 0;
            int count = 0, tables = 0;
            bool manifest = false, spatial = false;
            string checksum = null;
            var issues = new List<string>();
            try
            {
                Action<string> onComment = text =>
                {
                    if (text.IndexOf("mySQLPunk Native Manifest", StringComparison.OrdinalIgnoreCase) >= 0) manifest = true;
                    if (!manifest) return;
                    var t = Regex.Match(text, @"^--\s*Tables:\s*(\d+)", RegexOptions.IgnoreCase);
                    var r = Regex.Match(text, @"^--\s*TotalRows:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (t.Success) tables = int.Parse(t.Groups[1].Value);
                    if (r.Success) rows = long.Parse(r.Groups[1].Value);
                    ParseTableManifest(text); // Validate encoded names too.
                    var h = ManifestShaRegex.Match(text);
                    if (h.Success) { if (checksum != null) throw new InvalidDataException("Multiple checksum footers are not supported."); checksum = h.Groups[1].Value; }
                };
                using (var reader = new SqliteStatementReader(filePath, cancellation, onComment))
                {
                    string sql;
                    while ((sql = reader.Read()) != null)
                    {
                        count++;
                        foreach (string keyword in SpatialKeywords)
                            if (sql.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) { spatial = true; break; }
                    }
                }
                if (checksum != null) VerifyPayloadChecksum(filePath, checksum, cancellation);
            }
            catch (OperationCanceledException) { issues.Add("Scan cancelled by user."); }
            catch (Exception ex) { issues.Add("Error during preflight scan: " + ex.Message); }
            return new SqliteImportPreflight(issues.Count == 0, bytes, count, manifest, tables, rows, checksum, spatial, issues);
        }
        private static void VerifyPayloadChecksum(string path, string expected, CancellationToken cancellation)
        {
            using (var stream = File.OpenRead(path))
            {
                int tailLength = (int)Math.Min(512, stream.Length);
                var tail = new byte[tailLength];
                stream.Position = stream.Length - tailLength;
                int read = stream.Read(tail, 0, tailLength);
                string ending = Encoding.ASCII.GetString(tail, 0, read);
                var match = Regex.Match(ending, @"(?m)^-- Manifest-(?:Payload-)?SHA256: [a-fA-F0-9]{64}\r?\n?\z");
                if (!match.Success) throw new InvalidDataException("Checksum must be the final native footer line.");
                long payloadBytes = stream.Length - tailLength + match.Index;
                stream.Position = 0;
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[65536];
                    long remaining = payloadBytes;
                    while (remaining > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (n == 0) throw new EndOfStreamException();
                        sha.TransformBlock(buffer, 0, n, null, 0); remaining -= n;
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native payload SHA-256 mismatch; target was not modified.");
                }
            }
        }
    }
}
