using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Data.SQLite;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public class sqlite_add_edit : Form
    {
        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        private TextBox txtName;
        private TextBox txtPath;
        private CheckBox chkInitGeospatial;
        private Button btnBrowse;
        private Button btnCreateNew;
        private Button btnTest;
        private Button btnOk;
        private Button btnCancel;
        private bool _selectedNewFile = false;

        public sqlite_add_edit()
        {
            InitializeUi();
            Form1.ApplyModernTheme(this);
            Localization.ApplyTo(this);
        }

        private void InitializeUi()
        {
            Text = Localization.T("Common.SQLiteConnection");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(620, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Label lblName = new Label { Text = Localization.T("Common.ConnectionName"), Location = new Point(20, 24), AutoSize = true };
            txtName = new TextBox { Location = new Point(120, 20), Width = 450 };

            Label lblPath = new Label { Text = Localization.T("Common.SQLiteFile"), Location = new Point(20, 64), AutoSize = true };
            txtPath = new TextBox { Location = new Point(120, 60), Width = 330 };
            btnBrowse = new Button { Text = Localization.T("Common.Browse"), Location = new Point(460, 58), Size = new Size(80, 28) };
            btnCreateNew = new Button { Text = Localization.T("Common.CreateNew"), Location = new Point(120, 96), Size = new Size(120, 30) };

            chkInitGeospatial = new CheckBox
            {
                Text = Localization.T("Common.InitGeospatial"),
                Location = new Point(120, 136),
                Width = 360,
                Checked = true
            };

            btnTest = new Button { Text = Localization.T("Common.TestConnection"), Location = new Point(20, 170), Size = new Size(150, 34) };
            btnOk = new Button { Text = Localization.T("Common.OK"), Location = new Point(410, 170), Size = new Size(80, 34) };
            btnCancel = new Button { Text = Localization.T("Common.Cancel"), Location = new Point(500, 170), Size = new Size(80, 34) };

            Controls.Add(lblName);
            Controls.Add(txtName);
            Controls.Add(lblPath);
            Controls.Add(txtPath);
            Controls.Add(btnBrowse);
            Controls.Add(btnCreateNew);
            Controls.Add(chkInitGeospatial);
            Controls.Add(btnTest);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            Load += sqlite_add_edit_Load;
            btnBrowse.Click += btnBrowse_Click;
            btnCreateNew.Click += btnCreateNew_Click;
            btnTest.Click += btnTest_Click;
            btnOk.Click += btnOk_Click;
            btnCancel.Click += (s, e) => Close();
        }

        private void sqlite_add_edit_Load(object sender, EventArgs e)
        {
            if (F1 == null || editIndex < 0) return;

            Dictionary<string, object> conn = F1.get_connection(editIndex);
            txtName.Text = GetValue(conn, "conn_name");
            txtPath.Text = GetValue(conn, "path");
            chkInitGeospatial.Checked = GetValue(conn, "init_geospatial") != "F";
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = Localization.T("Connection.SqliteFileFilter");
                dlg.CheckFileExists = true;
                if (File.Exists(txtPath.Text)) dlg.FileName = txtPath.Text;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtPath.Text = dlg.FileName;
                    _selectedNewFile = false;
                    if (string.IsNullOrWhiteSpace(txtName.Text))
                        txtName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }

        private void btnCreateNew_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = Localization.T("Connection.SqliteNewFileFilter");
                dlg.DefaultExt = "sqlite";
                dlg.OverwritePrompt = false;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtPath.Text = dlg.FileName;
                    _selectedNewFile = !File.Exists(dlg.FileName);
                    if (string.IsNullOrWhiteSpace(txtName.Text))
                        txtName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            if (!ValidateInput()) return;

            try
            {
                TestAndMaybeInitialize(true);
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "SQLite"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Format("Connection.TestFailed", "SQLite", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            if (F1 == null)
            {
                MessageBox.Show(Localization.T("Connection.MainWindowNotInitialized"), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!ValidateInput()) return;

            try
            {
                TestAndMaybeInitialize(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Format("Connection.InitializationFailed", "SQLite", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["conn_name"] = txtName.Text.Trim();
            conn["host"] = "";
            conn["port"] = "";
            conn["initial_database"] = "main";
            conn["db_kind"] = "sqlite";
            conn["username"] = "";
            conn["pwd"] = "";
            conn["path"] = txtPath.Text.Trim();
            conn["init_geospatial"] = chkInitGeospatial.Checked ? "T" : "F";
            conn["isConnect"] = "F";

            if (editIndex >= 0) F1.update_connection(editIndex, conn);
            else F1.add_connection(conn);

            Close();
        }

        private void TestAndMaybeInitialize(bool fromTestButton)
        {
            bool fileExisted = File.Exists(txtPath.Text.Trim());
            EnsureDatabaseFile();

            using (my_sqlite db = new my_sqlite())
            {
                db.SetConn(BuildConnectionString());
                db.Open();

                if (!db.SpatiaLiteEnabled)
                {
                    MessageBox.Show(
                        Localization.Format("Connection.SpatiaLiteLoadFailed", db.SpatiaLiteLoadError),
                        "SpatiaLite",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                if (chkInitGeospatial.Checked)
                {
                    bool hasMetadata = false;
                    try { hasMetadata = db.HasSpatialMetadata(); } catch { hasMetadata = false; }
                    bool shouldInit = !hasMetadata;

                    if (fileExisted && shouldInit && !_selectedNewFile)
                    {
                        DialogResult answer = MessageBox.Show(
                            Localization.T("Connection.InitSpatialMetadataPrompt"),
                            Localization.T("Connection.InitSpatialMetadataTitle"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        shouldInit = answer == DialogResult.Yes;
                    }

                    if (shouldInit)
                    {
                        db.InitSpatialMetadata();
                    }
                }
            }
        }

        private void EnsureDatabaseFile()
        {
            string path = txtPath.Text.Trim();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
            {
                SQLiteConnection.CreateFile(path);
                _selectedNewFile = true;
            }
        }

        private string BuildConnectionString()
        {
            return "Data Source=" + txtPath.Text.Trim() + ";Version=3;";
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterConnectionName"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus();
                return false;
            }
            if (string.IsNullOrWhiteSpace(txtPath.Text))
            {
                MessageBox.Show(Localization.T("Connection.SelectOrCreateSqliteFile"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPath.Focus();
                return false;
            }
            return true;
        }

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null)
                return conn[key].ToString();
            return "";
        }
    }
}
