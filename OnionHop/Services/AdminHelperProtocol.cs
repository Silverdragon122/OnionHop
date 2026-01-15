using System.Text.Json;

namespace OnionHop;

internal static class AdminHelperProtocol
{
    public const string PipeName = "OnionHop.AdminHelper";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class HelperRequest
{
    public string? RequestId { get; set; }
    public string? Command { get; set; }
    public object? Payload { get; set; }
}

internal sealed class HelperResponse
{
    public string? RequestId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public object? Payload { get; set; }
}

internal sealed class AdminHelperStatus
{
    public bool VpnRunning { get; set; }
    public int? VpnExitCode { get; set; }
    public bool KillSwitchEnabled { get; set; }
}
