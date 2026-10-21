using System.Net.Http.Headers;
using Azure.Core;
using k8s.Authentication;

namespace Function.Infrastructure.Kubernetes;

/// <summary>
/// Bearer tokens for an AKS API server with Entra ID + Azure RBAC, from the function's managed identity (or your az login
/// locally). Cached until five minutes before they expire.
/// </summary>
public sealed class AksTokenProvider(TokenCredential credential) : ITokenProvider
{
    // The Entra application behind every AKS cluster's API (the same id in every tenant).
    private const string AksScope = "6dae42f8-4368-4678-94ff-3960e28e3630/.default";

    private readonly SemaphoreSlim gate = new(1, 1);
    private AccessToken token;

    public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        if (token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
                    token = await credential.GetTokenAsync(new TokenRequestContext([AksScope]), cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        return new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
