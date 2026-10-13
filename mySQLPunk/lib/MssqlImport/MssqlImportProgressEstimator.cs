using System;
using System.Collections.Generic;

namespace mySQLPunk.lib.MssqlImport
{
    // Worker-only, confirmed byte boundaries; no file-position, row-count or SQL inference.
    internal sealed class MssqlImportProgressEstimator
    {
        private sealed class Point
        {
            internal readonly DateTime Time;
            internal readonly long Bytes;
            internal Point(DateTime time, long bytes) { Time = time; Bytes = bytes; }
        }
        private readonly List<Point> samples = new List<Point>(64);
        private MssqlImportProgress latest;
        private DateTime observedAt;
        private DateTime? lastAdvance;
        private bool reported;
        private Guid reportedJob;
        private MssqlImportStage reportedStage;
        private DateTime reportedAt;

        internal void Observe(MssqlImportProgress progress, DateTime utcNow)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            bool reset = latest == null || latest.JobId != progress.JobId || latest.FileBytes != progress.FileBytes ||
                utcNow < observedAt || progress.ProcessedBytes < latest.ProcessedBytes;
            long previous = reset ? 0 : latest.ProcessedBytes;
            if (reset) { samples.Clear(); lastAdvance = null; }
            latest = progress; observedAt = utcNow;
            if (!CanEstimate(progress)) { samples.Clear(); lastAdvance = null; return; }
            if (progress.ProcessedBytes > previous)
            {
                // Update confirmed counters/timestamp even when a UI report or rate sample is throttled.
                lastAdvance = utcNow;
                if (samples.Count == 0 || utcNow - samples[samples.Count - 1].Time >= TimeSpan.FromMilliseconds(200))
                    samples.Add(new Point(utcNow, progress.ProcessedBytes));
            }
            Trim(utcNow);
        }
        private void Trim(DateTime utcNow)
        {
            DateTime cutoff = utcNow > DateTime.MinValue.AddSeconds(10) ? utcNow.AddSeconds(-10) : DateTime.MinValue;
            // Keep one sample at/before the window boundary so an interval remains measurable.
            while (samples.Count > 1 && samples[1].Time <= cutoff) samples.RemoveAt(0);
            while (samples.Count > 64) samples.RemoveAt(0);
        }
        private static bool CanEstimate(MssqlImportProgress progress)
        {
            return progress.Stage == MssqlImportStage.Executing && progress.Phase != MssqlImportPhase.Indexes &&
                progress.Phase != MssqlImportPhase.Constraints;
        }
        private static MssqlImportEstimate Unknown() { return new MssqlImportEstimate(null, null, false); }
        internal MssqlImportEstimate Estimate(DateTime utcNow)
        {
            if (latest == null || !CanEstimate(latest) || !lastAdvance.HasValue || utcNow < observedAt ||
                utcNow - lastAdvance.Value >= TimeSpan.FromSeconds(5) || latest.ProcessedBytes < 0 ||
                latest.FileBytes <= latest.ProcessedBytes) return Unknown();
            Trim(utcNow);
            if (samples.Count < 3) return Unknown();
            Point first = samples[0], last = samples[samples.Count - 1];
            double span = (last.Time - first.Time).TotalSeconds;
            if (span < 2) return Unknown();
            double rateSum = 0, rateSquares = 0;
            int rates = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                double rate = (samples[i].Bytes - samples[i - 1].Bytes) / (samples[i].Time - samples[i - 1].Time).TotalSeconds;
                if (rate <= 0) continue;
                rateSum += rate; rateSquares += rate * rate; rates++;
            }
            if (rates == 0) return Unknown();
            double mean = rateSum / rates;
            double deviation = Math.Sqrt(Math.Max(0, rateSquares / rates - mean * mean));
            if (deviation / mean > 1) return Unknown();
            double speed = (last.Bytes - first.Bytes) / span;
            double seconds = (latest.FileBytes - latest.ProcessedBytes) / speed;
            if (speed <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 ||
                seconds >= TimeSpan.MaxValue.TotalSeconds || seconds >= (DateTime.MaxValue - utcNow).TotalSeconds) return Unknown();
            return new MssqlImportEstimate(TimeSpan.FromSeconds(seconds), utcNow.AddSeconds(seconds), true);
        }
        internal bool ShouldReport(MssqlImportProgress progress, DateTime utcNow)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            bool force = !reported || reportedJob != progress.JobId || reportedStage != progress.Stage ||
                progress.Stage == MssqlImportStage.Finished || utcNow < reportedAt;
            if (!force && utcNow - reportedAt < TimeSpan.FromMilliseconds(200)) return false;
            reported = true; reportedJob = progress.JobId; reportedStage = progress.Stage; reportedAt = utcNow;
            return true;
        }
    }
}
