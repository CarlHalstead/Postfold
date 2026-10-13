using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

/// <summary>In-process authored responses for native reader integration; never opens a socket.</summary>
internal sealed class QualificationImageTransport : HttpMessageHandler
{
    internal bool RedirectToPrivateAddress
    {
        get; set;
    }
    internal bool Oversized
    {
        get; set;
    }
    internal int Requests
    {
        get; private set;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        if (request.RequestUri?.AbsoluteUri != "https://images.synthetic.invalid/pixel")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (RedirectToPrivateAddress)
        {
            HttpResponseMessage redirected = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirected.Headers.Location = new Uri("https://127.0.0.1/tracker");
            return Task.FromResult(redirected);
        }

        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Oversized ? new byte[4194305] : QualificationImage.CreatePixel())
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return Task.FromResult(response);
    }
}
