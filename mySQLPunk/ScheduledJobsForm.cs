using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ScheduledJobsForm : Form, IDockableForm
    {
        private readonly ScheduledJobStore store;
        private readonly string initialProfileName;
        private readonly DataGridView jobsGrid;
        private readonly DataGridView runsGrid;
        private readonly ToolStripButton editButton;
        private readonly ToolStripButton deleteButton;
        private readonly ToolStripButton runButton;
        private readonly ToolStripButton registerButton;
        private readonly ToolStripButton removeButton;
        private readonly ToolStripButton floatButton;
        private readonly ToolStripButton dockButton;
        private readonly ToolStripStatusLabel statusLabel;
        private Form1 mainHost;
        private bool loaded;
        private bool running;

        public ScheduledJobsForm(string initialProfileName, ScheduledJobStore store = null)
        {
            this.initialProfileName = string.IsNullOrWhiteSpace(initialProfileName) ? "default" : initialProfileName;
            this.store = store ?? new ScheduledJobStore();

            Text = Localization.T("Automation.Title");
            Width = 1180;
            Height = 740;
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;

            ToolStrip toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
            ToolStripButton newButton = new ToolStripButton(Localization.T("Automation.New"));
            editButton = new ToolStripButton(Localization.T("Automation.Edit"));
            deleteButton = new ToolStripButton(Localization.T("Automation.Delete"));
            runButton = new ToolStripButton(Localization.T("Automation.RunNow"));
            registerButton = new ToolStripButton(Localization.T("Automation.RegisterSchedule"));
            removeButton = new ToolStripButton(Localization.T("Automation.RemoveSchedule"));
            ToolStripButton refreshButton = new ToolStripButton(Localization.T("Common.Refresh"));
            ToolStripButton openFolderButton = new ToolStripButton(Localization.T("Automation.OpenFolder"));
            floatButton = new ToolStripButton(Localization.T("Query.Float"));
            dockButton = new ToolStripButton(Localization.T("Query.Dock")) { Visible = false };
            toolbar.Items.AddRange(new ToolStripItem[]
            {
                newButton,
                editButton,
                deleteButton,
                new ToolStripSeparator(),
                runButton,
                new ToolStripSeparator(),
                registerButton,
                removeButton,
                new ToolStripSeparator(),
                refreshButton,
                openFolderButton,
                new ToolStripSeparator(),
                floatButton,
                dockButton
            });

            jobsGrid = CreateGrid();
            jobsGrid.Columns.Add(NewColumn("Name", Localization.T("Automation.Name"), 24));
            jobsGrid.Columns.Add(NewColumn("Type", Localization.T("Automation.Type"), 11));
            jobsGrid.Columns.Add(NewColumn("Profile", Localization.T("Automation.Profile"), 13));
            jobsGrid.Columns.Add(NewColumn("Connection", Localization.T("Automation.Connection"), 17));
            jobsGrid.Columns.Add(NewColumn("Database", Localization.T("Automation.Database"), 15));
            jobsGrid.Columns.Add(NewColumn("DailyTime", Localization.T("Automation.DailyTime"), 9));
            jobsGrid.Columns.Add(NewColumn("Schedule", Localization.T("Automation.Schedule"), 11));
            jobsGrid.Columns.Add(NewColumn("Registered", Localization.T("Automation.Registered"), 11));

            runsGrid = CreateGrid();
            runsGrid.Columns.Add(NewColumn("Started", Localization.T("Automation.RunStartedAt"), 18));
            runsGrid.Columns.Add(NewColumn("Status", Localization.T("Automation.RunStatus"), 10));
            runsGrid.Columns.Add(NewColumn("Elapsed", Localization.T("Automation.Elapsed"), 10));
            runsGrid.Columns.Add(NewColumn("Rows", Localization.T("Automation.Rows"), 9));
            runsGrid.Columns.Add(NewColumn("Output", Localization.T("Automation.Output"), 23));
            runsGrid.Columns.Add(NewColumn("Message", Localization.T("Automation.Message"), 30));

            GroupBox jobsGroup = new GroupBox
            {
                Dock = DockStyle.Fill,
                Text = Localization.T("Automation.Jobs"),
                Padding = new Padding(8)
            };
            jobsGroup.Controls.Add(jobsGrid);
            GroupBox runsGroup = new GroupBox
            {
                Dock = DockStyle.Fill,
                Text = Localization.T("Automation.RecentRuns"),
                Padding = new Padding(8)
            };
            runsGroup.Controls.Add(runsGrid);
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 330
            };
            split.Panel1.Controls.Add(jobsGroup);
            split.Panel2.Controls.Add(runsGroup);

            StatusStrip status = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(Localization.T("Automation.Ready"))
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            status.Items.Add(statusLabel);

            Controls.Add(split);
            Controls.Add(status);
            Controls.Add(toolbar);

            newButton.Click += (sender, args) => EditJob(null);
            editButton.Click += (sender, args) => EditJob(SelectedJob);
            deleteButton.Click += (sender, args) => DeleteSelectedJob();
            runButton.Click += async (sender, args) => await RunSelectedJobAsync();
            registerButton.Click += (sender, args) => RegisterSelectedJob();
            removeButton.Click += (sender, args) => RemoveSelectedSchedule();
            refreshButton.Click += (sender, args) => ReloadJobs(SelectedJob == null ? null : SelectedJob.Id);
            openFolderButton.Click += (sender, args) => OpenStorageFolder();
            floatButton.Click += (sender, args) => { if (mainHost != null) mainHost.FloatDockableForm(this); };
            dockButton.Click += (sender, args) => { if (mainHost != null) mainHost.DockDockableForm(this); };
            jobsGrid.SelectionChanged += (sender, args) => { LoadRuns(); UpdateActionState(); };
            jobsGrid.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) EditJob(SelectedJob); };
            Shown += (sender, args) =>
            {
                if (loaded) return;
                loaded = true;
                ReloadJobs(null);
            };

            ThemeManager.ApplyTo(this);
            UpdateActionState();
        }

        public void SetMainHost(Form1 mainHost)
        {
            this.mainHost = mainHost;
        }

        public string GetDisplayTitle()
        {
            return Text;
        }

        public bool HasUnsavedChanges()
        {
            return false;
        }

        public bool UsesDatabase(IDatabase database)
        {
            return false;
        }

        public void PrepareForDocking()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            TopMost = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            floatButton.Visible = true;
            dockButton.Visible = false;
        }

        public void PrepareForFloating()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            Dock = DockStyle.None;
            TopLevel = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            floatButton.Visible = false;
            dockButton.Visible = mainHost != null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (mainHost != null) mainHost.NotifyDockableFormClosed(this);
            base.OnFormClosed(e);
        }

        private ScheduledJobDefinition SelectedJob
        {
            get
            {
                return jobsGrid.SelectedRows.Count == 0 ? null : jobsGrid.SelectedRows[0].Tag as ScheduledJobDefinition;
            }
        }

        private void ReloadJobs(string selectedId)
        {
            ScheduledJobStoreSnapshot snapshot = store.LoadJobs();
            string schedulerWarning = null;
            jobsGrid.Rows.Clear();
            foreach (ScheduledJobDefinition job in snapshot.Jobs)
            {
                string registered = Localization.T("Automation.NotRegistered");
                try
                {
                    if (WindowsScheduledTaskService.IsRegistered(job.Id)) registered = Localization.T("Automation.RegisteredYes");
                }
                catch (Exception ex)
                {
                    registered = Localization.T("Automation.RegisteredUnknown");
                    if (schedulerWarning == null) schedulerWarning = ExceptionMessageService.GetReason(ex);
                }

                int index = jobsGrid.Rows.Add(
                    job.Name,
                    GetJobTypeText(job.Type),
                    job.ProfileName,
                    job.ConnectionName,
                    job.DatabaseName,
                    job.DailyTime,
                    job.ScheduleEnabled ? Localization.T("Common.Yes") : Localization.T("Common.No"),
                    registered);
                jobsGrid.Rows[index].Tag = job;
                if (!string.IsNullOrWhiteSpace(selectedId) && string.Equals(job.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    jobsGrid.Rows[index].Selected = true;
                    jobsGrid.CurrentCell = jobsGrid.Rows[index].Cells[0];
                }
            }
            if (jobsGrid.SelectedRows.Count == 0 && jobsGrid.Rows.Count > 0)
            {
                jobsGrid.Rows[0].Selected = true;
                jobsGrid.CurrentCell = jobsGrid.Rows[0].Cells[0];
            }
            LoadRuns();
            UpdateActionState();

            List<string> warnings = new List<string>(snapshot.Warnings);
            if (!string.IsNullOrWhiteSpace(schedulerWarning)) warnings.Add(Localization.Format("Automation.SchedulerCheckFailed", schedulerWarning));
            statusLabel.Text = warnings.Count == 0
                ? Localization.Format("Automation.JobCount", snapshot.Jobs.Count)
                : Localization.Format("Automation.JobCountWithWarnings", snapshot.Jobs.Count, warnings.Count);
            statusLabel.ToolTipText = string.Join(Environment.NewLine, warnings.ToArray());
        }

        private void LoadRuns()
        {
            runsGrid.Rows.Clear();
            ScheduledJobDefinition job = SelectedJob;
            if (job == null) return;
            foreach (ScheduledJobRunRecord record in store.LoadRecentRuns(job.Id))
            {
                DateTime started;
                string startedText = DateTime.TryParse(record.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)
                    ? started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : record.StartedUtc;
                string rows = record.Rows < 0 ? "-" : record.Rows.ToString("N0");
                int index = runsGrid.Rows.Add(
                    startedText,
                    GetRunStatusText(record.Status),
                    FormatElapsed(record.ElapsedMilliseconds),
                    rows,
                    record.OutputPath ?? string.Empty,
                    record.Message ?? string.Empty);
                runsGrid.Rows[index].Tag = record;
            }
        }

        private void EditJob(ScheduledJobDefinition job)
        {
            using (ScheduledJobEditForm dialog = new ScheduledJobEditForm(job, initialProfileName))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ScheduledJobDefinition saved = dialog.Job;
                string path;
                try
                {
                    path = store.SaveJob(saved);
                    if (saved.ScheduleEnabled) WindowsScheduledTaskService.Register(saved, Application.ExecutablePath, path);
                    else WindowsScheduledTaskService.Delete(saved.Id);
                    ReloadJobs(saved.Id);
                    statusLabel.Text = Localization.Format("Automation.JobSaved", saved.Name);
                }
                catch (Exception ex)
                {
                    ReloadJobs(saved.Id);
                    ShowError(Localization.Format("Automation.JobSaveFailed", ExceptionMessageService.GetReason(ex)));
                }
            }
        }

        private void DeleteSelectedJob()
        {
            ScheduledJobDefinition job = SelectedJob;
            if (job == null) return;
            if (MessageBox.Show(this, Localization.Format("Automation.DeleteConfirm", job.Name), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                WindowsScheduledTaskService.Delete(job.Id);
                store.DeleteJob(job.Id);
                ReloadJobs(null);
                statusLabel.Text = Localization.Format("Automation.JobDeleted", job.Name);
            }
            catch (Exception ex)
            {
                ShowError(Localization.Format("Automation.JobDeleteFailed", ExceptionMessageService.GetReason(ex)));
            }
        }

        private async Task RunSelectedJobAsync()
        {
            ScheduledJobDefinition job = SelectedJob;
            if (job == null) return;
            running = true;
            UpdateActionState();
            statusLabel.Text = Localization.Format("Automation.RunningJob", job.Name);
            try
            {
                ScheduledJobRunRecord record = await Task.Run(() => ScheduledJobExecutionService.ExecuteFromProfile(job, store));
                LoadRuns();
                statusLabel.Text = Localization.Format("Automation.RunFinished", job.Name, GetRunStatusText(record.Status));
                if (!string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(record.Message);
                }
            }
            catch (Exception ex)
            {
                ShowError(Localization.Format("Automation.RunFailed", ExceptionMessageService.GetReason(ex)));
            }
            finally
            {
                running = false;
                UpdateActionState();
            }
        }

        private void RegisterSelectedJob()
        {
            ScheduledJobDefinition job = SelectedJob;
            if (job == null) return;
            try
            {
                job.ScheduleEnabled = true;
                string path = store.SaveJob(job);
                WindowsScheduledTaskService.Register(job, Application.ExecutablePath, path);
                ReloadJobs(job.Id);
                statusLabel.Text = Localization.Format("Automation.ScheduleRegistered", job.Name, job.DailyTime);
            }
            catch (Exception ex)
            {
                ReloadJobs(job.Id);
                ShowError(Localization.Format("Automation.ScheduleRegisterFailed", ExceptionMessageService.GetReason(ex)));
            }
        }

        private void RemoveSelectedSchedule()
        {
            ScheduledJobDefinition job = SelectedJob;
            if (job == null) return;
            try
            {
                WindowsScheduledTaskService.Delete(job.Id);
                job.ScheduleEnabled = false;
                store.SaveJob(job);
                ReloadJobs(job.Id);
                statusLabel.Text = Localization.Format("Automation.ScheduleRemoved", job.Name);
            }
            catch (Exception ex)
            {
                ShowError(Localization.Format("Automation.ScheduleRemoveFailed", ExceptionMessageService.GetReason(ex)));
            }
        }

        private void OpenStorageFolder()
        {
            try
            {
                Directory.CreateDirectory(store.RootDirectory);
                Process.Start(new ProcessStartInfo(store.RootDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError(ExceptionMessageService.GetReason(ex));
            }
        }

        private void UpdateActionState()
        {
            bool selected = SelectedJob != null;
            editButton.Enabled = selected;
            deleteButton.Enabled = selected;
            runButton.Enabled = selected && !running;
            registerButton.Enabled = selected;
            removeButton.Enabled = selected;
        }

        private void ShowError(string message)
        {
            statusLabel.Text = message ?? string.Empty;
            MessageBox.Show(this, message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
        }

        private static DataGridViewTextBoxColumn NewColumn(string name, string header, float weight)
        {
            return new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic };
        }

        private static string GetJobTypeText(ScheduledJobType type)
        {
            if (type == ScheduledJobType.Export) return Localization.T("Automation.TypeExport");
            if (type == ScheduledJobType.Backup) return Localization.T("Automation.TypeBackup");
            return Localization.T("Automation.TypeQuery");
        }

        private static string GetRunStatusText(string status)
        {
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return Localization.T("Automation.StatusSuccess");
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)) return Localization.T("Automation.StatusFailed");
            if (string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)) return Localization.T("Automation.StatusRunning");
            return status ?? string.Empty;
        }

        private static string FormatElapsed(long milliseconds)
        {
            if (milliseconds < 1000) return milliseconds.ToString("N0") + " ms";
            return TimeSpan.FromMilliseconds(milliseconds).TotalSeconds.ToString("N1") + " s";
        }
    }

    public sealed class ScheduledJobEditForm : Form
    {
        private readonly ScheduledJobDefinition original;
        private readonly TextBox nameBox;
        private readonly ComboBox typeBox;
        private readonly ComboBox profileBox;
        private readonly ComboBox connectionBox;
        private readonly TextBox databaseBox;
        private readonly CheckBox scheduleBox;
        private readonly DateTimePicker dailyTimePicker;
        private readonly ComboBox formatBox;
        private readonly TextBox outputBox;
        private readonly Button browseButton;
        private readonly TextBox sqlBox;
        private readonly Label hintLabel;
        private bool loading;

        public ScheduledJobEditForm(ScheduledJobDefinition job, string initialProfileName)
        {
            original = Clone(job);
            Text = job == null ? Localization.T("Automation.NewTitle") : Localization.T("Automation.EditTitle");
            Width = 780;
            Height = 680;
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 11,
                Padding = new Padding(14)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int row = 0; row < 8; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            nameBox = AddTextBox(root, 0, Localization.T("Automation.Name"));
            AddLabel(root, 1, Localization.T("Automation.Type"));
            typeBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = FieldMargin() };
            typeBox.Items.AddRange(new object[] { ScheduledJobType.Query, ScheduledJobType.Export, ScheduledJobType.Backup });
            root.Controls.Add(typeBox, 1, 1);
            root.SetColumnSpan(typeBox, 2);

            AddLabel(root, 2, Localization.T("Automation.Profile"));
            profileBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = FieldMargin() };
            root.Controls.Add(profileBox, 1, 2);
            root.SetColumnSpan(profileBox, 2);
            AddLabel(root, 3, Localization.T("Automation.Connection"));
            connectionBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "DisplayName", Margin = FieldMargin() };
            root.Controls.Add(connectionBox, 1, 3);
            root.SetColumnSpan(connectionBox, 2);
            databaseBox = AddTextBox(root, 4, Localization.T("Automation.Database"));

            AddLabel(root, 5, Localization.T("Automation.Schedule"));
            FlowLayoutPanel schedulePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = FieldMargin() };
            scheduleBox = new CheckBox { AutoSize = true, Text = Localization.T("Automation.EnableDailySchedule"), Margin = new Padding(0, 4, 14, 0) };
            dailyTimePicker = new DateTimePicker { Width = 90, Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
            schedulePanel.Controls.Add(scheduleBox);
            schedulePanel.Controls.Add(dailyTimePicker);
            root.Controls.Add(schedulePanel, 1, 5);
            root.SetColumnSpan(schedulePanel, 2);

            AddLabel(root, 6, Localization.T("Automation.ExportFormat"));
            formatBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = FieldMargin() };
            formatBox.Items.AddRange(Enum.GetValues(typeof(QueryResultExportFormat)).Cast<object>().ToArray());
            root.Controls.Add(formatBox, 1, 6);
            root.SetColumnSpan(formatBox, 2);

            AddLabel(root, 7, Localization.T("Automation.OutputPath"));
            outputBox = new TextBox { Dock = DockStyle.Fill, Margin = FieldMargin() };
            browseButton = new Button { AutoSize = true, Text = Localization.T("Common.Browse"), Margin = new Padding(6, 3, 0, 5) };
            root.Controls.Add(outputBox, 1, 7);
            root.Controls.Add(browseButton, 2, 7);

            AddLabel(root, 8, "SQL");
            sqlBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AcceptsReturn = true,
                AcceptsTab = true,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 10f),
                Margin = FieldMargin()
            };
            root.Controls.Add(sqlBox, 1, 8);
            root.SetColumnSpan(sqlBox, 2);

            hintLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = Color.Gray,
                Text = Localization.T("Automation.EditorHint"),
                Margin = new Padding(0, 3, 0, 8)
            };
            root.Controls.Add(hintLabel, 1, 9);
            root.SetColumnSpan(hintLabel, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            Button saveButton = new Button { AutoSize = true, Text = Localization.T("Common.Save"), Margin = new Padding(6, 0, 0, 0) };
            Button cancelButton = new Button { AutoSize = true, Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(cancelButton);
            root.Controls.Add(buttons, 0, 10);
            root.SetColumnSpan(buttons, 3);
            Controls.Add(root);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            saveButton.Click += (sender, args) => SaveAndClose();
            browseButton.Click += (sender, args) => BrowseOutputPath();
            typeBox.SelectedIndexChanged += (sender, args) => UpdateTypeState(true);
            profileBox.SelectedIndexChanged += (sender, args) => LoadConnections(null);
            connectionBox.SelectedIndexChanged += (sender, args) => ApplyInitialDatabase();
            scheduleBox.CheckedChanged += (sender, args) => dailyTimePicker.Enabled = scheduleBox.Checked;

            LoadValues(job, initialProfileName);
            ThemeManager.ApplyTo(this);
        }

        public ScheduledJobDefinition Job { get; private set; }

        private void LoadValues(ScheduledJobDefinition job, string initialProfileName)
        {
            loading = true;
            try
            {
                ScheduledJobDefinition value = job ?? new ScheduledJobDefinition
                {
                    Type = ScheduledJobType.Query,
                    ProfileName = string.IsNullOrWhiteSpace(initialProfileName) ? "default" : initialProfileName,
                    DailyTime = "02:00",
                    ExportFormat = QueryResultExportFormat.Csv
                };
                nameBox.Text = value.Name ?? string.Empty;
                typeBox.SelectedItem = value.Type;
                profileBox.Items.Clear();
                foreach (string profile in AutomationConnectionProfileService.GetProfileNames()) profileBox.Items.Add(profile);
                if (!profileBox.Items.Cast<object>().Any(item => string.Equals(Convert.ToString(item), value.ProfileName, StringComparison.OrdinalIgnoreCase)))
                {
                    profileBox.Items.Add(value.ProfileName);
                }
                profileBox.SelectedItem = profileBox.Items.Cast<object>().FirstOrDefault(item => string.Equals(Convert.ToString(item), value.ProfileName, StringComparison.OrdinalIgnoreCase));
                LoadConnections(value.ConnectionName);
                databaseBox.Text = value.DatabaseName ?? string.Empty;
                scheduleBox.Checked = value.ScheduleEnabled;
                DateTime parsed;
                dailyTimePicker.Value = DateTime.TryParseExact(value.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                    ? DateTime.Today.Add(parsed.TimeOfDay)
                    : DateTime.Today.AddHours(2);
                formatBox.SelectedItem = value.ExportFormat;
                outputBox.Text = value.OutputPath ?? string.Empty;
                sqlBox.Text = value.Sql ?? string.Empty;
            }
            finally
            {
                loading = false;
            }
            UpdateTypeState(false);
            dailyTimePicker.Enabled = scheduleBox.Checked;
        }

        private void LoadConnections(string selectedName)
        {
            if (profileBox.SelectedItem == null) return;
            string prior = selectedName;
            if (string.IsNullOrWhiteSpace(prior) && connectionBox.SelectedItem is ScheduledJobConnectionOption current) prior = current.Name;
            connectionBox.Items.Clear();
            try
            {
                foreach (ScheduledJobConnectionOption option in AutomationConnectionProfileService.LoadConnectionOptions(Convert.ToString(profileBox.SelectedItem)))
                {
                    connectionBox.Items.Add(option);
                }
                ScheduledJobConnectionOption selected = connectionBox.Items.Cast<ScheduledJobConnectionOption>()
                    .FirstOrDefault(option => string.Equals(option.Name, prior, StringComparison.OrdinalIgnoreCase));
                if (selected != null) connectionBox.SelectedItem = selected;
                else if (connectionBox.Items.Count > 0) connectionBox.SelectedIndex = 0;
                hintLabel.Text = Localization.T("Automation.EditorHint");
            }
            catch (Exception ex)
            {
                hintLabel.Text = Localization.Format("Automation.ProfileLoadFailed", ExceptionMessageService.GetReason(ex));
            }
        }

        private void ApplyInitialDatabase()
        {
            if (loading || !string.IsNullOrWhiteSpace(databaseBox.Text)) return;
            ScheduledJobConnectionOption option = connectionBox.SelectedItem as ScheduledJobConnectionOption;
            if (option != null) databaseBox.Text = option.InitialDatabase ?? string.Empty;
        }

        private void UpdateTypeState(bool provideDefaultOutput)
        {
            ScheduledJobType type = typeBox.SelectedItem is ScheduledJobType ? (ScheduledJobType)typeBox.SelectedItem : ScheduledJobType.Query;
            bool export = type == ScheduledJobType.Export;
            bool output = export || type == ScheduledJobType.Backup;
            bool sql = type != ScheduledJobType.Backup;
            formatBox.Enabled = export;
            outputBox.Enabled = output;
            browseButton.Enabled = output;
            sqlBox.Enabled = sql;
            if (provideDefaultOutput && output && string.IsNullOrWhiteSpace(outputBox.Text))
            {
                outputBox.Text = type == ScheduledJobType.Backup
                    ? "backups\\{job}-{yyyyMMdd_HHmmss}.sql"
                    : "exports\\{job}-{yyyyMMdd_HHmmss}.csv";
            }
        }

        private void BrowseOutputPath()
        {
            ScheduledJobType type = typeBox.SelectedItem is ScheduledJobType ? (ScheduledJobType)typeBox.SelectedItem : ScheduledJobType.Query;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Automation.SelectOutputPath");
                dialog.Filter = type == ScheduledJobType.Backup
                    ? Localization.T("Automation.BackupFileFilter")
                    : Localization.T("Automation.ExportFileFilter");
                if (!string.IsNullOrWhiteSpace(outputBox.Text) && Path.IsPathRooted(outputBox.Text))
                {
                    try
                    {
                        dialog.InitialDirectory = Path.GetDirectoryName(outputBox.Text);
                        dialog.FileName = Path.GetFileName(outputBox.Text);
                    }
                    catch { }
                }
                if (dialog.ShowDialog(this) == DialogResult.OK) outputBox.Text = dialog.FileName;
            }
        }

        private void SaveAndClose()
        {
            try
            {
                ScheduledJobConnectionOption connection = connectionBox.SelectedItem as ScheduledJobConnectionOption;
                ScheduledJobDefinition value = Clone(original) ?? new ScheduledJobDefinition();
                value.Name = nameBox.Text;
                value.Type = typeBox.SelectedItem is ScheduledJobType ? (ScheduledJobType)typeBox.SelectedItem : ScheduledJobType.Query;
                value.ProfileName = Convert.ToString(profileBox.SelectedItem);
                value.ConnectionName = connection == null ? string.Empty : connection.Name;
                value.DatabaseName = databaseBox.Text;
                value.ScheduleEnabled = scheduleBox.Checked;
                value.DailyTime = dailyTimePicker.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
                value.ExportFormat = formatBox.SelectedItem is QueryResultExportFormat
                    ? (QueryResultExportFormat)formatBox.SelectedItem
                    : QueryResultExportFormat.Csv;
                value.OutputPath = outputBox.Text;
                value.Sql = sqlBox.Text;
                ScheduledJobValidator.Validate(value);
                Job = value;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ExceptionMessageService.GetReason(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static ScheduledJobDefinition Clone(ScheduledJobDefinition value)
        {
            if (value == null) return null;
            return new ScheduledJobDefinition
            {
                Version = value.Version,
                Id = value.Id,
                Name = value.Name,
                Type = value.Type,
                ProfileName = value.ProfileName,
                ConnectionName = value.ConnectionName,
                DatabaseName = value.DatabaseName,
                Sql = value.Sql,
                OutputPath = value.OutputPath,
                ExportFormat = value.ExportFormat,
                DailyTime = value.DailyTime,
                ScheduleEnabled = value.ScheduleEnabled,
                CreatedUtc = value.CreatedUtc,
                UpdatedUtc = value.UpdatedUtc
            };
        }

        private static TextBox AddTextBox(TableLayoutPanel panel, int row, string label)
        {
            AddLabel(panel, row, label);
            TextBox box = new TextBox { Dock = DockStyle.Fill, Margin = FieldMargin() };
            panel.Controls.Add(box, 1, row);
            panel.SetColumnSpan(box, 2);
            return box;
        }

        private static void AddLabel(TableLayoutPanel panel, int row, string text)
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = text,
                Margin = new Padding(0, 6, 10, 6)
            }, 0, row);
        }

        private static Padding FieldMargin()
        {
            return new Padding(0, 3, 0, 5);
        }
    }
}
