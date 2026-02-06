using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV2.Core.Tor;

internal sealed class TorBridgeManager
{
    private static readonly Regex BridgeFrontsRegex = new(@"\bfronts=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BridgeFrontRegex = new(@"\bfront=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BridgeSniRegex = new(@"\bsni=(?<value>[^\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string WebTunnelBridgeType = "webtunnel";
    private const string WebTunnelClientFileName = "webtunnel-client.exe";

    private readonly string _baseDir;

    public TorBridgeManager(string baseDir)
    {
        _baseDir = baseDir;
    }

    public string? BridgeValidationMessage { get; private set; }

    public static bool IsPlaceholderBridgeLine(string line)
    {
        return line.Contains("2001:db8", StringComparison.OrdinalIgnoreCase)
               || line.Contains("192.0.2.", StringComparison.OrdinalIgnoreCase)
               || line.Contains("example.com", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetBridgeTypeKeys(PluggableTransportConfig? config)
    {
        var bridgeKeys = config?.Bridges?.Keys?
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        if (bridgeKeys.Count == 0)
        {
            bridgeKeys.AddRange(["obfs4", "snowflake", "meek-azure"]);
        }

        if (!bridgeKeys.Any(key => string.Equals(key, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Add(WebTunnelBridgeType);
        }

        return bridgeKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<string>> GetBridgeLinesAsync(
        OnionHopConnectOptions options,
        PluggableTransportConfig? config,
        Action<string> log,
        CancellationToken token)
    {
        IReadOnlyList<string> selected = Array.Empty<string>();
        var usingCustom = false;

        var custom = ExtractBridgeLines(options.CustomBridges);
        if (custom.Count > 0)
        {
            selected = custom;
            usingCustom = true;
        }
        else if (config?.Bridges != null &&
                 config.Bridges.TryGetValue(options.SelectedBridgeType, out var bridges) &&
                 bridges.Count > 0)
        {
            // When using bundled BridgeDB entries, shuffle the order so we don't get stuck retrying
            // the same dead/blocked bridge on every connect attempt.
            // (Users can still paste Custom Bridges to control ordering.)
            if (bridges.Count > 1)
            {
                var shuffled = new List<string>(bridges);
                ShuffleInPlace(shuffled);
                selected = shuffled;
                log($"Shuffled bundled {options.SelectedBridgeType} bridges.");
            }
            else
            {
                selected = bridges;
            }
        }

        if (selected.Count > 0 &&
            string.Equals(options.SelectedBridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
        {
            var filtered = selected.Where(line => !IsPlaceholderBridgeLine(line)).ToList();
            if (filtered.Count == 0)
            {
                if (usingCustom)
                {
                    log("Webtunnel bridge lines look like examples (2001:db8/192.0.2/etc). Attempting anyway.");
                }
                else
                {
                    log("No usable webtunnel bridges. Get bridges from bridges.torproject.org and paste in Custom Bridges.");
                    selected = Array.Empty<string>();
                }
            }
            else
            {
                if (usingCustom && filtered.Count != selected.Count)
                {
                    log("Removed example WebTunnel bridge lines (use real BridgeDB entries).");
                }

                selected = filtered;
            }
        }
        else if (selected.Count > 0)
        {
            // Users sometimes paste bridge lines missing the leading transport (e.g. "Bridge 1.2.3.4:443 ...").
            // Tor expects the first token to be the transport for PT bridges (e.g. "snowflake ...", "obfs4 ...").
            selected = NormalizeTransportPrefix(selected, options.SelectedBridgeType, log);
        }

        // If custom bridges were provided but none are usable, fall back to bundled BridgeDB entries.
        if (usingCustom && selected.Count == 0 && config?.Bridges != null &&
            config.Bridges.TryGetValue(options.SelectedBridgeType, out var fallback) &&
            fallback.Count > 0)
        {
            usingCustom = false;
            selected = fallback;
        }

        var customSni = ExtractSniHosts(options.CustomSniHosts);
        if (customSni.Count > 0 && selected.Count > 0)
        {
            selected = ApplyCustomSniHosts(selected, customSni);
        }

        return Task.FromResult(selected);
    }

    private static void ShuffleInPlace(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public IReadOnlyList<string> GetClientTransportPlugins(
        OnionHopConnectOptions options,
        IReadOnlyList<string> bridgeLines,
        string torDir,
        PluggableTransportConfig? config,
        Action<string> log)
    {
        var ptPath = Path.Combine(torDir, "pluggable_transports");
        var ptRelativePath = "pluggable_transports";
        var ptRelativePathWithSlash = ptRelativePath + Path.DirectorySeparatorChar;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport))
            {
                needed.Add(transport);
            }
        }

        if (needed.Count == 0)
        {
            BridgeValidationMessage = "Bridge lines are missing the transport type (expected e.g. 'snowflake ...', 'obfs4 ...').";
            return Array.Empty<string>();
        }

        string? webTunnelPlugin = null;
        if (needed.Contains(WebTunnelBridgeType))
        {
            if (!TryEnsureWebTunnelClient(ptPath, log, out webTunnelPlugin))
            {
                return Array.Empty<string>();
            }
        }

        if (config?.PluggableTransports != null && config.PluggableTransports.Count > 0)
        {
            var transportMap = BuildTransportPluginMap(config.PluggableTransports, ptRelativePathWithSlash);
            var pluginLines = new List<string>();
            foreach (var transport in needed)
            {
                if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
                {
                    pluginLines.Add(webTunnelPlugin!);
                    continue;
                }

                if (transportMap.TryGetValue(transport, out var plugin))
                {
                    pluginLines.Add(ReplaceTransportSegment(plugin, transport));
                    continue;
                }

                pluginLines.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, "lyrebird.exe")}");
            }

            return ApplySnowflakeOptions(options, pluginLines, ptRelativePath, log)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return ApplySnowflakeOptions(options, BuildFallbackTransportPlugins(needed, ptRelativePath, webTunnelPlugin), ptRelativePath, log)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ApplySnowflakeOptions(
        OnionHopConnectOptions options,
        IReadOnlyList<string> pluginLines,
        string ptRelativePath,
        Action<string> log)
    {
        if (pluginLines.Count == 0)
        {
            return pluginLines;
        }

        var updated = new List<string>(pluginLines.Count);
        foreach (var line in pluginLines)
        {
            if (!IsSnowflakePluginLine(line))
            {
                updated.Add(line);
                continue;
            }

            var pluginLine = EnsureSnowflakeClientPlugin(line, ptRelativePath);
            pluginLine = ApplySnowflakeAmpCache(options, pluginLine, log);
            updated.Add(pluginLine);
        }

        return updated;
    }

    private static bool IsSnowflakePluginLine(string pluginLine)
    {
        if (string.IsNullOrWhiteSpace(pluginLine))
        {
            return false;
        }

        var trimmed = pluginLine.TrimStart();
        const string prefix = "ClientTransportPlugin ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = trimmed.Substring(prefix.Length).TrimStart();
        return after.StartsWith("snowflake", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureSnowflakeClientPlugin(string pluginLine, string ptRelativePath)
    {
        if (pluginLine.Contains("snowflake-client.exe", StringComparison.OrdinalIgnoreCase))
        {
            return pluginLine;
        }

        // Some bundled pt_config.json files incorrectly map snowflake to lyrebird.exe. Override safely.
        return $"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, "snowflake-client.exe")}";
    }

    private static string ApplySnowflakeAmpCache(OnionHopConnectOptions options, string pluginLine, Action<string> log)
    {
        if (!options.UseSnowflakeAmp)
        {
            return pluginLine;
        }

        if (pluginLine.Contains("-ampcache", StringComparison.OrdinalIgnoreCase))
        {
            return pluginLine;
        }

        var cache = options.SnowflakeAmpCache;
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = "https://cdn.ampproject.org/";
        }
        cache = cache.Trim();

        if (!Uri.TryCreate(cache, UriKind.Absolute, out var uri))
        {
            log($"Invalid Snowflake AMP cache URL: {cache}");
            return pluginLine;
        }

        log($"Snowflake AMP cache enabled: {uri}");
        return pluginLine + $" -ampcache {uri}";
    }

    public static string NormalizeClientTransportPlugin(string pluginLine)
    {
        return pluginLine.Trim();
    }

    private static IReadOnlyList<string> BuildFallbackTransportPlugins(IReadOnlyCollection<string> transports, string ptRelativePath, string? webTunnelPlugin)
    {
        var plugins = new List<string>();
        foreach (var transport in transports)
        {
            if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(webTunnelPlugin))
                {
                    plugins.Add(webTunnelPlugin);
                }
                continue;
            }

            if (string.Equals(transport, "snowflake", StringComparison.OrdinalIgnoreCase))
            {
                plugins.Add($"ClientTransportPlugin snowflake exec {Path.Combine(ptRelativePath, "snowflake-client.exe")}");
                continue;
            }

            plugins.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptRelativePath, "lyrebird.exe")}");
        }

        return plugins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractBridgeTransport(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return null;
        }

        var trimmed = bridgeLine.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        return trimmed.Substring(0, firstSpace);
    }

    private static IReadOnlyList<string> NormalizeTransportPrefix(IReadOnlyList<string> lines, string selectedBridgeType, Action<string> log)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(selectedBridgeType))
        {
            return lines;
        }

        var normalizedType = selectedBridgeType.Trim();
        var updated = false;
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstToken = ExtractBridgeTransport(trimmed);
            if (string.IsNullOrWhiteSpace(firstToken))
            {
                // No space in the line. Only prefix if it looks like an endpoint; otherwise ignore it as invalid input.
                if (LooksLikeEndpoint(trimmed))
                {
                    result.Add($"{normalizedType} {trimmed}");
                    updated = true;
                }
                else
                {
                    log($"Ignoring invalid bridge line: {trimmed}");
                }
                continue;
            }

            // If the first token looks like an endpoint (IP:port/host:port), the line is missing the transport.
            if (LooksLikeEndpoint(firstToken))
            {
                result.Add($"{normalizedType} {trimmed}");
                updated = true;
                continue;
            }

            result.Add(trimmed);
        }

        if (updated)
        {
            log($"Normalized bridge lines by prefixing missing transport: {normalizedType}");
        }

        return result;
    }

    private static bool LooksLikeEndpoint(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Typical bridge endpoint forms:
        // - 1.2.3.4:443
        // - example.com:443
        // - [2001:db8::1]:443 (rare in bridges, but possible)
        if (!token.Contains(':'))
        {
            return false;
        }

        // Require at least one digit to avoid preferring random words with a colon.
        return token.Any(char.IsDigit);
    }

    private static Dictionary<string, string> BuildTransportPluginMap(Dictionary<string, string> pluginLines, string ptPathWithSlash)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pluginLines)
        {
            var normalized = entry.Value.Replace("${pt_path}", ptPathWithSlash, StringComparison.OrdinalIgnoreCase);
            var line = normalized.Trim();
            if (!line.StartsWith("ClientTransportPlugin ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            var transports = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var transport in transports)
            {
                map[transport] = line;
            }
        }

        return map;
    }

    private static string ReplaceTransportSegment(string pluginLine, string transport)
    {
        // Replace the transport list after ClientTransportPlugin with the single one Tor wants.
        var prefix = "ClientTransportPlugin ";
        var index = pluginLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return pluginLine;
        }

        var afterPrefix = pluginLine.Substring(index + prefix.Length);
        var space = afterPrefix.IndexOf(' ');
        if (space < 0)
        {
            return pluginLine;
        }

        return prefix + transport + afterPrefix.Substring(space);
    }

