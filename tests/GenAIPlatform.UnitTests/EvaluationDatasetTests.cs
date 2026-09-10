using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Evaluations.Dataset;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.UnitTests;

public sealed class EvaluationDatasetTests
{
    private readonly EvaluationDatasetValidator validator = new();

    [Fact]
    public async Task SampleDataset_ContainsAtLeastFiveSafeDeterministicCases()
    {
        var provider = new InMemoryEvaluationDatasetProvider();

        var dataset = await provider.GetDatasetAsync("sample-v1", CancellationToken.None);

        validator.Validate(dataset);
        Assert.Equal("sample-v1", dataset.Version);
        Assert.True(dataset.Cases.Count >= 5);
        Assert.All(dataset.Cases, evaluationCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(evaluationCase.Id));
            Assert.False(string.IsNullOrWhiteSpace(evaluationCase.Question));
            Assert.False(string.IsNullOrWhiteSpace(evaluationCase.Context));
            Assert.NotEmpty(evaluationCase.Checks);
            Assert.DoesNotContain("secret", evaluationCase.Question, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", evaluationCase.Context, StringComparison.OrdinalIgnoreCase);
            foreach (var check in evaluationCase.Checks.Where(static check => check.Type == "required_phrase"))
            {
                Assert.DoesNotContain(check.Phrase!, evaluationCase.Question, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Theory]
    [InlineData("required_phrase")]
    [InlineData("forbidden_phrase")]
    public void Validate_RejectsEmptyPhraseChecks(string checkType)
    {
        var dataset = CreateDataset(new EvaluationCheck(checkType, Phrase: " "));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("dataset-v1", exception.Message);
        Assert.Contains("case-1", exception.Message);
        Assert.Contains(checkType, exception.Message);
        Assert.Contains("non-empty phrase", exception.Message);
        Assert.DoesNotContain("expected answer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsInvalidRetrievalMinimum()
    {
        var dataset = CreateDataset(new EvaluationCheck("retrieval", MinimumHits: 0));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("case-1", exception.Message);
        Assert.Contains("minimumHits", exception.Message);
    }

    [Fact]
    public void Validate_RejectsRetrievalCheckWithFixtureContext()
    {
        var dataset = CreateDataset(CreateCase(checks: [new EvaluationCheck("retrieval", MinimumHits: 1)]));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("case-1", exception.Message);
        Assert.Contains("retrieval", exception.Message);
        Assert.Contains("fixture context", exception.Message);
        Assert.DoesNotContain("expected answer content", exception.Message);
    }

    [Fact]
    public void Validate_RejectsBlankCaseId()
    {
        var dataset = CreateDataset(CreateCase(id: " "));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("dataset-v1", exception.Message);
        Assert.Contains("#1", exception.Message);
        Assert.Contains("case id", exception.Message);
        Assert.DoesNotContain("expected answer content", exception.Message);
    }

    [Fact]
    public void Validate_RejectsBlankCaseName()
    {
        var dataset = CreateDataset(CreateCase(name: " "));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("case-1", exception.Message);
        Assert.Contains("case name", exception.Message);
        Assert.DoesNotContain("expected answer content", exception.Message);
    }

    [Fact]
    public void Validate_RejectsBlankCaseQuestion()
    {
        var dataset = CreateDataset(CreateCase(question: " "));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("case-1", exception.Message);
        Assert.Contains("question", exception.Message);
        Assert.DoesNotContain("expected answer content", exception.Message);
    }

    [Fact]
    public void Validate_RejectsBlankCitationReference()
    {
        var dataset = CreateDataset(new EvaluationCheck("citation", CitationReferences: ["[1]", " "]));

        var exception = Assert.Throws<EvaluationValidationException>(() =>
            validator.Validate(dataset));

        Assert.Contains("case-1", exception.Message);
        Assert.Contains("citation", exception.Message);
        Assert.Contains("empty citation reference", exception.Message);
    }

    private static EvaluationDataset CreateDataset(EvaluationCheck check)
    {
        return CreateDataset(CreateCase(checks: [check]));
    }

    private static EvaluationDataset CreateDataset(EvaluationCase evaluationCase)
    {
        return new EvaluationDataset("dataset-v1", [evaluationCase]);
    }

    private static EvaluationCase CreateCase(
        string id = "case-1",
        string name = "Case 1",
        string question = "Question",
        IReadOnlyList<EvaluationCheck>? checks = null)
    {
        return new EvaluationCase(
            id,
            name,
            question,
            checks ?? [new EvaluationCheck("required_phrase", Phrase: "expected answer")],
            Context: "expected answer content");
    }
}
