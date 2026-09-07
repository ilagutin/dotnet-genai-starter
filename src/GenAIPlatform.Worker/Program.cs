using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplicationCore(builder.Configuration);
builder.Services.AddKnowledgeApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWorker();

var host = builder.Build();
host.Run();
