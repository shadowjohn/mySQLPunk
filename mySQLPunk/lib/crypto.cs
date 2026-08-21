using System;
using utility;

namespace mySQLPunk.lib
{
    public static class Crypto
    {
        private static myinclude my = new myinclude();
        // 使用使用者指定的自定義金鑰
        private static readonly string thekey = "這裡可以放變數";

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                return my.enPWD_string(plainText, thekey);
            }
            catch
            {
                return plainText;
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";
            try
            {
                string plain = my.dePWD_string(cipherText, thekey);
                // 舊設定檔可能存的是明文（root、user 這類短字串剛好是合法 base64，
                // 解碼流程走得完但結果是亂碼）。用「再加密要能還原輸入」驗證
                // 這串真的是本程式加密過的資料，不是就原樣回傳。
                if (!string.Equals(my.enPWD_string(plain, thekey), cipherText, StringComparison.Ordinal))
                {
                    return cipherText;
                }
                return plain;
            }
            catch
            {
                // 解密失敗（舊資料或格式不對），回傳原字串
                return cipherText;
            }
        }
    }
}
