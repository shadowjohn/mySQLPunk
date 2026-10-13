using System;
using System.IO;
using System.Text;
using System.Threading;
using mySQLPunk.lib.MssqlExport;
public static partial class SmokeTests
{
    // Removing hash checks, commit ordering or ownership cleanup must break these cases.
    private static void Artifact_Pipeline()
    {
        string root = Path.Combine(Path.GetTempPath(), "mssql-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "fixture_backup_current.sql");
            File.WriteAllText(target, "old fixture");
            string other = Path.Combine(root, ".mysqlpunk-export-other.partial");
            File.WriteAllText(other, "other");
            var stamp = MssqlExportArtifact.CaptureDestination(target, CancellationToken.None);
            var request = ArtifactRequest(target, stamp);
            using (var artifact = MssqlExportArtifact.Create(request))
            {
                var receipt = FinishArtifact(artifact);
                AssertThrows<InvalidOperationException>(() => artifact.Publish(stamp, CancellationToken.None), "Unverified output cannot publish");
                using (var truncate = new FileStream(artifact.TemporaryPath, FileMode.Open, FileAccess.Write)) truncate.SetLength(receipt.Bytes - 1);
                AssertThrows<InvalidDataException>(() => artifact.Verify(receipt, null, CancellationToken.None), "Truncated output fails verification");
            }
            Assert(File.ReadAllText(target) == "old fixture" && File.Exists(other), "Failure preserves old and unrelated partial");
            using (var artifact = MssqlExportArtifact.Create(request))
            {
                var receipt = FinishArtifact(artifact);
                artifact.Verify(receipt, null, CancellationToken.None);
                var time = File.GetLastWriteTimeUtc(target);
                File.WriteAllText(target, "new fixture"); File.SetLastWriteTimeUtc(target, time);
                AssertThrows<IOException>(() => artifact.Publish(stamp, CancellationToken.None), "Same size/time changes fail consent recheck");
            }
            stamp = MssqlExportArtifact.CaptureDestination(target, CancellationToken.None);
            request = ArtifactRequest(target, stamp);
            using (var artifact = MssqlExportArtifact.Create(request))
            {
                var receipt = FinishArtifact(artifact);
                long last = 0;
                artifact.Verify(receipt, (done, total) => { Assert(done - last <= 81920, "Verification bounded read"); last = done; }, CancellationToken.None);
                Assert(last == receipt.Bytes, "Entire final marker is verified");
                var canceled = new CancellationTokenSource(); canceled.Cancel();
                AssertThrows<OperationCanceledException>(() => artifact.Publish(stamp, canceled.Token), "Cancel before commit");
                Assert(artifact.Publish(stamp, CancellationToken.None) == target, "Published path retained");
                canceled.Cancel(); Assert(artifact.PublishedPath == target, "Late cancel does not erase publication");
                var mirror = new MssqlExportMirror();
                Assert(mirror.CopyVerified(target, receipt, new MssqlMirrorOptions(root, 1), null, CancellationToken.None) == string.Empty, "Same directory is noop");
                string remote = Path.Combine(root, "remote"); Directory.CreateDirectory(remote);
                string old = Path.Combine(remote, "old_backup_fixture.sql"); File.WriteAllText(old, "old fixture"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
                string mirrorPath = mirror.CopyVerified(target, receipt, new MssqlMirrorOptions(remote, 1), null, CancellationToken.None);
                Assert(File.Exists(mirrorPath) && !File.Exists(old), "Verified mirror published before retention");
                var stampMirror = MssqlExportArtifact.CaptureDestination(mirrorPath, CancellationToken.None);
                Assert(stampMirror.Sha256 == receipt.Sha256 && stampMirror.Length == receipt.Bytes, "Mirror matches final receipt");
                AssertThrows<OperationCanceledException>(() => new MssqlExportMirror().CopyVerified(target, receipt, new MssqlMirrorOptions(remote, 1), null, canceled.Token), "Canceled mirror must not prune");
                Assert(File.Exists(mirrorPath), "Canceled mirror preserves old backups");
            }
            string consentPath = Path.Combine(root,"consent.sql");
            File.WriteAllText(consentPath,"approved fixture A");
            var approved = MssqlExportArtifact.CaptureDestination(consentPath,CancellationToken.None);
            var approvedRequest = ArtifactRequest(consentPath,approved);
            File.WriteAllText(consentPath,"changed fixture B");
            var refreshed = MssqlExportArtifact.CaptureDestination(consentPath,CancellationToken.None);
            using(var artifact=MssqlExportArtifact.Create(approvedRequest))
            {
                var receipt=FinishArtifact(artifact);artifact.Verify(receipt,null,CancellationToken.None);
                AssertThrows<ArgumentException>(()=>artifact.Publish(refreshed,CancellationToken.None),"Recaptured stamp cannot refresh immutable request consent");
                Assert(File.ReadAllText(consentPath)=="changed fixture B"&&artifact.PublishedPath==null,"Rejected refreshed consent preserves newer destination");
            }
            string corrupt = Path.Combine(root,"postverify.sql");
            var absent = MssqlExportArtifact.CaptureDestination(corrupt,CancellationToken.None);
            using(var artifact=MssqlExportArtifact.Create(ArtifactRequest(corrupt,absent)))
            {
                var receipt=FinishArtifact(artifact);artifact.Verify(receipt,null,CancellationToken.None);
                using(var tamper=new FileStream(artifact.TemporaryPath,FileMode.Open,FileAccess.Write)) tamper.WriteByte(0);
                AssertThrows<InvalidDataException>(()=>artifact.Publish(absent,CancellationToken.None),"Mutation after verification cannot publish corrupt output");
                Assert(!File.Exists(corrupt),"Invalid output leaves absent destination absent");
            }
            ArtifactFailures(root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static MssqlExportRequest ArtifactRequest(string path, ExportDestinationStamp stamp)
    {
        return new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] {new MssqlObjectKey(1,"dbo","T")}, MssqlExportMode.SchemaAndData, false, path, stamp, null);
    }
    private static MssqlArtifactReceipt FinishArtifact(MssqlExportArtifact artifact)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("-- fixture\r\n" + new string('x', 180000));
        artifact.Output.Write(bytes, 0, bytes.Length);
        return artifact.Complete(new[] {new MssqlTableRowCount(new MssqlObjectKey(1,"dbo","T"), 3000000000L)}, CancellationToken.None);
    }
    private static void ArtifactFailures(string root)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            string path = Path.Combine(root, "replace" + mode + ".sql"); File.WriteAllText(path, "old fixture");
            var stamp = MssqlExportArtifact.CaptureDestination(path, CancellationToken.None);
            var ops = new ArtifactFaultFiles { ReplaceFailure = mode + 1 };
            var artifact = MssqlExportArtifact.Create(ArtifactRequest(path, stamp), ops);
            var receipt = FinishArtifact(artifact); artifact.Verify(receipt, null, CancellationToken.None);
            AssertThrows<IOException>(() => artifact.Publish(stamp, CancellationToken.None), "Documented partial replace failure");
            artifact.Dispose();
            Assert(artifact.ResidualPaths.Count > 0, "Ambiguous candidates reported and preserved");
            string oldPath = mode == 2 ? artifact.RecoveryPath : path;
            Assert(File.ReadAllText(oldPath) == "old fixture", "Old bytes recoverable after replacement failure");
        }
        string cleanupPath = Path.Combine(root, "cleanup.sql");
        var cleanup = MssqlExportArtifact.Create(ArtifactRequest(cleanupPath, MssqlExportArtifact.CaptureDestination(cleanupPath, CancellationToken.None)), new ArtifactFaultFiles { FailDelete = true });
        cleanup.Dispose(); Assert(cleanup.ResidualPaths.Count == 1 && File.Exists(cleanup.TemporaryPath), "Failed cleanup reports owned residual");
        string local = Path.Combine(root,"mirror-source.sql");
        using(var artifact = MssqlExportArtifact.Create(ArtifactRequest(local, MssqlExportArtifact.CaptureDestination(local,CancellationToken.None))))
        {
            var receipt=FinishArtifact(artifact); artifact.Verify(receipt,null,CancellationToken.None); artifact.Publish(MssqlExportArtifact.CaptureDestination(local,CancellationToken.None),CancellationToken.None);
            var ops=new ArtifactFaultFiles { FailMove=true };
            var mirror=new MssqlExportMirror(ops);
            AssertThrows<IOException>(()=>mirror.CopyVerified(local,receipt,new MssqlMirrorOptions(Path.Combine(root,"failed-remote"),1),null,CancellationToken.None),"Move failure");
            Assert(ops.PruneCalls==0,"Failed mirror never prunes");
            var cancel=new CancellationTokenSource(); ops=new ArtifactFaultFiles { AfterMove=()=>cancel.Cancel() }; mirror=new MssqlExportMirror(ops);
            string published=mirror.CopyVerified(local,receipt,new MssqlMirrorOptions(Path.Combine(root,"late-cancel"),1),null,cancel.Token);
            Assert(File.Exists(published)&&mirror.MirrorPublishedPath==published&&ops.PruneCalls==0,"Late cancellation retains mirror and skips retention");
            ops=new ArtifactFaultFiles { FailPrune=true }; mirror=new MssqlExportMirror(ops);
            published=mirror.CopyVerified(local,receipt,new MssqlMirrorOptions(Path.Combine(root,"prune-fail"),1),null,CancellationToken.None);
            Assert(File.Exists(published)&&mirror.Warnings.Count>0,"Retention failure does not erase mirror publication");
        }
    }
    private static void Artifact_Adversarial()
    {
        string root=Path.Combine(Path.GetTempPath(),"mssql-fault-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"source_backup_fixture.sql"); File.WriteAllText(path,"old fixture");
            var stamp=MssqlExportArtifact.CaptureDestination(path,CancellationToken.None);
            foreach(bool flush in new[]{false,true})
            {
                var ops=new ArtifactFaultFiles { FailWrite=!flush, FailFlush=flush };
                using(var artifact=MssqlExportArtifact.Create(ArtifactRequest(path,stamp),ops))
                {
                    AssertThrows<IOException>(()=>FinishArtifact(artifact),"Disk write/flush failure cannot create completed receipt");
                    AssertThrows<InvalidOperationException>(()=>artifact.Publish(stamp,CancellationToken.None),"Faulted artifact cannot publish");
                }
                Assert(File.ReadAllText(path)=="old fixture","Disk failure leaves old version intact");
            }
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                AssertThrows<IOException>(()=>MssqlExportArtifact.CaptureDestination(path,CancellationToken.None),"Locked target is not absence");
            using(var artifact=MssqlExportArtifact.Create(ArtifactRequest(path,stamp)))
            {
                var receipt=FinishArtifact(artifact);
                using(var cancel=new CancellationTokenSource())
                    AssertThrows<OperationCanceledException>(()=>artifact.Verify(receipt,(done,total)=>cancel.Cancel(),cancel.Token),"Cancel while hashing");
                AssertThrows<InvalidOperationException>(()=>artifact.Publish(stamp,CancellationToken.None),"Canceled verifier cannot authorize publish");
                artifact.Verify(receipt,null,CancellationToken.None); artifact.Publish(stamp,CancellationToken.None);
                string remote=Path.Combine(root,"remote");Directory.CreateDirectory(remote);
                string prior=Path.Combine(remote,"old_backup_fixture.sql");File.WriteAllText(prior,"old fixture");
                var ops=new ArtifactFaultFiles { CollideMove=true }; var mirror=new MssqlExportMirror(ops);
                AssertThrows<IOException>(()=>mirror.CopyVerified(path,receipt,new MssqlMirrorOptions(remote,1),null,CancellationToken.None),"Mirror collision never overwrites");
                Assert(File.ReadAllText(ops.CollisionPath)=="collision fixture"&&File.Exists(prior)&&ops.PruneCalls==0,"Collision preserves other output and skips prune");
                ops=new ArtifactFaultFiles { FailWrite=true };mirror=new MssqlExportMirror(ops);
                AssertThrows<IOException>(()=>mirror.CopyVerified(path,receipt,new MssqlMirrorOptions(remote,1),null,CancellationToken.None),"Mirror disk full");
                Assert(ops.PruneCalls==0&&File.Exists(prior),"Copy failure never prunes");
                string active=Path.Combine(remote,".mysqlpunk-export-active.partial");File.WriteAllText(active,"other");
                string future=Path.Combine(remote,"future_backup_fixture.sql");File.WriteAllText(future,"old fixture");File.SetLastWriteTimeUtc(future,DateTime.UtcNow.AddDays(2));
                mirror=new MssqlExportMirror();string published=mirror.CopyVerified(path,receipt,new MssqlMirrorOptions(remote,1),null,CancellationToken.None);
                Assert(File.Exists(published)&&File.Exists(active)&&mirror.Warnings.Count>0,"Future timestamps cannot prune newly published mirror or active partial");
                File.WriteAllText(path,"changed fixture");ops=new ArtifactFaultFiles();mirror=new MssqlExportMirror(ops);
                AssertThrows<InvalidDataException>(()=>mirror.CopyVerified(path,receipt,new MssqlMirrorOptions(remote,1),null,CancellationToken.None),"Changed published source cannot be mirrored as old receipt");
                Assert(ops.PruneCalls==0&&File.Exists(published),"Invalid mirror never prunes");
            }
        }
        finally { Directory.Delete(root,true); }
    }
    private static void Artifact_CooperativeGates()
    {
        string root=Path.Combine(Path.GetTempPath(),"mssql-gate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"target.sql");File.WriteAllText(path,"old fixture");
            var stamp=MssqlExportArtifact.CaptureDestination(path,CancellationToken.None);
            using(var entered=new ManualResetEventSlim())
            using(var release=new ManualResetEventSlim())
            using(var captureStarted=new ManualResetEventSlim())
            using(var cancel=new CancellationTokenSource())
            {
                var ops=new ArtifactFaultFiles { BeforeReplace=()=> { entered.Set(); if(!release.Wait(10000)) throw new TimeoutException(); } };
                using(var first=MssqlExportArtifact.Create(ArtifactRequest(path,stamp),ops))
                using(var second=MssqlExportArtifact.Create(ArtifactRequest(path,stamp)))
                {
                    var receipt=FinishArtifact(first);first.Verify(receipt,null,CancellationToken.None);
                    receipt=FinishArtifact(second);second.Verify(receipt,null,CancellationToken.None);
                    var publish=System.Threading.Tasks.Task.Run(()=>first.Publish(stamp,CancellationToken.None));
                    Assert(entered.Wait(10000),"Publisher enters replacement");
                    var capture=System.Threading.Tasks.Task.Run(()=> {captureStarted.Set();return MssqlExportArtifact.CaptureDestination(path,cancel.Token);});
                    try
                    {
                        Assert(captureStarted.Wait(10000),"Capture started");cancel.Cancel();
                        AssertThrows<OperationCanceledException>(()=>capture.GetAwaiter().GetResult(),"Consent capture shares cancellable publication gate");
                    }
                    finally { release.Set(); }
                    Assert(publish.GetAwaiter().GetResult()==path,"First publisher completes");
                    AssertThrows<IOException>(()=>second.Publish(stamp,CancellationToken.None),"Second cooperating job rejects stale consent");
                }
            }
        }
        finally { Directory.Delete(root,true); }
    }
    private sealed class ArtifactFailureStream : Stream
    {
        private readonly Stream inner; private readonly bool write,flush;
        internal ArtifactFailureStream(Stream inner,bool write,bool flush){this.inner=inner;this.write=write;this.flush=flush;}
        public override void Write(byte[] b,int o,int c){if(write)throw new IOException("Injected disk full");inner.Write(b,o,c);}
        public override void Flush(){if(flush)throw new IOException("Injected flush full");inner.Flush();}
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin k)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();
    }
    private sealed class ArtifactFaultFiles : MssqlArtifactFiles
    {
        internal int ReplaceFailure; internal bool FailMove, FailDelete, FailPrune; internal int PruneCalls; internal Action AfterMove, BeforeReplace; internal bool FailWrite, FailFlush, CollideMove; internal string CollisionPath;
        internal override Stream Create(string path) { var stream=base.Create(path); return FailWrite||FailFlush ? new ArtifactFailureStream(stream,FailWrite,FailFlush) : stream; }
        internal override void Replace(string source,string target,string recovery)
        {
            BeforeReplace?.Invoke();
            if(ReplaceFailure==3) File.Move(target,recovery);
            if(ReplaceFailure>0) throw new IOException("Injected documented replace failure " + ReplaceFailure);
            base.Replace(source,target,recovery);
        }
        internal override void Move(string source,string target) { if(FailMove) throw new IOException("Injected rename failure"); if(CollideMove) {CollisionPath=target;File.WriteAllText(target,"collision fixture");} base.Move(source,target); AfterMove?.Invoke(); }
        internal override void Delete(string path) { if(FailDelete) throw new IOException("Injected cleanup failure"); base.Delete(path); }
        internal override void Prune(string directory,int count) { PruneCalls++; if(FailPrune) throw new IOException("Injected retention failure"); base.Prune(directory,count); }
    }
}
