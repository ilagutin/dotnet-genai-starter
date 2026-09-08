using System.Globalization;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.UnitTests;

public sealed class AgentToolSchemaValidationTests
{
    public static TheoryData<string, string> InvalidBuiltInArguments => new()
    {
        { "GetCurrentUserProfile", "[]" },
        { "GetCurrentUserProfile", "{\"unexpected\":true}" },
        { "CreateSupportTicket", "{\"title\":\"Missing description\"}" },
        { "CreateSupportTicket", "{\"title\":\"Help\",\"description\":\"Now\",\"extra\":true}" },
        { "CreateSupportTicket", "{\"title\":7,\"description\":\"Now\"}" },
        { "CreateSupportTicket", "{\"title\":\"Help\",\"description\":\"Now\",\"priority\":\"urgent\"}" },
        { "DraftEmail", "{\"to\":\"a@example.test\",\"subject\":\"Hello\"}" },
        { "DraftEmail", "{\"to\":\"a@example.test\",\"subject\":\"Hello\",\"body\":\"Body\",\"extra\":true}" },
        { "DraftEmail", "{\"to\":\"a@example.test\",\"subject\":\"Hello\",\"body\":9}" }
    };

    public static TheoryData<string> UnsafeSchemas => new()
    {
        "[]",
        "{\"type\":7}",
        "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\"}",
        "{\"type\":\"object\",\"$ref\":\"https://example.test/schema.json\"}",
        "{\"type\":\"object\",\"$recursiveRef\":\"#\"}",
        "{\"type\":\"object\",\"patternProperties\":{\".*\":{\"type\":\"string\"}}}",
        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"pattern\":\".*\"}}}"
    };

