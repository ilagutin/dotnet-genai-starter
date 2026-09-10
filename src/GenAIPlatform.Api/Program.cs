using GenAIPlatform.Api;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Generation;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Application.Usage;
using GenAIPlatform.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationCore(builder.Configuration);
builder.Services.AddKnowledgeApplication(builder.Configuration);
builder.Services.AddGenerationApplication(builder.Configuration);
builder.Services.AddAgenticApplication(builder.Configuration);
builder.Services.AddEvaluationsApplication();
builder.Services.AddUsageApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApi(builder.Configuration, builder.Environment);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.MapHealthChecks("/health");
app.MapApiV1();

app.Run();

public partial class Program;