    private bool TryEnsureWebTunnelClient(string ptPath, Action<string> log, out string? pluginLine)
    {
        pluginLine = null;
        BridgeValidationMessage = null;

        var clientPath = Path.Combine(ptPath, WebTunnelClientFileName);
        if (!File.Exists(clientPath))
        {
            var found = FindWebTunnelClientInTorBrowser();
            if (!string.IsNullOrWhiteSpace(found))
            {
                try
                {
                    File.Copy(found, clientPath, true);
                    log($"Copied {WebTunnelClientFileName} from Tor Browser.");
                }
                catch (Exception ex)
                {
                    BridgeValidationMessage = $"Failed to copy {WebTunnelClientFileName}: {ex.Message}";
                    return false;
                }
            }
        }

        if (!File.Exists(clientPath))
        {
            BridgeValidationMessage = $"Webtunnel client is missing ({WebTunnelClientFileName}). Install Tor Browser and copy it into tor\\pluggable_transports.";
            return false;
        }

        pluginLine = $"ClientTransportPlugin webtunnel exec {Path.Combine("pluggable_transports", WebTunnelClientFileName)}";
        return true;
    }

    private static string? FindWebTunnelClientInTorBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<string> ExtractBridgeLines(string? text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("Bridge ".Length).Trim();
            }

