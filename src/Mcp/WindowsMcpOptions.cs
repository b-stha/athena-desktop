using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athena.Desktop.Mcp;

/// <summary>Local process settings. Network policy and credentials belong to Windows-MCP's TOML.</summary>
public sealed record WindowsMcpOptions
{
    public required string Executable { get; init; }
    public required string ConfigFile { get; init; }
    public required string Endpoint { get; init; }
    public int StartupTimeoutSeconds { get; init; } = 30;

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AthenaDesktop", "mcp.json");

    public static WindowsMcpOptions Load(string path)
    {
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(path))
            throw new InvalidOperationException("Windows-MCP settings are missing. Configure ATHENA_MCP_SETTINGS; see docs/WINDOWS_MCP.md.");
        WindowsMcpOptions settings;
        try
        {
            settings = JsonSerializer.Deserialize<WindowsMcpOptions>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            // Do not echo settings content or parser input: local configuration may contain secrets.
            throw new InvalidOperationException("Invalid Windows-MCP settings JSON. Use config/mcp.example.json.");
        }
        settings = settings with
        {
            Executable = Resolve(settings.Executable, path),
            ConfigFile = Resolve(settings.ConfigFile, path)
        };
        settings.Validate();
        return settings;
    }

    private static string Resolve(string? value, string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Executable and ConfigFile are required.");
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(expanded, Path.GetDirectoryName(settingsPath)!);
    }

    public Uri EndpointUri
    {
        get
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(endpoint.Host) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath.TrimEnd('/') != "/mcp")
                throw new InvalidOperationException("Endpoint must be an HTTP(S) MCP URL ending in /mcp, without credentials, query, or fragment.");
            return endpoint;
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) || !Path.IsPathFullyQualified(Executable) || !File.Exists(Executable) ||
            !Path.GetExtension(Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Executable must identify a preinstalled Windows-MCP .exe. Startup never downloads software.");
        if (string.IsNullOrWhiteSpace(ConfigFile) || !Path.IsPathFullyQualified(ConfigFile) || !File.Exists(ConfigFile))
            throw new InvalidOperationException("ConfigFile must identify an existing Windows-MCP TOML file.");
        if (StartupTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("StartupTimeoutSeconds must be between 1 and 300.");
        _ = EndpointUri;
    }

    public ProcessStartInfo CreateStartInfo()
    {
        Validate();
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(ConfigFile)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // ArgumentList preserves paths with spaces/metacharacters as one value; no shell is used.
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(ConfigFile);
        start.ArgumentList.Add("--transport");
        start.ArgumentList.Add("streamable-http");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["ANONYMIZED_TELEMETRY"] = "false";
        return start;
    }
}
