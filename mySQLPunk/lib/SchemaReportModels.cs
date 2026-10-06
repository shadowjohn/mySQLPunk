using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace mySQLPunk.lib
{
    public enum ReportExportFormat { Docx, Xlsx, Pdf }

    public static class ReportText
    {
        public static string Get(string chinese, string english)
        {
            return Localization.CurrentLanguage == Localization.English ? english : chinese;
        }
    }

    public sealed class SchemaReportDocument
    {
        public string DatabaseName { get; set; }
        public string EngineName { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public List<SchemaReportTable> Tables { get; set; } = new List<SchemaReportTable>();
    }

    public sealed class SchemaReportTableInfo
    {
        public string Name { get; set; }
        public string Comment { get; set; }
        public string DisplayName
        {
            get
            {
                // Only the comment is flattened for the one-line picker; the table identifier stays exact.
                string comment = string.Join(" ", (Comment ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                return string.IsNullOrEmpty(comment) ? Name : Name + " - " + comment;
            }
        }
        public override string ToString() { return DisplayName; }
    }

    public sealed class SchemaReportTable
    {
        public string Name { get; set; }
        public string Comment { get; set; }
        public string Ddl { get; set; }
        public List<SchemaReportColumn> Columns { get; set; } = new List<SchemaReportColumn>();
        public DataTable Indexes { get; set; }
    }

    public sealed class SchemaReportColumn
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Nullable { get; set; }
        public string Key { get; set; }
        public string DefaultValue { get; set; }
        public string Comment { get; set; }
        public string Extra { get; set; }
    }

    public sealed class ReportTemplate
    {
        public string Id { get; private set; }
        public ReportExportFormat Format { get; private set; }
        public string SampleStem { get; private set; }
        private readonly string chineseName, englishName, chineseDescription, englishDescription;
        public string Name => ReportText.Get(chineseName, englishName);
        public string Description => ReportText.Get(chineseDescription, englishDescription);
        public string Extension => "." + Format.ToString().ToLowerInvariant();

        internal ReportTemplate(string id, ReportExportFormat format, string stem, string zh, string en, string descriptionZh, string descriptionEn)
        {
            Id = id; Format = format; SampleStem = stem;
            chineseName = zh; englishName = en; chineseDescription = descriptionZh; englishDescription = descriptionEn;
        }
    }

    public static class ReportTemplateCatalog
    {
        private static readonly ReportTemplate[] Templates =
        {
            new ReportTemplate("docx.classic-dictionary", ReportExportFormat.Docx, "docx-classic", "經典資料字典", "Classic dictionary", "橘色表頭與逐表欄位說明，方便編修。", "Orange table headers and editable field descriptions."),
            new ReportTemplate("docx.project-handover", ReportExportFormat.Docx, "docx-delivery", "專案交付文件", "Project handover", "封面、文件資訊、目錄與逐表章節。", "Cover, document information, contents and table chapters."),
            new ReportTemplate("docx.schema-specification", ReportExportFormat.Docx, "docx-schema-specification", "系統資料庫規格書", "Database specification", "直式清冊、主鍵與索引摘要，以及淡藍欄位規格表。", "Portrait inventory, key and index summaries, and blue field specifications."),
            new ReportTemplate("xlsx.table-sheets", ReportExportFormat.Xlsx, "xlsx-tables", "一表一工作表", "One sheet per table", "每張資料表各自一頁，方便分表檢閱。", "A separate worksheet for each table."),
            new ReportTemplate("xlsx.column-inventory", ReportExportFormat.Xlsx, "xlsx-inventory", "欄位總覽清冊", "Column inventory", "所有欄位集中一頁，方便篩選與比較。", "All fields in one filterable worksheet."),
            new ReportTemplate("pdf.structure-handbook", ReportExportFormat.Pdf, "pdf-standard", "標準結構手冊", "Structure handbook", "逐表章節與頁碼，適合固定版面分享。", "Table chapters and page numbers for sharing."),
            new ReportTemplate("pdf.compact-review", ReportExportFormat.Pdf, "pdf-compact", "精簡審閱版", "Compact review", "緊湊欄位表格，適合快速審閱。", "Compact field tables for quick review.")
        };

        public static IEnumerable<ReportTemplate> ForFormat(ReportExportFormat format)
        {
            return Templates.Where(template => template.Format == format);
        }

        public static ReportTemplate Find(string id)
        {
            var template = Templates.FirstOrDefault(item => item.Id == id);
            if (template == null) throw new ArgumentException(ReportText.Get("無效的報表樣版。", "Invalid report template."));
            return template;
        }
    }

    public sealed class ReportExportRequest
    {
        public string DatabaseName { get; set; }
        public string EngineName { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string TemplateId { get; set; }
        public string OutputPath { get; set; }
        public List<string> Tables { get; set; } = new List<string>();
        public bool Overwrite { get; set; }
    }

    public sealed class ReportExportProgress
    {
        public string Stage { get; set; }
        public string Message { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public bool IsIndeterminate { get; set; }
    }
}
