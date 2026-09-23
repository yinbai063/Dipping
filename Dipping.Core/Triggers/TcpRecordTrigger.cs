using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dipping.Core.Interfaces;

namespace Dipping.Core.Triggers
{
    /// <summary>
    /// 听本机 TCP。对端发一行 <see cref="StartCommandLeft"/> 或 <see cref="StartCommandRight"/>（末尾换行）即开对应相机。
    /// 连接保持到对端断开。退出时主动 Shutdown/RST 已接受的套接字，避免上位机还显示“已连接”。
    /// 旧连接上的字节到不了新进程；关 Dipping 后须重新连接再发指令。
    /// </summary>
    public sealed class TcpRecordTrigger : IRecordTrigger
    {
        public const string StartCommandLeft = "REC_START_DIPPING_L";
        public const string StartCommandRight = "REC_START_DIPPING_R";

        private readonly IAppLogger _logger;
        private readonly int _port;
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private readonly ConcurrentBag<Task> _handlers = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private TcpListener? _listener;
        private int _clientCount;

        public string Name => "PC程序(TCP)";
        public event EventHandler<RecordTriggerEventArgs>? StartRequested;
        public event EventHandler<bool>? ClientConnectedChanged;

        public bool HasClient => Volatile.Read(ref _clientCount) > 0;

        public TcpRecordTrigger(IAppLogger logger, int port)
        {
            _logger = logger;
            _port = port > 0 ? port : 10100;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => ListenAsync(_cts.Token));
            _logger.Info($"自动录像：TCP 监听 0.0.0.0:{_port}，左相机 {StartCommandLeft}，右相机 {StartCommandRight}（连接保持，对方断开后继续监听）。" +
                         "关闭本程序后上位机须断开再连，旧连接上的指令不会到达新进程。");
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            AbortSockets();
            if (_loop != null)
            {
                try { await _loop.ConfigureAwait(false); } catch { }
            }

            var pending = _handlers.ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(pending), Task.Delay(1500)).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _listener = null;
            if (Interlocked.Exchange(ref _clientCount, 0) != 0)
                ClientConnectedChanged?.Invoke(this, false);
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        public void Dispose()
        {
            AbortSockets();
            try { StopAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        private void AbortSockets()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            DropAllClients();
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpListener? listener = null;
                try
                {
                    listener = new TcpListener(IPAddress.Any, _port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start();
                    _listener = listener;
                    while (!ct.IsCancellationRequested)
                    {
                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                        {
                            if (ct.IsCancellationRequested)
                                return;
                            break;
                        }

                        var handler = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
                        _handlers.Add(handler);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn($"TCP 触发监听失败：{ex.Message}");
                    _logger.Info($"TCP 将在 2 秒后重新监听 0.0.0.0:{_port}，等待上位PC重连");
                    try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                finally
                {
                    if (ReferenceEquals(_listener, listener))
                        _listener = null;
                    try { listener?.Stop(); } catch { }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var remote = "未知";
            try
            {
                remote = client.Client.RemoteEndPoint?.ToString() ?? remote;
            }
            catch { }

            _clients.TryAdd(client, 0);
            EnableKeepAlive(client);
            var becameFirst = Interlocked.Increment(ref _clientCount) == 1;
            if (becameFirst)
                ClientConnectedChanged?.Invoke(this, true);
            _logger.Info($"自动录像 TCP 已连接：{remote}");

            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    var pending = new List<byte>(256);
                    var chunk = new byte[1024];
                    Encoding? encoding = null;
                    var loggedEncoding = false;

                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        int n;
                        try
                        {
                            n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        if (n == 0)
                            break;

                        for (var i = 0; i < n; i++)
                            pending.Add(chunk[i]);

                        encoding ??= TcpLineCodec.TryDetect(pending);
                        if (encoding == null)
                            continue;

                        if (!loggedEncoding)
                        {
                            loggedEncoding = true;
                            _logger.Info($"自动录像 TCP 编码：{TcpLineCodec.NameOf(encoding)}（{remote}）");
                        }

                        while (TcpLineCodec.TryTakeLine(pending, encoding, out var line, out var newLine))
                        {
                            if (!TryParseStartCommand(line, out var position))
                            {
                                if (LooksLikeOldStartCommand(line))
                                    _logger.Warn($"已忽略旧指令 REC_START_DIPPING，请改用 {StartCommandLeft}（左）或 {StartCommandRight}（右）。");
                                continue;
                            }

                            StartRequested?.Invoke(this, new RecordTriggerEventArgs
                            {
                                Source = Name,
                                JobHint = line.Trim(),
                                CameraPosition = position
                            });
                            var ack = TcpLineCodec.EncodeLine(encoding, "OK", newLine);
                            await stream.WriteAsync(ack, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"TCP 触发处理失败：{ex.Message}");
            }
            finally
            {
                ForceClose(client);
                _clients.TryRemove(client, out _);
                var last = Interlocked.Decrement(ref _clientCount) <= 0;
                if (last)
                {
                    Volatile.Write(ref _clientCount, 0);
                    ClientConnectedChanged?.Invoke(this, false);
                }

                _logger.Info($"自动录像 TCP 连接已断开：{remote}");
            }
        }

        private void DropAllClients()
        {
            foreach (var client in _clients.Keys)
                ForceClose(client);
        }

        private static void ForceClose(TcpClient client)
        {
            try
            {
                var socket = client.Client;
                if (socket != null)
                {
                    try { socket.LingerState = new LingerOption(true, 0); } catch { }
                    try { socket.Shutdown(SocketShutdown.Both); } catch { }
                    try { socket.Close(); } catch { }
                }
            }
            catch
            {
            }

            try { client.Close(); } catch { }
        }

        private static void EnableKeepAlive(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var values = new byte[12];
                BitConverter.GetBytes(1).CopyTo(values, 0);
                BitConverter.GetBytes(2000).CopyTo(values, 4);
                BitConverter.GetBytes(1000).CopyTo(values, 8);
                client.Client.IOControl(IOControlCode.KeepAliveValues, values, null);
            }
            catch
            {
            }
        }

        private static bool TryParseStartCommand(string? line, out string position)
        {
            position = string.Empty;
            var text = (line ?? string.Empty).Trim();
            if (text.Equals(StartCommandLeft, StringComparison.OrdinalIgnoreCase))
            {
                position = "left";
                return true;
            }

            if (text.Equals(StartCommandRight, StringComparison.OrdinalIgnoreCase))
            {
                position = "right";
                return true;
            }

            return false;
        }

        private static bool LooksLikeOldStartCommand(string? line)
        {
            var text = (line ?? string.Empty).Trim();
            return text.Equals("REC_START_DIPPING", StringComparison.OrdinalIgnoreCase);
        }
    }
}
