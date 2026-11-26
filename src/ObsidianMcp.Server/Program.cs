using System.Net;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using ObsidianMcp.Server;
using ObsidianMcp.Server.Tools;
using ObsidianMcp.Server.Vault;

CommandLine cli;
try { cli = CommandLine.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"obsidian-mcp: {ex.Message}");
    return 2;
}
if (cli.ShowHelp)
{
    Console.Error.WriteLine(CommandLine.HelpText);
    return 0;
}

try
{
    if (cli.Transport == "http") await RunHttp(cli);
    else await RunStdio(cli);
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"obsidian-mcp: {ex.Message}");
    return 1;
}

static async Task RunStdio(CommandLine cli)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        ContentRootPath = AppContext.BaseDirectory,
    });
    Configure(builder.Configuration, cli);
    // stdout is the MCP channel: every log line must go to stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var options = AddVault(builder.Services, builder.Configuration);
    builder.Services
        .AddMcpServer(ConfigureServer)
        .WithStdioServerTransport()
        .WithVaultTools(options, http: false);

    var host = builder.Build();
    host.Services.GetRequiredService<VaultService>(); // fail fast on a bad vault path
    await host.RunAsync();
}

static async Task RunHttp(CommandLine cli)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = AppContext.BaseDirectory,
    });
    Configure(builder.Configuration, cli);

    var urls = builder.Configuration["Http:Urls"] ?? "http://127.0.0.1:5199";
    builder.WebHost.UseUrls(urls);

    var apiKey = builder.Configuration["Http:ApiKey"];
    var allowAnonymous = builder.Configuration.GetValue<bool>("Http:AllowAnonymous");
    if (string.IsNullOrEmpty(apiKey) && !allowAnonymous)
        throw new InvalidOperationException(
            "HTTP mode requires an API key (--api-key or OBSIDIAN_MCP_API_KEY). " +
            "Pass --no-auth to run without one (only do that on a trusted, local-only setup).");

    var options = AddVault(builder.Services, builder.Configuration);
    builder.Services
        .AddMcpServer(ConfigureServer)
        .WithHttpTransport(o => o.Stateless = true)
        .WithVaultTools(options, http: true);

    var app = builder.Build();
    var vault = app.Services.GetRequiredService<VaultService>();
    var allowedOrigins = builder.Configuration.GetSection("Http:AllowedOrigins").Get<string[]>() ?? [];

    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/" || ctx.Request.Path == "/health") { await next(); return; }

        // DNS-rebinding protection (recommended by the MCP spec): browsers send Origin, server-to-server clients don't.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsAllowedOrigin(origin, allowedOrigins))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Origin not allowed");
            return;
        }

        if (!string.IsNullOrEmpty(apiKey) && !HasValidKey(ctx.Request, apiKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
        await next();
    });

    app.MapGet("/", () => $"Obsidian MCP server for vault '{vault.VaultName}'. MCP endpoint: /mcp");
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.MapMcp("/mcp");

    app.Logger.LogInformation("Serving vault '{Vault}' ({Root}) at {Urls}/mcp{ReadOnly}",
        vault.VaultName, vault.RootPath, urls, vault.IsReadOnly ? " [read-only]" : "");
    await app.RunAsync();
}

static void Configure(IConfigurationBuilder config, CommandLine cli)
{
    config.AddEnvironmentVariables("OBSIDIAN_MCP_");
    var overrides = new Dictionary<string, string?>();
    // Convenience env vars.
    if (Environment.GetEnvironmentVariable("OBSIDIAN_VAULT_PATH") is { Length: > 0 } envVault) overrides["Vault:Path"] = envVault;
    if (Environment.GetEnvironmentVariable("OBSIDIAN_MCP_API_KEY") is { Length: > 0 } envKey) overrides["Http:ApiKey"] = envKey;
    foreach (var (k, v) in cli.Settings) overrides[k] = v;
    config.AddInMemoryCollection(overrides);
}

static VaultOptions AddVault(IServiceCollection services, IConfiguration config)
{
    services.Configure<VaultOptions>(config.GetSection(VaultOptions.SectionName));
    services.AddSingleton<VaultService>();
    return config.GetSection(VaultOptions.SectionName).Get<VaultOptions>() ?? new VaultOptions();
}

static void ConfigureServer(ModelContextProtocol.Server.McpServerOptions o)
{
    o.ServerInfo = new Implementation { Name = "obsidian-vault", Title = "Obsidian Vault", Version = "1.0.0" };
    o.ServerInstructions =
        "Tools for reading and (unless read-only) editing the user's Obsidian vault of markdown notes. " +
        "Paths are vault-relative with '/' separators. Notes use Obsidian markdown: [[wikilinks]], #tags and YAML frontmatter properties. " +
        "Start with get_vault_info or search_notes; always read a note before editing it, and prefer append_to_note / replace_in_note over overwriting.";
}

