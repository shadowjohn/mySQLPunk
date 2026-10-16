using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.PostgreSqlExport
{
    public enum PostgreSqlExportMode
    {
        SchemaAndData,
        DataOnly,
        SchemaOnly
    }

    public enum PostgreSqlExportStage
    {
        NotStarted,
        Preflight,
        ExportingSchema,
        ExportingData,
        Finalizing,
        Completed,
        Cancelled,
        Failed
    }

    public sealed class PostgreSqlTableExportItem
    {
        public string Schema { get; set; } = "public";
        public string Name { get; set; } = string.Empty;
        public string FullQualifiedName => "\"" + (string.IsNullOrWhiteSpace(Schema) ? "public" : Schema) + "\".\"" + Name + "\"";
        public string DisplayName => string.Equals(Schema, "public", StringComparison.OrdinalIgnoreCase) ? Name : Schema + "." + Name;
        public long RowCount { get; set; }
        public long EstimatedBytes { get; set; }
        public bool IsView { get; set; }
        public bool IsSpatial { get; set; }
        public bool IsSpatialMetadata { get; set; }
        public List<string> SpatialColumnNames { get; } = new List<string>();
        public string SpatialFeatureDescription { get; set; } = string.Empty;
        public bool Selected { get; set; } = true;
    }

    public sealed class PostgreSqlExportWizardOptions
    {
        public PostgreSqlExportMode Mode { get; set; } = PostgreSqlExportMode.SchemaAndData;
        public string TargetPath { get; set; } = string.Empty;
        public List<string> SelectedTables { get; } = new List<string>();
        public bool UseTransactionSnapshot { get; set; } = true; // REPEATABLE READ
        public bool ExcludeSpatialMetadataTables { get; set; } = true; // 排除龐大空間中繼表 (spatial_ref_sys)
        public bool IncludeCreateExtensionPostgis { get; set; } = true; // CREATE EXTENSION IF NOT EXISTS postgis
        public bool DropTableIfExists { get; set; } = false;
        public bool TruncateBeforeInsert { get; set; } = false;
        public bool UseMultiRowInsert { get; set; } = true;
        public int BatchSize { get; set; } = 1000;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeIndexes { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        public bool IncludeComments { get; set; } = true;
        public bool IncludeSequences { get; set; } = true;
        public bool IncludeManifest { get; set; } = true;

        public PostgreSqlExportWizardOptions() { }

        public PostgreSqlExportWizardOptions(PostgreSqlExportMode mode, string targetPath)
        {
            Mode = mode;
            TargetPath = targetPath ?? string.Empty;
        }
    }

    public sealed class PostgreSqlExportProgress
    {
        public PostgreSqlExportStage Stage { get; set; } = PostgreSqlExportStage.NotStarted;
        public string CurrentTable { get; set; } = string.Empty;
        public string CurrentSchema { get; set; } = string.Empty;
        public int TablesCompleted { get; set; }
        public int TotalTables { get; set; }
        public long RowsExported { get; set; }
        public long TotalRows { get; set; }
        public long BytesExported { get; set; }
        public string Message { get; set; } = string.Empty;

        public double PercentComplete
        {
            get
            {
                if (TotalRows > 0)
                {
                    double p = (double)RowsExported / TotalRows * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                if (TotalTables > 0)
                {
                    double p = (double)TablesCompleted / TotalTables * 100.0;
                    return p > 100.0 ? 100.0 : p;
                }
                return 0.0;
            }
        }
    }

    public sealed class PostgreSqlExportResult
    {
        public bool Success { get; set; }
        public PostgreSqlExportStage Stage { get; set; }
        public int TableCount { get; set; }
        public int ViewCount { get; set; }
        public long TotalRows { get; set; }
        public long BytesWritten { get; set; }
        public string TargetPath { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public Dictionary<string, long> ManifestTableRows { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public string ManifestSha256 { get; set; } = string.Empty;
    }
}
