using IsbmProvider.Models;

namespace IsbmProvider.Http;

/// <summary>
/// Validates that a MessageContent has actual content — either inlineContent or payloadRef.
/// Catches the silent-discard bug where a client sends e.g. "content" instead of "inlineContent"
/// and gets a 201 with an empty payload that only fails two hops later on read.
/// </summary>
public static class PayloadValidator
{
    public static IsbmFaultException? Validate(MessageContent? content)
    {
        if (content is null)
            return IsbmFaultException.Operation("Missing messageContent.");

        if (string.IsNullOrEmpty(content.InlineContent) && string.IsNullOrEmpty(content.PayloadRef))
            return IsbmFaultException.Operation(
                "messageContent must have either inlineContent or payloadRef. " +
                "Did you use the correct property name? (inlineContent, not content)");

        if (string.IsNullOrEmpty(content.MediaType))
            return IsbmFaultException.Operation("messageContent.mediaType is required.");

        return null; // valid
    }
}
