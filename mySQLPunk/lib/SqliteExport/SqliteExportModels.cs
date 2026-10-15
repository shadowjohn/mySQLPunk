using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.SqliteExport
{
    public enum SqliteExportMode
    {
        SchemaAndData,
        SchemaOnly,
        DataOnly
    }

    public enum SqliteExportStage
    {
        Ready,
        ReadingCatalog,
        WritingPragmas,
        WritingSchema,
        WritingData,
        WritingIndexes,
        WritingTriggers,
        WritingSidecarComments,
        WritingManifest,
        Completed,
        Cancelled,
        Failed
    }

    public sealed class SqliteTableExportItem
    {
        public string Name { get; set; }
        public long EstimatedRows { get; set; }
        public bool IsSelected { get; set; }
        public bool IsSpatial { get; set; }
        public bool IsSpatialMetadata { get; set; }
        public bool IsVirtualTable { get; set; }
        public string SpatialInfo { get; set; }

        public SqliteTableExportItem(
            string name,
            long estimatedRows,
            bool isSelected = true,
            bool isSpatial = false,
            bool isSpatialMetadata = false,
            bool isVirtualTable = false,
            string spatialInfo = null)
        {
            Name = name;
            EstimatedRows = estimatedRows;
            IsSelected = isSelected;
            IsSpatial = isSpatial;
            IsSpatialMetadata = isSpatialMetadata;
            IsVirtualTable = isVirtualTable;
            SpatialInfo = spatialInfo ?? (isSpatial ? "GIS Layer" : (isSpatialMetadata ? "Spatial Metadata" : (isVirtualTable ? "Virtual Table" : "")));
        }
    }

    public sealed class SqliteExportWizardOptions
    {
        public SqliteExportMode Mode { get; set; }
        public string TargetPath { get; set; }
        public bool SingleTransaction { get; set; } = true;
        public bool PragmaForeignKeysOff { get; set; } = true;
        public bool PragmaSynchronousOff { get; set; } = true;
        public bool IncludeDropStatements { get; set; } = true;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        public bool IncludeIndexes { get; set; } = true;
        public bool IncludeComments { get; set; } = true;
        public bool ExcludeSpatialMetadata { get; set; } = false;
        public int InsertBatchSize { get; set; } = 1000;
        public List<string> SelectedTables { get; set; } = new List<string>();
        public List<string> SelectedViews { get; set; } = new List<string>();
        public List<string> SelectedTriggers { get; set; } = new List<string>();

        public SqliteExportWizardOptions(SqliteExportMode mode = SqliteExportMode.SchemaAndData, string targetPath = null)
        {
            Mode = mode;
            TargetPath = targetPath;
        }
    }

    public sealed class SqliteExportProgress
    {
        public SqliteExportStage Stage { get; }
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

        public SqliteExportProgress(
            SqliteExportStage stage,
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

    public sealed class SqliteExportResult
    {
        public SqliteExportStage Stage { get; }
        public bool Success => Stage == SqliteExportStage.Completed;
        public string TargetPath { get; }
        public int TableCount { get; }
        public int ViewCount { get; }
        public int TriggerCount { get; }
        public int IndexCount { get; }
        public long TotalRows { get; }
        public long FileBytes { get; }
        public string Sha256Hash { get; }
        public TimeSpan Elapsed { get; }
        public string ErrorMessage { get; }

        public SqliteExportResult(
            SqliteExportStage stage,
            string targetPath,
            int tableCount,
            int viewCount,
            int triggerCount,
            int indexCount,
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
            TriggerCount = triggerCount;
            IndexCount = indexCount;
            TotalRows = totalRows;
            FileBytes = fileBytes;
            Sha256Hash = sha256Hash;
            Elapsed = elapsed;
            ErrorMessage = errorMessage;
        }
    }
}
