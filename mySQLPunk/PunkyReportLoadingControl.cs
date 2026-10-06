using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Media;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace mySQLPunk
{
    public sealed class PunkyReportLoadingControl : Control
    {
        private readonly Timer timer;
        private const int FrameCount = 73;
        private const int AtlasColumns = 8;
        private const int AudioDurationMilliseconds = 6000;
        private Image atlas;
        private SoundPlayer audio;
        private MemoryStream audioStream;
        private readonly Stopwatch audioPlayback = new Stopwatch();
        private bool sprite, running, exportActive, muted;
        private int frameSide;
        private int frame;
        public bool Muted
        {
            get { return muted; }
            set { muted = value; if (value) StopAudio(); }
        }
        public bool AudioAvailable { get { return audio != null; } }
        public PunkyReportLoadingControl()
        {
            Name = "reportPunky";
            AccessibleName = "Punky";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "image");
            LoadArtwork(root);
            LoadAudio(root);
            timer = new Timer { Interval = 83 };
            timer.Tick += (s, e) => { frame = (frame + 1) % FrameCount; Invalidate(); };
            Size = new Size(320, 320);
        }
        public void BeginExport()
        {
            if (exportActive || IsDisposed) return;
            exportActive = true;
            running = true;
            frame = 0;
            UpdateTimer();
            Invalidate();
            // Sound belongs to the first cycle only. Unmuting never replays it.
            if (!muted && audio != null)
            {
                try { audio.Play(); audioPlayback.Restart(); }
                catch (Exception error) when (IsOptionalMediaError(error)) { DisableAudio(); }
            }
        }
        public void EndExport()
        {
            exportActive = false;
            running = false;
            frame = 0;
            StopAudio();
            UpdateTimer();
            Invalidate();
        }
        // Compatibility for non-export activity: this never starts sound.
        public void SetRunning(bool value)
        {
            if (!value) { EndExport(); return; }
            running = true;
            UpdateTimer();
            Invalidate();
        }
        private void LoadArtwork(string root)
        {
            try
            {
                string path = Path.Combine(root, "punky_report_loading_atlas.png");
                sprite = File.Exists(path);
                if (!sprite) path = Path.Combine(root, "punky_report_loading.png");
                if (!File.Exists(path)) return;
                using (Image original = Image.FromFile(path)) atlas = new Bitmap(original);
                frameSide = atlas.Width / AtlasColumns;
                sprite = sprite && frameSide > 0 && atlas.Width == frameSide * AtlasColumns
                    && atlas.Height == frameSide * ((FrameCount + AtlasColumns - 1) / AtlasColumns);
            }
            catch (Exception error) when (IsOptionalMediaError(error))
            {
                if (atlas != null) atlas.Dispose();
                atlas = null;
                sprite = false;
            }
        }
        private void LoadAudio(string root)
        {
            try
            {
                string path = Path.Combine(root, "punky_report_loading.wav");
                // This small local asset is loaded once, so Play does not initiate a
                // background file load whose error could escape the export handler.
                if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return;
                audioStream = new MemoryStream(File.ReadAllBytes(path), false);
                audio = new SoundPlayer(audioStream) { LoadTimeout = 1000 };
                audio.Load();
            }
            catch (Exception error) when (IsOptionalMediaError(error)) { DisableAudio(); }
        }
        private static bool IsOptionalMediaError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is InvalidOperationException
                || error is TimeoutException || error is ExternalException
                || error is System.Security.SecurityException;
        }
        private void StopAudio()
        {
            // Avoid issuing a stop after our one-shot sound has already finished.
            if (audio != null && audioPlayback.IsRunning
                && audioPlayback.ElapsedMilliseconds < AudioDurationMilliseconds)
            {
                try { audio.Stop(); }
                catch (Exception error) when (IsOptionalMediaError(error)) { }
            }
            audioPlayback.Reset();
        }
        private void DisableAudio()
        {
            StopAudio();
            if (audio != null) { audio.Dispose(); audio = null; }
            if (audioStream != null) { audioStream.Dispose(); audioStream = null; }
        }
        private void UpdateTimer()
        {
            if (timer != null) timer.Enabled = running && sprite && Visible && !IsDisposed;
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) StopAudio();
            UpdateTimer();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            if (atlas == null) return;
            Rectangle source = sprite
                ? new Rectangle(frame % AtlasColumns * frameSide, frame / AtlasColumns * frameSide, frameSide, frameSide)
                : new Rectangle(0, 0, atlas.Width, atlas.Height);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int side = Math.Min(ClientSize.Width, ClientSize.Height);
            e.Graphics.DrawImage(atlas, new Rectangle((Width - side) / 2, (Height - side) / 2, side, side), source, GraphicsUnit.Pixel);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                DisableAudio();
                if (atlas != null) { atlas.Dispose(); atlas = null; }
            }
            base.Dispose(disposing);
        }
    }
}
