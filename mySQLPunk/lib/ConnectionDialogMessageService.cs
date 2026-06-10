using System;

namespace mySQLPunk.lib
{
    public static class ConnectionDialogMessageService
    {
        public static string BuildTestFailedMessage(string providerName, Exception ex)
        {
            return Localization.Format("Connection.TestFailed", providerName, BuildExceptionReason(ex));
        }

        public static string BuildInitializationFailedMessage(string providerName, Exception ex)
        {
            return Localization.Format("Connection.InitializationFailed", providerName, BuildExceptionReason(ex));
        }

        private static string BuildExceptionReason(Exception ex)
        {
            string reason = ex == null ? null : ex.Message;
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Localization.T("Object.UnknownError");
            }

            return reason.Trim();
        }
    }
}
