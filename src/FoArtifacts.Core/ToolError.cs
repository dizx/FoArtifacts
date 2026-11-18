namespace FoArtifacts.Core;

/// <summary>Why a build or refresh could not run.</summary>
public sealed record ToolError(string Code, string Message, string? Hint);
