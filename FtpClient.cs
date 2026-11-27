using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HvBackup;

public sealed class FtpEntry
{
    public string Name = "";
    public bool IsDirectory;
    public long Size;
}

public sealed class FtpException : Exception
{
    public int Code { get; }
    public FtpException(int code, string message) : base($"FTP {code}: {message}") => Code = code;
}

/// <summary>Минимальный FTP/FTPS-клиент без внешних зависимостей.</summary>
public sealed class FtpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly bool _explicitTls;
    private readonly bool _implicitTls;

    private TcpClient? _tcp;
    private Stream? _control;
    private readonly List<byte> _pending = new();
    private bool _protPrivate;

    /// <summary>Пассивный режим (по умолчанию). В активном режиме сервер подключается к нам.</summary>
    public bool Passive { get; set; } = true;

    public int TimeoutMs { get; set; } = 60000;

    /// <summary>Не проверять сертификат сервера (для FTPS с самоподписанным сертификатом).</summary>
    public bool SkipCertificateValidation { get; set; }

    public FtpClient(string host, int port, string user, string password, bool explicitTls, bool implicitTls)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _explicitTls = explicitTls;
        _implicitTls = implicitTls;
    }

    // ─────────────────────────────── Подключение ───────────────────────────────

    public async Task ConnectAsync()
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port);
        _control = _tcp.GetStream();

        if (_implicitTls)
        {
            _control = await UpgradeToTlsAsync(_control);
            _pending.Clear();
        }

        var greeting = await ReadReplyAsync();
        Expect(greeting, 220, "приветствие сервера");

        if (_explicitTls)
        {
            var auth = await SendCommandAsync("AUTH TLS", null);
            if (auth.Code != 234)
                throw new FtpException(auth.Code, "Сервер не поддержал AUTH TLS: " + auth.Text);
            _control = await UpgradeToTlsAsync(_control);
            _pending.Clear();
        }

        var user = await SendCommandAsync("USER " + _user, null);
        if (user.Code == 331)
        {
            var pass = await SendCommandAsync("PASS " + _password, null);
            if (pass.Code >= 400)
                throw new FtpException(pass.Code, "Ошибка входа: " + pass.Text);
        }
        else if (user.Code >= 400)
        {
            throw new FtpException(user.Code, "Ошибка входа: " + user.Text);
        }

        await SendCommandAsync("TYPE I", null);

        if (_explicitTls || _implicitTls)
        {
            var pbsz = await SendCommandAsync("PBSZ 0", null);
            if (pbsz.Code < 400)
            {
                var prot = await SendCommandAsync("PROT P", null);
                _protPrivate = prot.Code < 400;
            }
        }
    }

    private async Task<Stream> UpgradeToTlsAsync(Stream inner)
    {
        var ssl = new SslStream(new PrefixStream(_pending, inner), false,
            (sender, cert, chain, errors) => SkipCertificateValidation || errors == SslPolicyErrors.None);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        });
        return ssl;
    }

    // ─────────────────────────────── Команды ───────────────────────────────

    public sealed class Reply
    {
        public int Code;
        public string Text = "";
    }

    private async Task<Reply> SendCommandAsync(string command, int[]? expected)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        await _control!.WriteAsync(bytes, 0, bytes.Length);
        await _control.FlushAsync();
        var reply = await ReadReplyAsync();
        if (expected != null && !expected.Contains(reply.Code))
            throw new FtpException(reply.Code, $"Команда «{command}» отклонена: {reply.Text}");
        return reply;
    }

    private async Task<Reply> ReadReplyAsync()
    {
        var first = await ReadLineAsync();
        if (first.Length < 3 || !int.TryParse(first[..3], out var code))
            throw new FtpException(0, "Некорректный ответ сервера: " + first);

        var text = first;
        if (first.Length > 3 && first[3] == '-')
        {
            // многострочный ответ
            while (true)
            {
                var line = await ReadLineAsync();
                text += "\n" + line;
                if (line.Length >= 4 && line.StartsWith(first[..3], StringComparison.Ordinal) && line[3] == ' ')
                    break;
            }
        }
        return new Reply { Code = code, Text = text.Length > 4 ? text[4..] : "" };
    }

    private async Task<string> ReadLineAsync()
    {
        while (true)
        {
            var idx = _pending.IndexOf((byte)'\n');
            if (idx >= 0)
            {
                var line = _pending.GetRange(0, idx);
                _pending.RemoveRange(0, idx + 1);
                if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                return Encoding.UTF8.GetString(line.ToArray());
            }
            var buf = new byte[1024];
            var n = await ReadControlAsync(buf, 0, buf.Length);
            if (n <= 0) throw new FtpException(0, "Соединение закрыто сервером");
            for (var i = 0; i < n; i++) _pending.Add(buf[i]);
        }
    }

    private async Task<int> ReadControlAsync(byte[] buffer, int offset, int count)
    {
        var tcs = _control!.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask();
        var done = await Task.WhenAny(tcs, Task.Delay(TimeoutMs));
        if (done != tcs) throw new FtpException(0, "Превышено время ожидания ответа сервера.");
        return await tcs;
    }

    private static void Expect(Reply reply, int code, string what)
    {
        if (reply.Code != code)
            throw new FtpException(reply.Code, $"Ожидался код {code} ({what}), получен: {reply.Text}");
    }

    // ─────────────────────────────── Канал данных ───────────────────────────────

    private async Task<TcpClient> OpenDataConnectionAsync()
    {
        if (Passive)
        {
            var epsv = await SendCommandAsync("EPSV", null);
            if (epsv.Code == 229)
            {
                var port = ParseExtendedPassive(epsv.Text);
                var c = new TcpClient();
                await c.ConnectAsync(_host, port);
                return c;
            }

            var pasv = await SendCommandAsync("PASV", null);
            if (pasv.Code != 227)
                throw new FtpException(pasv.Code, "Сервер не дал пассивный канал: " + pasv.Text);
            var (ip, p) = ParsePassive(pasv.Text);
            var client = new TcpClient();
            await client.ConnectAsync(ip, p);
            return client;
        }

        // Активный режим: мы слушаем, сервер подключается к нам.
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var localIp = (_tcp!.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        var portBytes = new[]
        {
            (byte)(localIp.GetAddressBytes()[0]), (byte)(localIp.GetAddressBytes()[1]),
            (byte)(localIp.GetAddressBytes()[2]), (byte)(localIp.GetAddressBytes()[3]),
            (byte)(localPort >> 8), (byte)(localPort & 0xFF)
        };
        var portArg = string.Join(",", portBytes);
        var portReply = await SendCommandAsync("PORT " + portArg, new[] { 200, 500 });
        if (portReply.Code >= 400)
            throw new FtpException(portReply.Code, "Сервер отклонил PORT: " + portReply.Text);

        var acceptTask = listener.AcceptTcpClientAsync();
        var finished = await Task.WhenAny(acceptTask, Task.Delay(TimeoutMs));
        listener.Stop();
        if (finished != acceptTask) throw new FtpException(0, "Сервер не подключился к каналу данных.");
        return await acceptTask;
    }

    /// <summary>Разбор ответа PASV: "227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)".</summary>
    public static (IPAddress Ip, int Port) ParsePassive(string text)
    {
        var open = text.IndexOf('(');
        var close = text.IndexOf(')', open + 1);
        if (open < 0 || close < 0) throw new FtpException(0, "Не удалось разобрать PASV-ответ: " + text);
        var parts = text[(open + 1)..close].Split(',');
        if (parts.Length < 6) throw new FtpException(0, "Неверный PASV-ответ: " + text);
        var ip = new IPAddress(new[] { byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()),
                                      byte.Parse(parts[2].Trim()), byte.Parse(parts[3].Trim()) });
        var port = int.Parse(parts[4].Trim()) * 256 + int.Parse(parts[5].Trim());
        return (ip, port);
    }

    /// <summary>Разбор ответа EPSV: "229 Entering Extended Passive Mode (|||port|)".</summary>
    public static int ParseExtendedPassive(string text)
    {
        var open = text.IndexOf('(');
        var close = text.IndexOf(')', open + 1);
        if (open < 0 || close < 0) throw new FtpException(0, "Не удалось разобрать EPSV-ответ: " + text);
        var inner = text[(open + 1)..close];
        var parts = inner.Split('|');
        var portPart = parts.Length >= 4 ? parts[3] : inner.Trim('|');
        if (!int.TryParse(portPart, out var port) || port <= 0)
            throw new FtpException(0, "Неверный EPSV-ответ: " + text);
        return port;
    }

    // ─────────────────────────────── Операции ───────────────────────────────

    public async Task UploadFileAsync(string localPath, string remotePath, long limitBytesPerSec,
        IProgress<long>? progress, CancellationToken ct)
    {
        var fi = new FileInfo(localPath);
        var data = await OpenDataConnectionAsync();
        try
        {
            var reply = await SendCommandAsync("STOR " + remotePath, null);
            if (reply.Code != 150 && reply.Code != 125)
                throw new FtpException(reply.Code, "Сервер не принял файл: " + reply.Text);

            Stream stream = data.GetStream();
            if (_protPrivate)
            {
                var ssl = new SslStream(stream, false,
                    (s, c, ch, e) => SkipCertificateValidation || e == SslPolicyErrors.None);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                });
                stream = ssl;
            }

            var buffer = new byte[256 * 1024];
            long sent = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             256 * 1024, useAsync: true))
            {
                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    sent += read;
                    if (limitBytesPerSec > 0)
                    {
                        var expected = (double)sent / limitBytesPerSec;
                        var delay = (expected - sw.Elapsed.TotalSeconds) * 1000.0;
                        if (delay > 1) await Task.Delay((int)Math.Min(delay, 2000), ct);
                    }
                    progress?.Report(sent);
                }
            }
            await stream.FlushAsync(ct);
            data.Close();

            var done = await ReadReplyAsync();
            if (done.Code >= 400)
                throw new FtpException(done.Code, "Ошибка загрузки файла: " + done.Text);
        }
        finally
        {
            data.Dispose();
        }
    }

    public async Task<long> GetSizeAsync(string remotePath)
    {
        var reply = await SendCommandAsync("SIZE " + remotePath, null);
        if (reply.Code != 213) return -1;
        var parts = reply.Text.Trim().Split(' ');
        return parts.Length > 0 && long.TryParse(parts[^1], out var size) ? size : -1;
    }

    /// <summary>Создаёт каталог (и все родительские).</summary>
    public async Task EnsureDirectoryAsync(string remotePath)
    {
        var current = "";
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var reply = await SendCommandAsync("MKD " + current, null);
            if (reply.Code >= 400 &&
                !reply.Text.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
                !reply.Text.Contains("550", StringComparison.Ordinal))
                throw new FtpException(reply.Code, "Не удалось создать каталог " + current + ": " + reply.Text);
        }
    }

    public async Task<List<FtpEntry>> ListAsync(string remotePath)
    {
        var data = await OpenDataConnectionAsync();
        string listing;
        try
        {
            var reply = await SendCommandAsync("MLSD " + remotePath, null);
            if (reply.Code >= 400)
            {
                var listReply = await SendCommandAsync("LIST " + remotePath, null);
                if (listReply.Code >= 400) throw new FtpException(listReply.Code, "Не удалось получить список: " + listReply.Text);
                reply = listReply;
            }

            Stream stream = data.GetStream();
            if (_protPrivate)
            {
                var ssl = new SslStream(stream, false,
                    (s, c, ch, e) => SkipCertificateValidation || e == SslPolicyErrors.None);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                });
                stream = ssl;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            listing = Encoding.UTF8.GetString(ms.ToArray());
            data.Close();
            var done = await ReadReplyAsync();
            if (done.Code >= 400) throw new FtpException(done.Code, "Ошибка чтения каталога: " + done.Text);
        }
        finally
        {
            data.Dispose();
        }

        return ParseListing(listing);
    }

    /// <summary>Разбор листинга: поддерживаются форматы MLSD (type=...;size=...) и LIST (unix).</summary>
    public static List<FtpEntry> ParseListing(string text)
    {
        var entries = new List<FtpEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.Contains(";") && line.Contains("type="))
            {
                var entry = new FtpEntry();
                var facts = line.Split(';');
                foreach (var fact in facts)
                {
                    var kv = fact.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim();
                    var value = kv[1].Trim();
                    if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                        entry.IsDirectory = value.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
                                           value.Equals("cdir", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("size", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var size))
                        entry.Size = size;
                }
                var nameFact = facts.LastOrDefault()?.Trim() ?? "";
                if (!nameFact.Contains('=')) entry.Name = nameFact;
                if (!string.IsNullOrEmpty(entry.Name)) entries.Add(entry);
            }
            else
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;
                var entry = new FtpEntry
                {
                    Name = parts[^1],
                    IsDirectory = parts[0].StartsWith('d')
                };
                if (long.TryParse(parts[4], out var size)) entry.Size = size;
                entries.Add(entry);
            }
        }
        return entries;
    }

    public async Task DeleteFileAsync(string remotePath)
    {
        var reply = await SendCommandAsync("DELE " + remotePath, null);
        if (reply.Code >= 400)
            throw new FtpException(reply.Code, "Не удалось удалить файл " + remotePath + ": " + reply.Text);
    }

    /// <summary>Удаляет каталог вместе со всем содержимым.</summary>
    public async Task DeleteDirectoryAsync(string remotePath)
    {
        var entries = await ListAsync(remotePath);
        foreach (var e in entries)
        {
            if (e.Name == "." || e.Name == "..") continue;
            var child = remotePath.TrimEnd('/') + "/" + e.Name;
            if (e.IsDirectory) await DeleteDirectoryAsync(child);
            else await DeleteFileAsync(child);
        }
        var reply = await SendCommandAsync("RMD " + remotePath, null);
        if (reply.Code >= 400)
            throw new FtpException(reply.Code, "Не удалось удалить каталог " + remotePath + ": " + reply.Text);
    }

    public async Task<bool> TestAsync()
    {
        await ConnectAsync();
        var reply = await SendCommandAsync("PWD", null);
        return reply.Code == 257;
    }

    public void Dispose()
    {
        try
        {
            if (_control != null)
            {
                var bytes = Encoding.ASCII.GetBytes("QUIT\r\n");
                _control.Write(bytes, 0, bytes.Length);
                _control.Flush();
            }
        }
        catch { /* не критично */ }

        _control?.Dispose();
        _tcp?.Dispose();
    }

    /// <summary>Поток, который сначала отдаёт накопленные байты, затем идёт во внутренний поток.</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _pos;
        private readonly Stream _inner;

        public PrefixStream(List<byte> prefix, Stream inner)
        {
            _prefix = prefix.ToArray();
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _pos);
                Array.Copy(_prefix, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_pos < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _pos);
                Array.Copy(_prefix, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        }
    }
}
