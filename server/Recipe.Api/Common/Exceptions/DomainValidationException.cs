namespace Recipe.Api.Common.Exceptions;

/// <summary>
/// Thrown by a service when the caller's input is invalid in a way DataAnnotations
/// cannot express. Controllers translate it to 400.
/// </summary>
/// <remarks>
/// Deliberately its own type rather than <see cref="InvalidOperationException"/> or
/// <see cref="ArgumentException"/>: EF Core wraps database connection failures in
/// <c>InvalidOperationException</c> ("An exception has been raised that is likely due to
/// a transient failure"), so a controller catching that broadly reports an unreachable
/// database as a client error and hides a real outage behind a 400.
/// </remarks>
public class DomainValidationException(string message) : Exception(message);
