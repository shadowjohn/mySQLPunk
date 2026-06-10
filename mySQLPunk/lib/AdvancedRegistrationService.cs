using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace mySQLPunk.lib
{
    public sealed class AdvancedRegistrationPlan
    {
        public string ApplicationPath { get; set; }
        public string OpenWithCommand { get; set; }
        public string UrlProtocolCommand { get; set; }
        public bool RegisterSqlOpenWith { get; set; }
        public bool RegisterUrlProtocol { get; set; }
        public List<string> RegistryPaths { get; private set; }

        public AdvancedRegistrationPlan()
        {
            RegistryPaths = new List<string>();
        }
    }

    public static class AdvancedRegistrationService
    {
        public const string ApplicationRegistryName = "mySQLPunk.exe";
        public const string UrlProtocolName = "mysqlpunk";

        public static AdvancedRegistrationPlan BuildPlan(string applicationPath, bool registerSqlOpenWith, bool registerUrlProtocol)
        {
            string exePath = (applicationPath ?? string.Empty).Trim();
            AdvancedRegistrationPlan plan = new AdvancedRegistrationPlan
            {
                ApplicationPath = exePath,
                RegisterSqlOpenWith = registerSqlOpenWith,
                RegisterUrlProtocol = registerUrlProtocol,
                OpenWithCommand = Quote(exePath) + " " + Quote("%1"),
                UrlProtocolCommand = Quote(exePath) + " " + Quote("%1")
            };

            if (registerSqlOpenWith)
            {
                plan.RegistryPaths.Add(@"Software\Classes\Applications\" + ApplicationRegistryName);
                plan.RegistryPaths.Add(@"Software\Classes\Applications\" + ApplicationRegistryName + @"\SupportedTypes");
                plan.RegistryPaths.Add(@"Software\Classes\.sql\OpenWithList\" + ApplicationRegistryName);
            }

            if (registerUrlProtocol)
            {
                plan.RegistryPaths.Add(@"Software\Classes\" + UrlProtocolName);
            }

            return plan;
        }

        public static void ApplyFromOptions(string applicationPath)
        {
            Apply(BuildPlan(
                applicationPath,
                ApplicationOptionSettings.GetBool("AdvancedRegisterSqlFileOpen"),
                ApplicationOptionSettings.GetBool("AdvancedRegisterUrlProtocol")));
        }

        public static void Apply(AdvancedRegistrationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(plan.ApplicationPath)) throw new InvalidOperationException(Localization.T("AdvancedRegistration.ApplicationPathRequired"));

            if (plan.RegisterSqlOpenWith) RegisterSqlOpenWith(plan);
            else UnregisterSqlOpenWith();

            if (plan.RegisterUrlProtocol) RegisterUrlProtocol(plan);
            else UnregisterUrlProtocol();
        }

        private static void RegisterSqlOpenWith(AdvancedRegistrationPlan plan)
        {
            using (RegistryKey appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + ApplicationRegistryName))
            using (RegistryKey iconKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + ApplicationRegistryName + @"\DefaultIcon"))
            using (RegistryKey shellKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + ApplicationRegistryName + @"\shell\open\command"))
            using (RegistryKey supportedTypesKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + ApplicationRegistryName + @"\SupportedTypes"))
            using (RegistryKey openWithKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.sql\OpenWithList\" + ApplicationRegistryName))
            {
                if (appKey != null)
                {
                    appKey.SetValue("FriendlyAppName", "mySQLPunk", RegistryValueKind.String);
                }
                if (iconKey != null)
                {
                    iconKey.SetValue(string.Empty, Quote(plan.ApplicationPath) + ",0", RegistryValueKind.String);
                }
                if (shellKey != null)
                {
                    shellKey.SetValue(string.Empty, plan.OpenWithCommand, RegistryValueKind.String);
                }
                if (supportedTypesKey != null)
                {
                    supportedTypesKey.SetValue(".sql", string.Empty, RegistryValueKind.String);
                }
                if (openWithKey != null)
                {
                    openWithKey.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
                }
            }
        }

        private static void RegisterUrlProtocol(AdvancedRegistrationPlan plan)
        {
            using (RegistryKey protocolKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + UrlProtocolName))
            using (RegistryKey iconKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + UrlProtocolName + @"\DefaultIcon"))
            using (RegistryKey commandKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + UrlProtocolName + @"\shell\open\command"))
            {
                if (protocolKey != null)
                {
                    protocolKey.SetValue(string.Empty, "URL:mySQLPunk Protocol", RegistryValueKind.String);
                    protocolKey.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
                }
                if (iconKey != null)
                {
                    iconKey.SetValue(string.Empty, Quote(plan.ApplicationPath) + ",0", RegistryValueKind.String);
                }
                if (commandKey != null)
                {
                    commandKey.SetValue(string.Empty, plan.UrlProtocolCommand, RegistryValueKind.String);
                }
            }
        }

        private static void UnregisterSqlOpenWith()
        {
            DeleteSubKeyTreeSafe(Registry.CurrentUser, @"Software\Classes\.sql\OpenWithList\" + ApplicationRegistryName);
            DeleteSubKeyTreeSafe(Registry.CurrentUser, @"Software\Classes\Applications\" + ApplicationRegistryName);
        }

        private static void UnregisterUrlProtocol()
        {
            DeleteSubKeyTreeSafe(Registry.CurrentUser, @"Software\Classes\" + UrlProtocolName);
        }

        private static void DeleteSubKeyTreeSafe(RegistryKey root, string subKey)
        {
            try
            {
                using (RegistryKey existingKey = root.OpenSubKey(subKey))
                {
                    if (existingKey != null)
                    {
                        root.DeleteSubKeyTree(subKey);
                    }
                }
            }
            catch
            {
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
