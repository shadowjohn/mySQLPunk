using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace mySQLPunk.lib.MssqlImport
{
    internal sealed class MssqlImportInput : IDisposable
    {
        private readonly FileStream file;
        private readonly string databaseName;
        private bool disposed, active;
        internal string ExecutionSha256 { get; private set; }
        private MssqlImportInput(string path, string databaseName)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
            this.databaseName = databaseName;
        }
        internal static MssqlImportInput Open(string path, string databaseName = null) => new MssqlImportInput(path, databaseName);
        internal MssqlImportScan Scan(CancellationToken cancellation, Action<long> progress)
        {
            Enter();
            try
            {
                file.Position = 0; ExecutionSha256 = null;
                var inspector = new MssqlImportScriptInspector(databaseName);
                long count = 0, max = 0;
                using (var reader = new MssqlGoBatchReader(file, 8192, progress, inspector.Observe))
                {
                    foreach (var batch in reader.ReadBatches(cancellation)) { count++; max = Math.Max(max, (long)batch.Text.Length); }
                    return new MssqlImportScan(file.Length, count, max, reader.EncodingName, reader.Sha256, inspector.Complete());
                }
            }
            finally { active = false; }
        }
        internal IEnumerable<MssqlImportBatch> ReadBatches(CancellationToken cancellation)
        {
            Enter();
            try
            {
                file.Position = 0; ExecutionSha256 = null;
                using (var reader = new MssqlGoBatchReader(file))
                {
                    foreach (var batch in reader.ReadBatches(cancellation)) yield return batch;
                    ExecutionSha256 = reader.Sha256;
                }
            }
            finally { active = false; }
        }
        private void Enter()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MssqlImportInput));
            if (active) throw new InvalidOperationException("Input already has an active reader.");
            active = true;
        }
        public void Dispose() { if (disposed) return; disposed = true; file.Dispose(); }
    }
}
