using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.UnitTests;

public sealed class StatusValueMappingTests
{
    [Fact]
    public void Mappings_PreserveEveryDefinedValueAndRejectUndefinedValues()
    {
        Verify<AgenticChatStatus>(static value => value.ToPublicValue(),
            ["Succeeded", "TimedOut", "BudgetExceeded", "ToolLimitExceeded", "ToolFailed", "ToolRejected",
             "ApprovalRequired", "StepLimitExceeded", "UsageUnavailable", "InvalidUsage"]);
        Verify<ToolExecutionStatus>(static value => value.ToPublicValue(),
            ["NotExecuted", "Rejected", "ValidationFailed", "ApprovalRequired", "Failed", "Succeeded"]);
        Verify<ToolApprovalState>(static value => value.ToPublicValue(), ["NotRequired", "Required", "SimulatedApproved"]);
        Verify<ToolValidationStatus>(static value => value.ToPublicValue(), ["Valid", "Invalid"]);
        Verify<EvaluationRunStatus>(static value => value.ToPublicValue(), ["Running", "Succeeded", "Failed", "Canceled"]);
        Verify<EvaluationCaseStatus>(static value => value.ToPublicValue(), ["Passed", "Failed"]);
        Verify<AiRequestLogStatus>(static value => value.ToPublicValue(), ["Succeeded", "Failed"]);
    }

    private static void Verify<T>(Func<T, string> map, string[] expected) where T : struct, Enum
    {
        Assert.Equal(expected, Enum.GetValues<T>().Select(map));
        foreach (var undefined in new[] { -1, int.MinValue, int.MaxValue })
        {
            var value = (T)Enum.ToObject(typeof(T), undefined);
            var failure = Assert.Throws<ArgumentOutOfRangeException>(() => map(value));
            Assert.Equal("status", failure.ParamName);
            Assert.Equal(value, failure.ActualValue);
        }
    }
}
