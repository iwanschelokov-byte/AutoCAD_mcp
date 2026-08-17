using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Agent;

/// <summary>Generation failed in a way worth reporting to the caller verbatim.</summary>
public sealed class GenerationException : Exception
{
    public GenerationException(string message) : base(message) { }
}

/// <summary>
/// Asks Claude for a plan and the C# to carry it out.
///
/// Streams rather than waiting for a whole message: generated drawings can run
/// to thousands of tokens, and a non-streaming request that long risks hitting
/// the request timeout.
/// </summary>
public sealed class Generator
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly Effort _effort;

    public Generator(string model, Effort effort)
    {
        // The SDK resolves credentials itself (ANTHROPIC_API_KEY, or an
        // `ant auth login` profile). Nothing here reads or stores a key.
        _client = new AnthropicClient();
        _model = model;
        _effort = effort;
    }

    /// <summary>
    /// The SDK models a JSON Schema as a property bag of System.Text.Json
    /// elements, while the schema itself is authored with Newtonsoft alongside
    /// the rest of this codebase. Round-trip through text to bridge the two.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> SchemaDictionary(JObject schema)
    {
        using var document = JsonDocument.Parse(schema.ToString(Newtonsoft.Json.Formatting.None));
        return document.RootElement.EnumerateObject()
                       .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public async Task<JObject> GenerateAsync(string request, string context, CancellationToken ct)
    {
        string userContent = string.IsNullOrWhiteSpace(context)
            ? request
            : $"{request}\n\nAdditional context:\n{context}";

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 32000,
            // The catalogue is large and identical between calls, so cache it.
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = Prompts.BuildSystemPrompt(),
                    CacheControl = new CacheControlEphemeral(),
                },
            },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = _effort,
                Format = new JsonOutputFormat { Schema = SchemaDictionary(Prompts.ResponseSchema()) },
            },
            Messages = [new() { Role = Role.User, Content = userContent }],
        };

        var text = new StringBuilder();
        string? stopReason = null;
        string? modelUsed = null;
        long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0;

        try
        {
            await foreach (var e in _client.Messages.CreateStreaming(parameters).WithCancellation(ct))
            {
                if (e.TryPickStart(out var start))
                {
                    modelUsed = start.Message.Model;
                    inputTokens = start.Message.Usage.InputTokens;
                    cacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
                }
                else if (e.TryPickContentBlockDelta(out var block) &&
                         block.Delta.TryPickText(out var chunk))
                {
                    text.Append(chunk.Text);
                }
                else if (e.TryPickDelta(out var delta))
                {
                    stopReason = delta.Delta.StopReason?.ToString();
                    outputTokens = delta.Usage.OutputTokens;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GenerationException($"Claude API error: {ex.Message}");
        }

        // Check why generation stopped before reading content — a refusal has no
        // usable content block, and max_tokens leaves truncated JSON.
        // The SDK may surface this as the wire value ("max_tokens") or as the
        // enum name ("MaxTokens"); normalise so both compare equal.
        string reason = (stopReason ?? "").Replace("_", "").ToLowerInvariant();

        if (reason == "refusal")
            throw new GenerationException(
                "Claude declined this request. Rephrase it, or handle the drawing manually.");

        if (reason == "maxtokens")
            throw new GenerationException(
                "The generated code was cut off at the token limit. Split the request " +
                "into smaller pieces.");

        if (text.Length == 0)
            throw new GenerationException($"Claude returned no code (stop_reason={stopReason ?? "none"}).");

        JObject payload;
        try
        {
            payload = JObject.Parse(text.ToString());
        }
        catch (Exception ex)
        {
            throw new GenerationException($"Claude's response was not valid JSON: {ex.Message}");
        }

        payload["model"] = modelUsed ?? _model;
        payload["usage"] = new JObject
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
            ["cache_read_input_tokens"] = cacheReadTokens,
        };
        return payload;
    }
}
