using System;
using System.Collections.Generic;
using System.IO;

namespace mySQLPunk.lib.MssqlExport
{
    internal enum MssqlExportMode { SchemaAndData, SchemaOnly, DataOnly }

    internal sealed class MssqlObjectKey
    {
        public int ObjectId { get; }
        public string Schema { get; }
        public string Name { get; }
        public MssqlObjectKey(int objectId, string schema, string name)
        {
            if (objectId <= 0) throw new ArgumentOutOfRangeException(nameof(objectId));
            if (string.IsNullOrWhiteSpace(schema)) throw new ArgumentException("Schema is required", nameof(schema));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required", nameof(name));
            ObjectId = objectId; Schema = schema; Name = name;
        }
    }

    internal sealed class MssqlMirrorOptions
    {
        public string Directory { get; }
        public int RetainCount { get; }
        public MssqlMirrorOptions(string directory, int retainCount)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Mirror directory is required", nameof(directory));
            if (retainCount < 1) throw new ArgumentOutOfRangeException(nameof(retainCount));
            Directory = Path.GetFullPath(directory); RetainCount = retainCount;
        }
    }

    // Captured destination facts; capture/revalidation belongs to the publication task.
    internal sealed class ExportDestinationStamp
    {
        public bool Exists { get; }
        public string FullPath { get; }
        public string FileIdentity { get; }
        public long Length { get; }
        public DateTime LastWriteTimeUtc { get; }
        public string Sha256 { get; }
        public ExportDestinationStamp(bool exists, string fullPath, string fileIdentity, long length, DateTime lastWriteTimeUtc, string sha256)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) throw new ArgumentException("Destination path is required", nameof(fullPath));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Exists = exists; FullPath = Path.GetFullPath(fullPath); FileIdentity = fileIdentity;
            Length = length; LastWriteTimeUtc = lastWriteTimeUtc; Sha256 = sha256;
        }
    }

    internal sealed class MssqlExportRequest
    {
        public Guid JobId { get; }
        public string DatabaseName { get; }
        public IReadOnlyList<MssqlObjectKey> Tables { get; }
        public MssqlExportMode Mode { get; }
        public bool IncludeViews { get; }
        public string TargetPath { get; }
        public ExportDestinationStamp Destination { get; }
        public MssqlMirrorOptions Mirror { get; }
        public MssqlExportRequest(Guid jobId, string databaseName, IReadOnlyList<MssqlObjectKey> tables, MssqlExportMode mode, bool includeViews, string targetPath, ExportDestinationStamp destination, MssqlMirrorOptions mirror)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Job identity is required", nameof(jobId));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database is required", nameof(databaseName));
            if (tables == null || tables.Count == 0) throw new ArgumentException("Select at least one table", nameof(tables));
            if (!Enum.IsDefined(typeof(MssqlExportMode), mode)) throw new ArgumentException("Unknown export mode", nameof(mode));
            if (mode == MssqlExportMode.DataOnly && includeViews) throw new ArgumentException("Data-only export cannot include views", nameof(includeViews));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Output path is required", nameof(targetPath));
            var selection = new List<MssqlObjectKey>(tables);
            if (selection.Contains(null)) throw new ArgumentException("Table selection contains null", nameof(tables));
            JobId = jobId; DatabaseName = databaseName; Tables = selection.AsReadOnly();
            Mode = mode; IncludeViews = includeViews; TargetPath = Path.GetFullPath(targetPath);
            Destination = destination; Mirror = mirror;
        }
    }

    internal static class MssqlExportLimits
    {
        public const int SmallRowBytes = 262144;
        public const int StatementBytes = 262144;
        public const int BatchBytes = 4194304;
        public const int BinaryChunkBytes = 65536;
        public const int TextChunkCodeUnits = 16384;
    }
}
