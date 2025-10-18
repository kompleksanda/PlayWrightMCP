using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

public sealed class PlaywrightManager : IAsyncDisposable
{
    private readonly IPlaywright _pw;
    private readonly IBrowserType _chromium;
    private readonly PlaywrightOptions _options;
    private readonly ConcurrentDictionary<string, IBrowser> _browsers = new();
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    // When we launch a persistent context (via userDataDir) we return a browserId but
    // actually hold the persistent IBrowserContext. This map lets us translate that
    // browserId back to the context id so callers of NewContext can get a valid context.
    private readonly ConcurrentDictionary<string, string> _persistentBrowserToContext = new();
    private readonly ConcurrentDictionary<string, IPage> _pages = new();

    private PlaywrightManager(IPlaywright pw, PlaywrightOptions? options = null)
    {
        _pw = pw;
        _chromium = _pw.Chromium;
        _options = options ?? new PlaywrightOptions();
    }

    public static async Task<PlaywrightManager> CreateAsync()
    {
        var pw = await Playwright.CreateAsync();
        return new PlaywrightManager(pw, null);
    }

    public static async Task<PlaywrightManager> CreateAsync(PlaywrightOptions options)
    {
        var pw = await Playwright.CreateAsync();
        return new PlaywrightManager(pw, options);
    }

    public async Task<string> LaunchBrowserAsync(bool headless = true)
    {
        // If caller didn't specify headless explicitly, use the configured default
        var useHeadless = headless;
        if (_options is not null)
        {
            // Use configured headless option
            useHeadless = _options.Headless;
        }

        // If a UserDataDir is configured we must create a persistent context via
        // LaunchPersistentContextAsync(userDataDir, options) rather than passing
        // a --user-data-dir argument to LaunchAsync (Playwright rejects that).
        if (!string.IsNullOrEmpty(_options?.UserDataDir))
        {
            var persistentOptions = new BrowserTypeLaunchPersistentContextOptions { Headless = useHeadless };
            var context = await _chromium.LaunchPersistentContextAsync(_options.UserDataDir, persistentOptions);

            // Create a browserId to return (to preserve the existing API) and
            // create a contextId that callers will use for pages.
            var browserId = Guid.NewGuid().ToString();
            var contextId = Guid.NewGuid().ToString();
            _contexts[contextId] = context;
            _persistentBrowserToContext[browserId] = contextId;
            return browserId;
        }

        var launchOptions = new BrowserTypeLaunchOptions { Headless = useHeadless };
        var browser = await _chromium.LaunchAsync(launchOptions);
        var id = Guid.NewGuid().ToString();
        _browsers[id] = browser;
        return id;
    }

    public async Task<string> NewContextAsync(string browserId)
    {
        // If this browserId refers to a persistent context launch, return the
        // persistent context id so callers can use it to create pages.
        if (_persistentBrowserToContext.TryGetValue(browserId, out var existingContextId))
        {
            return existingContextId;
        }

        if (!_browsers.TryGetValue(browserId, out var browser))
            throw new ArgumentException("Unknown browserId");
        var context = await browser.NewContextAsync();
        var id = Guid.NewGuid().ToString();
        _contexts[id] = context;
        return id;
    }

    public async Task<string> NewPageAsync(string contextId)
    {
        if (!_contexts.TryGetValue(contextId, out var context))
            throw new ArgumentException("Unknown contextId");
        var page = await context.NewPageAsync();
        var id = Guid.NewGuid().ToString();
        _pages[id] = page;
        return id;
    }

    public IPage GetPage(string pageId)
    {
        if (!_pages.TryGetValue(pageId, out var page))
            throw new ArgumentException("Unknown pageId");
        return page;
    }

    public async Task<byte[]> ScreenshotAsync(string pageId, PageScreenshotOptions? options = null)
    {
        var page = GetPage(pageId);
        var stream = await page.ScreenshotAsync(options ?? new PageScreenshotOptions { FullPage = true });
        return stream;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _pages.Values)
            try { await p.CloseAsync(); } catch { }
        foreach (var c in _contexts.Values)
            try { await c.CloseAsync(); } catch { }
        foreach (var b in _browsers.Values)
            try { await b.CloseAsync(); } catch { }
        try { _pw?.Dispose(); } catch { }
    }
}
