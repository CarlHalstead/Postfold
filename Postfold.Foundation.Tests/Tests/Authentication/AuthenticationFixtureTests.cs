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

    [Test]
    public async Task OpenAsync_WhenCallbackListenerStartsLater_RetriesAndSendsCallback()
    {
        // Arrange
        using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Uri redirect = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndPoint!).Port + "/");
        Uri authorization = new Uri("https://fixture.test/?redirect_uri=" + Uri.EscapeDataString(redirect.AbsoluteUri) + "&state=synthetic-state");
        await using AuthenticationFixture fixture = new AuthenticationFixture();

        // Act
        Failure opened = await fixture.OpenAsync(authorization, deadline.Token);
        await fixture.CallbackRetrying.Task.WaitAsync(deadline.Token);
        listener.Listen(1);
        using Socket connection = await listener.AcceptAsync(deadline.Token);
        using NetworkStream stream = new NetworkStream(connection);
        using StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        string? request = await reader.ReadLineAsync(deadline.Token);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token)))
        {
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"), deadline.Token);
        string response = await fixture.Callback!.WaitAsync(deadline.Token);

        // Assert
        Assert.That(opened, Is.EqualTo(Failure.None));
        Assert.That(request, Is.EqualTo("GET /?code=synthetic-code&state=synthetic-state HTTP/1.1"));
        Assert.That(response, Does.Contain("200 OK"));
    }

    [Test]
    public async Task OpenAsync_WhenCancelledBeforeListenerStarts_StopsCallbackRetry()
    {
        // Arrange
        using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Uri redirect = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndPoint!).Port + "/");
        Uri authorization = new Uri("https://fixture.test/?redirect_uri=" + Uri.EscapeDataString(redirect.AbsoluteUri) + "&state=synthetic-state");
        AuthenticationFixture fixture = new AuthenticationFixture();
        try
        {
            await fixture.OpenAsync(authorization, cancellation.Token);
            await fixture.CallbackRetrying.Task.WaitAsync(deadline.Token);

            // Act
            await cancellation.CancelAsync();

            // Assert
            await Assert.ThatAsync(async () => await fixture.Callback!.WaitAsync(deadline.Token), Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThatAsync(async () => await fixture.DisposeAsync(), Throws.InstanceOf<OperationCanceledException>());
        }
    }
}
