using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public interface IDockableForm
    {
        void SetMainHost(Form1 mainHost);
        string GetDisplayTitle();
        void PrepareForDocking();
        void PrepareForFloating();
        /// <summary>是否還有未儲存的變更（決定停靠時能不能安全銷毀重複視窗）。</summary>
        bool HasUnsavedChanges();
        /// <summary>這個視窗是否使用指定的資料庫連線（關閉連線時要連帶關閉）。</summary>
        bool UsesDatabase(IDatabase database);
    }
}
