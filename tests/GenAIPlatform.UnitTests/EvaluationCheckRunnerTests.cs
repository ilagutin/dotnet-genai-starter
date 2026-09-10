using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Evaluations.Checks;

namespace GenAIPlatform.UnitTests;

public sealed class EvaluationCheckRunnerTests
{
    private readonly EvaluationCheckRunner runner = new();

    [Fact]
    public void Run_PassesRetrievalCheckWhenMinimumHitsAreMet()
    {
        var result = Run(new EvaluationCheck("retrieval", MinimumHits: 2), "answer [1]", retrievedCount: 2);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Run_FailsRetrievalCheckWhenMinimumHitsAreMissing()
    {
        var result = Run(new EvaluationCheck("retrieval", MinimumHits: 2), "answer", retrievedCount: 1);

        Assert.False(result.Passed);
        Assert.Contains("Expected at least 2", result.Message);
    }

    [Fact]
    public void Run_PassesCitationCheckWhenRequiredReferenceIsPresent()
    {
        var result = Run(new EvaluationCheck("citation", CitationReferences: ["[2]"]), "answer [2]", 0);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Run_FailsCitationCheckWhenRequiredReferenceIsMissing()
    {
        var result = Run(new EvaluationCheck("citation", CitationReferences: ["[1]"]), "answer", 0);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Run_PassesRequiredPhraseCheckCaseInsensitively()
    {
        var result = Run(new EvaluationCheck("required_phrase", Phrase: "Clean Architecture"), "clean architecture works", 0);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Run_FailsRequiredPhraseCheckWhenPhraseIsEmpty()
    {
        var result = Run(new EvaluationCheck("required_phrase", Phrase: " "), "answer", 0);

        Assert.False(result.Passed);
        Assert.Contains("Required phrase", result.Message);
    }

    [Fact]
    public void Run_FailsForbiddenPhraseCheckWhenPhraseIsPresent()
    {
        var result = Run(new EvaluationCheck("forbidden_phrase", Phrase: "secret"), "contains SECRET", 0);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Run_FailsForbiddenPhraseCheckWhenPhraseIsEmpty()
    {
        var result = Run(new EvaluationCheck("forbidden_phrase", Phrase: " "), "answer", 0);

        Assert.False(result.Passed);
        Assert.Contains("non-empty phrase", result.Message);
    }

    private EvaluationCheckResult Run(
        EvaluationCheck check,
        string answer,
        int retrievedCount)
    {
        var evaluationCase = new EvaluationCase("case-1", "Case 1", "Question", [check]);
        return Assert.Single(runner.Run(evaluationCase, answer, retrievedCount));
    }
}