            results.Add(line);
        }

        return results;
    }

    private static List<string> ExtractSniHosts(string? text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var raw in line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var host = raw.Trim();
                if (IsValidSniHost(ref host))
                {
                    results.Add(host);
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsValidSniHost(ref string host)
    {
        host = host.Trim();
        if (host.Length == 0)
        {
            return false;
        }

        // Allow users to paste URLs.
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
        }

        host = host.Trim().TrimEnd('.');

        // Strip a single :port suffix if present (and not an IPv6 literal).
        var firstColon = host.IndexOf(':');
        var lastColon = host.LastIndexOf(':');
        if (firstColon > 0 && firstColon == lastColon)
        {
            host = host.Substring(0, firstColon);
        }

        return host.Length > 0;
    }

    private static IReadOnlyList<string> ApplyCustomSniHosts(IReadOnlyList<string> bridgeLines, IReadOnlyList<string> sniHosts)
    {
        if (bridgeLines.Count == 0 || sniHosts.Count == 0)
        {
            return bridgeLines;
        }

        var frontsValue = string.Join(",", sniHosts);
        var updated = new List<string>(bridgeLines.Count);
        foreach (var line in bridgeLines)
        {
            var chosen = sniHosts[Random.Shared.Next(sniHosts.Count)];
            var modified = BridgeFrontsRegex.Replace(line, $"fronts={frontsValue}");
            modified = BridgeFrontRegex.Replace(modified, $"front={chosen}");
            modified = BridgeSniRegex.Replace(modified, $"sni={chosen}");
            updated.Add(modified);
        }

        return updated;
    }
}
