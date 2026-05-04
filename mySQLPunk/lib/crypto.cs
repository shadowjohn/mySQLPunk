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
                return my.dePWD_string(cipherText, thekey);
            }
            catch
            {
                // 如果解密失敗（可能是舊資料或格式不對），回傳原字串
                return cipherText;
            }
        }
    }
}
