using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHop;

internal sealed class AdminHelperClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<bool> TryConnectWithoutStartAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (_pipe?.IsConnected == true)
        {
            return true;
        }

        ResetPipe();
        return await TryConnectAsync().ConfigureAwait(false);
    }

    public async Task<bool> EnsureConnectedAsync()
    {
        if (_disposed)
        {
            return false;
        }

        if (_pipe?.IsConnected == true)
        {
            return true;
        }

        ResetPipe();
        if (await TryConnectAsync().ConfigureAwait(false))
        {
            return true;
        }

        if (!StartHelperProcess())
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await TryConnectAsync().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<bool> StartVpnAsync(VpnLaunchConfig config)
    {
        var response = await SendAsync("StartVpn", config).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> StopVpnAsync()
    {
        var response = await SendAsync("StopVpn", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> StopVpnIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("StopVpn", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> EnableKillSwitchAsync()
    {
        var response = await SendAsync("EnableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchAsync()
    {
        var response = await SendAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchIfConnectedAsync()
    {
        var response = await SendIfConnectedAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<bool> DisableKillSwitchIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("DisableKillSwitch", null).ConfigureAwait(false);
        return response?.Success == true;
    }

    public async Task<AdminHelperStatus?> GetStatusAsync()
    {
        // Don't start helper just to check status - use existing connection only
        var response = await SendIfConnectedAsync("GetStatus", null).ConfigureAwait(false);
        if (response?.Success != true)
        {
            return null;
        }

        if (response.Payload is JsonElement element)
        {
            return JsonSerializer.Deserialize<AdminHelperStatus>(element.GetRawText(), AdminHelperProtocol.JsonOptions);
        }

        return null;
    }

    public async Task<bool> ShutdownAsync()
    {
        var response = await SendAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public async Task<bool> ShutdownIfConnectedAsync()
    {
        var response = await SendIfConnectedAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public async Task<bool> ShutdownIfAvailableAsync()
    {
        if (!await TryConnectWithoutStartAsync().ConfigureAwait(false))
        {
            return false;
        }

        var response = await SendIfConnectedAsync("Shutdown", null).ConfigureAwait(false);
        ResetPipe();
        return response?.Success == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetPipe();
        _lock.Dispose();
    }

    private async Task<HelperResponse?> SendAsync(string command, object? payload)
    {
        return await SendCoreAsync(command, payload, ensureConnected: true).ConfigureAwait(false);
    }

    private async Task<HelperResponse?> SendIfConnectedAsync(string command, object? payload)
    {
        return await SendCoreAsync(command, payload, ensureConnected: false).ConfigureAwait(false);
    }

    private async Task<HelperResponse?> SendCoreAsync(string command, object? payload, bool ensureConnected)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ensureConnected)
            {
                if (!await EnsureConnectedAsync().ConfigureAwait(false))
                {
                    return null;
                }
            }
            else if (_pipe?.IsConnected != true)
            {
                return null;
            }

            if (_writer == null || _reader == null)
            {
                return null;
            }

            var request = new HelperRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Command = command,
                Payload = payload
            };

            var json = JsonSerializer.Serialize(request, AdminHelperProtocol.JsonOptions);
            await _writer.WriteLineAsync(json).ConfigureAwait(false);

            var responseLine = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                ResetPipe();
                return null;
            }

            var response = JsonSerializer.Deserialize<HelperResponse>(responseLine, AdminHelperProtocol.JsonOptions);
            if (response?.RequestId != request.RequestId)
            {
                return response;
            }

            return response;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"AdminHelperClient.SendCoreAsync failed: {ex.Message}");
            ResetPipe();
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryConnectAsync()
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", AdminHelperProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            _pipe.ReadMode = PipeTransmissionMode.Message;
            _reader = new StreamReader(_pipe, Encoding.UTF8);
            _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
            return true;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"AdminHelperClient.TryConnectAsync failed: {ex.Message}");
            ResetPipe();
            return false;
        }
    }

    private static bool StartHelperProcess()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--helper"
            };
            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"AdminHelperClient.StartHelperProcess failed: {ex.Message}");
            return false;
        }
    }

    private void ResetPipe()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose writer: {ex.Message}");
        }

        try
        {
            _reader?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose reader: {ex.Message}");
        }

        try
        {
            _pipe?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"ResetPipe: Failed to dispose pipe: {ex.Message}");
        }

        _writer = null;
        _reader = null;
        _pipe = null;
    }
}
