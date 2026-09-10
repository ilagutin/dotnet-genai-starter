using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GenAIPlatform.Evaluations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class ApiV1EndpointTests
{
    [Fact]
    public async Task EvaluationEndpoints_RunAndReturnSummaryThroughApplicationService()
    {
        using var evaluationFactory = CreateEvaluationFactory();
        using var client = evaluationFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/evaluations/runs")
        {
            Content = JsonContent.Create(new
            {
                correlationId = "api-evaluation-test"
            })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EvaluationRunResponse>();
        Assert.NotNull(body);
        Assert.Equal("Succeeded", body.Status);
        var runId = body.RunId;

        using var summaryRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/evaluations/runs/{runId}/summary");
        summaryRequest.Headers.Add("X-Demo-User-Id", "alice");
        summaryRequest.Headers.Add("X-Demo-Tenant-Id", "tenant-a");
        var summaryResponse = await client.SendAsync(summaryRequest);
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);

        var summary = await summaryResponse.Content.ReadFromJsonAsync<EvaluationSummaryResponse>();

        Assert.NotNull(summary);
        Assert.Equal(runId, summary.RunId);
        Assert.Equal(1, summary.TotalCases);
        Assert.Equal(1, summary.PassedCases);
        Assert.Equal(0, summary.FailedCaseCount);

        var repository = evaluationFactory.Services.GetRequiredService<CapturingEvaluationRunRepository>();
        var capturedRun = Assert.Single(repository.Runs.Values);
        Assert.Equal("mock-chat-evaluation", capturedRun.Run.Model);
        using var modelSettings = JsonDocument.Parse(capturedRun.Run.ModelSettings);
        Assert.Equal(0, modelSettings.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(256, modelSettings.RootElement.GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public async Task EvaluationEndpoints_RejectInvalidModelOptionsWithBadRequest()
    {
        using var evaluationFactory = CreateEvaluationFactory();
        using var client = evaluationFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/evaluations/runs",
            new
            {
                model = "unapproved-model"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EvaluationCliRunner_UsesSameApplicationServiceBehaviorAsApi()
    {
        using var evaluationFactory = CreateEvaluationFactory();
        using var client = evaluationFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/evaluations/runs")
        {
            Content = JsonContent.Create(new { correlationId = "api-shared-eval" })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");
        var apiResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);

        using var scope = evaluationFactory.Services.CreateScope();
        var exitCode = await EvaluationCliRunner.RunAsync(
            scope.ServiceProvider.GetRequiredService<GenAIPlatform.Application.Core.Dispatching.IApplicationDispatcher>(),
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var repository = evaluationFactory.Services.GetRequiredService<CapturingEvaluationRunRepository>();
        Assert.Equal(2, repository.Runs.Count);
        Assert.All(repository.Runs.Values, run => Assert.Equal("Succeeded", run.Run.Status));
    }

}
