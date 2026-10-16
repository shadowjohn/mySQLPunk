using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.SqliteImport
{
    public enum SqliteImportStatus
    {
        Ready,
        Running,
        Completed,
        Cancelled,
        Failed
    }

    public enum SqliteExistingObjectStrategy
    {
        AbortIfExists,
        SkipExisting,
        DropAndRecreate,
        OverwriteKeepExisting
    }

    public static class SqliteExistingObjectSelection
    {
        public static SqliteExistingObjectStrategy FromIndex(int index)
        {
            switch (index)
            {
                case 0: return SqliteExistingObjectStrategy.SkipExisting;
                case 1: return SqliteExistingObjectStrategy.DropAndRecreate;
                case 2: return SqliteExistingObjectStrategy.OverwriteKeepExisting;
                case 3: return SqliteExistingObjectStrategy.AbortIfExists;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public sealed class SqliteImportRequest
    {
        public Guid JobId { get; }
        public string SourcePath { get; }
        public string DatabaseName { get; }

        public SqliteImportRequest(Guid jobId, string sourcePath, string databaseName)
        {
            JobId = jobId;
            SourcePath = sourcePath;
            DatabaseName = databaseName;
        }
    }

    public sealed class SqliteImportOptions
    {
        public bool ContinueOnError { get; set; } = false;
        public SqliteExistingObjectStrategy ExistingObjectStrategy { get; set; } = SqliteExistingObjectStrategy.SkipExisting;
        public bool AutoLoadSpatiaLite { get; set; } = true;
        public bool WrapInTransaction { get; set; } = true;
        public bool PragmaForeignKeysOff { get; set; } = true;
        public bool PragmaSynchronousOff { get; set; } = false;
    }

    public sealed class SqliteImportPreflight
    {
        public bool Passed { get; }
        public long FileBytes { get; }
        public int StatementCount { get; }
        public bool HasManifest { get; }
        public int ManifestTables { get; }
        public long ManifestRows { get; }
        public string ManifestSha256 { get; }
        public bool DetectedSpatiaLite { get; }
        public IReadOnlyList<string> Issues { get; }

        public SqliteImportPreflight(
            bool passed,
            long fileBytes,
            int statementCount,
            bool hasManifest,
            int manifestTables,
            long manifestRows,
            string manifestSha256,
            bool detectedSpatiaLite,
            IReadOnlyList<string> issues)
        {
            Passed = passed;
            FileBytes = fileBytes;
            StatementCount = statementCount;
            HasManifest = hasManifest;
            ManifestTables = manifestTables;
            ManifestRows = manifestRows;
            ManifestSha256 = manifestSha256;
            DetectedSpatiaLite = detectedSpatiaLite;
            Issues = issues ?? Array.Empty<string>();
        }
    }

    public sealed class SqliteImportProgress
    {
        public long ProcessedBytes { get; }
        public long TotalBytes { get; }
        public int ExecutedStatements { get; }
        public int FailedStatements { get; }
        public int SkippedStatements { get; }
        public long TotalRowsAffected { get; }
        public double BytesPerSecond { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan? EstimatedRemaining { get; }
        public string Message { get; }
        public string CurrentStatementPreview { get; }

        public SqliteImportProgress(
            long processedBytes,
            long totalBytes,
            int executedStatements,
            int failedStatements,
            int skippedStatements,
            long totalRowsAffected,
            double bytesPerSecond,
            TimeSpan elapsed,
            TimeSpan? estimatedRemaining,
            string message,
            string currentStatementPreview)
        {
            ProcessedBytes = processedBytes;
            TotalBytes = totalBytes;
            ExecutedStatements = executedStatements;
            FailedStatements = failedStatements;
            SkippedStatements = skippedStatements;
            TotalRowsAffected = totalRowsAffected;
            BytesPerSecond = bytesPerSecond;
            Elapsed = elapsed;
            EstimatedRemaining = estimatedRemaining;
            Message = message;
            CurrentStatementPreview = currentStatementPreview;
        }
    }

    public sealed class SqliteImportResult
    {
        public SqliteImportStatus Status { get; }
        public int ExecutedStatements { get; }
        public int FailedStatements { get; }
        public int SkippedStatements { get; }
        public long TotalRowsAffected { get; }
        public string LogPath { get; }
        public TimeSpan Elapsed { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool ManifestVerified { get; }
        public string ManifestReport { get; }

        public SqliteImportResult(
            SqliteImportStatus status,
            int executedStatements,
            int failedStatements,
            int skippedStatements,
            long totalRowsAffected,
            string logPath,
            TimeSpan elapsed,
            IReadOnlyList<string> errors,
            bool manifestVerified = false,
            string manifestReport = null)
        {
            Status = status;
            ExecutedStatements = executedStatements;
            FailedStatements = failedStatements;
            SkippedStatements = skippedStatements;
            TotalRowsAffected = totalRowsAffected;
            LogPath = logPath;
            Elapsed = elapsed;
            Errors = errors ?? Array.Empty<string>();
            ManifestVerified = manifestVerified;
            ManifestReport = manifestReport;
        }
    }
}
