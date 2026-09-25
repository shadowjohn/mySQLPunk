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
            ToolStripButton smtpButton = new ToolStripButton(Localization.T("Automation.SmtpSettings"));
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
                smtpButton,
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
            smtpButton.Click += (sender, args) =>
            {
                using (AutomationSmtpSettingsForm form = new AutomationSmtpSettingsForm(store)) form.ShowDialog(this);
            };
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
                    DescribeSchedule(job),
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
                statusLabel.Text = Localization.Format("Automation.ScheduleRegistered", job.Name, DescribeSchedule(job));
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

        public static string DescribeSchedule(ScheduledJobDefinition job)
        {
            switch (job.ScheduleKind)
            {
                case ScheduledJobScheduleKind.Weekly:
                    return Localization.Format("Automation.DescribeWeekly",
                        string.Join(" ", (job.WeekDays ?? new List<DayOfWeek>()).OrderBy(day => ((int)day + 6) % 7)
                            .Select(day => CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(day))), job.DailyTime);
                case ScheduledJobScheduleKind.Hourly:
                    return Localization.Format("Automation.DescribeHourly", job.IntervalHours, job.DailyTime);
                case ScheduledJobScheduleKind.Logon:
                    return Localization.T("Automation.ScheduleLogon");
                default:
                    return Localization.Format("Automation.DescribeDaily", job.DailyTime);
            }
        }

        private static string GetJobTypeText(ScheduledJobType type)
        {
            if (type == ScheduledJobType.Export) return Localization.T("Automation.TypeExport");
            if (type == ScheduledJobType.Backup) return Localization.T("Automation.TypeBackup");
            if (type == ScheduledJobType.Import) return Localization.T("Automation.TypeImport");
            if (type == ScheduledJobType.Transfer) return Localization.T("Automation.TypeTransfer");
            if (type == ScheduledJobType.DataDictionary) return Localization.T("Automation.TypeDictionary");
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
        private readonly TabControl detailTabs;
        private readonly TabPage sqlPage;
        private readonly TabPage importPage;
        private readonly TabPage transferPage;
        private readonly TextBox inputBox;
        private readonly TextBox targetTableBox;
        private readonly ComboBox delimiterBox;
        private readonly CheckBox headerBox;
        private readonly ComboBox targetConnectionBox;
        private readonly TextBox targetDatabaseBox;
        private readonly TextBox transferTablesBox;
        private readonly NumericUpDown retryCountBox;
        private readonly NumericUpDown retryDelayBox;
        private readonly TextBox webhookBox;
        private readonly CheckBox failureOnlyBox;
        private readonly TextBox emailBox;
        private readonly CheckBox attachBox;
        private readonly Button dictionaryButton;
        private DataDictionaryOptions dictionaryOptions;
        private readonly ComboBox scheduleKindBox;
        private readonly NumericUpDown intervalBox;
        private readonly CheckBox[] weekDayBoxes;
        private static readonly DayOfWeek[] WeekOrder = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        private static readonly string[] Delimiters = { ",", ";", "\\t", "|" };
        private bool loading;

        public ScheduledJobEditForm(ScheduledJobDefinition job, string initialProfileName)
        {
            original = Clone(job);
            Text = job == null ? Localization.T("Automation.NewTitle") : Localization.T("Automation.EditTitle");
            Width = 820;
            Height = 720;
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
            typeBox.Items.AddRange(new object[] { ScheduledJobType.Query, ScheduledJobType.Export, ScheduledJobType.Backup, ScheduledJobType.Import, ScheduledJobType.Transfer, ScheduledJobType.DataDictionary });
            Control typeField = UiField.Wrap(typeBox);
            typeField.Dock = DockStyle.Fill;
            typeField.Margin = FieldMargin();
            root.Controls.Add(typeField, 1, 1);
            root.SetColumnSpan(typeField, 2);

            AddLabel(root, 2, Localization.T("Automation.Profile"));
            profileBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = FieldMargin() };
            Control profileField = UiField.Wrap(profileBox);
            profileField.Dock = DockStyle.Fill;
            profileField.Margin = FieldMargin();
            root.Controls.Add(profileField, 1, 2);
            root.SetColumnSpan(profileField, 2);
            AddLabel(root, 3, Localization.T("Automation.Connection"));
            connectionBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "DisplayName", Margin = FieldMargin() };
            Control connectionField = UiField.Wrap(connectionBox);
            connectionField.Dock = DockStyle.Fill;
            connectionField.Margin = FieldMargin();
            root.Controls.Add(connectionField, 1, 3);
            root.SetColumnSpan(connectionField, 2);
            databaseBox = AddTextBox(root, 4, Localization.T("Automation.Database"));

            AddLabel(root, 5, Localization.T("Automation.Schedule"));
            FlowLayoutPanel schedulePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = FieldMargin() };
            scheduleBox = new CheckBox { AutoSize = true, Text = Localization.T("Automation.EnableSchedule"), Margin = new Padding(0, 4, 14, 0) };
            scheduleKindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(0, 1, 10, 0) };
            scheduleKindBox.Items.AddRange(new object[]
            {
                Localization.T("Automation.ScheduleDaily"), Localization.T("Automation.ScheduleWeekly"),
                Localization.T("Automation.ScheduleHourly"), Localization.T("Automation.ScheduleLogon")
            });
            intervalBox = new NumericUpDown { Minimum = 1, Maximum = 24, Width = 55, Margin = new Padding(10, 1, 0, 0) };
            weekDayBoxes = WeekOrder.Select(day => new CheckBox
            {
                AutoSize = true,
                Text = CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(day),
                Tag = day,
                Margin = new Padding(0, 4, 4, 0)
            }).ToArray();
            dailyTimePicker = new DateTimePicker { Width = 90, Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
            schedulePanel.Controls.Add(scheduleBox);
            schedulePanel.Controls.Add(scheduleKindBox);
            Control timeField = UiField.Wrap(dailyTimePicker);
            timeField.Width = 90;
            timeField.Margin = new Padding(0, 1, 0, 0);
            schedulePanel.Controls.Add(timeField);
            schedulePanel.Controls.Add(intervalBox);
            foreach (CheckBox day in weekDayBoxes) schedulePanel.Controls.Add(day);
            root.Controls.Add(schedulePanel, 1, 5);
            root.SetColumnSpan(schedulePanel, 2);

            AddLabel(root, 6, Localization.T("Automation.ExportFormat"));
            formatBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = FieldMargin() };
            formatBox.Items.AddRange(Enum.GetValues(typeof(QueryResultExportFormat)).Cast<object>().ToArray());
            Control formatField = UiField.Wrap(formatBox);
            formatField.Dock = DockStyle.Fill;
            formatField.Margin = FieldMargin();
            root.Controls.Add(formatField, 1, 6);
            root.SetColumnSpan(formatField, 2);

            AddLabel(root, 7, Localization.T("Automation.OutputPath"));
            outputBox = new TextBox { Dock = DockStyle.Fill, Margin = FieldMargin() };
            browseButton = new Button { AutoSize = true, Text = Localization.T("Common.Browse"), Margin = new Padding(6, 3, 0, 5) };
            Control outputField = UiField.Wrap(outputBox);
            outputField.Dock = DockStyle.Fill;
            outputField.Margin = FieldMargin();
            root.Controls.Add(outputField, 1, 7);
            FlowLayoutPanel outputButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            dictionaryButton = new Button { AutoSize = true, Text = Localization.T("Automation.DictionaryOptions"), Margin = new Padding(6, 3, 0, 5) };
            outputButtons.Controls.Add(browseButton);
            outputButtons.Controls.Add(dictionaryButton);
            root.Controls.Add(outputButtons, 2, 7);
            dictionaryButton.Click += (sender, args) =>
            {
                using (DataDictionaryOptionsForm form = new DataDictionaryOptionsForm(dictionaryOptions))
                {
                    if (form.ShowDialog(this) == DialogResult.OK) dictionaryOptions = form.Options;
                }
            };

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
            Control sqlField = UiField.Wrap(sqlBox);
            sqlField.Dock = DockStyle.Fill;
            sqlPage = new TabPage("SQL");
            sqlPage.Controls.Add(sqlField);

            TableLayoutPanel importPanel = DetailPanel(4);
            inputBox = AddDetailText(importPanel, 0, Localization.T("Automation.InputPath"));
            Button browseInput = new Button { AutoSize = true, Text = Localization.T("Common.Browse"), Margin = new Padding(6, 3, 0, 5) };
            importPanel.Controls.Add(browseInput, 2, 0);
            targetTableBox = AddDetailText(importPanel, 1, Localization.T("Automation.TargetTable"));
            delimiterBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Margin = FieldMargin() };
            delimiterBox.Items.AddRange(new object[] { ", (comma)", "; (semicolon)", "Tab", "| (pipe)" });
            AddLabel(importPanel, 2, Localization.T("Automation.Delimiter"));
            importPanel.Controls.Add(delimiterBox, 1, 2);
            headerBox = new CheckBox { AutoSize = true, Text = Localization.T("Automation.HasHeader"), Margin = FieldMargin() };
            importPanel.Controls.Add(headerBox, 1, 3);
            importPage = new TabPage(Localization.T("Automation.Section.Import"));
            importPage.Controls.Add(importPanel);

            TableLayoutPanel transferPanel = DetailPanel(4);
            AddLabel(transferPanel, 0, Localization.T("Automation.TargetConnection"));
            targetConnectionBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "DisplayName", Margin = FieldMargin() };
            transferPanel.Controls.Add(targetConnectionBox, 1, 0);
            transferPanel.SetColumnSpan(targetConnectionBox, 2);
            targetDatabaseBox = AddDetailText(transferPanel, 1, Localization.T("Automation.TargetDatabase"));
            AddLabel(transferPanel, 2, Localization.T("Automation.TransferTables"));
            transferTablesBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f), Margin = FieldMargin() };
            transferPanel.Controls.Add(transferTablesBox, 1, 2);
            transferPanel.SetColumnSpan(transferTablesBox, 2);
            transferPanel.RowStyles[2] = new RowStyle(SizeType.Percent, 100);
            Label transferHint = new Label { AutoSize = true, ForeColor = Color.Gray, Text = Localization.T("Automation.TransferTablesHint"), Margin = new Padding(0, 3, 0, 3) };
            transferPanel.Controls.Add(transferHint, 1, 3);
            transferPanel.SetColumnSpan(transferHint, 2);
            transferPage = new TabPage(Localization.T("Automation.Section.Transfer"));
            transferPage.Controls.Add(transferPanel);

            TableLayoutPanel reliabilityPanel = DetailPanel(5);
            AddLabel(reliabilityPanel, 0, Localization.T("Automation.RetryCount"));
            FlowLayoutPanel retryFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = FieldMargin() };
            retryCountBox = new NumericUpDown { Minimum = 0, Maximum = 5, Width = 60 };
            retryDelayBox = new NumericUpDown { Minimum = 0, Maximum = 3600, Width = 80, Increment = 30 };
            retryFlow.Controls.Add(retryCountBox);
            retryFlow.Controls.Add(new Label { AutoSize = true, Text = Localization.T("Automation.RetryDelay"), Margin = new Padding(14, 6, 6, 0) });
            retryFlow.Controls.Add(retryDelayBox);
            reliabilityPanel.Controls.Add(retryFlow, 1, 0);
            webhookBox = AddDetailText(reliabilityPanel, 1, Localization.T("Automation.Webhook"));
            failureOnlyBox = new CheckBox { AutoSize = true, Text = Localization.T("Automation.NotifyOnlyOnFailure"), Margin = FieldMargin() };
            emailBox = AddDetailText(reliabilityPanel, 2, Localization.T("Automation.EmailTo"));
            reliabilityPanel.Controls.Add(failureOnlyBox, 1, 3);
            attachBox = new CheckBox { AutoSize = true, Text = Localization.T("Automation.EmailAttachOutput"), Margin = FieldMargin() };
            reliabilityPanel.Controls.Add(attachBox, 1, 4);
            TabPage reliabilityPage = new TabPage(Localization.T("Automation.Section.Reliability"));
            reliabilityPage.Controls.Add(reliabilityPanel);

            detailTabs = new TabControl { Dock = DockStyle.Fill, Margin = FieldMargin() };
            detailTabs.TabPages.AddRange(new[] { sqlPage, importPage, transferPage, reliabilityPage });
            root.Controls.Add(detailTabs, 0, 8);
            root.SetColumnSpan(detailTabs, 3);
            browseInput.Click += (sender, args) => BrowseInputPath();

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
            profileBox.SelectedIndexChanged += (sender, args) => { LoadConnections(null); LoadTargetConnections(null); };
            connectionBox.SelectedIndexChanged += (sender, args) => ApplyInitialDatabase();
            scheduleBox.CheckedChanged += (sender, args) => UpdateScheduleState();
            scheduleKindBox.SelectedIndexChanged += (sender, args) => UpdateScheduleState();

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
                scheduleKindBox.SelectedIndex = (int)value.ScheduleKind;
                intervalBox.Value = Math.Max(1, Math.Min(24, value.IntervalHours));
                foreach (CheckBox day in weekDayBoxes) day.Checked = (value.WeekDays ?? new List<DayOfWeek>()).Contains((DayOfWeek)day.Tag);
                DateTime parsed;
                dailyTimePicker.Value = DateTime.TryParseExact(value.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                    ? DateTime.Today.Add(parsed.TimeOfDay)
                    : DateTime.Today.AddHours(2);
                formatBox.SelectedItem = value.ExportFormat;
                outputBox.Text = value.OutputPath ?? string.Empty;
                sqlBox.Text = value.Sql ?? string.Empty;
                inputBox.Text = value.InputPath ?? string.Empty;
                targetTableBox.Text = value.TargetTable ?? string.Empty;
                int delimiter = Array.IndexOf(Delimiters, value.CsvDelimiter ?? ",");
                delimiterBox.SelectedIndex = delimiter < 0 ? 0 : delimiter;
                headerBox.Checked = value.CsvHasHeader;
                LoadTargetConnections(value.TargetConnectionName);
                targetDatabaseBox.Text = value.TargetDatabaseName ?? string.Empty;
                transferTablesBox.Text = ScheduledTransferTableText.Format(value.TransferTables);
                retryCountBox.Value = Math.Max(0, Math.Min(5, value.RetryCount));
                retryDelayBox.Value = Math.Max(0, Math.Min(3600, value.RetryDelaySeconds));
                webhookBox.Text = value.WebhookUrl ?? string.Empty;
                failureOnlyBox.Checked = value.NotifyOnlyOnFailure;
                emailBox.Text = value.EmailTo ?? string.Empty;
                attachBox.Checked = value.EmailAttachOutput;
                dictionaryOptions = value.DictionaryOptions ?? new DataDictionaryOptions();
            }
            finally
            {
                loading = false;
            }
            UpdateTypeState(false);
            UpdateScheduleState();
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

        private void UpdateScheduleState()
        {
            ScheduledJobScheduleKind kind = (ScheduledJobScheduleKind)Math.Max(0, scheduleKindBox.SelectedIndex);
            scheduleKindBox.Enabled = scheduleBox.Checked;
            dailyTimePicker.Enabled = scheduleBox.Checked && kind != ScheduledJobScheduleKind.Logon;
            intervalBox.Visible = kind == ScheduledJobScheduleKind.Hourly;
            intervalBox.Enabled = scheduleBox.Checked;
            foreach (CheckBox day in weekDayBoxes)
            {
                day.Visible = kind == ScheduledJobScheduleKind.Weekly;
                day.Enabled = scheduleBox.Checked;
            }
        }

        private void LoadTargetConnections(string selectedName)
        {
            if (profileBox.SelectedItem == null) return;
            string prior = selectedName;
            if (string.IsNullOrWhiteSpace(prior) && targetConnectionBox.SelectedItem is ScheduledJobConnectionOption current) prior = current.Name;
            targetConnectionBox.Items.Clear();
            try
            {
                foreach (ScheduledJobConnectionOption option in AutomationConnectionProfileService.LoadConnectionOptions(Convert.ToString(profileBox.SelectedItem)))
                {
                    targetConnectionBox.Items.Add(option);
                }
                ScheduledJobConnectionOption selected = targetConnectionBox.Items.Cast<ScheduledJobConnectionOption>()
                    .FirstOrDefault(option => string.Equals(option.Name, prior, StringComparison.OrdinalIgnoreCase));
                if (selected != null) targetConnectionBox.SelectedItem = selected;
            }
            catch (Exception)
            {
                // 連線清單讀取失敗已由主要連線欄位顯示原因。
            }
        }

        private void BrowseInputPath()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = Localization.T("Automation.SelectInputPath"), Filter = "CSV (*.csv;*.txt)|*.csv;*.txt|*.*|*.*" })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) inputBox.Text = dialog.FileName;
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
            bool dictionary = type == ScheduledJobType.DataDictionary;
            bool output = export || type == ScheduledJobType.Backup || dictionary;
            dictionaryButton.Visible = dictionary;
            bool sql = type == ScheduledJobType.Query || type == ScheduledJobType.Export;
            formatBox.Enabled = export;
            outputBox.Enabled = output;
            browseButton.Enabled = output;
            sqlBox.Enabled = sql;
            if (type == ScheduledJobType.Import) detailTabs.SelectedTab = importPage;
            else if (type == ScheduledJobType.Transfer) detailTabs.SelectedTab = transferPage;
            else if (sql) detailTabs.SelectedTab = sqlPage;
            if (provideDefaultOutput && output && string.IsNullOrWhiteSpace(outputBox.Text))
            {
                outputBox.Text = type == ScheduledJobType.Backup
                    ? "backups\\{job}-{yyyyMMdd_HHmmss}.sql"
                    : dictionary ? "dictionaries\\{job}-{yyyyMMdd}.html"
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
                value.ScheduleKind = (ScheduledJobScheduleKind)Math.Max(0, scheduleKindBox.SelectedIndex);
                value.IntervalHours = (int)intervalBox.Value;
                value.WeekDays = weekDayBoxes.Where(day => day.Checked).Select(day => (DayOfWeek)day.Tag).ToList();
                value.DailyTime = dailyTimePicker.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
                value.ExportFormat = formatBox.SelectedItem is QueryResultExportFormat
                    ? (QueryResultExportFormat)formatBox.SelectedItem
                    : QueryResultExportFormat.Csv;
                value.OutputPath = outputBox.Text;
                value.Sql = value.Type == ScheduledJobType.Query || value.Type == ScheduledJobType.Export ? sqlBox.Text : string.Empty;
                value.InputPath = inputBox.Text.Trim();
                value.TargetTable = targetTableBox.Text.Trim();
                value.CsvDelimiter = Delimiters[Math.Max(0, delimiterBox.SelectedIndex)];
                value.CsvHasHeader = headerBox.Checked;
                ScheduledJobConnectionOption targetConnection = targetConnectionBox.SelectedItem as ScheduledJobConnectionOption;
                value.TargetConnectionName = targetConnection == null ? string.Empty : targetConnection.Name;
                value.TargetDatabaseName = targetDatabaseBox.Text.Trim();
                value.TransferTables = value.Type == ScheduledJobType.Transfer ? ScheduledTransferTableText.Parse(transferTablesBox.Text) : new List<ScheduledTransferTable>();
                value.RetryCount = (int)retryCountBox.Value;
                value.RetryDelaySeconds = (int)retryDelayBox.Value;
                value.WebhookUrl = webhookBox.Text.Trim();
                value.NotifyOnlyOnFailure = failureOnlyBox.Checked;
                value.EmailTo = emailBox.Text.Trim();
                value.EmailAttachOutput = attachBox.Checked;
                value.DictionaryOptions = value.Type == ScheduledJobType.DataDictionary ? dictionaryOptions : null;
                List<ScheduledTransferTable> replaced = value.TransferTables.Where(table => table.Mode == TransferMode.ReplaceData).ToList();
                if (value.Type == ScheduledJobType.Transfer && replaced.Count > 0 &&
                    !string.Equals(value.ConfirmedTargetDatabase, value.TargetDatabaseName, StringComparison.Ordinal))
                {
                    string typed = PromptConfirmation(Localization.Format("Automation.ConfirmReplace", value.TargetDatabaseName,
                        string.Join(", ", replaced.Select(table => table.Target))));
                    if (!string.Equals(typed, value.TargetDatabaseName, StringComparison.Ordinal)) return;
                    value.ConfirmedTargetDatabase = typed;
                }
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
                ScheduleKind = value.ScheduleKind,
                WeekDays = new List<DayOfWeek>(value.WeekDays ?? new List<DayOfWeek>()),
                IntervalHours = value.IntervalHours,
                CreatedUtc = value.CreatedUtc,
                UpdatedUtc = value.UpdatedUtc,
                InputPath = value.InputPath,
                TargetTable = value.TargetTable,
                CsvDelimiter = value.CsvDelimiter,
                CsvHasHeader = value.CsvHasHeader,
                TargetConnectionName = value.TargetConnectionName,
                TargetDatabaseName = value.TargetDatabaseName,
                TransferTables = (value.TransferTables ?? new List<ScheduledTransferTable>())
                    .Select(table => new ScheduledTransferTable { Source = table.Source, Target = table.Target, Mode = table.Mode }).ToList(),
                ConfirmedTargetDatabase = value.ConfirmedTargetDatabase,
                RetryCount = value.RetryCount,
                RetryDelaySeconds = value.RetryDelaySeconds,
                WebhookUrl = value.WebhookUrl,
                NotifyOnlyOnFailure = value.NotifyOnlyOnFailure,
                EmailTo = value.EmailTo,
                EmailAttachOutput = value.EmailAttachOutput,
                DictionaryOptions = value.DictionaryOptions == null ? null : JsonConvertClone(value.DictionaryOptions)
            };
        }

        private static DataDictionaryOptions JsonConvertClone(DataDictionaryOptions value)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<DataDictionaryOptions>(Newtonsoft.Json.JsonConvert.SerializeObject(value));
        }

        private string PromptConfirmation(string message)
        {
            using (Form dialog = new Form { Text = Text, Width = 560, Height = 260, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent })
            {
                Label label = new Label { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0), Text = message };
                TextBox input = new TextBox { Dock = DockStyle.Bottom };
                Button ok = new Button { Text = Localization.T("Common.OK"), DialogResult = DialogResult.OK, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dialog.Controls.Add(label);
                dialog.Controls.Add(input);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        private static TableLayoutPanel DetailPanel(int rows)
        {
            TableLayoutPanel panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = rows, Padding = new Padding(8) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int row = 0; row < rows; row++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return panel;
        }

        private static TextBox AddDetailText(TableLayoutPanel panel, int row, string label)
        {
            AddLabel(panel, row, label);
            TextBox box = new TextBox { Dock = DockStyle.Fill, Margin = FieldMargin() };
            panel.Controls.Add(box, 1, row);
            return box;
        }

        private static TextBox AddTextBox(TableLayoutPanel panel, int row, string label)
        {
            AddLabel(panel, row, label);
            TextBox box = new TextBox { Dock = DockStyle.Fill, Margin = FieldMargin() };
            Control field = UiField.Wrap(box);
            field.Dock = DockStyle.Fill;
            field.Margin = FieldMargin();
            panel.Controls.Add(field, 1, row);
            panel.SetColumnSpan(field, 2);
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

    /// <summary>自動執行通知信的 SMTP 設定；密碼存進 Windows 認證管理員，可寄測試信。</summary>
    public sealed class AutomationSmtpSettingsForm : Form
    {
        private readonly ScheduledJobStore store;
        private readonly TextBox hostBox = new TextBox { Dock = DockStyle.Fill };
        private readonly NumericUpDown portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 587, Width = 90 };
        private readonly CheckBox tlsBox = new CheckBox { AutoSize = true, Checked = true };
        private readonly TextBox userBox = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly TextBox fromBox = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox testToBox = new TextBox { Dock = DockStyle.Fill };
        private readonly Label statusLabel = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.Gray };

        public AutomationSmtpSettingsForm(ScheduledJobStore store)
        {
            this.store = store;
            Text = Localization.T("Automation.SmtpSettings");
            Width = 560;
            Height = 400;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            tlsBox.Text = Localization.T("Automation.SmtpUseTls");

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(layout, 0, Localization.T("Automation.SmtpHost"), hostBox);
            AddRow(layout, 1, Localization.T("Automation.SmtpPort"), portBox);
            AddRow(layout, 2, string.Empty, tlsBox);
            AddRow(layout, 3, Localization.T("Automation.SmtpUser"), userBox);
            AddRow(layout, 4, Localization.T("Automation.SmtpPassword"), passwordBox);
            AddRow(layout, 5, Localization.T("Automation.SmtpFrom"), fromBox);
            AddRow(layout, 6, Localization.T("Automation.SmtpTestTo"), testToBox);
            layout.Controls.Add(statusLabel, 0, 7);
            layout.SetColumnSpan(statusLabel, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            Button save = new Button { AutoSize = true, Text = Localization.T("Common.Save") };
            Button cancel = new Button { AutoSize = true, Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel };
            Button test = new Button { AutoSize = true, Text = Localization.T("Automation.SmtpSendTest") };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(test);
            layout.Controls.Add(buttons, 0, 8);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;

            AutomationSmtpSettings current = null;
            try { current = AutomationEmailService.Load(store); }
            catch (Exception ex) { statusLabel.Text = ExceptionMessageService.GetReason(ex); }
            if (current != null)
            {
                hostBox.Text = current.Host;
                portBox.Value = Math.Max(1, Math.Min(65535, current.Port));
                tlsBox.Checked = current.UseTls;
                userBox.Text = current.UserName;
                fromBox.Text = current.From;
            }
            statusLabel.Text = Localization.T("Automation.SmtpPasswordHint");

            save.Click += (sender, args) =>
            {
                try
                {
                    AutomationEmailService.Save(store, Collect(), passwordBox.Text.Length == 0 ? null : passwordBox.Text);
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    statusLabel.Text = ExceptionMessageService.GetReason(ex);
                }
            };
            test.Click += (sender, args) =>
            {
                try
                {
                    string password = passwordBox.Text;
                    if (password.Length == 0) WindowsCredentialService.TryReadPassword(AutomationEmailService.CredentialTarget, out password);
                    AutomationEmailService.Send(Collect(), password, AutomationEmailService.ParseRecipients(testToBox.Text),
                        "[mySQLPunk] " + Localization.T("Automation.SmtpTestSubject"), Localization.T("Automation.SmtpTestBody"));
                    statusLabel.Text = Localization.T("Automation.SmtpTestSent");
                }
                catch (Exception ex)
                {
                    statusLabel.Text = ExceptionMessageService.GetReason(ex);
                }
            };
            ThemeManager.ApplyTo(this);
        }

        private AutomationSmtpSettings Collect()
        {
            return new AutomationSmtpSettings
            {
                Host = hostBox.Text,
                Port = (int)portBox.Value,
                UseTls = tlsBox.Checked,
                UserName = userBox.Text,
                From = fromBox.Text
            };
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Text = label, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 5);
            layout.Controls.Add(control, 1, row);
        }
    }
}
