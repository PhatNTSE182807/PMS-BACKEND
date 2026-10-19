namespace PMS.Application.Exceptions;

/// <summary>
/// Someone else changed the target first (HTTP 409), e.g. another customer held the slot a moment
/// earlier (SRS FR-RSV-02 "first holder wins").
/// </summary>
public class ConflictException(string message) : Exception(message);
