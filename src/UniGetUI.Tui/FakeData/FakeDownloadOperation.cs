using System.Net;
using System.Text;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;

namespace UniGetUI.Tui.FakeData;

/// <summary>
/// A real <see cref="DownloadOperation"/> whose HTTP client never touches the network: every request
/// is answered in memory with a small placeholder "installer". The download pipeline (details lookup,
/// file naming, progress, the saved file) is exercised exactly as in production.
/// </summary>
internal sealed class FakeDownloadOperation(IPackage package, string downloadPath) : DownloadOperation(package, downloadPath)
{
    protected override HttpClient CreateHttpClient() => new(new FakeInstallerHandler());

    internal static byte[] PlaceholderInstaller(Uri? installerUrl)
    {
        string body = $"UniGetUI fake-data placeholder installer.\nRequested: {installerUrl?.AbsoluteUri}\n"
                      + "This file is not an installer and contains no executable code.\n";
        return Encoding.UTF8.GetBytes(body + new string('.', 64 * 1024));
    }

    private sealed class FakeInstallerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] payload = PlaceholderInstaller(request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }
}
