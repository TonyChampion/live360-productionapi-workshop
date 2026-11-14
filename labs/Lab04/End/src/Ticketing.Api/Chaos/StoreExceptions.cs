namespace Ticketing.Api.Chaos;

/// <summary>A failure that may succeed if retried.</summary>
public class TransientStoreException(string message) : Exception(message);

/// <summary>The store cannot be reached at all.</summary>
public class StoreUnavailableException(string message) : Exception(message);
