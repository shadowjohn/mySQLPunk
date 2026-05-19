using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk
{
    public sealed class RunnerProgressOverlay : IDisposable
    {
        private readonly Form owner;
        private readonly Form maskForm;
        private readonly Form progressForm;
        private bool disposed;

        public AnimatedRunnerProgressBar ProgressBar { get; private set; }

        private RunnerProgressOverlay(Form ownerForm, string title, string initialMessage)
        {
            owner = ownerForm;

            maskForm = new Form
            {
                BackColor = Color.Black,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0.34,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = owner != null && owner.TopMost
            };

            progressForm = new Form
            {
                Text = title,
                BackColor = ThemeManager.ElevatedColor,
                ClientSize = new Size(560, 138),
                ControlBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = owner != null && owner.TopMost
            };

            ProgressBar = new AnimatedRunnerProgressBar
            {
                Location = new Point(22, 28),
                Size = new Size(516, 88),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                BackColor = ThemeManager.ElevatedColor,
                ForeColor = ThemeManager.TextColor
            };
            ProgressBar.LoadRunnerImage(Path.Combine(Application.StartupPath, "image", "progress_runner.gif"));
            ProgressBar.SetProgress(0, 1, initialMessage);
            progressForm.Controls.Add(ProgressBar);
            ThemeManager.ApplyTo(progressForm);
            progressForm.BackColor = ThemeManager.ElevatedColor;
            ProgressBar.BackColor = ThemeManager.ElevatedColor;
            ProgressBar.ForeColor = ThemeManager.TextColor;

            if (owner != null)
            {
                owner.Move += OwnerBoundsChanged;
                owner.Resize += OwnerBoundsChanged;
                owner.SizeChanged += OwnerBoundsChanged;
                owner.VisibleChanged += OwnerVisibleChanged;
            }
        }

        public static RunnerProgressOverlay Show(Form owner, string title, string initialMessage)
        {
            RunnerProgressOverlay overlay = new RunnerProgressOverlay(owner, title, initialMessage);
            overlay.Show();
            return overlay;
        }

        public void SetProgress(int current, int total, string message)
        {
            if (disposed || ProgressBar == null || ProgressBar.IsDisposed) return;
            ProgressBar.SetProgress(current, total, message);
            ProgressBar.Refresh();
            progressForm.Refresh();
            Application.DoEvents();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (owner != null)
            {
                owner.Move -= OwnerBoundsChanged;
                owner.Resize -= OwnerBoundsChanged;
                owner.SizeChanged -= OwnerBoundsChanged;
                owner.VisibleChanged -= OwnerVisibleChanged;
            }

            CloseForm(progressForm);
            CloseForm(maskForm);
        }

        private void Show()
        {
            Rectangle bounds = GetOverlayBounds();
            maskForm.Bounds = bounds;
            CenterProgressForm(bounds);

            if (owner != null && owner.IsHandleCreated)
            {
                maskForm.Show(owner);
                progressForm.Show(maskForm);
            }
            else
            {
                maskForm.Show();
                progressForm.Show(maskForm);
            }

            maskForm.Refresh();
            progressForm.Refresh();
            Application.DoEvents();
        }

        private void OwnerBoundsChanged(object sender, EventArgs e)
        {
            if (disposed) return;
            Rectangle bounds = GetOverlayBounds();
            maskForm.Bounds = bounds;
            CenterProgressForm(bounds);
        }

        private void OwnerVisibleChanged(object sender, EventArgs e)
        {
            if (disposed || owner == null) return;
            maskForm.Visible = owner.Visible;
            progressForm.Visible = owner.Visible;
        }

        private Rectangle GetOverlayBounds()
        {
            if (owner != null && owner.IsHandleCreated)
            {
                Rectangle clientBounds = owner.RectangleToScreen(owner.ClientRectangle);
                if (clientBounds.Width > 0 && clientBounds.Height > 0) return clientBounds;
            }

            Form activeForm = Form.ActiveForm;
            if (activeForm != null) return Screen.FromControl(activeForm).WorkingArea;
            return Screen.PrimaryScreen.WorkingArea;
        }

        private void CenterProgressForm(Rectangle bounds)
        {
            int left = bounds.Left + Math.Max(0, (bounds.Width - progressForm.Width) / 2);
            int top = bounds.Top + Math.Max(0, (bounds.Height - progressForm.Height) / 2);
            progressForm.Location = new Point(left, top);
        }

        private static void CloseForm(Form form)
        {
            if (form == null || form.IsDisposed) return;
            form.Hide();
            form.Close();
            form.Dispose();
        }
    }
}
