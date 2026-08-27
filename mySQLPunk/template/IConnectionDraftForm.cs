using System.Collections.Generic;

namespace mySQLPunk.template
{
    public interface IConnectionDraftForm
    {
        void ApplyConnectionDraft(Dictionary<string, object> connection);
    }
}
