namespace Aexon.Core.Auth;

/// <summary>
/// Represents a missing or expired NyxID login state.
/// </summary>
public sealed class NotLoggedInException : InvalidOperationException
{
    public NotLoggedInException(string message)
        : base(message)
    {
    }

    public NotLoggedInException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