static bool HasValidKey(HttpRequest req, string expected)
{
    string? supplied = null;
    var auth = req.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = auth[7..].Trim();
    supplied ??= req.Headers["X-API-Key"].FirstOrDefault();
    supplied ??= req.Query["key"].FirstOrDefault(); // for clients that can only be given a URL
    if (string.IsNullOrEmpty(supplied)) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}

static bool IsAllowedOrigin(string origin, string[] allowed)
{
    if (allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
    return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
}

namespace ObsidianMcp.Server
{
    internal static class McpBuilderExtensions
    {
        public static IMcpServerBuilder WithVaultTools(this IMcpServerBuilder builder, VaultOptions options, bool http)
        {
            builder.WithTools<VaultReadTools>();
            if (!options.ReadOnly) builder.WithTools<VaultWriteTools>();
            if (options.EnableSearchFetchTools ?? http) builder.WithTools<SearchFetchTools>();
            return builder;
        }
    }

    /// <summary>Tiny command-line parser that maps friendly switches onto configuration keys.</summary>
    internal sealed class CommandLine
    {
        public string Transport { get; private set; } = "stdio";
        public bool ShowHelp { get; private set; }
        public Dictionary<string, string?> Settings { get; } = new();

        public const string HelpText = """
            Obsidian vault MCP server

            Usage: obsidian-mcp --vault <path> [options]

              --vault <path>          Path to the Obsidian vault folder (or env OBSIDIAN_VAULT_PATH)
              --read-only             Expose only read tools
              --transport <mode>      stdio (default; Claude Desktop, Claude Code, Gemini CLI)
                                      or http (Streamable HTTP; ChatGPT, claude.ai, remote clients)
              --urls <urls>           HTTP listen address (default http://127.0.0.1:5199)
              --api-key <key>         Required for http unless --no-auth (or env OBSIDIAN_MCP_API_KEY).
                                      Send as 'Authorization: Bearer <key>', 'X-API-Key', or ?key=<key>
              --no-auth               Allow http without an API key (local testing only)
              --search-fetch          Also expose ChatGPT-style 'search' and 'fetch' tools (default on for http)
              --no-search-fetch       Do not expose 'search' and 'fetch'
              --exclude <folder>      Additional folder to hide (repeatable)
              -h, --help              Show this help

            Any setting can also be given as configuration, e.g. --Vault:MaxResults=100 or
            env OBSIDIAN_MCP_Vault__MaxResults=100, or in appsettings.json next to the executable.
            """;

        public static CommandLine Parse(string[] args)
        {
            var cl = new CommandLine();
            var excludes = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
                switch (a)
                {
                    case "-h" or "--help": cl.ShowHelp = true; break;
                    case "--vault": cl.Settings["Vault:Path"] = Next(); break;
                    case "--read-only": cl.Settings["Vault:ReadOnly"] = "true"; break;
                    case "--transport": cl.Transport = Next().ToLowerInvariant(); break;
                    case "--http": cl.Transport = "http"; break;
                    case "--urls": cl.Settings["Http:Urls"] = Next(); break;
                    case "--api-key": cl.Settings["Http:ApiKey"] = Next(); break;
                    case "--no-auth": cl.Settings["Http:AllowAnonymous"] = "true"; break;
                    case "--search-fetch": cl.Settings["Vault:EnableSearchFetchTools"] = "true"; break;
                    case "--no-search-fetch": cl.Settings["Vault:EnableSearchFetchTools"] = "false"; break;
                    case "--exclude": excludes.Add(Next()); break;
                    default:
                        if (a.StartsWith("--") && a.Contains('='))
                        {
                            var (k, v) = (a[2..a.IndexOf('=')], a[(a.IndexOf('=') + 1)..]);
                            cl.Settings[k] = v;
                        }
                        else if (!a.StartsWith('-') && !cl.Settings.ContainsKey("Vault:Path"))
                        {
                            cl.Settings["Vault:Path"] = a; // positional vault path
                        }
                        else throw new ArgumentException($"Unknown argument '{a}'. Use --help.");
                        break;
                }
            }
            if (cl.Transport is not ("stdio" or "http")) throw new ArgumentException("--transport must be 'stdio' or 'http'.");
            // The configuration binder appends list items to the defaults (.obsidian, .trash, .git).
            for (var i = 0; i < excludes.Count; i++) cl.Settings[$"Vault:ExcludedFolders:{i}"] = excludes[i];
            return cl;
        }
    }
}
