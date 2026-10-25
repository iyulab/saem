using System.Net.Http.Headers;

namespace Saem.Materialization.Cli;

/// <summary>The client a run speaks to the group's Formbase with.</summary>
internal static class GroupStore
{
    /// <summary>A client for <paramref name="store"/>, sending <paramref name="token"/> (one of the store's access keys) as a bearer token when there is one.</summary>
    public static HttpClient Client(Uri store, string? token)
    {
        var client = new HttpClient { BaseAddress = store };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}