    [Theory]
    [MemberData(nameof(InvalidBuiltInArguments))]
    public async Task ExecuteAsync_RejectsInvalidBuiltInPayloadBeforeExecution(
        string toolName,
        string argumentsJson)
    {
        var tool = DemoTools().Single(candidate => candidate.Definition.Name == toolName);

        var (result, audit) = await ExecuteAsync(tool, Json(argumentsJson), approveRiskyTools: false);

        Assert.Equal(AgentToolArgumentValidator.SchemaInvalidCode, result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        Assert.Null(result.Output);
        Assert.Equal("Invalid", audit.ValidationStatus);
        Assert.Equal("ValidationFailed", audit.ExecutionStatus);
        Assert.Equal("{}", audit.Arguments.GetRawText());
        Assert.Null(audit.Output);
        Assert.Equal(AgentToolArgumentValidator.SchemaInvalidCode, audit.ErrorCode);
        Assert.NotNull(audit.ErrorMessage);
        Assert.InRange(audit.ErrorMessage.Length, 1, AgentToolArgumentValidator.MaxErrorLength);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesGenuineSchemaEvaluationPath()
    {
        var tool = DemoTools().Single(candidate => candidate.Definition.Name == "CreateSupportTicket");

        var (result, _) = await ExecuteAsync(
            tool,
            Json("""{"title":7,"description":"synthetic-schema-value-marker"}"""),
            approveRiskyTools: false);

        Assert.Equal(AgentToolArgumentValidator.SchemaInvalidCode, result.ErrorCode);
        Assert.Equal(
            "Tool arguments do not match the declared schema at /properties/title",
            result.ErrorMessage);
        Assert.DoesNotContain("synthetic-schema-value-marker", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesBuiltInSemanticNormalizationAndDeterministicTicketId()
    {
        var tool = DemoTools().Single(candidate => candidate.Definition.Name == "CreateSupportTicket");
        var arguments = Json("""{"title":"  Help  ","description":"  Need help  "}""");

        var first = await ExecuteAsync(tool, arguments, approveRiskyTools: false);
        var second = await ExecuteAsync(tool, arguments, approveRiskyTools: false);

        Assert.Equal("Help", first.Audit.Arguments.GetProperty("title").GetString());
        Assert.Equal("Need help", first.Audit.Arguments.GetProperty("description").GetString());
        Assert.Equal("normal", first.Audit.Arguments.GetProperty("priority").GetString());
        Assert.Equal(
            first.Result.Output!.Value.GetProperty("ticketId").GetString(),
            second.Result.Output!.Value.GetProperty("ticketId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_PreservesDraftOnlyModeAndRejectsSemanticWhitespace()
    {
        var draft = DemoTools().Single(candidate => candidate.Definition.Name == "DraftEmail");
        var valid = Json("""{"to":"  a@example.test ","subject":" Hello ","body":" Body "}""");

        var executed = await ExecuteAsync(draft, valid, approveRiskyTools: true);
        var invalid = await ExecuteAsync(
            draft,
            Json("""{"to":"a@example.test","subject":"Hello","body":"   "}"""),
            approveRiskyTools: true);

        Assert.Equal("draft", executed.Audit.Arguments.GetProperty("mode").GetString());
        Assert.Equal("a@example.test", executed.Audit.Arguments.GetProperty("to").GetString());
        Assert.Equal("Body", executed.Audit.Arguments.GetProperty("body").GetString());
        Assert.Equal(ToolExecutionStatus.Succeeded, executed.Result.ExecutionStatus);
        Assert.Equal("missing_required_argument", invalid.Result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, invalid.Result.ExecutionStatus);
    }

    [Theory]
    [MemberData(nameof(UnsafeSchemas))]
    public async Task ExecuteAsync_RejectsInvalidOrUnsupportedSchemaBeforeSemanticValidation(string schema)
    {
        var tool = new ProbeTool(schema);

        var (result, audit) = await ExecuteAsync(tool, Json("{}"), approveRiskyTools: true);

        Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        Assert.Equal(0, tool.SemanticValidationCalls);
        Assert.Equal(0, tool.ExecutionCalls);
        Assert.Equal("{}", audit.Arguments.GetRawText());
        Assert.InRange(audit.ErrorMessage!.Length, 1, AgentToolArgumentValidator.MaxErrorLength);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsSchemaSizeDepthAndNodeBoundsBeforeToolCode()
    {
        var cases = new[]
        {
            new ProbeTool(JsonSerializer.Serialize(new { type = "object", description = new string('s', 65536) })),
            new ProbeTool(NestedSchema(33)),
            new ProbeTool(WideSchema(260)),
            new ProbeTool(NestedLiteralSchema(33)),
            new ProbeTool(LiteralNodeSchema(260))
        };

        foreach (var tool in cases)
        {
            var (result, _) = await ExecuteAsync(tool, Json("{}"), approveRiskyTools: true);
            Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
            Assert.Equal(0, tool.SemanticValidationCalls);
            Assert.Equal(0, tool.ExecutionCalls);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsArgumentSizeAndDepthBoundsBeforeToolCode()
    {
        const string secretKey = "syntheticArgumentKeyMarker";
        const string secretValue = "synthetic-argument-value-marker";
        const string schema = "{\"type\":\"object\"}";
        var oversizedTool = new ProbeTool(schema);
        var deepTool = new ProbeTool(schema);

        var oversized = await ExecuteAsync(
            oversizedTool,
            Json(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [$"{secretKey}{new string('k', 65536)}"] = secretValue
            })),
            approveRiskyTools: true);
        var deep = await ExecuteAsync(
            deepTool,
            Json(NestedArguments(33, secretKey, secretValue), maxDepth: 128),
            approveRiskyTools: true);

        foreach (var outcome in new[] { oversized, deep })
        {
            Assert.Equal(AgentToolArgumentValidator.SchemaInvalidCode, outcome.Result.ErrorCode);
            Assert.Equal("Tool arguments do not match the declared schema at /", outcome.Result.ErrorMessage);
            Assert.Equal("{}", outcome.Audit.Arguments.GetRawText());
            Assert.Null(outcome.Audit.Output);
            Assert.Equal(outcome.Result.ErrorMessage, outcome.Audit.ErrorMessage);
            Assert.DoesNotContain(secretKey, outcome.Result.ErrorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        }
        Assert.Equal(0, oversizedTool.SemanticValidationCalls);
        Assert.Equal(0, deepTool.SemanticValidationCalls);
        Assert.Equal(0, oversizedTool.ExecutionCalls);
        Assert.Equal(0, deepTool.ExecutionCalls);
    }

    [Fact]
    public async Task ExecuteAsync_SupportsLocalDefinitionFragmentReference()
    {
        var tool = new ProbeTool("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": { "text": { "type": "string" } },
          "type": "object",
          "properties": { "value": { "$ref": "#/$defs/text" } },
          "required": [ "value" ],
          "additionalProperties": false
        }
        """);

        var (result, _) = await ExecuteAsync(
            tool,
            Json("""{"value":"local"}"""),
            approveRiskyTools: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal(1, tool.SemanticValidationCalls);
        Assert.Equal(1, tool.ExecutionCalls);
    }

    [Fact]
    public async Task ExecuteAsync_TreatsPropertyDefinitionAndLiteralNamesAsData()
    {
        var tool = new ProbeTool("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": {
            "pattern": { "type": "string" },
            "$ref": { "type": "integer" },
            "$schema": { "type": "boolean" }
          },
          "type": "object",
          "properties": {
            "pattern": { "$ref": "#/$defs/pattern" },
            "$ref": { "$ref": "#/$defs/$ref" },
            "$schema": { "$ref": "#/$defs/$schema" },
            "literal": {
              "const": {
                "pattern": ".*",
                "$ref": "https://literal.example/schema",
                "$schema": "literal"
              }
            },
            "examples": {
              "enum": [
                { "patternProperties": {}, "$recursiveRef": "literal" }
              ]
            }
          },
          "required": [ "pattern", "$ref", "$schema", "literal", "examples" ],
          "additionalProperties": false
        }
        """);

        var (result, _) = await ExecuteAsync(
            tool,
            Json("""
            {
              "pattern": "plain name",
              "$ref": 7,
              "$schema": true,
              "literal": {
                "pattern": ".*",
                "$ref": "https://literal.example/schema",
                "$schema": "literal"
              },
              "examples": { "patternProperties": {}, "$recursiveRef": "literal" }
            }
            """),
            approveRiskyTools: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal(1, tool.ExecutionCalls);
    }

    [Theory]
    [InlineData("additionalProperties")]
    [InlineData("unevaluatedProperties")]
    [InlineData("propertyNames")]
    [InlineData("contains")]
    [InlineData("items")]
    [InlineData("unevaluatedItems")]
    [InlineData("not")]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("contentSchema")]
    public async Task ExecuteAsync_RejectsRestrictedKeywordInSingleSubschemaLocation(string keyword)
    {
        var tool = new ProbeTool($"{{\"type\":\"array\",\"{keyword}\":{{\"pattern\":\".*\"}}}}");

        var (result, _) = await ExecuteAsync(tool, Json("[]"), approveRiskyTools: true);

        Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
        Assert.Equal(0, tool.ExecutionCalls);
    }

    [Theory]
    [InlineData("allOf")]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    [InlineData("prefixItems")]
    public async Task ExecuteAsync_RejectsRestrictedKeywordInSubschemaArray(string keyword)
    {
        var tool = new ProbeTool($"{{\"{keyword}\":[{{\"pattern\":\".*\"}}]}}");

        var (result, _) = await ExecuteAsync(tool, Json("{}"), approveRiskyTools: true);

        Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
        Assert.Equal(0, tool.ExecutionCalls);
    }

    [Theory]
    [InlineData("{\"$ref\":\"#/examples/0/hidden\",\"examples\":[{\"hidden\":{\"pattern\":\".*\"}}]}")]
    [InlineData("{\"$ref\":\"#/const/hidden\",\"const\":{\"hidden\":{\"pattern\":\".*\"}}}")]
    [InlineData("{\"$ref\":\"#/x-extension/hidden\",\"x-extension\":{\"hidden\":{\"pattern\":\".*\"}}}")]
    public async Task ExecuteAsync_InspectsReferenceTargetReachedThroughNonSchemaContainer(string schema)
    {
        var tool = new ProbeTool(schema);

        var (result, _) = await ExecuteAsync(tool, Json("{}"), approveRiskyTools: true);

        Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
        Assert.Equal(0, tool.ExecutionCalls);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsValidReferenceTargetReachedThroughConstData()
    {
        var tool = new ProbeTool("""
        {
          "$ref": "#/const/hidden",
          "const": { "hidden": { "type": "object" } }
        }
        """);

        var (result, _) = await ExecuteAsync(
            tool,
            Json("""{"hidden":{"type":"object"}}"""),
            approveRiskyTools: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal(1, tool.ExecutionCalls);
    }

    [Theory]
    [InlineData("#/$defs/a~1b~0c")]
    [InlineData("#named")]
    [InlineData("#dynamic")]
    public async Task ExecuteAsync_SupportsLocalPointerAnchorAndDynamicAnchorReferences(string reference)
    {
        var referenceKeyword = reference == "#dynamic" ? "$dynamicRef" : "$ref";
        var tool = new ProbeTool($$"""
        {
          "$defs": {
            "a/b~c": { "type": "string" },
            "named": { "$anchor": "named", "type": "string" },
            "dynamic": { "$dynamicAnchor": "dynamic", "type": "string" }
          },
          "{{referenceKeyword}}": "{{reference}}"
        }
        """);

        var (result, _) = await ExecuteAsync(tool, Json("\"value\""), approveRiskyTools: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_SupportsFragmentReferenceWithinNestedIdResource()
    {
        var tool = new ProbeTool("""
        {
          "type": "object",
          "properties": {
            "value": {
              "$id": "nested",
              "$defs": { "text": { "type": "string" } },
              "$ref": "#/$defs/text"
            }
          },
          "required": [ "value" ]
        }
        """);

        var (result, _) = await ExecuteAsync(tool, Json("{\"value\":\"local\"}"), approveRiskyTools: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesInvisibleBidiControlAndNonPrintableSchemaPathCharacters()
    {
        const string unsafeSegment = "field\u0001\u202e\ue000\u0378";
        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                [unsafeSegment] = new { type = "string" }
            },
            required = new[] { unsafeSegment },
            additionalProperties = false
        });
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object> { [unsafeSegment] = 7 });
        var tool = new ProbeTool(schema);

        var (result, audit) = await ExecuteAsync(tool, Json(arguments), approveRiskyTools: true);

        Assert.Equal(AgentToolArgumentValidator.SchemaInvalidCode, result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        var errorMessage = Assert.IsType<string>(audit.ErrorMessage);
        Assert.Contains('?', errorMessage);
        Assert.DoesNotContain(errorMessage, static character =>
            char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or
                UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned);
        Assert.InRange(errorMessage.Length, 1, AgentToolArgumentValidator.MaxErrorLength);
    }

    private static IReadOnlyList<IAgentTool> DemoTools()
    {
        return new DemoAgentToolRegistry(new TestUserContext()).GetAvailableTools();
    }

    private static async Task<(AgentToolExecutionResult Result, ToolAuditLogEntry Audit)> ExecuteAsync(
        IAgentTool tool,
        JsonElement arguments,
        bool approveRiskyTools)
    {
        var audit = new CapturingAuditRepository();
        var executor = new GovernedAgentToolExecutor(
            new ToolPolicy(),
            new AgentToolArgumentValidator(),
            new AgentToolAuditLogWriter(audit, TimeProvider.System));
        var result = await executor.ExecuteAsync(
            new AgentToolExecutionRequest(
                "call-1",
                tool.Definition.Name,
                tool.Definition.SchemaVersion,
                arguments,
                [tool],
                new AgentToolExecutionContext(
                    Guid.NewGuid(),
                    "tenant-a",
                    "alice",
                    "schema-test",
                    "tool-policy-v1",
                    approveRiskyTools)),
            CancellationToken.None);
        return (result, Assert.Single(audit.Entries));
    }

    private static JsonElement Json(string json, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return document.RootElement.Clone();
    }

    private static string NestedSchema(int levels)
    {
        var schema = "{\"type\":\"object\"}";
        for (var index = 0; index < levels; index++)
        {
            schema = $"{{\"allOf\":[{schema}]}}";
        }

        return schema;
    }

    private static string NestedArguments(int levels, string key = "nested", string leafValue = "value")
    {
        var arguments = JsonSerializer.Serialize(leafValue);
        for (var index = 0; index < levels; index++)
        {
            arguments = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
            {
                [$"{key}{index}"] = Json(arguments)
            });
        }

        return arguments;
    }

    private static string NestedLiteralSchema(int levels)
    {
        return JsonSerializer.Serialize(new { examples = new[] { Json(NestedArguments(levels)) } });
    }

    private static string LiteralNodeSchema(int nodeCount)
    {
        var examples = Enumerable.Range(0, nodeCount)
            .Select(static index => new { value = index })
            .ToArray();
        return JsonSerializer.Serialize(new { examples });
    }

    private static string WideSchema(int propertyCount)
    {
        var properties = Enumerable.Range(0, propertyCount)
            .ToDictionary(index => $"p{index}", static _ => new { type = "string" });
        return JsonSerializer.Serialize(new { type = "object", properties });
    }

    private sealed class ProbeTool(string schema) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "SchemaProbe",
            "Schema validation probe.",
            "v1",
            Json(schema, maxDepth: 128));

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed("Test probe.");

        public int SemanticValidationCalls { get; private set; }

        public int ExecutionCalls { get; private set; }

        public ToolValidationResult Validate(JsonElement arguments)
        {
            SemanticValidationCalls++;
            return ToolValidationResult.Valid(arguments.Clone());
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            ExecutionCalls++;
            return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, Json("{}")));
        }
    }

    private sealed class CapturingAuditRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestUserContext : IUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "alice";

        public string? TenantId => "tenant-a";

        public IReadOnlyCollection<string> Roles { get; } = ["developer"];

        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
    }
}
