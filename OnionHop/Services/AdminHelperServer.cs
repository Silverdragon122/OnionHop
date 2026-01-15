using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHop;

internal sealed class AdminHelperServer
{
    private const string HelperMutexName = "OnionHop.AdminHelper.SingleInstance";
    private readonly VpnService _vpnService;
    private bool _killSwitchEnabled;
    private bool _isStopping;
    private bool _shutdownRequested;

    public AdminHelperServer()
    {
        _vpnService = new VpnService(_ => { });
        _vpnService.Exited += OnVpnExited;
    }

    public static bool IsHelperMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--helper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void Run()
    {
        // Use a mutex to ensure only one helper instance runs at a time
        using var mutex = new Mutex(true, HelperMutexName, out var createdNew);
        if (!createdNew)
        {
            StartupLogger.Write("Another admin helper instance is already running. Exiting.");
            return;
        }

        try
        {
            new AdminHelperServer().RunAsync().GetAwaiter().GetResult();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private async Task RunAsync()
    {
        using var pipe = new NamedPipeServerStream(
            AdminHelperProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous);

        await pipe.WaitForConnectionAsync().ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };

        while (pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            var request = JsonSerializer.Deserialize<HelperRequest>(line, AdminHelperProtocol.JsonOptions);
            if (request == null)
            {
                continue;
            }

            var response = await HandleRequestAsync(request).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(response, AdminHelperProtocol.JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            if (_shutdownRequested)
            {
                break;
            }
        }

        Cleanup();
    }

    private async Task<HelperResponse> HandleRequestAsync(HelperRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case "StartVpn":
                {
                    var config = DeserializePayload<VpnLaunchConfig>(request.Payload);
                    if (config == null)
                    {
                        return Fail(request, "Invalid VPN configuration.");
                    }

                    _isStopping = false;
                    await _vpnService.StartAsync(config, default).ConfigureAwait(false);
                    return Ok(request, null);
                }
                case "StopVpn":
                    _isStopping = true;
                    _vpnService.Stop();
                    return Ok(request, null);
                case "EnableKillSwitch":
                    _killSwitchEnabled = true;
                    EnableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                case "DisableKillSwitch":
                    _killSwitchEnabled = false;
                    DisableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                case "GetStatus":
                    return Ok(request, new AdminHelperStatus
                    {
                        VpnRunning = _vpnService.IsRunning,
                        VpnExitCode = _vpnService.ExitCode,
                        KillSwitchEnabled = _killSwitchEnabled
                    });
                case "Shutdown":
                    _shutdownRequested = true;
                    _isStopping = true;
                    _vpnService.Stop();
                    DisableKillSwitchEmergencyBlock();
                    return Ok(request, null);
                default:
                    return Fail(request, "Unknown command.");
            }
        }
        catch (Exception ex)
        {
            return Fail(request, ex.Message);
        }
    }

    private void OnVpnExited(object? sender, EventArgs e)
    {
        if (_killSwitchEnabled && !_isStopping)
        {
            EnableKillSwitchEmergencyBlock();
        }
    }

    private static T? DeserializePayload<T>(object? payload)
    {
        if (payload is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), AdminHelperProtocol.JsonOptions);
        }

        return payload is T typed ? typed : default;
    }

    private static HelperResponse Ok(HelperRequest request, object? payload)
    {
        return new HelperResponse
        {
            RequestId = request.RequestId,
            Success = true,
            Payload = payload
        };
    }

    private static HelperResponse Fail(HelperRequest request, string message)
    {
        return new HelperResponse
        {
            RequestId = request.RequestId,
            Success = false,
            Error = message
        };
    }

    private void Cleanup()
    {
        _isStopping = true;
        _vpnService.Stop();
        DisableKillSwitchEmergencyBlock();
    }

    private static string GetKillSwitchRuleName() => "OnionHop KillSwitch Emergency Block";

    private static string GetKillSwitchCleanupTaskName() => "OnionHop KillSwitch Cleanup";

    private void EnableKillSwitchEmergencyBlock()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{GetKillSwitchRuleName()}\" dir=out action=block profile=any enable=yes");
            EnableKillSwitchFailsafe();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"EnableKillSwitchEmergencyBlock failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchEmergencyBlock()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            DisableKillSwitchFailsafe();
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"DisableKillSwitchEmergencyBlock failed: {ex.Message}");
        }
    }

    private void EnableKillSwitchFailsafe()
    {
        try
        {
            var action = $"cmd /c netsh advfirewall firewall delete rule name=\\\"{GetKillSwitchRuleName()}\\\"";
            RunSchTasks($"/Create /TN \"{GetKillSwitchCleanupTaskName()}\" /TR \"{action}\" /SC ONSTART /RL HIGHEST /RU SYSTEM /F");
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"EnableKillSwitchFailsafe failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchFailsafe()
    {
        try
        {
            RunSchTasks($"/Delete /TN \"{GetKillSwitchCleanupTaskName()}\" /F");
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"DisableKillSwitchFailsafe failed: {ex.Message}");
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }

    private static void RunSchTasks(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }
}
