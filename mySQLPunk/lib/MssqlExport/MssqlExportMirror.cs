using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    internal sealed class MssqlExportMirror
    {
        private readonly MssqlArtifactFiles files;
        private readonly List<string> residuals = new List<string>();
        private readonly List<string> warnings = new List<string>();
        internal string MirrorPublishedPath { get; private set; }
        internal IReadOnlyList<string> ResidualPaths => new List<string>(residuals).AsReadOnly();
        internal IReadOnlyList<string> Warnings => new List<string>(warnings).AsReadOnly();
        internal MssqlExportMirror() : this(new MssqlArtifactFiles()) { }
        internal MssqlExportMirror(MssqlArtifactFiles files) { this.files = files; }
        internal string CopyVerified(string publishedPath, MssqlArtifactReceipt receipt, MssqlMirrorOptions options, Action<long, long> progress, CancellationToken cancellation)
        {
            if (MirrorPublishedPath != null) throw new InvalidOperationException("Mirror already published");
            if (receipt == null || options == null) throw new ArgumentNullException();
            string source = Path.GetFullPath(publishedPath);
            string directory = options.Directory;
            if (directory.Length > Path.GetPathRoot(directory).Length) directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            cancellation.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetDirectoryName(source), directory, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".mysqlpunk-export-" + receipt.JobId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".partial");
            bool owned = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, MssqlExportArtifact.BufferSize))
                using (var output = files.Create(temporary))
                {
                    owned = true;
                    byte[] buffer = new byte[MssqlExportArtifact.BufferSize]; int read; long copied = 0;
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested(); read = input.Read(buffer, 0, buffer.Length); if (read == 0) break;
                        output.Write(buffer, 0, read); copied = checked(copied + read); progress?.Invoke(copied, receipt.Bytes);
                    }
                    output.Flush();
                }
                MssqlExportArtifact.VerifyFile(temporary, receipt, progress, cancellation);
                var gate = MssqlArtifactGates.Get(directory); gate.Wait(cancellation);
                try
                {
                    string target = Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "_" + receipt.JobId.ToString("N") + "_" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
                    File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow);
                    using (var commit = new MssqlPublicationCommit(cancellation))
                    {
                        commit.Enter(); files.Move(temporary, target); MirrorPublishedPath = target; owned = false;
                    }
                    if (cancellation.IsCancellationRequested) warnings.Add("Mirror published; retention skipped after cancellation");
                    else
                    {
                        try
                        {
                            // Future-dated backups must not make the just-published file the prune victim.
                            if (WouldPrunePublished(target, options.RetainCount)) warnings.Add("Mirror published; retention skipped because newer timestamps would remove it");
                            else
                            {
                                files.Prune(directory, options.RetainCount);
                                if (RetentionExceeded(directory, options.RetainCount)) warnings.Add("Mirror published; some managed backups exceed the retention limit");
                            }
                        }
                        catch (Exception ex) { warnings.Add("Mirror published; retention incomplete: " + ex.Message); }
                    }
                    return MirrorPublishedPath;
                }
                finally { gate.Release(); }
            }
            finally
            {
                if (owned)
                {
                    try { files.Delete(temporary); }
                    catch (Exception ex) { residuals.Add(temporary); warnings.Add("Mirror temporary cleanup failed: " + ex.Message); }
                }
            }
        }
        // Matches BackupRemoteMirrorService's existing classification and precedence.
        private static string Kind(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("_before_delete_")) return "before_delete";
            if (lower.Contains("_before_restore_")) return "before_restore";
            if (lower.Contains("_backup_")) return "backup";
            return string.Empty;
        }
        private static bool WouldPrunePublished(string path, int retain)
        {
            string kind = Kind(Path.GetFileName(path)); if (kind.Length == 0) return false;
            DateTime stamp = File.GetLastWriteTimeUtc(path); int newerOrEqual = 0;
            foreach (var file in new DirectoryInfo(Path.GetDirectoryName(path)).GetFiles())
                if (!string.Equals(file.FullName, path, StringComparison.OrdinalIgnoreCase) && Kind(file.Name) == kind && file.LastWriteTimeUtc >= stamp) newerOrEqual++;
            return newerOrEqual >= retain;
        }
        private static bool RetentionExceeded(string directory, int retain)
        {
            var counts = new Dictionary<string, int>();
            foreach (var file in new DirectoryInfo(directory).GetFiles())
            {
                string kind = Kind(file.Name); if (kind.Length == 0) continue;
                int count; counts.TryGetValue(kind, out count); counts[kind] = ++count; if (count > retain) return true;
            }
            return false;
        }
    }
}
