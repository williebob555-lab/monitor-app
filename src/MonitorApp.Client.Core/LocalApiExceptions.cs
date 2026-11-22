namespace MonitorApp.Client.Core;

public abstract class LocalApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The node could not be reached at all (connection refused, timeout, DNS).</summary>
public sealed class NodeUnreachableException(Uri address, Exception inner)
    : LocalApiException($"Cannot reach the monitor node at {address}. Is the local peer running?", inner);

/// <summary>The node rejected the request as invalid (HTTP 400); the message comes from the node.</summary>
public sealed class ApiValidationException(string message) : LocalApiException(message);

/// <summary>The node answered, but not in a shape this client understands, or refused the request.</summary>
public sealed class IncompatibleApiException(string message, Exception? inner = null) : LocalApiException(message, inner);
