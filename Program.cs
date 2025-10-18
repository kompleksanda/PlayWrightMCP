using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;
using PlayWright;

// Parse a couple of simple CLI flags before building the host.
// Supported:
// --user-data-dir <path>   path to user data directory. If not specified, a temporary directory is created.
// --headless               run browser in headless mode. If not provided, headed (headless=false) by default.
string? userDataDir = null;
bool headlessFlag = false;

for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (string.Equals(a, "--user-data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        userDataDir = args[++i];
    }
    else if (string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase))
    {
        headlessFlag = true;
    }
}

// If user didn't provide a user data dir, create a temporary directory to use during this run.
if (string.IsNullOrWhiteSpace(userDataDir))
{
    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playwright-user-data-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(tmp);
    userDataDir = tmp;
}

var builder = Host.CreateApplicationBuilder(args);
// Register Playwright options so hosted services and manager can read them
builder.Services.Configure<PlaywrightOptions>(opts =>
{
    opts.UserDataDir = userDataDir!;
    opts.Headless = headlessFlag;
});
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});
// Add file logger that writes to a file on the user's Desktop and overwrites it each run.
var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
var logPath = Path.Combine(desktop, "PlayWright.log");
builder.Logging.AddProvider(new FileLoggerProvider(logPath, LogLevel.Trace));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
// Register hosted service that will create and initialize PlaywrightManager at startup
builder.Services.AddHostedService<PlaywrightInitializerHostedService>();
await builder.Build().RunAsync();