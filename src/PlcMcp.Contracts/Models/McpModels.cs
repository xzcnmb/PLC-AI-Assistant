using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMcp.Contracts.Models;

public sealed record ToolAnnotations(
    [property: JsonPropertyName("readOnlyHint")] bool ReadOnlyHint,
    [property: JsonPropertyName("destructiveHint")] bool DestructiveHint,
    [property: JsonPropertyName("openWorldHint")] bool OpenWorldHint = false);

public sealed record ToolDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; init; }

    [JsonPropertyName("destructive")]
    public bool Destructive { get; init; }

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; init; }

    [JsonPropertyName("annotations")]
    public ToolAnnotations Annotations { get; init; }

    public ToolDescriptor(
        string name,
        string description,
        bool readOnly,
        bool destructive,
        object inputSchema,
        ToolAnnotations? annotations = null)
    {
        Name = name;
        Description = description;
        ReadOnly = readOnly;
        Destructive = destructive;
        InputSchema = inputSchema;
        Annotations = annotations ?? new ToolAnnotations(readOnly, destructive, false);
    }

    public void Deconstruct(
        out string name,
        out string description,
        out bool readOnly,
        out bool destructive,
        out object inputSchema)
    {
        name = Name;
        description = Description;
        readOnly = ReadOnly;
        destructive = Destructive;
        inputSchema = InputSchema;
    }
}

public sealed record ToolContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text)
{
    public static ToolContentBlock TextBlock(string text) => new("text", text);
}

public sealed record ToolCallResult
{
    [JsonPropertyName("content")]
    public IReadOnlyList<ToolContentBlock> Content { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public ToolCallResult(
        IReadOnlyList<ToolContentBlock> content,
        bool isError = false,
        Dictionary<string, JsonElement>? extensionData = null)
    {
        Content = content;
        IsError = isError;
        ExtensionData = extensionData;
    }

    public ToolCallResult(bool isError, object content)
    {
        IsError = isError;
        if (content is IReadOnlyList<ToolContentBlock> blocks)
        {
            Content = blocks;
        }
        else if (content is ToolContentBlock block)
        {
            Content = [block];
        }
        else if (content is string str)
        {
            Content = [ToolContentBlock.TextBlock(str)];
        }
        else
        {
            Content = [ToolContentBlock.TextBlock(content?.ToString() ?? string.Empty)];
        }
    }
}

public sealed record JsonRpcError(int Code, string Message, object? Data = null);

