using System.Windows.Forms;

namespace mySQLPunk
{
    public interface IDockableForm
    {
        void SetMainHost(Form1 mainHost);
        string GetDisplayTitle();
        void PrepareForDocking();
        void PrepareForFloating();
    }
}
