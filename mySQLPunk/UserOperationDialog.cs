using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    internal enum MySqlUserOperationMode
    {
        Create,
        Alter,
        Drop,
        Grant,
        Revoke
    }

    internal sealed class UserOperationDialog : Form
    {
        private readonly MySqlUserOperationMode mode;
        private readonly string initialDatabaseName;
        private readonly List<string> privilegeDatabaseChoices;
        private readonly List<string> privilegeObjectChoices;

        private TextBox txtUser;
        private TextBox txtHost;
        private TextBox txtCreatePassword;
        private TextBox txtCreatePlugin;
        private CheckBox chkCreateRequireSsl;
        private CheckBox chkCreateExpirePassword;
        private CheckBox chkCreateLockAccount;

        private CheckBox chkRenameUser;
        private TextBox txtNewUser;
        private TextBox txtNewHost;
        private CheckBox chkChangePassword;
        private TextBox txtAlterPassword;
        private TextBox txtAlterPlugin;
        private CheckBox chkAlterRequireSsl;
        private CheckBox chkAlterExpirePassword;
        private CheckBox chkAlterLockAccount;
        private CheckBox chkUnlockAccount;
        private CheckBox chkClearSsl;
        private TextBox txtMaxQuestions;
        private TextBox txtMaxUpdates;
        private TextBox txtMaxConnectionsPerHour;
        private TextBox txtMaxUserConnections;

        private ComboBox cboPrivilegeDatabase;
        private ComboBox cboPrivilegeObject;
        private ComboBox cboPrivilegeObjectType;
        private CheckedListBox lstPrivileges;
        private CheckBox chkWithGrantOption;
        private Label lblPrivilegeTargetPreview;

        private RichTextBox txtPreview;
        private Label lblHint;
        private GroupBox createGroup;
        private GroupBox alterGroup;
        private GroupBox limitsGroup;
        private GroupBox privilegeGroup;

        public UserOperationDialog(MySqlUserOperationMode mode, string databaseName, string user, string host)
            : this(mode, databaseName, user, host, null, null)
        {
        }

        public UserOperationDialog(MySqlUserOperationMode mode, string databaseName, string user, string host, IEnumerable<string> privilegeDatabases, IEnumerable<string> privilegeObjects)
        {
            this.mode = mode;
            initialDatabaseName = databaseName ?? string.Empty;
            privilegeDatabaseChoices = BuildChoiceList(initialDatabaseName, privilegeDatabases);
            privilegeObjectChoices = BuildChoiceList(string.Empty, privilegeObjects);
            Statements = new List<string>();

            InitializeComponent(user, host);
            ApplyOperationMode();
            UpdatePrivilegeTargetPreview();
            TryUpdatePreview(false);
        }

        public List<string> Statements { get; private set; }

        public string PreviewSql
        {
            get { return txtPreview == null ? string.Empty : txtPreview.Text; }
        }

        private void InitializeComponent(string user, string host)
        {
            Text = BuildDialogTitle(mode);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(820, 720);
            MinimumSize = new Size(700, 560);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            FlowLayoutPanel content = new FlowLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            content.AutoScroll = true;
            content.Padding = new Padding(12);
            content.Resize += (s, e) => ResizeFlowChildren(content);

            lblHint = new Label();
            lblHint.AutoSize = false;
            lblHint.Height = 42;
            lblHint.TextAlign = ContentAlignment.MiddleLeft;
            lblHint.Padding = new Padding(4, 0, 4, 0);
            content.Controls.Add(lblHint);

            GroupBox accountGroup = CreateGroupBox(Localization.T("User.AccountSection"), 92);
            TableLayoutPanel accountLayout = CreateTwoColumnLayout(2);
            txtUser = CreateTextBox(user);
            txtHost = CreateTextBox(string.IsNullOrWhiteSpace(host) ? "%" : host);
            AddLabeledControl(accountLayout, 0, Localization.T("User.UserName"), txtUser);
            AddLabeledControl(accountLayout, 1, Localization.T("Common.Host"), txtHost);
            accountGroup.Controls.Add(accountLayout);
            content.Controls.Add(accountGroup);

            createGroup = CreateGroupBox(Localization.T("User.AuthenticationSection"), 132);
            TableLayoutPanel createLayout = CreateTwoColumnLayout(3);
            txtCreatePassword = CreateTextBox(string.Empty);
            txtCreatePassword.UseSystemPasswordChar = true;
            txtCreatePlugin = CreateTextBox(string.Empty);
            FlowLayoutPanel createOptions = CreateInlineOptionsPanel();
            chkCreateRequireSsl = new CheckBox { Text = Localization.T("User.RequireSsl"), AutoSize = true };
            chkCreateExpirePassword = new CheckBox { Text = Localization.T("User.ExpirePassword"), AutoSize = true };
            chkCreateLockAccount = new CheckBox { Text = Localization.T("User.LockAccount"), AutoSize = true };
            createOptions.Controls.Add(chkCreateRequireSsl);
            createOptions.Controls.Add(chkCreateExpirePassword);
            createOptions.Controls.Add(chkCreateLockAccount);
            AddLabeledControl(createLayout, 0, Localization.T("Common.Password"), txtCreatePassword);
            AddLabeledControl(createLayout, 1, Localization.T("User.Plugin"), txtCreatePlugin);
            AddLabeledControl(createLayout, 2, Localization.T("User.Options"), createOptions);
            createGroup.Controls.Add(createLayout);
            content.Controls.Add(createGroup);

            alterGroup = CreateGroupBox(Localization.T("User.AlterSection"), 198);
            TableLayoutPanel alterLayout = CreateTwoColumnLayout(5);
            chkRenameUser = new CheckBox { Text = Localization.T("User.RenameUser"), AutoSize = true };
            txtNewUser = CreateTextBox(user);
            txtNewHost = CreateTextBox(string.IsNullOrWhiteSpace(host) ? "%" : host);
            chkChangePassword = new CheckBox { Text = Localization.T("User.ChangePassword"), AutoSize = true };
            txtAlterPassword = CreateTextBox(string.Empty);
            txtAlterPassword.UseSystemPasswordChar = true;
            txtAlterPlugin = CreateTextBox(string.Empty);
            FlowLayoutPanel alterOptions = CreateInlineOptionsPanel();
            chkAlterLockAccount = new CheckBox { Text = Localization.T("User.LockAccount"), AutoSize = true };
            chkAlterExpirePassword = new CheckBox { Text = Localization.T("User.ExpirePassword"), AutoSize = true };
            chkAlterRequireSsl = new CheckBox { Text = Localization.T("User.RequireSsl"), AutoSize = true };
            chkUnlockAccount = new CheckBox { Text = Localization.T("User.UnlockAccount"), AutoSize = true };
            chkClearSsl = new CheckBox { Text = Localization.T("User.ClearSsl"), AutoSize = true };
            alterOptions.Controls.Add(chkRenameUser);
            alterOptions.Controls.Add(chkChangePassword);
            alterOptions.Controls.Add(chkAlterLockAccount);
            alterOptions.Controls.Add(chkUnlockAccount);
            alterOptions.Controls.Add(chkAlterExpirePassword);
            alterOptions.Controls.Add(chkAlterRequireSsl);
            alterOptions.Controls.Add(chkClearSsl);
            AddLabeledControl(alterLayout, 0, Localization.T("User.NewUserName"), txtNewUser);
            AddLabeledControl(alterLayout, 1, Localization.T("User.NewHost"), txtNewHost);
            AddLabeledControl(alterLayout, 2, Localization.T("User.AlterOptions"), alterOptions);
            AddLabeledControl(alterLayout, 3, Localization.T("Common.Password"), txtAlterPassword);
            AddLabeledControl(alterLayout, 4, Localization.T("User.Plugin"), txtAlterPlugin);
            alterGroup.Controls.Add(alterLayout);
            content.Controls.Add(alterGroup);

            limitsGroup = CreateGroupBox(Localization.T("User.LimitsSection"), 154);
            TableLayoutPanel limitsLayout = CreateTwoColumnLayout(4);
            txtMaxQuestions = CreateTextBox(string.Empty);
            txtMaxUpdates = CreateTextBox(string.Empty);
            txtMaxConnectionsPerHour = CreateTextBox(string.Empty);
            txtMaxUserConnections = CreateTextBox(string.Empty);
            AddLabeledControl(limitsLayout, 0, Localization.T("Detail.Property.MaxQuestionsPerHour"), txtMaxQuestions);
            AddLabeledControl(limitsLayout, 1, Localization.T("Detail.Property.MaxUpdatesPerHour"), txtMaxUpdates);
            AddLabeledControl(limitsLayout, 2, Localization.T("Detail.Property.MaxConnectionsPerHour"), txtMaxConnectionsPerHour);
            AddLabeledControl(limitsLayout, 3, Localization.T("Detail.Property.MaxConnections"), txtMaxUserConnections);
            limitsGroup.Controls.Add(limitsLayout);
            content.Controls.Add(limitsGroup);

            privilegeGroup = CreateGroupBox(Localization.T("User.PrivilegeSection"), 260);
            TableLayoutPanel privilegeLayout = CreateTwoColumnLayout(6);
            cboPrivilegeDatabase = CreateComboBox(initialDatabaseName, privilegeDatabaseChoices);
            cboPrivilegeObject = CreateComboBox(string.Empty, privilegeObjectChoices);
            cboPrivilegeObjectType = CreateComboBox(Localization.T("User.ObjectTypeTableOrView"), new[]
            {
                Localization.T("User.ObjectTypeTableOrView"),
                Localization.T("User.ObjectTypeFunction"),
                Localization.T("User.ObjectTypeProcedure")
            });
            cboPrivilegeObjectType.DropDownStyle = ComboBoxStyle.DropDownList;
            lstPrivileges = new CheckedListBox();
            lstPrivileges.CheckOnClick = true;
            lstPrivileges.Height = 92;
            lstPrivileges.Dock = DockStyle.Fill;
            foreach (string privilege in BuildPrivilegeList()) lstPrivileges.Items.Add(privilege);
            chkWithGrantOption = new CheckBox { Text = Localization.T("User.WithGrantOption"), AutoSize = true };
            lblPrivilegeTargetPreview = new Label();
            lblPrivilegeTargetPreview.Dock = DockStyle.Fill;
            lblPrivilegeTargetPreview.TextAlign = ContentAlignment.MiddleLeft;
            lblPrivilegeTargetPreview.AutoEllipsis = true;
            AddLabeledControl(privilegeLayout, 0, Localization.T("Detail.Property.Database"), cboPrivilegeDatabase);
            AddLabeledControl(privilegeLayout, 1, Localization.T("User.ObjectName"), cboPrivilegeObject);
            AddLabeledControl(privilegeLayout, 2, Localization.T("Detail.Property.Privileges"), lstPrivileges);
            AddLabeledControl(privilegeLayout, 3, Localization.T("User.ObjectType"), cboPrivilegeObjectType);
            AddLabeledControl(privilegeLayout, 4, Localization.T("User.Options"), chkWithGrantOption);
            AddLabeledControl(privilegeLayout, 5, Localization.T("User.TargetPreview"), lblPrivilegeTargetPreview);
            privilegeGroup.Controls.Add(privilegeLayout);
            content.Controls.Add(privilegeGroup);

            cboPrivilegeDatabase.TextChanged += (s, e) => UpdatePrivilegeTargetPreview();
            cboPrivilegeObject.TextChanged += (s, e) => UpdatePrivilegeTargetPreview();
            cboPrivilegeObjectType.SelectedIndexChanged += (s, e) => UpdatePrivilegeTargetPreview();

            GroupBox previewGroup = CreateGroupBox(Localization.T("User.SqlPreview"), 208);
            txtPreview = new RichTextBox();
            txtPreview.Dock = DockStyle.Fill;
            txtPreview.Font = new Font("Consolas", 10f);
            txtPreview.ReadOnly = true;
            txtPreview.WordWrap = false;
            txtPreview.ScrollBars = RichTextBoxScrollBars.Both;
            previewGroup.Controls.Add(txtPreview);
            content.Controls.Add(previewGroup);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 54;
            footer.Padding = new Padding(12, 8, 12, 8);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.RightToLeft;

            Button btnCancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, Width = 96, Height = 32 };
            Button btnExecute = new Button { Text = Localization.T("User.Execute"), Width = 110, Height = 32 };
            Button btnPreview = new Button { Text = Localization.T("User.RefreshPreview"), Width = 110, Height = 32 };
            btnPreview.Click += (s, e) => TryUpdatePreview(true);
            btnExecute.Click += (s, e) => ExecuteButtonClicked();
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnExecute);
            buttons.Controls.Add(btnPreview);
            footer.Controls.Add(buttons);

            Controls.Add(content);
            Controls.Add(footer);
            CancelButton = btnCancel;
            AcceptButton = btnExecute;

            ThemeManager.ApplyTo(this);
        }

        private void ApplyOperationMode()
        {
            bool isCreate = mode == MySqlUserOperationMode.Create;
            bool isAlter = mode == MySqlUserOperationMode.Alter;
            bool isDrop = mode == MySqlUserOperationMode.Drop;
            bool isPrivilege = mode == MySqlUserOperationMode.Grant || mode == MySqlUserOperationMode.Revoke;

            createGroup.Visible = isCreate;
            alterGroup.Visible = isAlter;
            limitsGroup.Visible = isAlter;
            privilegeGroup.Visible = isPrivilege;
            txtUser.ReadOnly = !isCreate;
            txtHost.ReadOnly = !isCreate;
            chkWithGrantOption.Visible = mode == MySqlUserOperationMode.Grant;

            if (isDrop)
            {
                lblHint.Text = Localization.T("User.DropHint");
            }
            else if (isCreate)
            {
                lblHint.Text = Localization.T("User.CreateHint");
            }
            else if (isAlter)
            {
                lblHint.Text = Localization.T("User.AlterHint");
            }
            else
            {
                lblHint.Text = Localization.T(mode == MySqlUserOperationMode.Grant ? "User.GrantHint" : "User.RevokeHint");
            }
        }

        private void ExecuteButtonClicked()
        {
            if (!TryUpdatePreview(true)) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool TryUpdatePreview(bool showErrors)
        {
            try
            {
                Statements = BuildStatements();
                txtPreview.Text = MySqlUserManagerService.BuildUserOperationPreview(Statements);
                return true;
            }
            catch (Exception ex)
            {
                if (txtPreview != null) txtPreview.Text = "-- " + BuildErrorMessage(ex);
                if (showErrors) MessageBox.Show(this, BuildErrorMessage(ex), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private List<string> BuildStatements()
        {
            string user = RequireText(txtUser, Localization.T("User.UserName"));
            string host = NormalizeHost(txtHost.Text);

            if (mode == MySqlUserOperationMode.Create)
            {
                return MySqlUserManagerService.BuildCreateUserSqlStatements(new MySqlCreateUserOptions
                {
                    User = user,
                    Host = host,
                    Password = txtCreatePassword.Text,
                    Plugin = TrimOrEmpty(txtCreatePlugin.Text),
                    RequireSsl = chkCreateRequireSsl.Checked,
                    ExpirePassword = chkCreateExpirePassword.Checked,
                    LockAccount = chkCreateLockAccount.Checked
                });
            }

            if (mode == MySqlUserOperationMode.Alter)
            {
                bool? lockAccount = null;
                if (chkAlterLockAccount.Checked && chkUnlockAccount.Checked)
                {
                    throw new InvalidOperationException(Localization.T("User.LockUnlockConflict"));
                }
                if (chkAlterLockAccount.Checked) lockAccount = true;
                if (chkUnlockAccount.Checked) lockAccount = false;

                return MySqlUserManagerService.BuildAlterUserSqlStatements(new MySqlAlterUserOptions
                {
                    User = user,
                    Host = host,
                    RenameUser = chkRenameUser.Checked,
                    NewUser = TrimOrEmpty(txtNewUser.Text),
                    NewHost = NormalizeHost(txtNewHost.Text),
                    ChangePassword = chkChangePassword.Checked,
                    Password = txtAlterPassword.Text,
                    Plugin = TrimOrEmpty(txtAlterPlugin.Text),
                    LockAccount = lockAccount,
                    ExpirePassword = chkAlterExpirePassword.Checked,
                    RequireSsl = chkAlterRequireSsl.Checked,
                    ClearSslRequirement = chkClearSsl.Checked,
                    MaxQuestionsPerHour = ParseOptionalLimit(txtMaxQuestions, Localization.T("Detail.Property.MaxQuestionsPerHour")),
                    MaxUpdatesPerHour = ParseOptionalLimit(txtMaxUpdates, Localization.T("Detail.Property.MaxUpdatesPerHour")),
                    MaxConnectionsPerHour = ParseOptionalLimit(txtMaxConnectionsPerHour, Localization.T("Detail.Property.MaxConnectionsPerHour")),
                    MaxUserConnections = ParseOptionalLimit(txtMaxUserConnections, Localization.T("Detail.Property.MaxConnections"))
                });
            }

            if (mode == MySqlUserOperationMode.Drop)
            {
                return MySqlUserManagerService.BuildDropUserSqlStatements(user, host);
            }

            string databaseName = TrimOrEmpty(cboPrivilegeDatabase.Text);
            string objectName = TrimOrEmpty(cboPrivilegeObject.Text);
            MySqlPrivilegeTargetType targetType = GetSelectedPrivilegeTargetType();
            if ((targetType == MySqlPrivilegeTargetType.Function || targetType == MySqlPrivilegeTargetType.Procedure) && objectName.Length == 0)
            {
                throw new InvalidOperationException(Localization.Format("User.FieldRequired", Localization.T("User.ObjectName")));
            }
            List<string> privileges = lstPrivileges.CheckedItems.Cast<string>().ToList();
            if (mode == MySqlUserOperationMode.Grant)
            {
                return new List<string>
                {
                    MySqlUserManagerService.BuildGrantSql(privileges, databaseName, objectName, user, host, chkWithGrantOption.Checked, targetType)
                };
            }

            return new List<string>
            {
                MySqlUserManagerService.BuildRevokeSql(privileges, databaseName, objectName, user, host, targetType)
            };
        }

        private static TextBox CreateTextBox(string text)
        {
            return new TextBox { Text = text ?? string.Empty, Dock = DockStyle.Fill };
        }

        private static ComboBox CreateComboBox(string text, IEnumerable<string> choices)
        {
            ComboBox combo = new ComboBox();
            combo.Dock = DockStyle.Fill;
            combo.DropDownStyle = ComboBoxStyle.DropDown;
            combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            combo.AutoCompleteSource = AutoCompleteSource.ListItems;
            if (choices != null)
            {
                foreach (string choice in choices)
                {
                    if (!string.IsNullOrWhiteSpace(choice) && !combo.Items.Contains(choice)) combo.Items.Add(choice);
                }
            }
            combo.Text = text ?? string.Empty;
            return combo;
        }

        private static List<string> BuildChoiceList(string firstChoice, IEnumerable<string> choices)
        {
            List<string> result = new List<string>();
            AddChoice(result, firstChoice);
            if (choices != null)
            {
                foreach (string choice in choices) AddChoice(result, choice);
            }
            return result;
        }

        private static void AddChoice(List<string> result, string value)
        {
            string normalized = TrimOrEmpty(value);
            if (normalized.Length == 0) return;
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
        }

        private void UpdatePrivilegeTargetPreview()
        {
            if (lblPrivilegeTargetPreview == null) return;
            try
            {
                lblPrivilegeTargetPreview.Text = MySqlUserManagerService.BuildPrivilegeTargetPreview(
                    TrimOrEmpty(cboPrivilegeDatabase == null ? string.Empty : cboPrivilegeDatabase.Text),
                    TrimOrEmpty(cboPrivilegeObject == null ? string.Empty : cboPrivilegeObject.Text),
                    GetSelectedPrivilegeTargetType());
            }
            catch
            {
                lblPrivilegeTargetPreview.Text = string.Empty;
            }
        }

        private MySqlPrivilegeTargetType GetSelectedPrivilegeTargetType()
        {
            string selected = TrimOrEmpty(cboPrivilegeObjectType == null ? string.Empty : cboPrivilegeObjectType.Text);
            if (string.Equals(selected, Localization.T("User.ObjectTypeFunction"), StringComparison.OrdinalIgnoreCase)) return MySqlPrivilegeTargetType.Function;
            if (string.Equals(selected, Localization.T("User.ObjectTypeProcedure"), StringComparison.OrdinalIgnoreCase)) return MySqlPrivilegeTargetType.Procedure;
            return MySqlPrivilegeTargetType.TableOrView;
        }

        private static FlowLayoutPanel CreateInlineOptionsPanel()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true
            };
        }

        private static GroupBox CreateGroupBox(string title, int height)
        {
            return new GroupBox
            {
                Text = title,
                Height = height,
                Width = 760,
                Padding = new Padding(10, 18, 10, 10)
            };
        }

        private static TableLayoutPanel CreateTwoColumnLayout(int rows)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = rows;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 2 && rows >= 4 ? 96 : 30));
            return layout;
        }

        private static void AddLabeledControl(TableLayoutPanel layout, int row, string labelText, Control control)
        {
            Label label = new Label();
            label.Text = labelText;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private static void ResizeFlowChildren(FlowLayoutPanel panel)
        {
            int width = Math.Max(200, panel.ClientSize.Width - 30);
            foreach (Control child in panel.Controls)
            {
                child.Width = width;
            }
        }

        private static string[] BuildPrivilegeList()
        {
            return new[]
            {
                "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX",
                "REFERENCES", "CREATE VIEW", "SHOW VIEW", "CREATE ROUTINE", "ALTER ROUTINE",
                "EXECUTE", "EVENT", "TRIGGER", "PROCESS", "RELOAD", "SUPER", "CREATE USER"
            };
        }

        private static string BuildDialogTitle(MySqlUserOperationMode mode)
        {
            if (mode == MySqlUserOperationMode.Create) return Localization.T("User.NewUser");
            if (mode == MySqlUserOperationMode.Alter) return Localization.T("User.AlterUser");
            if (mode == MySqlUserOperationMode.Drop) return Localization.T("User.DropUser");
            if (mode == MySqlUserOperationMode.Grant) return Localization.T("User.GrantPrivileges");
            return Localization.T("User.RevokePrivileges");
        }

        private static string NormalizeHost(string host)
        {
            string value = TrimOrEmpty(host);
            return value.Length == 0 ? "%" : value;
        }

        private static string TrimOrEmpty(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string RequireText(TextBox textBox, string label)
        {
            string value = TrimOrEmpty(textBox == null ? string.Empty : textBox.Text);
            if (value.Length == 0) throw new InvalidOperationException(Localization.Format("User.FieldRequired", label));
            return value;
        }

        private static int? ParseOptionalLimit(TextBox textBox, string label)
        {
            string value = TrimOrEmpty(textBox == null ? string.Empty : textBox.Text);
            if (value.Length == 0) return null;
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < 0)
            {
                throw new InvalidOperationException(Localization.Format("User.NonNegativeIntegerRequired", label));
            }
            return parsed;
        }

        private static string BuildErrorMessage(Exception ex)
        {
            string message = ex == null ? string.Empty : ex.Message;
            return string.IsNullOrWhiteSpace(message) ? Localization.T("Object.UnknownError") : message;
        }
    }
}
