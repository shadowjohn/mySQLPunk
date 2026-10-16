using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.OracleImport
{
    public sealed class OracleImportPreflightScanner
    {
        public OracleImportPreflight Scan(string filePath, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return new OracleImportPreflight(false, "SQL file not found: " + filePath);
            var result = new OracleImportPreflight { FileSizeBytes = new FileInfo(filePath).Length, DetectedEncoding = DetectEncoding(filePath) };
            try
            {
                Action<string> comment = line =>
                {
                    if (line.Contains("mySQLPunk Native Manifest")) result.ContainsManifest = true;
                    var table = Regex.Match(line, @"^--\s*Manifest-Table:\s*(.+?)\s*\|\s*Rows:\s*(\d+)\s*$", RegexOptions.IgnoreCase);
                    long rows;
                    if (table.Success && long.TryParse(table.Groups[2].Value, out rows)) result.ManifestDeclaredTables[table.Groups[1].Value] = rows;
                    var hash = Regex.Match(line, @"^--\s*Manifest-Checksum-SHA256:\s*([a-fA-F0-9]{64})", RegexOptions.IgnoreCase);
                    if (hash.Success) result.ManifestSha256 = hash.Groups[1].Value.ToLowerInvariant();
                };
                using (var reader = new StreamReader(filePath, result.DetectedEncoding, true, 65536))
                {
                    foreach (var item in OracleImportRunner.EnumerateStatements(reader, cancellationToken, comment))
                    {
                        result.StatementCount++;
                        if (Regex.IsMatch(item.Sql, @"^\s*(DECLARE|BEGIN|CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE))\b", RegexOptions.IgnoreCase))
                        {
                            result.HasPlSqlBlocks = true;
                            result.PlSqlBlocksCount++;
                        }
                        foreach (string keyword in new[] { "sdo_geometry", "sdo_util", "user_sdo_geom_metadata", "spatial_index", "sdo_dim_array", "sdo_dim_element" })
                            if (item.Sql.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                result.HasSpatialFeatures = true;
                                if (!result.DetectedSpatialKeywords.Contains(keyword)) result.DetectedSpatialKeywords.Add(keyword);
                            }
                    }
                }
                if (result.StatementCount == 0) { result.CanProceed = false; result.Issues.Add("No executable SQL statements found."); }
            }
            catch (Exception ex) { result.CanProceed = false; result.Issues.Add(ex.Message); }
            return result;
        }

        private static Encoding DetectEncoding(string filePath)
        {
            var bom = new byte[4];
            using (var stream = File.OpenRead(filePath))
            {
                int length = stream.Read(bom, 0, bom.Length);
                if (length >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff) return new UTF32Encoding(true, true, true);
                if (length >= 4 && bom[0] == 0xff && bom[1] == 0xfe && bom[2] == 0 && bom[3] == 0) return new UTF32Encoding(false, true, true);
                if (length >= 2 && bom[0] == 0xff && bom[1] == 0xfe) return new UnicodeEncoding(false, true, true);
                if (length >= 2 && bom[0] == 0xfe && bom[1] == 0xff) return new UnicodeEncoding(true, true, true);
                return new UTF8Encoding(false, true);
            }
        }
    }
}
