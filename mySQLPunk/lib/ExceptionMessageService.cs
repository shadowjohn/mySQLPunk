using System;

namespace mySQLPunk.lib
{
    public static class ExceptionMessageService
    {
        public static string GetReason(Exception ex)
        {
            string reason = ex == null ? null : ex.Message;
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Localization.T("Object.UnknownError");
            }

            return reason.Trim();
        }

        public static string Format(string messageKey, Exception ex)
        {
            return Localization.Format(messageKey, GetReason(ex));
        }

        public static string Format(string messageKey, object arg0, Exception ex)
        {
            return Localization.Format(messageKey, arg0, GetReason(ex));
        }
    }
}
