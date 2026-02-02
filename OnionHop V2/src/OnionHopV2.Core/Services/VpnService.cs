using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Services;

internal sealed class VpnService : IDisposable
{
    private readonly Action<string> _log;
    private Process? _process;
    private bool _disposed;

    public VpnService(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event DataReceivedEventHandler? OutputReceived;
    public event EventHandler? Exited;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    public async Task StartAsync(VpnLaunchConfig config, CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VpnService));
        }

        Stop();
        token.ThrowIfCancellationRequested();

        if (!File.Exists(config.SingBoxPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\sing-box.exe", config.SingBoxPath);
        }

        if (!File.Exists(config.WintunPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\wintun.dll", config.WintunPath);
        }

        var workDir = Path.GetDirectoryName(config.SingBoxPath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", "sing-box");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "sing-box.json");

        var configJson = BuildConfigJson(
            config.HybridRouting,
            config.SecureDns,
            config.SocksPort,
            config.TorAppProcessNames,
            config.BypassAppProcessNames,
            config.RouteAllWebTrafficThroughTor,
            config.BlockQuicForTorApps,
            config.DohServer,
            config.DohServerPort,
            config.DohPath);
        await File.WriteAllTextAsync(configPath, configJson, token);

        _log($"Starting sing-box with config: {configPath}");

        var psi = new ProcessStartInfo(config.SingBoxPath, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
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
            throw new InvalidOperationException("Unable to launch sing-box.exe");
        }

        config.ProcessStarted?.Invoke(_process);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(750, token);
        if (_process.HasExited)
        {
            throw new InvalidOperationException("sing-box exited unexpectedly during startup.");
        }
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
            _log($"Failed to stop sing-box: {ex.Message}");
        }
        finally
        {
            _process.OutputDataReceived -= HandleOutput;
            _process.ErrorDataReceived -= HandleOutput;
            _process.Exited -= HandleExited;
            _process.Dispose();
            _process = null;
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

    private void HandleExited(object? sender, EventArgs e)
    {
        Exited?.Invoke(sender, e);
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        OutputReceived?.Invoke(sender, e);
    }

    private static string BuildConfigJson(
        bool hybridRouting,
        bool secureDns,
        int socksPort,
        IReadOnlyList<string> torAppProcessNames,
        IReadOnlyList<string> bypassAppProcessNames,
        bool routeAllWebTrafficThroughTor,
        bool blockQuicForTorApps,
        string? dohServer,
        int dohServerPort,
        string? dohPath)
    {
        // Important: Tor's pluggable transports must bypass the tunnel ("tor" outbound),
        // otherwise they can end up routed back into Tor, causing a bootstrap loop and bridge failures.
        var torRelatedProcessNames = new[]
        {
            "tor.exe",
            "snowflake-client.exe",
            "lyrebird.exe",
            "obfs4proxy.exe",
            "conjure-client.exe",
            "webtunnel-client.exe"
        };

        var rules = new List<object>
        {
            new { action = "sniff" },
            new { process_name = torRelatedProcessNames, outbound = "direct" },
            new { ip_is_private = true, outbound = "direct" }
        };

        if (!hybridRouting)
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { network = "udp", outbound = "block" });
        }
        else
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });

            if (bypassAppProcessNames.Count > 0)
            {
                // Allow bypassing Tor even if "route all web traffic" is enabled.
                rules.Add(new { process_name = bypassAppProcessNames, outbound = "direct" });
            }

            if (blockQuicForTorApps && torAppProcessNames.Count > 0)
            {
                // Prevent QUIC/UDP bypass for apps intended to go over Tor.
                rules.Add(new { process_name = torAppProcessNames, network = "udp", port = 443, outbound = "block" });
                rules.Add(new { process_name = torAppProcessNames, network = "udp", outbound = "block" });
            }

            if (torAppProcessNames.Count > 0)
            {
                rules.Add(new { process_name = torAppProcessNames, outbound = "tor" });
            }

            if (routeAllWebTrafficThroughTor)
            {
                rules.Add(new { network = "tcp", port = new[] { 80, 443 }, outbound = "tor" });
            }
        }

        var resolvedDohServer = string.IsNullOrWhiteSpace(dohServer) ? "cloudflare-dns.com" : dohServer.Trim();
        var resolvedDohPath = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : dohPath.Trim();
        if (!resolvedDohPath.StartsWith("/", StringComparison.Ordinal))
        {
            resolvedDohPath = "/" + resolvedDohPath;
        }

        var resolvedDohPort = dohServerPort is > 0 and <= 65535 ? dohServerPort : 443;

        // sing-box requires a domain resolver when a server is specified by hostname (e.g. cloudflare-dns.com).
        // Provide a bootstrap resolver and set route.default_domain_resolver to avoid startup failures.
        var dnsServers = new List<object>();

        var dohIsIp = IPAddress.TryParse(resolvedDohServer, out _);
        if (secureDns && !dohIsIp)
        {
            dnsServers.Add(hybridRouting
                ? new Dictionary<string, object?>
                {
                    ["tag"] = "bootstrap",
                    ["type"] = "udp",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 53
                }
                : new Dictionary<string, object?>
                {
                    ["tag"] = "bootstrap",
                    ["type"] = "tcp",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 53,
                    ["detour"] = "tor"
                });
        }

        var remoteDnsServer = new Dictionary<string, object?>
        {
            ["tag"] = "remote"
        };

        if (secureDns)
        {
            remoteDnsServer["type"] = "https";
            remoteDnsServer["server"] = resolvedDohServer;
            remoteDnsServer["server_port"] = resolvedDohPort;
            remoteDnsServer["path"] = resolvedDohPath;

            if (!hybridRouting)
            {
                remoteDnsServer["detour"] = "tor";
            }

            if (!dohIsIp)
            {
                remoteDnsServer["domain_resolver"] = "bootstrap";
            }
        }
        else
        {
            if (hybridRouting)
            {
                remoteDnsServer["type"] = "udp";
                remoteDnsServer["server"] = "1.1.1.1";
                remoteDnsServer["server_port"] = 53;
            }
            else
            {
                remoteDnsServer["type"] = "tcp";
                remoteDnsServer["server"] = "1.1.1.1";
                remoteDnsServer["server_port"] = 53;
                remoteDnsServer["detour"] = "tor";
            }
        }

        dnsServers.Add(remoteDnsServer);

        var config = new
        {
            log = new
            {
                level = secureDns ? "debug" : "info",
                timestamp = true
            },
            dns = new
            {
                servers = dnsServers,
                final = "remote"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "OnionHop",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "socks",
                    tag = "tor",
                    server = "127.0.0.1",
                    server_port = socksPort,
                    version = "5"
                },
                new
                {
                    type = "direct",
                    tag = "direct"
                },
                new
                {
                    type = "block",
                    tag = "block"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                default_domain_resolver = "remote",
                rules = rules,
                final = hybridRouting ? "direct" : "tor"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }
}

internal sealed class VpnLaunchConfig
{
    public string SingBoxPath { get; init; } = string.Empty;
    public string WintunPath { get; init; } = string.Empty;
    public bool HybridRouting { get; init; }
    public bool SecureDns { get; init; }
    public int SocksPort { get; init; }
    public string DohServer { get; init; } = "cloudflare-dns.com";
    public int DohServerPort { get; init; } = 443;
    public string DohPath { get; init; } = "/dns-query";
    public bool RouteAllWebTrafficThroughTor { get; init; } = true;
    public bool BlockQuicForTorApps { get; init; } = true;
    public IReadOnlyList<string> TorAppProcessNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassAppProcessNames { get; init; } = Array.Empty<string>();
    [JsonIgnore]
    public Action<Process>? ProcessStarted { get; init; }
}
