using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests;

internal sealed class LocalMailServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly X509Certificate2 _leaf;
    private readonly bool _imap;
    private readonly bool _startTls;
    private readonly Task _server;

    internal LocalMailServer(bool imap, bool startTls = false)
    {
        _imap = imap;
        _startTls = startTls;
        using RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new CertificateRequest("CN=Postfold synthetic fixture CA", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Authority = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using X509Certificate2 publicLeaf = request.Create(Authority, DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 withKey = publicLeaf.CopyWithPrivateKey(key);
        _leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.UserKeySet);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = RunAsync();
    }

    internal X509Certificate2 Authority
    {
        get;
    }
    internal int Port
    {
        get;
    }
    internal bool RejectSecondRecipient
    {
        get; set;
    }
    internal bool RejectAllRecipients
    {
        get; set;
    }
    internal bool RejectData
    {
        get; set;
    }
    internal bool AdvertiseStartTls { get; set; } = true;
    internal bool LoseAcknowledgement
    {
        get; set;
    }
    internal uint UidValidity { get; set; } = 7;
    internal bool AdvancedImap
    {
        get; set;
    }

    internal bool AttachmentMessage
    {
        get; set;
    }
    internal bool OmitNativeMove
    {
        get; set;
    }
    internal bool IncludeArchive
    {
        get; set;
    }
    internal List<uint> ArchiveUids { get; } = new List<uint>();
    internal uint[] CurrentUids { get; set; } = [1, 2];
    internal Dictionary<uint, string> RemoteContent { get; } = new Dictionary<uint, string>();
    internal List<string> Commands { get; } = new List<string>();
    internal string? SubmittedMime
    {
        get; private set;
    }
    internal int SubmissionCount
    {
        get; private set;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _server;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or
            AuthenticationException)
        {
            // A fixture connection cancelled by test shutdown is observed here.
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            // Stopping the listener can dispose its socket while an accept is pending.
        }

        Authority.Dispose();
        _leaf.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Stream transport = client.GetStream();
            if (!_startTls)
            {
                transport = await SecureAsync(transport);
            }

            await using Stream owned = transport;
            if (_imap)
            {
                await ImapAsync(transport);
            }
            else
            {
                await SmtpAsync(transport);
            }
        }
    }

    private async Task<Stream> SecureAsync(Stream source)
    {
        SslStream ssl = new SslStream(source, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _leaf,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, _stop.Token);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    private async Task SmtpAsync(Stream stream)
    {
        await ReplyAsync(stream, "220 localhost synthetic SMTP\r\n");
        int recipients = 0;
        while (await LineAsync(stream) is string line)
        {
            Commands.Add(line);
            if (line.StartsWith("EHLO", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, _startTls && stream is not SslStream && AdvertiseStartTls ?
                    "250-localhost\r\n250 STARTTLS\r\n" :
                    "250-localhost\r\n250 AUTH PLAIN\r\n");
            }
            else if (line == "STARTTLS")
            {
                await ReplyAsync(stream, "220 Begin TLS\r\n");
                stream = await SecureAsync(stream);
            }
            else if (line.StartsWith("AUTH ", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "235 Authenticated\r\n");
            }
            else if (line.StartsWith("MAIL FROM:", StringComparison.Ordinal))
            {
                recipients = 0;
                await ReplyAsync(stream, "250 Sender accepted\r\n");
            }
            else if (line.StartsWith("RCPT TO:", StringComparison.Ordinal))
            {
                recipients++;
                await ReplyAsync(stream, RejectAllRecipients || RejectSecondRecipient && recipients == 2 ?
                    "550 Recipient rejected\r\n" : "250 Recipient accepted\r\n");
            }
            else if (line == "DATA")
            {
                await ReplyAsync(stream, "354 Send data\r\n");
                StringBuilder mime = new StringBuilder();
                while (await LineAsync(stream) is string data && data != ".")
                {
                    mime.Append(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data);
                    mime.Append("\r\n");
                }

                SubmittedMime = mime.ToString();
                SubmissionCount++;
                if (LoseAcknowledgement)
                {
                    await stream.DisposeAsync();
                    return;
                }

                await ReplyAsync(stream, RejectData ? "550 Synthetic data rejection\r\n" :
                    "250 Accepted synthetic receipt\r\n");
            }
            else if (line == "QUIT")
            {
                await ReplyAsync(stream, "221 Goodbye\r\n");
                return;
            }
            else
            {
                await ReplyAsync(stream, "250 OK\r\n");
            }
        }
    }

    private async Task ImapAsync(Stream stream)
    {
        string capabilities = "IMAP4rev1 AUTH=PLAIN" + (_startTls && AdvertiseStartTls ? " STARTTLS" : "") +
            (AdvancedImap ? " CONDSTORE QRESYNC UIDPLUS IDLE ENABLE" + (OmitNativeMove ? "" : " MOVE") : "");
        await ReplyAsync(stream, "* OK [CAPABILITY " + capabilities + "] synthetic IMAP\r\n");
        while (await LineAsync(stream) is string line)
        {
            Commands.Add(line);
            int space = line.IndexOf(' ');
            string tag = line[..space];
            string command = line[(space + 1)..];
            if (command.StartsWith("CAPABILITY", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* CAPABILITY " + capabilities + "\r\n");
            }
            else if (command == "STARTTLS")
            {
                await ReplyAsync(stream, tag + " OK Begin TLS\r\n");
                stream = await SecureAsync(stream);
                capabilities = capabilities.Replace(" STARTTLS", "", StringComparison.Ordinal);
                continue;
            }
            else if (command.StartsWith("AUTHENTICATE", StringComparison.Ordinal))
            {
                if (command.Split(' ').Length == 2)
                {
                    await ReplyAsync(stream, "+ \r\n");
                    await LineAsync(stream);
                }
            }
            else if (command.StartsWith("LIST", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n");
                if (IncludeArchive)
                {
                    await ReplyAsync(stream, "* LIST (\\HasNoChildren) \"/\" \"Archive\"\r\n");
                }
            }
            else if (command.StartsWith("SELECT", StringComparison.Ordinal) ||
                command.StartsWith("EXAMINE", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* FLAGS (\\Seen \\Flagged \\Deleted)\r\n* 2 EXISTS\r\n" +
                    "* OK [UIDVALIDITY " + UidValidity + "] generation\r\n* OK [UIDNEXT 3] next\r\n");
                if (AdvancedImap)
                {
                    await ReplyAsync(stream, "* OK [HIGHESTMODSEQ 10] modification sequence\r\n");
                }
                await ReplyAsync(stream, tag + (command.StartsWith("SELECT", StringComparison.Ordinal) ?
                    " OK [READ-WRITE] selected\r\n" : " OK [READ-ONLY] selected\r\n"));
                continue;
            }
            else if (command.StartsWith("UID MOVE", StringComparison.Ordinal) ||
                command.StartsWith("UID COPY", StringComparison.Ordinal))
            {
                uint uid = uint.Parse(command.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
                ArchiveUids.Add(3);
                if (command.StartsWith("UID MOVE", StringComparison.Ordinal))
                {
                    CurrentUids = Array.FindAll(CurrentUids, item => item != uid);
                    await ReplyAsync(stream, "* 1 EXPUNGE\r\n");
                }

                await ReplyAsync(stream, tag + " OK [COPYUID " + UidValidity + " " + uid + " 3] complete\r\n");
                continue;
            }
            else if (command.StartsWith("UID EXPUNGE", StringComparison.Ordinal))
            {
                uint uid = uint.Parse(command.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
                CurrentUids = Array.FindAll(CurrentUids, item => item != uid);
                await ReplyAsync(stream, "* 1 EXPUNGE\r\n");
            }
            else if (command.StartsWith("UID FETCH", StringComparison.Ordinal))
            {
                if (command.Contains("BODY.PEEK[", StringComparison.Ordinal))
                {
                    uint uid = uint.Parse(command.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
                    string content = RemoteContent.GetValueOrDefault(uid) ??
                        "From: sender@synthetic.invalid\r\nSubject: Synthetic body\r\nContent-Type: text/plain\r\n\r\nsynthetic body\r\n";
                    int partStart = command.IndexOf('[', StringComparison.Ordinal) + 1;
                    string part = command[partStart..command.IndexOf(']', partStart)];
                    if (part.Length > 0)
                    {
                        content = AttachmentMessage && part == "2" ? "c3ludGhldGljIGF0dGFjaG1lbnQ=\r\n" : "synthetic body\r\n";
                    }

                    await ReplyAsync(stream, "* 1 FETCH (UID " + uid + " BODY[" + part + "]<0> {" +
                        Encoding.UTF8.GetByteCount(content) + "}\r\n" + content + ")\r\n");
                }
                else if (!command.Contains("CHANGEDSINCE 10", StringComparison.Ordinal))
                {
                    foreach (uint uid in CurrentUids)
                    {
                        await ReplyAsync(stream, "* " + uid + " FETCH (UID " + uid +
                            " FLAGS () RFC822.SIZE 100 " + (AdvancedImap ? "MODSEQ (10) " : "") +
                            "BODYSTRUCTURE " + (AttachmentMessage ?
                                "((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 16 1) " +
                                "(\"APPLICATION\" \"OCTET-STREAM\" (\"NAME\" \"report.bin\") NIL NIL \"BASE64\" 30 " +
                                "NIL (\"ATTACHMENT\" (\"FILENAME\" \"report.bin\")) NIL NIL) \"MIXED\" NIL NIL NIL) " :
                                "(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 16 1) ") +
                            "ENVELOPE (\"01 Jan 2026 00:00:00 +0000\" \"Synthetic " + uid + "\" " +
                            "((\"Sender\" NIL \"sender\" \"synthetic.invalid\")) NIL NIL " +
                            "((NIL NIL \"recipient\" \"synthetic.invalid\")) NIL NIL NIL \"<" + uid +
                            "@synthetic.invalid>\"))\r\n");
                    }
                }
            }
            else if (command.StartsWith("UID SEARCH", StringComparison.Ordinal) ||
                command.StartsWith("SEARCH", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* SEARCH " + string.Join(" ", CurrentUids) + "\r\n");
            }
            else if (command.StartsWith("ENABLE", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* ENABLED QRESYNC\r\n");
            }
            else if (command == "IDLE")
            {
                await ReplyAsync(stream, "+ idling\r\n* 3 EXISTS\r\n");
                string? done = await LineAsync(stream);
                Commands.Add(done ?? "");
            }
            else if (command.StartsWith("APPEND", StringComparison.Ordinal))
            {
                int begin = command.LastIndexOf('{');
                int length = int.Parse(command[(begin + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
                await ReplyAsync(stream, "+ literal\r\n");
                byte[] content = new byte[length];
                await stream.ReadExactlyAsync(content, _stop.Token);
                await LineAsync(stream);
                RemoteContent[3] = Encoding.UTF8.GetString(content);
                if (LoseAcknowledgement)
                {
                    await stream.DisposeAsync();
                    return;
                }

                await ReplyAsync(stream, tag + " OK [APPENDUID " + UidValidity + " 3] appended\r\n");
                continue;
            }
            else if (command.StartsWith("LOGOUT", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "* BYE Goodbye\r\n" + tag + " OK complete\r\n");
                return;
            }

            await ReplyAsync(stream, tag + " OK complete\r\n");
        }
    }

    private async Task<string?> LineAsync(Stream stream)
    {
        List<byte> bytes = new List<byte>();
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one, _stop.Token) != 0)
        {
            if (one[0] == 10)
            {
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(one[0]);
            if (bytes.Count > 1048576)
            {
                throw new InvalidDataException("Synthetic protocol line exceeds fixture limit.");
            }
        }

        return null;
    }

    private async Task ReplyAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }
}
