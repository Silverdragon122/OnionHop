using System;

namespace OnionHop;

internal sealed class UpdateInfo
{
    public Version Version { get; init; } = new Version(0, 0, 0);
    public string? DownloadUrl { get; init; }
    public string? HtmlUrl { get; init; }
    public string? FileName { get; init; }
}
