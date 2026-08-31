using System;
using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public enum AiModelComparisonFailure
    {
        None,
        InvalidProvider,
        MissingEndpoint,
        MissingModel,
        SameModel
    }

    /// <summary>建立兩個模型共用的請求內容，並在送出前檢查比較設定。</summary>
    public static class AiModelComparisonService
    {
        public static AiChatSettings CreateSettings(string providerId, string model, AiChatSettings currentSettings)
        {
            string normalizedProvider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
            AiProviderPreset preset = FindExactPreset(normalizedProvider);
            string endpoint = preset == null ? string.Empty : preset.Endpoint;
            if (currentSettings != null
                && string.Equals(currentSettings.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = currentSettings.Endpoint;
            }

            string normalizedModel = (model ?? string.Empty).Trim();
            if (preset != null && normalizedModel.Length == 0 && preset.AuthStyle != "cli")
                normalizedModel = preset.DefaultModel ?? string.Empty;

            return new AiChatSettings
            {
                Provider = normalizedProvider,
                Endpoint = (endpoint ?? string.Empty).Trim(),
                Model = AiChatService.NormalizeCliModel(normalizedProvider, normalizedModel)
            };
        }

        public static bool TryValidate(
            AiChatSettings left,
            AiChatSettings right,
            out AiModelComparisonFailure failure)
        {
            failure = ValidateChoice(left);
            if (failure != AiModelComparisonFailure.None) return false;

            failure = ValidateChoice(right);
            if (failure != AiModelComparisonFailure.None) return false;

            if (string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals((left.Endpoint ?? string.Empty).Trim(), (right.Endpoint ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals((left.Model ?? string.Empty).Trim(), (right.Model ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                failure = AiModelComparisonFailure.SameModel;
                return false;
            }

            return true;
        }

        public static List<AiChatMessage> BuildMessages(
            string systemPrompt,
            string contextPrefix,
            string schemaContext,
            IList<AiChatMessage> history,
            string userPrompt)
        {
            List<AiChatMessage> messages = new List<AiChatMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new AiChatMessage("system", systemPrompt));
            if (!string.IsNullOrWhiteSpace(schemaContext))
                messages.Add(new AiChatMessage("system", (contextPrefix ?? string.Empty) + "\n" + schemaContext));

            int start = history == null ? 0 : Math.Max(0, history.Count - 12);
            if (history != null)
            {
                for (int i = start; i < history.Count; i++)
                {
                    AiChatMessage message = history[i];
                    if (message == null) continue;
                    if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                    messages.Add(new AiChatMessage(message.Role.ToLowerInvariant(), message.Content ?? string.Empty));
                }
            }

            messages.Add(new AiChatMessage("user", (userPrompt ?? string.Empty).Trim()));
            return messages;
        }

        public static AiProviderPreset FindExactPreset(string providerId)
        {
            foreach (AiProviderPreset preset in AiChatService.Presets)
            {
                if (string.Equals(preset.Id, providerId, StringComparison.OrdinalIgnoreCase)) return preset;
            }
            return null;
        }

        private static AiModelComparisonFailure ValidateChoice(AiChatSettings settings)
        {
            if (settings == null) return AiModelComparisonFailure.InvalidProvider;
            AiProviderPreset preset = FindExactPreset(settings.Provider);
            if (preset == null) return AiModelComparisonFailure.InvalidProvider;
            if (preset.AuthStyle != "cli" && string.IsNullOrWhiteSpace(settings.Endpoint))
                return AiModelComparisonFailure.MissingEndpoint;
            if (preset.AuthStyle != "cli" && string.IsNullOrWhiteSpace(settings.Model))
                return AiModelComparisonFailure.MissingModel;
            return AiModelComparisonFailure.None;
        }
    }
}
