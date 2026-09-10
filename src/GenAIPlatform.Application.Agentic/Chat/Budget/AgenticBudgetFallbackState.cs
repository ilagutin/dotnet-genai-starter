namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticBudgetFallbackState(Guid conversationId, string correlationId)
{
    private int used;

    public Guid ConversationId { get; } = conversationId;

    public string CorrelationId { get; } = correlationId;

    public bool TryMark() => Interlocked.Exchange(ref used, 1) == 0;
}
