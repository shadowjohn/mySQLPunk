using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.OracleExport
{
    public enum OracleExportMode
    {
        SchemaAndData,
        DataOnly,
        SchemaOnly
    }

    public enum OracleExportStage
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

    public sealed class OracleTableExportItem
    {
        public string Owner { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FullQualifiedName => "\"" + Owner.Replace("\"", "\"\"") + "\".\"" + Name.Replace("\"", "\"\"") + "\"";
        public string DisplayName => string.IsNullOrWhiteSpace(Owner) ? Name : Owner + "." + Name;
        public long RowCount { get; set; }
        public long EstimatedBytes { get; set; }
        public bool IsView { get; set; }
        public bool IsSpatial { get; set; }
        public bool IsSpatialMetadata { get; set; }
        public List<string> SpatialColumnNames { get; } = new List<string>();
        public string SpatialFeatureDescription { get; set; } = string.Empty;
        public bool Selected { get; set; } = true;
    }

    public sealed class OracleExportWizardOptions
    {
        public OracleExportMode Mode { get; set; } = OracleExportMode.SchemaAndData;
        public string TargetPath { get; set; } = string.Empty;
        public List<string> SelectedTables { get; } = new List<string>();
        public bool UseTransactionSnapshot { get; set; } = true; // SET TRANSACTION READ ONLY;
        public bool ExcludeSpatialIndexInternalTables { get; set; } = true; // 排除 Oracle Spatial 內部 R-Tree 影子表 (MDRT_*, MDXT_*, MDRS_*)
        public bool IncludeSpatialMetadata { get; set; } = true; // USER_SDO_GEOM_METADATA 註冊語句
        public bool IncludeSpatialIndexes { get; set; } = true; // INDEXTYPE IS MDSYS.SPATIAL_INDEX
        public bool DropTableIfExists { get; set; } = false;
        public bool TruncateBeforeInsert { get; set; } = false;
        public int BatchSize { get; set; } = 500;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeIndexes { get; set; } = true;
        public bool IncludeComments { get; set; } = true;
        public bool IncludeManifest { get; set; } = true;

        public OracleExportWizardOptions() { }

        public OracleExportWizardOptions(OracleExportMode mode, string targetPath)
        {
            Mode = mode;
            TargetPath = targetPath ?? string.Empty;
        }
    }

    public sealed class OracleExportProgress
    {
        public OracleExportStage Stage { get; set; } = OracleExportStage.NotStarted;
        public string CurrentTable { get; set; } = string.Empty;
        public string CurrentOwner { get; set; } = string.Empty;
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

    public sealed class OracleExportResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int TablesExported { get; set; }
        public int ViewsExported { get; set; }
        public long TotalRowsExported { get; set; }
        public long FileSizeBytes { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Sha256Checksum { get; set; } = string.Empty;
        public Dictionary<string, long> ManifestTables { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }
}
