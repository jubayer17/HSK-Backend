namespace HousekeepingParser.Core;

/// <summary>A rule was broken by the request. The message is shown to the user as written.</summary>
public sealed class BusinessRuleException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static BusinessRuleException BadRequest(string message) => new(400, message);
    public static BusinessRuleException Conflict(string message) => new(409, message);
    public static BusinessRuleException Forbidden(string message) => new(403, message);
    public static BusinessRuleException NotFound(string message) => new(404, message);
}
