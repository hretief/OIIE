using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using IsbmProvider.Abstractions;
using IsbmProvider.Durable;
using IsbmProvider.Models;

namespace IsbmProvider.Http;

/// <summary>
/// Shared helper for session operations:
///   1. Opens sessions via Table Storage (synchronous, immediately queryable) + Durable Entity (cursor)
///   2. Validates sessions against Table Storage first (consistent), falls back to Durable Entity
///   3. Returns distinct fault messages for "not found", "closed", and "wrong type"
/// </summary>
public static class SessionHelper
{
    /// <summary>
    /// Opens a session: writes to Table Storage registry (synchronous, immediately visible
    /// to all instances), then signals the Durable Entity (eventually consistent, used for
    /// cursor state only). The session is usable immediately.
    /// </summary>
    public static async Task<string> OpenAndConfirmAsync(
        DurableTaskClient durable, SessionMetadata meta, ISessionRegistry registry)
    {
        // Register in Table Storage — synchronous, immediately visible to all instances
        registry.Register(meta);

        // Signal the Durable Entity — eventually consistent, used for cursor state
        await durable.Entities.SignalEntityAsync(
            new EntityInstanceId(nameof(SessionEntity), meta.SessionId),
            nameof(SessionEntity.Open), meta);

        return meta.SessionId;
    }

    /// <summary>
    /// Retrieves and validates a session. Checks Table Storage registry first (always consistent),
    /// then loads Durable Entity cursor state for read operations.
    /// </summary>
    public static async Task<(SessionState State, IsbmFaultException? Fault)> GetValidatedSessionAsync(
        DurableTaskClient durable, string sessionId, SessionType expectedType,
        ISessionRegistry registry)
    {
        // Check session registry first (Table Storage — always consistent)
        var meta = registry.GetSession(sessionId);

        if (meta is null)
        {
            // Fallback: check Durable Entity (may have been opened before registry existed)
            var fallback = await durable.Entities.GetEntityAsync<SessionState>(
                new EntityInstanceId(nameof(SessionEntity), sessionId));
            if (fallback?.State?.Metadata is null)
                return (null!, IsbmFaultException.Session($"Session '{sessionId}' does not exist.", 404));
            meta = fallback.State.Metadata;
        }

        if (meta.SessionType != expectedType)
            return (null!, IsbmFaultException.Session(
                $"Session '{sessionId}' is type {meta.SessionType}, expected {expectedType}.", 422));

        // Load cursor state from Durable Entity (for ReadNotRemoved / Removed sets)
        var entityState = await durable.Entities.GetEntityAsync<SessionState>(
            new EntityInstanceId(nameof(SessionEntity), sessionId));
        var state = entityState?.State ?? new SessionState { Metadata = meta, IsOpen = true };

        // Ensure metadata is populated even if entity hasn't caught up yet
        state.Metadata ??= meta;

        return (state, null);
    }
}
