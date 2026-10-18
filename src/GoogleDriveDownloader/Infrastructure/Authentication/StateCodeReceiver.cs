using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

internal sealed class StateCodeReceiver(ICodeReceiver receiver) : ICodeReceiver
{
    public string RedirectUri => receiver.RedirectUri;

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken cancellationToken)
    {
        // SDK перевіряє PKCE, але його loopback receiver не звіряє state відповіді.
        url.State = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var response = await receiver.ReceiveCodeAsync(url, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(response.State) || !string.Equals(url.State, response.State, StringComparison.Ordinal))
        {
            throw new AuthenticationException("Не вдалося перевірити відповідь браузера. Повторіть вхід через Google.");
        }

        return response;
    }
}
