using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace mySQLPunk.lib
{
    public static class WindowsCredentialService
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_SESSION = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        public static string BuildTargetName(string profileName, IDictionary<string, object> conn)
        {
            string profile = SanitizePart(profileName);
            string name = SanitizePart(GetValue(conn, "conn_name"));
            string kind = SanitizePart(GetValue(conn, "db_kind"));
            string user = SanitizePart(GetValue(conn, "username"));
            string host = SanitizePart(GetValue(conn, "host"));
            string port = SanitizePart(GetValue(conn, "port"));
            string path = SanitizePart(GetValue(conn, "path"));

            string location = !string.IsNullOrWhiteSpace(host)
                ? host + (string.IsNullOrWhiteSpace(port) ? "" : "_" + port)
                : path;
            if (string.IsNullOrWhiteSpace(location)) location = "local";

            return "mySQLPunk/" + profile + "/" + kind + "/" + name + "/" + user + "@" + location;
        }

        public static bool TryWritePassword(string targetName, string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return false;
            if (password == null) password = string.Empty;

            return TryWritePassword(targetName, userName, password, CRED_PERSIST_LOCAL_MACHINE)
                || TryWritePassword(targetName, userName, password, CRED_PERSIST_SESSION);
        }

        private static bool TryWritePassword(string targetName, string userName, string password, int persist)
        {
            byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
            IntPtr blob = IntPtr.Zero;
            try
            {
                blob = Marshal.AllocCoTaskMem(passwordBytes.Length);
                Marshal.Copy(passwordBytes, 0, blob, passwordBytes.Length);

                CREDENTIAL credential = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = targetName,
                    CredentialBlobSize = passwordBytes.Length,
                    CredentialBlob = blob,
                    Persist = persist,
                    UserName = userName ?? string.Empty,
                    Comment = "mySQLPunk connection password"
                };

                return CredWrite(ref credential, 0);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (blob != IntPtr.Zero) Marshal.FreeCoTaskMem(blob);
            }
        }

        public static bool TryReadPassword(string targetName, out string password)
        {
            password = string.Empty;
            if (string.IsNullOrWhiteSpace(targetName)) return false;

            IntPtr credentialPtr;
            if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out credentialPtr))
            {
                return false;
            }

            try
            {
                CREDENTIAL credential = (CREDENTIAL)Marshal.PtrToStructure(credentialPtr, typeof(CREDENTIAL));
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                {
                    password = string.Empty;
                    return true;
                }

                byte[] passwordBytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, passwordBytes, 0, passwordBytes.Length);
                password = Encoding.Unicode.GetString(passwordBytes);
                return true;
            }
            catch
            {
                password = string.Empty;
                return false;
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        public static bool TryDeletePassword(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return true;
            if (CredDelete(targetName, CRED_TYPE_GENERIC, 0)) return true;

            const int ERROR_NOT_FOUND = 1168;
            if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return true;

            string ignored;
            return !TryReadPassword(targetName, out ignored);
        }

        private static string GetValue(IDictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null) return conn[key].ToString();
            return string.Empty;
        }

        private static string SanitizePart(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return "default";

            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-')
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append('_');
                }
            }

            string result = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "default" : result;
        }
    }
}
