namespace GenAIPlatform.Domain.Observability;

public static class AiRequestLogStatusMapping
{
    public static string ToPublicValue(this AiRequestLogStatus status) => status switch
    {
        AiRequestLogStatus.Succeeded => "Succeeded",
        AiRequestLogStatus.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined AiRequestLogStatus value.")
    };
}
