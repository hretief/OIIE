namespace IsbmProvider.Models;

/// <summary>ISBM ChannelType — a channel is for publications (fan-out) or requests (correlated).</summary>
public enum ChannelType { Publication, Request }

/// <summary>
/// The four ISBM session flavours. A session owns the per-session read cursor and filters;
/// its type constrains which operations are legal (e.g. ReadPublication only on Subscription).
/// </summary>
public enum SessionType { Publication, Subscription, ProviderRequest, ConsumerRequest }

/// <summary>ISBM Security Level conformance (spec §8). Surfaced via configuration discovery.</summary>
public enum SecurityLevel { None = 1, Core = 2, InterEnterprise = 3, Defense = 4 }

/// <summary>Body content filtering languages (spec conformance items 12–13).</summary>
public enum ContentFilteringLanguage { XPath10, JSONPath }
