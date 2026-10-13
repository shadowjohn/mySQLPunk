using System;
using System.Collections.Generic;
using System.Linq;

namespace mySQLPunk.lib.MssqlExport
{
    // All time and work are supplied by the caller. This class neither polls nor queries SQL.
    internal sealed class MssqlExportProgressEstimator
    {
        private readonly Guid jobId;
        private readonly MssqlExportSchema schema;
        private readonly bool mirror;
        private readonly HashSet<int> completed = new HashSet<int>();
        private readonly Queue<Tuple<TimeSpan, double>> samples = new Queue<Tuple<TimeSpan, double>>();
        private MssqlObjectKey current;
        private long tableRows, tableBytes, completedRows, completedBytes, lastRows;
        private MssqlExportStage lastStage;
        private bool emitted;
        private TimeSpan lastReport, lastAdvance;
        private double lastWork;
        private double? lastPercent;
        internal MssqlExportProgressEstimator(Guid jobId, MssqlExportSchema schema, bool includeMirror)
        { this.jobId = jobId; this.schema = schema ?? throw new ArgumentNullException(nameof(schema)); mirror = includeMirror; }
        internal MssqlExportProgress Sample(MssqlExportStage stage, MssqlObjectKey currentTable, long currentTableRows,
            long currentTableSourceBytes, long writtenBytes, TimeSpan elapsed, bool force)
        { return Build(stage, currentTable, currentTableRows, currentTableSourceBytes, writtenBytes, 0, null, elapsed, force); }
        internal MssqlExportProgress SamplePhase(MssqlExportStage stage, long processedBytes, long totalBytes, long writtenBytes, TimeSpan elapsed, bool force)
        { return Build(stage, null, 0, 0, writtenBytes, processedBytes, totalBytes, elapsed, force || processedBytes == totalBytes); }
        private MssqlExportProgress Build(MssqlExportStage stage, MssqlObjectKey table, long rows, long bytes, long written,
            long phaseBytes, long? phaseTotal, TimeSpan elapsed, bool force)
        {
            bool changedTable = !Same(current, table), changedStage = !emitted || lastStage != stage;
            bool terminal = stage == MssqlExportStage.Completed || stage == MssqlExportStage.Canceled || stage == MssqlExportStage.Failed || stage == MssqlExportStage.LocalCompletedMirrorIncomplete;
            if (changedTable)
            {
                if (current != null)
                {
                    // A terminal failure does not claim that its interrupted table completed.
                    completedRows += tableRows; completedBytes += tableBytes;
                    if ((!terminal && stage != MssqlExportStage.RestoringSnapshot && stage != MssqlExportStage.EnablingSnapshot) || stage == MssqlExportStage.Completed) completed.Add(current.ObjectId);
                }
                current = table; tableRows = 0; tableBytes = 0;
            }
            tableRows = Math.Max(tableRows, rows); tableBytes = Math.Max(tableBytes, bytes);
            long actualRows = completedRows + tableRows, actualBytes = completedBytes + tableBytes;
            double work = stage == MssqlExportStage.Data ? actualBytes : phaseBytes;
            if (changedStage) { samples.Clear(); lastWork = work; lastAdvance = elapsed; }
            else if (work > lastWork || stage == MssqlExportStage.Data && actualRows > lastRows) { lastAdvance = elapsed; lastWork = work; }
            lastRows = actualRows;
            // Actual progress time is updated even for suppressed UI samples.
            if (!force && !changedStage && !changedTable && !terminal && elapsed - lastReport < TimeSpan.FromMilliseconds(200)) return null;
            if (changedStage || samples.Count == 0 || elapsed > samples.Last().Item1) samples.Enqueue(Tuple.Create(elapsed, work));
            while (samples.Count > 1 && elapsed - samples.Peek().Item1 > TimeSpan.FromSeconds(10)) samples.Dequeue();
            lastStage = stage; lastReport = elapsed; emitted = true;
            long? estimatedRows = schema.Tables.All(t => t.EstimatedRows.HasValue) ? (long?)schema.Tables.Sum(t => t.EstimatedRows.Value) : null;
            long? estimatedBytes = schema.Tables.All(t => t.EstimatedBytes.HasValue) ? (long?)schema.Tables.Sum(t => t.EstimatedBytes.Value) : null;
            double? remaining = null, candidate = null;
            bool unreliable = false;
            double dataCeiling = mirror ? 75 : 85;
            if (stage == MssqlExportStage.Data)
            {
                double rest = 0; bool known = true;
                foreach (var t in schema.Tables)
                {
                    if (completed.Contains(t.Key.ObjectId)) continue;
                    if (!t.EstimatedRows.HasValue || !t.EstimatedBytes.HasValue) { known = false; continue; }
                    if (Same(t.Key, current))
                    {
                        if (tableRows > t.EstimatedRows || tableBytes > t.EstimatedBytes * 4.0) unreliable = true;
                        double width = tableRows > 0 ? tableBytes / (double)tableRows : t.EstimatedBytes.Value / (double)Math.Max(1, t.EstimatedRows.Value);
                        rest += Math.Max(0, Math.Max(t.EstimatedBytes.Value - tableBytes, (t.EstimatedRows.Value - tableRows) * width));
                    }
                    else rest += t.EstimatedBytes.Value;
                }
                if (known && !unreliable) { remaining = rest; candidate = dataCeiling * actualBytes / Math.Max(1, actualBytes + rest); }
            }
            else if (stage == MssqlExportStage.Verifying || stage == MssqlExportStage.Mirroring)
            {
                double start = stage == MssqlExportStage.Verifying ? dataCeiling : 90;
                double span = stage == MssqlExportStage.Verifying ? 10 : 9;
                if (phaseTotal.HasValue) { remaining = Math.Max(0, phaseTotal.Value - phaseBytes); candidate = start + span * Math.Min(1, phaseBytes / (double)Math.Max(1, phaseTotal.Value)); }
                else candidate = start;
            }
            else if (stage == MssqlExportStage.Publishing) candidate = mirror ? 85 : 95;
            if (candidate.HasValue) lastPercent = Math.Max(lastPercent ?? 0, Math.Min(99, candidate.Value));
            TimeSpan? eta = null; double? bytesPerSecond = null; var state = unreliable ? MssqlExportEstimationState.Unreliable : MssqlExportEstimationState.Estimating;
            if (elapsed - lastAdvance >= TimeSpan.FromSeconds(5)) state = MssqlExportEstimationState.Waiting;
            else if (!unreliable && samples.Count >= 3 && elapsed - samples.Peek().Item1 >= TimeSpan.FromSeconds(2))
            {
                var points = samples.ToArray(); double low = double.MaxValue, high = 0;
                for (int i = 1; i < points.Length; i++)
                {
                    double dt = (points[i].Item1 - points[i - 1].Item1).TotalSeconds;
                    double rate = dt > 0 ? (points[i].Item2 - points[i - 1].Item2) / dt : 0;
                    if (rate > 0) { low = Math.Min(low, rate); high = Math.Max(high, rate); }
                }
                double speed = (work - points[0].Item2) / (elapsed - points[0].Item1).TotalSeconds;
                if (speed > 0 && high <= low * 4)
                {
                    bytesPerSecond = speed;
                    double? seconds = remaining / speed;
                    if (seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds) { eta = TimeSpan.FromSeconds(seconds.Value); state = MssqlExportEstimationState.Measuring; }
                }
                else state = MssqlExportEstimationState.Unreliable;
            }
            if (stage == MssqlExportStage.EnablingSnapshot || stage == MssqlExportStage.RestoringSnapshot) state = MssqlExportEstimationState.Waiting;
            if (terminal || stage == MssqlExportStage.Publishing || stage == MssqlExportStage.EnablingSnapshot || stage == MssqlExportStage.RestoringSnapshot) { eta = null; bytesPerSecond = null; }
            if (stage == MssqlExportStage.Completed) { lastPercent = 100; state = MssqlExportEstimationState.Complete; }
            string message = state == MssqlExportEstimationState.Waiting ? "Waiting for progress" : stage.ToString();
            return new MssqlExportProgress(jobId, stage, current, tableRows, tableBytes, actualRows, actualBytes, written,
                estimatedRows, estimatedBytes, completed.Count, schema.Tables.Count, elapsed, lastAdvance, bytesPerSecond, eta, lastPercent, state, phaseBytes, phaseTotal, message);
        }
        private static bool Same(MssqlObjectKey a, MssqlObjectKey b)
        { return ReferenceEquals(a, b) || a != null && b != null && a.ObjectId == b.ObjectId && a.Schema == b.Schema && a.Name == b.Name; }
    }
}
