using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHop;

internal sealed class TorService : IDisposable
{
    private const string ControlPortFileName = "control_port.txt";
    private const string ControlAuthCookieFileName = "control_auth_cookie";
    private readonly Action<string> _log;
    private Process? _process;
    private string? _dataDirectory;
    private int? _controlPort;
    private bool _disposed;

    public TorService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public Task StartAsync(TorLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TorService));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(config.TorPath))
        {
            throw new ArgumentException("Tor path is required.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.GeoIpPath) || string.IsNullOrWhiteSpace(config.GeoIp6Path))
        {
            throw new ArgumentException("GeoIP paths are required.", nameof(config));
        }

        _dataDirectory = string.IsNullOrWhiteSpace(config.DataDirectory)
            ? Path.Combine(Path.GetTempPath(), "OnionHop", "tor-data")
            : config.DataDirectory;
        Directory.CreateDirectory(_dataDirectory);

        _controlPort = null;
        TryDeleteFile(Path.Combine(_dataDirectory, ControlPortFileName));
        TryDeleteFile(Path.Combine(_dataDirectory, ControlAuthCookieFileName));

        var args = BuildArguments(config, _dataDirectory);
        var psi = new ProcessStartInfo(config.TorPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory
                ?? Path.GetDirectoryName(config.TorPath)
                ?? AppContext.BaseDirectory
        };

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.Exited += HandleExited;
        _process.OutputDataReceived += HandleOutput;
        _process.ErrorDataReceived += HandleOutput;

        if (!_process.Start())
        {
            throw new InvalidOperationException("Unable to launch tor.exe");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    public async Task<bool> SendControlSignalAsync(string command, CancellationToken token)
    {
        var port = await GetControlPortAsync(token);
        if (!port.HasValue)
        {
            _log("Tor control port not available.");
            return false;
        }

        var cookie = await GetControlCookieHexAsync(token);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            _log("Tor control cookie not available.");
            return false;
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port.Value, token);

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        await writer.WriteLineAsync($"AUTHENTICATE {cookie}");
        var authResponse = await ReadControlResponseAsync(reader);
        if (!authResponse.StartsWith("250", StringComparison.Ordinal))
        {
            _log($"Tor control auth failed: {authResponse}");
            return false;
        }

        await writer.WriteLineAsync(command);
        var response = await ReadControlResponseAsync(reader);
        if (!response.StartsWith("250", StringComparison.Ordinal))
        {
            _log($"Tor control command failed: {response}");
            return false;
        }

        await writer.WriteLineAsync("QUIT");
        return true;
    }

    public void Stop()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.CloseMainWindow();
                    _process.WaitForExit(1500);
                }
                catch
                {
                }

                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _log($"Failed to stop Tor: {ex.Message}");
        }
        finally
        {
            _process.OutputDataReceived -= HandleOutput;
            _process.ErrorDataReceived -= HandleOutput;
            _process.Exited -= HandleExited;
            _process.Dispose();
            _process = null;
            _controlPort = null;
            _dataDirectory = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task<int?> GetControlPortAsync(CancellationToken token)
    {
        if (_controlPort.HasValue)
        {
            return _controlPort.Value;
        }

        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return null;
        }

        var portFile = Path.Combine(_dataDirectory, ControlPortFileName);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (File.Exists(portFile))
            {
                var content = await File.ReadAllTextAsync(portFile, token);
                var parsed = ParsePortFromFile(content);
                if (parsed.HasValue)
                {
                    _controlPort = parsed.Value;
                    return _controlPort;
                }
            }

            await Task.Delay(200, token);
        }

        return null;
    }

    private async Task<string?> GetControlCookieHexAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return null;
        }

        var cookiePath = Path.Combine(_dataDirectory, ControlAuthCookieFileName);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (File.Exists(cookiePath))
            {
                var bytes = await File.ReadAllBytesAsync(cookiePath, token);
                if (bytes.Length > 0)
                {
                    return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                }
            }

            await Task.Delay(200, token);
        }

        return null;
    }

    private static async Task<string> ReadControlResponseAsync(StreamReader reader)
    {
        var line = await reader.ReadLineAsync() ?? string.Empty;
        if (!line.StartsWith("250-", StringComparison.Ordinal))
        {
            return line;
        }

        var current = line;
        while (current.StartsWith("250-", StringComparison.Ordinal))
        {
            var next = await reader.ReadLineAsync();
            if (next == null)
            {
                break;
            }

            current = next;
            if (current.StartsWith("250 ", StringComparison.Ordinal))
            {
                break;
            }
        }

        return current;
    }

    private static int? ParsePortFromFile(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var last = -1;
        var current = -1;
        foreach (var ch in content)
        {
            if (char.IsDigit(ch))
            {
                current = current < 0 ? ch - '0' : (current * 10) + (ch - '0');
            }
            else if (current >= 0)
            {
                last = current;
                current = -1;
            }
        }

        if (current >= 0)
        {
            last = current;
        }

        return last >= 0 ? last : null;
    }

    private static string BuildArguments(TorLaunchConfig config, string dataDirectory)
    {
        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"--SocksPort {config.SocksPort} ");
        argsBuilder.Append($"--DataDirectory \"{dataDirectory}\" ");
        argsBuilder.Append("--ClientOnly 1 ");
        argsBuilder.Append("--Log \"notice stdout\" ");
        argsBuilder.Append("--CookieAuthentication 1 ");
        argsBuilder.Append("--ControlPort auto ");
        argsBuilder.Append($"--ControlPortWriteToFile \"{Path.Combine(dataDirectory, ControlPortFileName)}\" ");
        argsBuilder.Append($"--GeoIPFile \"{config.GeoIpPath}\" ");
        argsBuilder.Append($"--GeoIPv6File \"{config.GeoIp6Path}\" ");

        if (config.BridgeLines != null && config.BridgeLines.Count > 0)
        {
            if (config.ClientTransportPlugins == null || config.ClientTransportPlugins.Count == 0)
            {
                throw new InvalidOperationException("Bridges enabled but no transport plugins were found.");
            }

            argsBuilder.Append("--UseBridges 1 ");
            foreach (var pluginLine in config.ClientTransportPlugins)
            {
                if (!string.IsNullOrWhiteSpace(pluginLine))
                {
                    argsBuilder.Append($"--ClientTransportPlugin \"{pluginLine}\" ");
                }
            }

            foreach (var bridgeLine in config.BridgeLines)
            {
                if (!string.IsNullOrWhiteSpace(bridgeLine))
                {
                    argsBuilder.Append($"--Bridge \"{bridgeLine}\" ");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ExitCountryCode))
        {
            argsBuilder.Append($"--ExitNodes {{{config.ExitCountryCode}}} ");
        }

        return argsBuilder.ToString();
    }

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        OutputReceived?.Invoke(sender, e);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class TorLaunchConfig
{
    public string TorPath { get; init; } = string.Empty;
    public int SocksPort { get; init; }
    public string? DataDirectory { get; init; }
    public string GeoIpPath { get; init; } = string.Empty;
    public string GeoIp6Path { get; init; } = string.Empty;
    public IReadOnlyList<string>? BridgeLines { get; init; }
    public IReadOnlyList<string>? ClientTransportPlugins { get; init; }
    public string? ExitCountryCode { get; init; }
    public string? WorkingDirectory { get; init; }
    public Action<Process>? ProcessStarted { get; init; }
}
