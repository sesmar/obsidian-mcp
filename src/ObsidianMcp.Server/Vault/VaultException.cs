using ModelContextProtocol;

namespace ObsidianMcp.Server.Vault;

/// <summary>
/// An error whose message is safe and useful to return to the model (e.g. "note not found").
/// Derives from <see cref="McpException"/> so the MCP SDK surfaces the message as a tool error.
/// </summary>
public sealed class VaultException(string message) : McpException(message);
