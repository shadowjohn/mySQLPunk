using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.MySqlExport
{
    public enum MySqlExportMode
    {
        SchemaAndData,
        SchemaOnly,
        DataOnly
    }

    public enum MySqlExportStage
    {
        Ready,
        ReadingCatalog,
        WritingSchema,
        WritingData,
        WritingRoutines,
        WritingTriggers,
        WritingManifest,
        Completed,
        Cancelled,
        Failed
    }

    public sealed class MySqlTableExportItem
    {
        public string Name { get; set; }
        public long EstimatedRows { get; set; }
        public bool IsSelected { get; set; }

        public MySqlTableExportItem(string name, long estimatedRows, bool isSelected = true)
        {
            Name = name;
            EstimatedRows = estimatedRows;
            IsSelected = isSelected;
        }
    }

    public sealed class MySqlExportWizardOptions
    {
        public MySqlExportMode Mode { get; set; }
        public string TargetPath { get; set; }
        public bool SingleTransaction { get; set; } = true;
        public bool DisableForeignKeyChecks { get; set; } = true;
        public bool DisableUniqueChecks { get; set; } = true;
        public bool SetNoAutoValueOnZero { get; set; } = true;
        public bool IncludeDropStatements { get; set; } = true;
        public bool IncludeCreateDatabase { get; set; } = false;
        public bool IncludeUseDatabase { get; set; } = true;
        public bool RemoveDefiner { get; set; } = true;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeRoutines { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        public int InsertBatchSize { get; set; } = 1000;
        public List<string> SelectedTables { get; set; } = new List<string>();
        public List<string> SelectedViews { get; set; } = new List<string>();
        public List<string> SelectedRoutines { get; set; } = new List<string>();
        public List<string> SelectedTriggers { get; set; } = new List<string>();

        public MySqlExportWizardOptions(MySqlExportMode mode = MySqlExportMode.SchemaAndData, string targetPath = null)
        {
            Mode = mode;
            TargetPath = targetPath;
        }
    }

    public sealed class MySqlExportProgress
    {
        public MySqlExportStage Stage { get; }
        public string CurrentObject { get; }
        public int CompletedTables { get; }
        public int TotalTables { get; }
        public long ProcessedRows { get; }
        public long EstimatedTotalRows { get; }
        public long WrittenBytes { get; }
        public double RowsPerSecond { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan? EstimatedRemaining { get; }
        public string Message { get; }

        public MySqlExportProgress(
            MySqlExportStage stage,
            string currentObject,
            int completedTables,
            int totalTables,
            long processedRows,
            long estimatedTotalRows,
            long writtenBytes,
            double rowsPerSecond,
            TimeSpan elapsed,
            TimeSpan? estimatedRemaining,
            string message)
        {
            Stage = stage;
            CurrentObject = currentObject;
            CompletedTables = completedTables;
            TotalTables = totalTables;
            ProcessedRows = processedRows;
            EstimatedTotalRows = estimatedTotalRows;
            WrittenBytes = writtenBytes;
            RowsPerSecond = rowsPerSecond;
            Elapsed = elapsed;
            EstimatedRemaining = estimatedRemaining;
            Message = message;
        }
    }

    public sealed class MySqlExportResult
    {
        public MySqlExportStage Stage { get; }
        public bool Success => Stage == MySqlExportStage.Completed;
        public string TargetPath { get; }
        public int TableCount { get; }
        public int ViewCount { get; }
        public int RoutineCount { get; }
        public int TriggerCount { get; }
        public long TotalRows { get; }
        public long FileBytes { get; }
        public string Sha256Hash { get; }
        public TimeSpan Elapsed { get; }
        public string ErrorMessage { get; }

        public MySqlExportResult(
            MySqlExportStage stage,
            string targetPath,
            int tableCount,
            int viewCount,
            int routineCount,
            int triggerCount,
            long totalRows,
            long fileBytes,
            string sha256Hash,
            TimeSpan elapsed,
            string errorMessage = null)
        {
            Stage = stage;
            TargetPath = targetPath;
            TableCount = tableCount;
            ViewCount = viewCount;
            RoutineCount = routineCount;
            TriggerCount = triggerCount;
            TotalRows = totalRows;
            FileBytes = fileBytes;
            Sha256Hash = sha256Hash;
            Elapsed = elapsed;
            ErrorMessage = errorMessage;
        }
    }
}
