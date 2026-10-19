namespace PMS.Application.Exceptions;

/// <summary>The caller is signed in but the target is outside their scope (e.g. another Manager's lot).</summary>
public class ForbiddenException(string message) : Exception(message);
