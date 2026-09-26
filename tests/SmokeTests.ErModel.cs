using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using mySQLPunk;
using mySQLPunk.lib;

/// <summary>需要 ER 模型視窗的共用測試；Windows smoke test 與 mono 驗證程式都會編譯這個檔案。</summary>
public static partial class SmokeTests
{    /// <summary>資料庫 → 模型 → 編輯 → 模型 → 資料庫 → 資料庫外部變更 → 模型的完整往返（SQLite）。</summary>
    public static void AssertErModelSchemaFlow(ErDiagramForm form, IDatabase db, string modelPath)
    {
        Func<string, bool> tableExists = name => db.SelectSQL("SELECT name FROM sqlite_master WHERE type = 'table' AND name = '" + name + "'").Rows.Count == 1;
        Func<string, string, bool> columnExists = (table, column) => db.SelectSQL("PRAGMA table_info('" + table + "')").Rows.Cast<System.Data.DataRow>().Any(row => Convert.ToString(row["name"]) == column);
        Func<int> remaining = () => form.CompareModelToDatabase().Differences.Count(item => item.Kind != SchemaDifferenceKind.MetadataWarning);

        Assert(!form.IsModelFirst, "A new ER diagram shows the database schema.");
        Assert(form.CaptureFromDatabase(false) == null && form.IsModelFirst && form.Model.Schema.Find("orders") != null, "Capturing stores the database schema in the model.");
        AssertEquals("0", remaining().ToString(), "A fresh capture matches the database.");
        ErModelTable original = form.Model.Schema.Find("customers");
        using (ErModelTableEditorForm editor = new ErModelTableEditorForm(original, new List<ErModelRelationship>(), form.Model.Schema.Tables.Select(table => table.Name).ToList(), (table, keys) => 0))
        {
            editor.CreateControl();
            ErModelTable unchanged = editor.BuildTable();
            AssertEquals(string.Join("|", original.Columns.Select(column => column.Name + ":" + column.DataType + ":" + column.Nullable + ":" + column.PrimaryKey)),
                string.Join("|", unchanged.Columns.Select(column => column.Name + ":" + column.DataType + ":" + column.Nullable + ":" + column.PrimaryKey)),
                "Opening the table editor without changes keeps every column as it was.");
        }

        ErModelTable products = new ErModelTable { Name = "products" };
        products.Columns.Add(new ErModelColumn { Name = "id", DataType = "INTEGER", PrimaryKey = true, Nullable = false });
        products.Columns.Add(new ErModelColumn { Name = "title", DataType = "TEXT", Nullable = false });
        form.ApplyModelTable(null, products, null);
        ErModelTable notes = new ErModelTable { Name = "notes" };
        foreach (ErModelColumn column in form.Model.Schema.Find("notes").Columns) notes.Columns.Add(new ErModelColumn { Name = column.Name, DataType = column.DataType, Nullable = column.Nullable, PrimaryKey = column.PrimaryKey });
        notes.Columns.Add(new ErModelColumn { Name = "author", DataType = "TEXT" });
        form.ApplyModelTable("notes", notes, null);
        Assert(form.CurrentDiagram.Find("products") != null && form.HasUnsavedChanges(), "New model tables are placed on the diagram.");
        Assert(!tableExists("products") && !columnExists("notes", "author"), "Editing the model must not touch the database.");
        AssertEquals("2", remaining().ToString(), "The model differs from the database by one table and one column.");

        using (SchemaSyncScriptForm sync = form.CreateModelSyncForm())
        {
            Assert(sync != null, "Model changes produce a sync script.");
            sync.CreateControl();
            SchemaSyncBatchResult result = sync.RunSelected();
            Assert(result.Succeeded, "Syncing the model to the database succeeds: " + result.Summary);
        }
        Assert(tableExists("products") && columnExists("notes", "author"), "Syncing creates the new table and column.");
        AssertEquals("0", remaining().ToString(), "After syncing, the database matches the model.");
        Assert(form.CreateModelSyncForm() == null, "No sync script when nothing differs.");

        db.ExecSQL("ALTER TABLE customers ADD COLUMN phone TEXT;");
        db.ExecSQL("CREATE TABLE audit (id INTEGER PRIMARY KEY);");
        SchemaComparisonResult changes = form.CaptureFromDatabase(false);
        Assert(changes.Differences.Any(item => item.Kind == SchemaDifferenceKind.ColumnMissingInTarget && item.DetailName == "phone") &&
               changes.Differences.Any(item => item.Kind == SchemaDifferenceKind.TableMissingInTarget && item.ObjectName == "audit"), "Capturing again lists database-side changes.");
        Assert(form.Model.Schema.Find("customers").Columns.Any(column => column.Name == "phone") && form.CurrentDiagram.Find("audit") != null, "Database changes flow into the model and diagram.");

        form.SaveModelTo(modelPath);
        ErModelDocument reloaded = ErModelService.Load(modelPath);
        Assert(reloaded.Schema != null && reloaded.Schema.Find("products") != null && reloaded.Schema.Find("audit") != null, "The model file keeps the schema.");
        form.DropModelTable("audit");
        Assert(form.Model.Schema.Find("audit") == null && form.CurrentDiagram.Find("audit") == null && tableExists("audit"), "Dropping from the model leaves the database alone.");
        using (SchemaSyncScriptForm sync = form.CreateModelSyncForm())
        {
            Assert(sync != null && sync.Script.DestructiveStatements.Any(statement => statement.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase) >= 0), "Dropping a model table proposes a DROP for review.");
            AssertEquals("0", sync.Script.Statements.Count.ToString(), "A model-only drop has no statement that runs by default.");
        }
        Assert(tableExists("audit"), "Proposing a DROP does not run it.");
        form.LoadModel(modelPath);
        Assert(form.IsModelFirst && form.Model.Schema.Find("audit") != null, "Reloading the model restores its schema.");
    }

    /// <summary>連接線手動路徑：可拖曳的垂直段落、存讀、SVG、重設與自動排列清除。</summary>
    public static void AssertErRouteFlow(ErDiagramForm form, string modelPath)
    {
        form.Canvas.MeasureRoutes();
        string key = form.Canvas.RouteKeys.FirstOrDefault(item => item.StartsWith("orders|customer_id|customers|", StringComparison.OrdinalIgnoreCase));
        Assert(key != null, "The orders → customers line should be routable: " + string.Join(", ", form.Canvas.RouteKeys));
        ErModelTablePlacement orders = form.CurrentDiagram.Find("orders");
        ErModelTablePlacement customers = form.CurrentDiagram.Find("customers");
        int customerY = customers.Y + ErModelService.HeaderHeight + ErModelService.RowHeight / 2;
        int orderY = orders.Y + ErModelService.HeaderHeight + ErModelService.RowHeight + ErModelService.RowHeight / 2;
        int midY = (customerY + orderY) / 2;
        int currentX = (orders.X <= customers.X ? orders.X + ErModelService.CardWidth + customers.X : customers.X + ErModelService.CardWidth + orders.X) / 2;
        AssertEquals(key, form.Canvas.RouteHitTest(new System.Drawing.Point(currentX, midY)), "The vertical segment should be hit-testable at its automatic position.");
        Assert(form.Canvas.RouteHitTest(new System.Drawing.Point(currentX + 40, midY)) == null, "Points away from the segment are not hits.");
        Assert(form.Canvas.MoveRoute(key, currentX + 37) && form.CurrentDiagram.FindRoute(key).X == currentX + 37 && form.HasUnsavedChanges(), "Dragging a line stores its route.");
        form.Canvas.MeasureRoutes();
        AssertEquals(key, form.Canvas.RouteHitTest(new System.Drawing.Point(currentX + 37, midY)), "The moved segment is hit-testable at its new position.");
        int middle = currentX;
        form.SaveModelTo(modelPath);
        ErModelDocument reloaded = ErModelService.Load(modelPath);
        Assert(reloaded.Diagrams[0].FindRoute(key) != null && reloaded.Diagrams[0].FindRoute(key).X == middle + 37, "Routes are saved in the model file.");
        Assert(form.Canvas.ResetRoute(key) && form.CurrentDiagram.FindRoute(key) == null, "Resetting a route removes it.");
        form.Canvas.MoveRoute(key, middle + 11);
        form.AutoArrange();
        Assert(form.CurrentDiagram.Routes.Count == 0, "Auto arrange clears manual routes.");
    }

    /// <summary>Data Vault 2.0：從模型中的資料表產生 Hub／Link／Satellite，並同步建立到 SQLite。</summary>
    public static void AssertErDataVaultFlow(ErDiagramForm form, IDatabase db)
    {
        Assert(form.IsModelFirst, "Data Vault generation needs a model schema.");
        DataVaultResult result = form.GenerateDataVault(new List<string> { "customers", "orders" });
        Assert(result.Hubs.Count == 2 && result.Links.Count == 1 && result.Satellites.SequenceEqual(new[] { "sat_customers" }) && form.CurrentDiagram.Name.StartsWith("Data Vault"),
            "Data Vault tables are generated into a new diagram (orders has no descriptive attributes, so no satellite): " + string.Join(",", result.AllTables));
        Assert(form.SuggestRoles() >= 0 && ErModelPatternService.RoleOf(form.Model, "hub_orders") == ErTableRoles.Hub, "Generated tables carry their roles.");
        using (SchemaSyncScriptForm sync = form.CreateModelSyncForm())
        {
            Assert(sync != null, "Data Vault tables produce a sync script.");
            sync.CreateControl();
            SchemaSyncBatchResult applied = sync.RunSelected();
            Assert(applied.Succeeded, "Creating the Data Vault tables succeeds: " + applied.Summary);
        }
        foreach (string table in result.AllTables)
        {
            Assert(db.SelectSQL("SELECT name FROM sqlite_master WHERE type = 'table' AND name = '" + table + "'").Rows.Count == 1, table + " exists after syncing.");
        }
        Assert(db.SelectSQL("PRAGMA table_info('sat_customers')").Rows.Cast<System.Data.DataRow>().Count(row => Convert.ToInt32(row["pk"]) > 0) == 2, "Satellites use a composite key.");
        form.SetTableRole("orders", ErTableRoles.Fact);
        Assert(ErModelPatternService.RoleOf(form.Model, "orders") == ErTableRoles.Fact, "Roles can be set by hand.");
        form.SetTableRole("orders", null);
        Assert(ErModelPatternService.RoleOf(form.Model, "orders") == null, "Roles can be cleared.");
    }
}
