using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Entities;
using IsbmProvider.Models;

namespace IsbmProvider.Durable;

/// <summary>
/// One Durable Entity instance per ISBM SessionID. Single-threaded per key, so the read cursor
/// and (for request-response) the response-correlation state are consistent without locks.
/// The entity is the hot working copy; Azure SQL holds the authoritative, queryable record.
/// </summary>
public class SessionEntity : TaskEntity<SessionState>
{
    public void Open(SessionMetadata metadata)
    {
        State.Metadata = metadata;
        State.IsOpen = true;
    }

    public void Close() => State.IsOpen = false;

    /// <summary>Record that a message was Read (so a later Remove targets the right one).</summary>
    public void RecordRead(string messageId) => State.ReadNotRemoved.Add(messageId);

    /// <summary>Record a Remove; idempotent.</summary>
    public void RecordRemoved(string messageId)
    {
        State.ReadNotRemoved.Remove(messageId);
        State.Removed.Add(messageId);
    }

    public SessionState Snapshot() => State;

    /// <summary>
    /// Static entry point — avoids AmbiguousMatchException with the base class's own RunAsync.
    /// </summary>
    [Function(nameof(SessionEntity))]
    public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
        => dispatcher.DispatchAsync<SessionEntity>();
}
