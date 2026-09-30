using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwinkleTray.WinUI.Services;

internal sealed class UdpControlService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly UdpClient _socket;
    private readonly string _key;
    private readonly Func<JsonElement, Task<object?>> _handle;
    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    public UdpControlService(int port, bool remote, string key, Func<JsonElement, Task<object?>> handle)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("UDP key cannot be empty.", nameof(key));
        _key = key; _handle = handle;
        SocketException? error = null;
        foreach (int candidate in new[] { port, port + 13137, port + 1603 }.Where(x => x is > 0 and <= 65535))
        {
            try { _socket = new UdpClient(new IPEndPoint(remote ? IPAddress.Any : IPAddress.Loopback, candidate)); _ = ListenAsync(); return; }
            catch (SocketException exception) { error = exception; }
        }
        throw error ?? new SocketException();
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var received = await _socket.ReceiveAsync(_lifetime.Token);
                if (received.Buffer.Length > 16384) continue;
                using var document = JsonDocument.Parse(received.Buffer);
                var command = document.RootElement;
                if (command.ValueKind != JsonValueKind.Object) continue;
                if (!command.TryGetProperty("key", out var supplied) || supplied.ValueKind != JsonValueKind.String ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_key), Encoding.UTF8.GetBytes(supplied.GetString()!))) continue;
                object? response;
                try { response = await _handle(command.Clone()).WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token); }
                catch (Exception exception) { response = new { error = exception.Message }; }
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response);
                if (payload.Length < 65000) await _socket.SendAsync(payload, received.RemoteEndPoint, _lifetime.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (JsonException) { }
            catch (SocketException exception) { if (!_lifetime.IsCancellationRequested) Program.Log(exception); }
        }
    }
    public void Dispose() { _lifetime.Cancel(); _socket.Dispose(); _lifetime.Dispose(); }
}
