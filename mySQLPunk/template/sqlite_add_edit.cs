using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Data.SQLite;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public class sqlite_add_edit : Form, IConnectionDraftForm
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

            Label lblName = new Label { Text = Localization.T("Common.ConnectionName") };
            txtName = new TextBox();
            Label lblPath = new Label { Text = Localization.T("Common.SQLiteFile") };
            txtPath = new TextBox();
            btnBrowse = new Button { Text = Localization.T("Common.Browse"), AutoSize = true, MinimumSize = new Size(80, 30) };
            btnCreateNew = new Button { Text = Localization.T("Common.CreateNew"), AutoSize = true, MinimumSize = new Size(110, 30) };
            chkInitGeospatial = new CheckBox { Text = Localization.T("Common.InitGeospatial"), Checked = true };
            btnTest = new Button { Text = Localization.T("Common.TestConnection") };
            btnOk = new Button { Text = Localization.T("Common.OK") };
            btnCancel = new Button { Text = Localization.T("Common.Cancel") };

            ConnectionDialogUi.Shell shell = ConnectionDialogUi.Build(this, "SQLite", ConnectionDialogUi.SqliteColor);
            ConnectionDialogUi.AddField(shell, lblName, txtName, ConnectionDialogUi.FieldWide);

            // 檔案列：路徑輸入 + 瀏覽按鈕
            FlowLayoutPanel pathRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false
            };
            UiInputShell pathShell = new UiInputShell(txtPath)
            {
                Width = ConnectionDialogUi.FieldWide - 88,
                Margin = new Padding(0, 4, 8, 4),
                Anchor = AnchorStyles.Left
            };
            btnBrowse.Margin = new Padding(0, 2, 0, 2);
            pathRow.Controls.Add(pathShell);
            pathRow.Controls.Add(btnBrowse);
            ConnectionDialogUi.AddField(shell, lblPath, pathRow, 0);
            ConnectionDialogUi.AddFieldOnly(shell, btnCreateNew);
            ConnectionDialogUi.AddFieldOnly(shell, chkInitGeospatial);
            ConnectionDialogUi.Finish(this, shell, btnTest, btnOk, btnCancel);

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

        public void ApplyConnectionDraft(Dictionary<string, object> conn)
        {
            if (conn == null) return;
            txtName.Text = GetValue(conn, "conn_name");
            txtPath.Text = GetValue(conn, "path");
            chkInitGeospatial.Checked = GetValue(conn, "init_geospatial") == "T";
            _selectedNewFile = false;
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
            catch (OperationCanceledException)
            {
                return; // 使用者取消建立新檔
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("SQLite", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            catch (OperationCanceledException)
            {
                return; // 使用者取消建立新檔
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildInitializationFailedMessage("SQLite", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                // 手動輸入或貼上的路徑打錯字時，靜默建新檔會讓使用者以為連到舊資料庫；
                // 只有透過檔案對話框明確選了新檔（_selectedNewFile）才不再多問
                if (!_selectedNewFile)
                {
                    DialogResult answer = MessageBox.Show(this,
                        Localization.Format("Connection.SqliteCreateFileConfirm", path),
                        Localization.T("Common.Warning"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes)
                    {
                        throw new OperationCanceledException();
                    }
                }
                SQLiteConnection.CreateFile(path);
                _selectedNewFile = true;
            }
        }

        private string BuildConnectionString()
        {
            // 用 builder 組字串：路徑含 ; 時字串串接會被拆錯
            var builder = new System.Data.SQLite.SQLiteConnectionStringBuilder
            {
                DataSource = txtPath.Text.Trim(),
                Version = 3
            };
            return builder.ConnectionString;
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
