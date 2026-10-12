using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace mySQLPunk.lib.MssqlExport
{
    // These gates coordinate this process only, not external writers or path aliases.
    internal static class MssqlArtifactGates
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        internal static SemaphoreSlim Get(string path) => Gates.GetOrAdd(Path.GetFullPath(path).TrimEnd('\\', '/'), _ => new SemaphoreSlim(1, 1));
    }
    internal class MssqlArtifactFiles
    {
        internal virtual Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        internal virtual void Move(string source, string target) => File.Move(source, target);
        internal virtual void Replace(string source, string target, string recovery) => File.Replace(source, target, recovery, false);
        internal virtual void Delete(string path) => File.Delete(path);
        internal virtual void Prune(string directory, int count) => BackupRemoteMirrorService.PruneRemoteBackups(directory, count);
    }
    // Cancellation and entry to the short commit region have a single lock order.
    // A request arriving after entry cannot turn a successful publication into "nothing output".
    internal sealed class MssqlPublicationCommit : IDisposable
    {
        private readonly object sync = new object();
        private readonly CancellationToken token;
        private readonly CancellationTokenRegistration registration;
        private bool canceled, entered;
        internal MssqlPublicationCommit(CancellationToken token)
        {
            this.token = token;
            registration = token.Register(() => { lock (sync) { if (!entered) canceled = true; } });
        }
        internal void Enter() { lock (sync) { if (canceled || token.IsCancellationRequested) throw new OperationCanceledException(token); entered = true; } }
        public void Dispose() => registration.Dispose();
    }
    internal sealed class MssqlExportArtifact : IDisposable
    {
        internal const int BufferSize = 81920;
        private readonly MssqlExportRequest request;
        private readonly MssqlArtifactFiles files;
        private readonly HashingOutput output;
        private readonly List<string> residuals = new List<string>();
        private readonly List<string> warnings = new List<string>();
        private MssqlArtifactReceipt receipt;
        private bool verified, preserveCandidates, disposed;
        internal string TemporaryPath { get; }
        internal string RecoveryPath { get; private set; }
        internal string PublishedPath { get; private set; }
        internal IReadOnlyList<string> ResidualPaths => new List<string>(residuals).AsReadOnly();
        internal IReadOnlyList<string> Warnings => new List<string>(warnings).AsReadOnly();
        internal Stream Output => output;
        private MssqlExportArtifact(MssqlExportRequest request, MssqlArtifactFiles files)
        {
            this.request = request ?? throw new ArgumentNullException(nameof(request)); this.files = files;
            TemporaryPath = Path.Combine(Path.GetDirectoryName(request.TargetPath), ".mysqlpunk-export-" + request.JobId.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".partial");
            output = new HashingOutput(files.Create(TemporaryPath));
        }
        internal static MssqlExportArtifact Create(MssqlExportRequest request) => Create(request, new MssqlArtifactFiles());
        internal static MssqlExportArtifact Create(MssqlExportRequest request, MssqlArtifactFiles files) => new MssqlExportArtifact(request, files);
        internal static ExportDestinationStamp CaptureDestination(string path, CancellationToken cancellation)
        {
            string full = Path.GetFullPath(path); var gate = MssqlArtifactGates.Get(full); gate.Wait(cancellation);
            try { return CaptureCore(full, cancellation); } finally { gate.Release(); }
        }
        private static ExportDestinationStamp CaptureCore(string full, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            FileStream stream;
            try { stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize); }
            catch (FileNotFoundException) { return new ExportDestinationStamp(false, full, null, 0, DateTime.MinValue, null); }
            using (stream)
            {
                FileInformation info;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                string identity = info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8");
                long length; string hash = Hash(stream, null, cancellation, out length);
                long timestamp = ((long)info.WriteHigh << 32) | info.WriteLow;
                return new ExportDestinationStamp(true, full, identity, length, DateTime.FromFileTimeUtc(timestamp), hash);
            }
        }
        internal MssqlArtifactReceipt Complete(IReadOnlyList<MssqlTableRowCount> rows, CancellationToken cancellation)
        {
            if (disposed || receipt != null) throw new InvalidOperationException("Artifact is already closed");
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            long total = 0; foreach (var row in rows) { if (row == null) throw new ArgumentException("Null row count"); total = checked(total + row.Rows); }
            cancellation.ThrowIfCancellationRequested();
            string marker = "\r\n-- mysqlpunk-export-complete job=" + request.JobId.ToString("N") + " tables=" + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows=" + total.ToString(CultureInfo.InvariantCulture) + "\r\n";
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(marker); output.Write(bytes, 0, bytes.Length);
            output.Finish();
            receipt = new MssqlArtifactReceipt(request.JobId, TemporaryPath, request.TargetPath, output.Bytes, output.Digest, rows, marker);
            return receipt;
        }
        internal void Verify(MssqlArtifactReceipt candidate, Action<long, long> progress, CancellationToken cancellation)
        {
            verified = false;
            if (disposed || receipt == null || !ReferenceEquals(candidate, receipt)) throw new InvalidOperationException("Complete this artifact before verifying");
            VerifyFile(TemporaryPath, candidate, progress, cancellation); verified = true;
        }
        internal static void VerifyFile(string path, MssqlArtifactReceipt expected, Action<long, long> progress, CancellationToken cancellation)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                long length; string hash = Hash(stream, progress, cancellation, out length);
                if (length != expected.Bytes || !string.Equals(hash, expected.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Export length or SHA-256 mismatch");
            }
        }
        internal static string Hash(Stream stream, Action<long, long> progress, CancellationToken cancellation, out long length)
        {
            byte[] buffer = new byte[BufferSize]; length = 0; long total = stream.Length;
            using (var hash = SHA256.Create())
            {
                int read;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested(); read = stream.Read(buffer, 0, buffer.Length); if (read == 0) break;
                    hash.TransformBlock(buffer, 0, read, null, 0); length = checked(length + read); progress?.Invoke(length, total);
                }
                cancellation.ThrowIfCancellationRequested(); hash.TransformFinalBlock(buffer, 0, 0); return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }
        internal string Publish(ExportDestinationStamp expectedDestination, CancellationToken cancellation)
        {
            if (disposed || !verified || PublishedPath != null) throw new InvalidOperationException("Artifact must be verified and unpublished");
            var approved = request.Destination;
            if (approved == null || !string.Equals(approved.FullPath, request.TargetPath, StringComparison.OrdinalIgnoreCase) || !SameDestination(expectedDestination, approved)) throw new ArgumentException("Consent stamp does not match the immutable request");
            var gate = MssqlArtifactGates.Get(request.TargetPath); gate.Wait(cancellation);
            try
            {
                VerifyFile(TemporaryPath, receipt, null, cancellation);
                var actual = CaptureCore(request.TargetPath, cancellation);
                if (!SameDestination(actual, approved)) throw new IOException("Destination changed after overwrite consent");
                using (var commit = new MssqlPublicationCommit(cancellation))
                {
                    commit.Enter();
                    if (actual.Exists)
                    {
                        RecoveryPath = Path.Combine(Path.GetDirectoryName(request.TargetPath), ".mysqlpunk-recovery-" + Guid.NewGuid().ToString("N") + ".recovery");
                        try { files.Replace(TemporaryPath, request.TargetPath, RecoveryPath); }
                        catch { preserveCandidates = true; ObserveCandidate(TemporaryPath); ObserveCandidate(RecoveryPath); ObserveCandidate(request.TargetPath); throw; }
                    }
                    else files.Move(TemporaryPath, request.TargetPath);
                    PublishedPath = request.TargetPath;
                }
                // A successful replace retains the old version for explicit recovery, not retention pruning.
                if (RecoveryPath != null) ObserveCandidate(RecoveryPath);
                return PublishedPath;
            }
            finally { gate.Release(); }
        }
        private static bool SameDestination(ExportDestinationStamp left, ExportDestinationStamp right)
        {
            return left != null && right != null && string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase)
                && left.Exists == right.Exists && left.FileIdentity == right.FileIdentity && left.Length == right.Length
                && left.LastWriteTimeUtc == right.LastWriteTimeUtc && left.Sha256 == right.Sha256;
        }
        private void ObserveCandidate(string path)
        {
            try { File.GetAttributes(path); if (!residuals.Contains(path)) residuals.Add(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { if (!residuals.Contains(path)) residuals.Add(path); warnings.Add("Candidate state unknown: " + path + ": " + ex.Message); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { output.Dispose(); } catch (Exception ex) { warnings.Add("Close failed: " + ex.Message); }
            if (PublishedPath != null || preserveCandidates) return;
            try { files.Delete(TemporaryPath); } catch (Exception ex) { residuals.Add(TemporaryPath); warnings.Add("Temporary cleanup failed: " + ex.Message); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            internal uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        private sealed class HashingOutput : Stream
        {
            private readonly Stream inner; private readonly SHA256 hash = SHA256.Create(); private bool closed, faulted;
            internal long Bytes { get; private set; } internal string Digest { get; private set; }
            internal HashingOutput(Stream inner) { this.inner = inner; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (closed || faulted) throw new InvalidOperationException("Output is closed or faulted");
                try { inner.Write(buffer, offset, count); hash.TransformBlock(buffer, offset, count, null, 0); Bytes = checked(Bytes + count); }
                catch { faulted = true; throw; }
            }
            internal void Finish()
            {
                if (closed || faulted) throw new InvalidOperationException("Cannot complete faulted output");
                try { inner.Flush(); inner.Dispose(); closed = true; hash.TransformFinalBlock(new byte[0], 0, 0); Digest = BitConverter.ToString(hash.Hash).Replace("-", ""); }
                catch { faulted = true; throw; }
            }
            public override void Flush() { try { inner.Flush(); } catch { faulted = true; throw; } }
            protected override void Dispose(bool disposing) { if (disposing) { try { if (!closed) inner.Dispose(); } finally { closed = true; hash.Dispose(); } } base.Dispose(disposing); }
            public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => !closed && !faulted;
            public override long Length => Bytes; public override long Position { get => Bytes; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
