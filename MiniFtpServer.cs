using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HvBackup;

/// <summary>
/// Минимальный FTP-сервер для самопроверки клиента: работает в папке на диске,
/// поддерживает USER/PASS/TYPE/PASV/EPSV/STOR/LIST/MLSD/MKD/DELE/RMD/SIZE/CWD/PWD.
/// </summary>
internal sealed class MiniFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string Root { get; }

    public MiniFtpServer(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        TcpListener? dataListener = null;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string currentDir = "/";
                await writer.WriteLineAsync("220 Mini FTP ready");

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    var cmd = line.Trim();
                    if (cmd.Length == 0) continue;

                    var space = cmd.IndexOf(' ');
                    var verb = (space < 0 ? cmd : cmd[..space]).ToUpperInvariant();
                    var arg = space < 0 ? "" : cmd[(space + 1)..];

                    switch (verb)
                    {
                        case "USER":
                            await writer.WriteLineAsync("331 Password required");
                            break;
                        case "PASS":
                            await writer.WriteLineAsync("230 Login successful");
                            break;
                        case "TYPE":
                            await writer.WriteLineAsync("200 Type set");
                            break;
                        case "SYST":
                            await writer.WriteLineAsync("215 UNIX Type: L8");
                            break;
                        case "PWD":
                            await writer.WriteLineAsync("257 \"" + currentDir + "\"");
                            break;
                        case "CWD":
                            currentDir = Normalize(arg);
                            await writer.WriteLineAsync("250 Directory changed");
                            break;
                        case "PASV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            var p = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{p >> 8},{p & 0xFF})");
                            break;
                        case "EPSV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            var ep = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{ep}|)");
                            break;
                        case "MKD":
                        {
                            var local = LocalPath(arg);
                            Directory.CreateDirectory(local);
                            await writer.WriteLineAsync("257 \"" + Normalize(arg) + "\" created");
                            break;
                        }
                        case "SIZE":
                        {
                            var fi = new FileInfo(LocalPath(arg));
                            await writer.WriteLineAsync(fi.Exists ? $"213 {fi.Length}" : "550 File not found");
                            break;
                        }
                        case "STOR":
                        {
                            var local = LocalPath(arg);
                            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                            await writer.WriteLineAsync("150 Opening data connection");
                            using (var data = await AcceptDataAsync(dataListener, ct))
                            {
                                await using var fs = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None);
                                await data.CopyToAsync(fs, ct);
                            }
                            await writer.WriteLineAsync("226 Transfer complete");
                            break;
                        }
                        case "LIST":
                        case "MLSD":
                        {
                            var local = LocalPath(arg);
                            await writer.WriteLineAsync("150 Here comes the directory listing");
                            var listing = BuildListing(local, verb == "MLSD");
                            using (var data = await AcceptDataAsync(dataListener, ct))
                            {
                                var bytes = Encoding.UTF8.GetBytes(listing);
                                await data.WriteAsync(bytes, ct);
                                await data.FlushAsync(ct);
                            }
                            await writer.WriteLineAsync("226 Directory send OK");
                            break;
                        }
                        case "DELE":
                        {
                            var local = LocalPath(arg);
                            if (File.Exists(local)) { File.Delete(local); await writer.WriteLineAsync("250 File deleted"); }
                            else await writer.WriteLineAsync("550 File not found");
                            break;
                        }
                        case "RMD":
                        {
                            var local = LocalPath(arg);
                            if (Directory.Exists(local)) { Directory.Delete(local, true); await writer.WriteLineAsync("250 Directory deleted"); }
                            else await writer.WriteLineAsync("550 Directory not found");
                            break;
                        }
                        case "QUIT":
                            await writer.WriteLineAsync("221 Goodbye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 Command not implemented");
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            dataListener?.Stop();
        }
    }

    private static async Task<NetworkStream> AcceptDataAsync(TcpListener? listener, CancellationToken ct)
    {
        if (listener == null) throw new InvalidOperationException("Нет пассивного канала данных");
        var client = await listener.AcceptTcpClientAsync(ct);
        return client.GetStream();
    }

    private static string BuildListing(string localPath, bool mlsd)
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(localPath)) return "";
        foreach (var dir in Directory.GetDirectories(localPath).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            if (mlsd) sb.AppendLine($"type=dir;size=0;modify=20260101000000; {name}");
            else sb.AppendLine($"drwxr-xr-x 2 owner group 4096 Jan  1 00:00 {name}");
        }
        foreach (var file in Directory.GetFiles(localPath).OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            if (mlsd) sb.AppendLine($"type=file;size={size};modify=20260101000000; {name}");
            else sb.AppendLine($"-rw-r--r-- 1 owner group {size} Jan  1 00:00 {name}");
        }
        return sb.ToString();
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        return path.StartsWith('/') ? path : "/" + path;
    }

    private string LocalPath(string remotePath)
    {
        var normalized = Normalize(remotePath).Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..");
        var combined = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        return combined;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
