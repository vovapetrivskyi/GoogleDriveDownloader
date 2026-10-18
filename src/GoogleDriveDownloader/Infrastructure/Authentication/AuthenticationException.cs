namespace GoogleDriveDownloader.Infrastructure.Authentication;

public sealed class AuthenticationException(string message) : Exception(message);

public enum AuthenticationStatus
{
    SignedOut,
    Authorized,
    AuthorizationRequired
}
