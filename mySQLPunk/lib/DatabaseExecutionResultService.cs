using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public static class DatabaseExecutionResultService
    {
        public static string GetFailureReason(IDictionary<string, string> result)
        {
            if (result != null && result.ContainsKey("reason") && !string.IsNullOrWhiteSpace(result["reason"]))
            {
                return result["reason"];
            }

            return Localization.T("Common.SqlExecutionFailed");
        }
    }
}
