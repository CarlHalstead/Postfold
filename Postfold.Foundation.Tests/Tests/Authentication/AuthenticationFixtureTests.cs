using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

[TestFixture]
public sealed class AuthenticationFixtureTests
{
    [TestCase("127.0.0.1", "127.0.0.1")]
    [TestCase("localhost", "127.0.0.1")]
    [TestCase("localhost", "::1")]
    public async Task OpenAsync_WhenRedirectUsesEitherLoopbackFamily_DeliversCallback(string host, string address)
    {
        // Arrange
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        TcpListener listener = new TcpListener(IPAddress.Parse(address), 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Uri redirect = new Uri("http://" + host + ":" + port + "/");
            Uri authorization = new Uri("https://fixture.test/authorize?redirect_uri=" +
                Uri.EscapeDataString(redirect.AbsoluteUri) + "&state=synthetic-state");

            // Act
            Failure opened = await fixture.OpenAsync(authorization, timeout.Token);
            using TcpClient callback = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = callback.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? request = await reader.ReadLineAsync(timeout.Token);
            string? header;
            do
            {
                header = await reader.ReadLineAsync(timeout.Token);
            }
            while (!string.IsNullOrEmpty(header));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            string response = await fixture.Callback!;

            // Assert
            Assert.That(opened, Is.EqualTo(Failure.None));
            Assert.That(request, Is.EqualTo("GET /?code=synthetic-code&state=synthetic-state HTTP/1.1"));
            Assert.That(response, Does.StartWith("HTTP/1.1 200 OK"));
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
        }
    }
}
