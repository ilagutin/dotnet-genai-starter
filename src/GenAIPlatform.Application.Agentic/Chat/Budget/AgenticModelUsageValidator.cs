using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Chat;

internal static class AgenticModelUsageValidator
{
    public static AgenticChatStatus? Validate(AiModelUsage? usage, out int totalTokens)
    {
        totalTokens = 0;
        if (usage is null || usage is { InputTokens: null, OutputTokens: null, TotalTokens: null })
        {
            return AgenticChatStatus.UsageUnavailable;
        }

        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.TotalTokens < 0)
        {
            return AgenticChatStatus.InvalidUsage;
        }

        try
        {
            int? componentTotal = usage is { InputTokens: { } input, OutputTokens: { } output }
                ? checked(input + output)
                : null;
            if (usage.TotalTokens is { } suppliedTotal)
            {
                if (usage.InputTokens is { } knownInput && suppliedTotal < knownInput
                    || usage.OutputTokens is { } knownOutput && suppliedTotal < knownOutput)
                {
                    return AgenticChatStatus.InvalidUsage;
                }

                if (componentTotal is { } sum && sum != suppliedTotal)
                {
                    return AgenticChatStatus.InvalidUsage;
                }

                totalTokens = suppliedTotal;
                return null;
            }

            if (componentTotal is { } derivedTotal)
            {
                totalTokens = derivedTotal;
                return null;
            }
        }
        catch (OverflowException)
        {
            return AgenticChatStatus.InvalidUsage;
        }

        return AgenticChatStatus.InvalidUsage;
    }
}
