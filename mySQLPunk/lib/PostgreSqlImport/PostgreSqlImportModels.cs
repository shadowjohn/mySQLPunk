using System;
using System.Collections.Generic;
using System.Text;

namespace mySQLPunk.lib.PostgreSqlImport
{
    public enum PostgreSqlConflictStrategy
    {
        KeepExisting,       // 保留現存物件 (遇衝突略過)
        ReplaceAll,         // 全部替換 (CREATE OR REPLACE / 覆寫)
        DropAndRecreate,    // 刪表重建 (DROP TABLE IF EXISTS ... CASCADE)
        TruncateAndInsert   // 清空重匯 (TRUNCATE TABLE ... CASCADE)
    }

    public sealed class PostgreSqlImportOptions
    {
        public PostgreSqlConflictStrategy ConflictStrategy { get; set; } = PostgreSqlConflictStrategy.KeepExisting;
        public bool StopOnError { get; set; } = true;
        public bool AutoInstallPostgis { get; set; } = true; // 偵測到 PostGIS 空間特徵時自動安裝擴充套件
        public bool UseTransaction { get; set; } = true;     // 交易保護，遇錯 ROLLBACK
        public string TargetSchema { get; set; } = string.Empty;
    }

    public sealed class PostgreSqlImportRequest
    {
        public string FilePath { get; set; } = string.Empty;
        public string TargetDatabase { get; set; } = string.Empty;
        public PostgreSqlImportOptions Options { get; set; } = new PostgreSqlImportOptions();

        public PostgreSqlImportRequest() { }

        public PostgreSqlImportRequest(string filePath, string targetDatabase, PostgreSqlImportOptions options = null)
        {
            FilePath = filePath ?? string.Empty;
            TargetDatabase = targetDatabase ?? string.Empty;
            Options = options ?? new PostgreSqlImportOptions();
        }
    }

    public sealed class PostgreSqlImportPreflightResult
    {
        public bool CanProceed { get; set; } = true;
        public Encoding DetectedEncoding { get; set; } = Encoding.UTF8;
        public int StatementCount { get; set; }
        public bool HasPostgisFeatures { get; set; }
        public List<string> DetectedSpatialKeywords { get; } = new List<string>();
        public bool HasDollarQuotes { get; set; }
        public int DollarQuoteBlocksCount { get; set; }
        public bool ContainsManifest { get; set; }
        public Dictionary<string, long> ManifestDeclaredTables { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public string ManifestSha256 { get; set; } = string.Empty;
        public List<string> Issues { get; } = new List<string>();
        public long FileSize { get; set; }
    }

    public sealed class PostgreSqlImportProgress
    {
        public string Stage { get; set; } = string.Empty;
        public int CurrentStatement { get; set; }
        public int TotalStatements { get; set; }
        public long LinesRead { get; set; }
        public long TotalLines { get; set; }
        public long BytesRead { get; set; }
        public long TotalBytes { get; set; }
        public long RowsAffected { get; set; }
        public string CurrentObject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public double PercentComplete
        {
            get
            {
                if (TotalBytes > 0 && BytesRead > 0)
                {
                    double p = (double)BytesRead / TotalBytes * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                if (TotalStatements > 0 && CurrentStatement > 0)
                {
                    double p = (double)CurrentStatement / TotalStatements * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                return 0.0;
            }
        }
    }

    public sealed class PostgreSqlImportResult
    {
        public bool Success { get; set; }
        public int StatementsExecuted { get; set; }
        public int StatementsFailed { get; set; }
        public long TotalRowsAffected { get; set; }
        public Dictionary<string, long> RestoredTables { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public bool ManifestMatched { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string LogFilePath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
