using System.Windows.Forms;

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
    }
}
