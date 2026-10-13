using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Kyuplan.Client.Blazor;

/// <summary>
/// The login screen sends the login itself (<c>BasicAuthHandler</c>), so the browser must not take part: with credentials omitted a 401
/// does not open the browser's own login dialog, the page shows its login screen instead.
/// </summary>
internal sealed class BrowserNoCredentialsHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
        return base.SendAsync(request, cancellationToken);
    }
}
