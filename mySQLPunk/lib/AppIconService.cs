using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace mySQLPunk.lib
{
    /// <summary>
    /// 應用程式圖示（看板娘 Punky）的單一來源。
    /// 優先載入輸出目錄的 punky.ico（多尺寸：小圖是臉部特寫、大圖是完整貼紙），
    /// 缺檔時退回 exe 內嵌圖示，再不行就用系統預設。
    /// </summary>
    public static class AppIconService
    {
        private static Icon _appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null) _appIcon = LoadAppIcon();
                return _appIcon;
            }
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "punky.ico");
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }

            try
            {
                Icon fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (fromExe != null) return fromExe;
            }
            catch { }

            return SystemIcons.Application;
        }
    }
}
