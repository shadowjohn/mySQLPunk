using System;
using System.Collections.Generic;
using System.Text;

namespace mySQLPunk.lib.OracleImport
{
    public enum OracleConflictStrategy
    {
        KeepExisting,
        ReplaceAll,
        DropAndRecreate,
        TruncateAndInsert
    }

    public sealed class OracleImportOptions
    {
        public OracleConflictStrategy ConflictStrategy { get; set; } = OracleConflictStrategy.KeepExisting;
        public bool UseTransaction { get; set; } = true;
        public bool StopOnError { get; set; } = true;
        public bool AutoRegisterSpatialMetadata { get; set; } = true;
        public int CommitBatchSize { get; set; } = 1000;
    }

    public sealed class OracleImportRequest
    {
        public string FilePath { get; set; }
        public string DatabaseName { get; set; } // Owner / Schema
        public OracleImportOptions Options { get; set; }

        public OracleImportRequest()
        {
            Options = new OracleImportOptions();
        }

        public OracleImportRequest(string filePath, string databaseName, OracleImportOptions options = null)
        {
            FilePath = filePath;
            DatabaseName = databaseName;
            Options = options ?? new OracleImportOptions();
        }
    }

    public sealed class OracleImportPreflight
    {
        public bool CanProceed { get; set; } = true;
        public long FileSizeBytes { get; set; }
        public Encoding DetectedEncoding { get; set; } = Encoding.UTF8;
        public int StatementCount { get; set; }
        public bool ContainsManifest { get; set; }
        public Dictionary<string, long> ManifestDeclaredTables { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public string ManifestSha256 { get; set; } = string.Empty;
        public bool HasSpatialFeatures { get; set; }
        public List<string> DetectedSpatialKeywords { get; } = new List<string>();
        public bool HasPlSqlBlocks { get; set; }
        public int PlSqlBlocksCount { get; set; }
        public List<string> Issues { get; } = new List<string>();

        public OracleImportPreflight() { }

        public OracleImportPreflight(bool canProceed, string issue)
        {
            CanProceed = canProceed;
            if (!string.IsNullOrWhiteSpace(issue))
            {
                Issues.Add(issue);
            }
        }
    }

    public sealed class OracleImportProgress
    {
        public string Stage { get; set; } = "Initializing";
        public int CurrentStatement { get; set; }
        public int TotalStatements { get; set; }
        public long BytesRead { get; set; }
        public long TotalBytes { get; set; }
        public long RowsAffected { get; set; }
        public string Message { get; set; } = string.Empty;

        public double PercentComplete
        {
            get
            {
                if (TotalBytes > 0)
                {
                    double p = (double)BytesRead / TotalBytes * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                if (TotalStatements > 0)
                {
                    double p = (double)CurrentStatement / TotalStatements * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                return 0.0;
            }
        }
    }

    public sealed class OracleImportResult
    {
        public bool Success { get; set; }
        public int StatementsExecuted { get; set; }
        public int StatementsFailed { get; set; }
        public long TotalRowsAffected { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string LogFilePath { get; set; } = string.Empty;
        public bool ManifestMatched { get; set; } = true;
        public Dictionary<string, long> RestoredTables { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }
}
