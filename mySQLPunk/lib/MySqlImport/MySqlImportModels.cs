using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.MySqlImport
{
    public enum MySqlImportStatus
    {
        Ready,
        Completed,
        CompletedWithErrors,
        Cancelled,
        Failed
    }

    public enum MySqlImportStage
    {
        Ready,
        Scanning,
        Executing,
        Stopping,
        Finished
    }

    public sealed class MySqlImportRequest
    {
        public Guid JobId { get; }
        public string SourcePath { get; set; }
        public string DatabaseName { get; }

        public MySqlImportRequest(Guid jobId, string sourcePath, string databaseName)
        {
            JobId = jobId;
            SourcePath = sourcePath ?? string.Empty;
            DatabaseName = databaseName ?? string.Empty;
        }
    }

    public sealed class MySqlImportPreflight
    {
        public bool Passed { get; }
        public long FileBytes { get; }
        public int EstimatedStatements { get; }
        public bool HasManifest { get; }
        public int ManifestTables { get; }
        public long ManifestRows { get; }
        public string ManifestSha256 { get; }
        public List<string> Issues { get; } = new List<string>();

        public MySqlImportPreflight(
            bool passed,
            long fileBytes,
            int estimatedStatements,
            bool hasManifest,
            int manifestTables,
            long manifestRows,
            string manifestSha256,
            IEnumerable<string> issues = null)
        {
            Passed = passed;
            FileBytes = fileBytes;
            EstimatedStatements = estimatedStatements;
            HasManifest = hasManifest;
            ManifestTables = manifestTables;
            ManifestRows = manifestRows;
            ManifestSha256 = manifestSha256;
            if (issues != null) Issues.AddRange(issues);
        }
    }

    public sealed class MySqlImportProgress
    {
        public Guid JobId { get; }
        public MySqlImportStage Stage { get; }
        public long TotalBytes { get; }
        public long ProcessedBytes { get; }
        public int ExecutedStatements { get; }
        public int FailedStatements { get; }
        public int SkippedStatements { get; }
        public double BytesPerSecond { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan? EstimatedRemaining { get; }
        public string CurrentStatementPreview { get; }
        public string Message { get; }

        public MySqlImportProgress(
            Guid jobId,
            MySqlImportStage stage,
            long totalBytes,
            long processedBytes,
            int executedStatements,
            int failedStatements,
            int skippedStatements,
            double bytesPerSecond,
            TimeSpan elapsed,
            TimeSpan? estimatedRemaining,
            string currentStatementPreview,
            string message)
        {
            JobId = jobId;
            Stage = stage;
            TotalBytes = totalBytes;
            ProcessedBytes = processedBytes;
            ExecutedStatements = executedStatements;
            FailedStatements = failedStatements;
            SkippedStatements = skippedStatements;
            BytesPerSecond = bytesPerSecond;
            Elapsed = elapsed;
            EstimatedRemaining = estimatedRemaining;
            CurrentStatementPreview = currentStatementPreview;
            Message = message;
        }
    }

    public sealed class MySqlImportResult
    {
        public Guid JobId { get; }
        public MySqlImportStatus Status { get; }
        public long FileBytes { get; }
        public int ExecutedStatements { get; }
        public int FailedStatements { get; }
        public int SkippedStatements { get; }
        public string LogPath { get; }
        public TimeSpan Elapsed { get; }
        public string Summary { get; }
        public List<string> Errors { get; } = new List<string>();

        public MySqlImportResult(
            Guid jobId,
            MySqlImportStatus status,
            long fileBytes,
            int executedStatements,
            int failedStatements,
            int skippedStatements,
            string logPath,
            TimeSpan elapsed,
            string summary,
            IEnumerable<string> errors = null)
        {
            JobId = jobId;
            Status = status;
            FileBytes = fileBytes;
            ExecutedStatements = executedStatements;
            FailedStatements = failedStatements;
            SkippedStatements = skippedStatements;
            LogPath = logPath;
            Elapsed = elapsed;
            Summary = summary;
            if (errors != null) Errors.AddRange(errors);
        }
    }
}
