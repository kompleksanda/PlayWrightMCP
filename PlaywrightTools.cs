using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Playwright;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

[McpServerToolType]
public static class PlaywrightTools
{
    private static PlaywrightManager? _manager;

    // Called by DI on startup to set manager instance
    public static void SetManager(PlaywrightManager manager) => _manager = manager;

    // --- existing basic helpers ---
    private static CdpRelayServer? _relayServer;

    public static bool ContinuousCaptureEnabled { get; set; } = true;
    public const int MaxConsoleLogCapacity = 50;
    public const int MaxNetworkTransactionCapacity = 50;

    public static void EnsureContinuousCapture(string? pageId = null)
    {
        if (!ContinuousCaptureEnabled) return;
        try
        {
            StartConsoleCapture(pageId);
            StartNetworkCapture(pageId);
        }
        catch { }
    }

    private static void DisambiguatePageIdAndSelector(ref string? pageId, ref string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector) && !string.IsNullOrWhiteSpace(pageId))
        {
            if (pageId.StartsWith("#") || pageId.StartsWith(".") || pageId.StartsWith("//") || pageId.StartsWith("[") || pageId.Contains(" ") || pageId.StartsWith("button") || pageId.StartsWith("input") || pageId.StartsWith("div") || pageId.StartsWith("a") || pageId.StartsWith("table") || pageId.StartsWith("form"))
            {
                selector = pageId;
                pageId = null;
            }
        }
    }

    [McpServerTool, Description("Connects to a running Playwright MCP Bridge browser extension. Spawns Chrome automatically. Returns the browserId, contextId and list of pages discovered. Set enableContinuousCapture to automatically maintain rolling console and network buffers. Set persistStorageState to true to auto-restore and preserve cookies/localStorage across reconnects.")]
    public static async Task<object> ConnectToExtensionBridge(string? token = null, bool enableContinuousCapture = true, bool persistStorageState = false)
    {
        EnsureManager();
        try
        {
            ContinuousCaptureEnabled = enableContinuousCapture;
            token ??= Environment.GetEnvironmentVariable("PLAYWRIGHT_MCP_EXTENSION_TOKEN") ?? "";
            
            if (_relayServer != null)
            {
                _relayServer.Dispose();
            }
            _relayServer = new CdpRelayServer();
            
            // Starts server and spawns browser process, blocks until connected
            await _relayServer.StartAndConnectAsync(token);
            
            // Re-route internal Playwright connecting to that relay CDP endpoint
            var connResult = await _manager!.ConnectBrowserAsync(_relayServer.CdpEndpoint);
            if (connResult?.Contexts != null)
            {
                foreach (var ctx in connResult.Contexts)
                {
                    if (persistStorageState)
                    {
                        try
                        {
                            var bCtx = _manager.GetContext(ctx.ContextId);
                            await RestoreDefaultStorageStateAsync(bCtx);
                        }
                        catch { }
                    }

                    if (enableContinuousCapture && ctx.Pages != null)
                    {
                        foreach (var p in ctx.Pages)
                        {
                            EnsureContinuousCapture(p.PageId);
                        }
                    }
                }
            }
            return connResult!;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [McpServerTool, Description("Launches a Chromium browser and returns a browserId. You should only call this once and reuse the browserId for multiple contexts/pages. Set enableContinuousCapture to true to maintain rolling console and network buffers automatically.")]
    public static async Task<string> LaunchBrowser(bool headless = true, bool enableContinuousCapture = true)
    {
        EnsureManager();
        ContinuousCaptureEnabled = enableContinuousCapture;
        return await _manager!.LaunchBrowserAsync(headless);
    }

    [McpServerTool, Description("Creates a new browser context for given browserId and returns contextId. Call the new page tool next.")]
    public static async Task<string> NewContext(string browserId)
    {
        EnsureManager();
        return await _manager!.NewContextAsync(browserId);
    }

    [McpServerTool, Description("Creates a new page in the given context and returns pageId. Continuous capture is automatically activated if enabled.")]
    public static async Task<string> NewPage(string contextId)
    {
        EnsureManager();
        var pageId = await _manager!.NewPageAsync(contextId);
        EnsureContinuousCapture(pageId);
        return pageId;
    }

    [McpServerTool, Description("Injects a session token or cookie into the browser context. This allows you to simulate logged-in sessions by passing a cookie name, value, and optional domain. If domain is omitted or passed as a full URL, it is automatically derived from the page's current URL. pageId is optional and defaults to active page.")]
    public static async Task InjectCookie(string? pageId = null, string? name = null, string? value = null, string? domain = null, string path = "/")
    {
        EnsureManager();
        if (value == null && name != null && pageId != null)
        {
            value = name;
            name = pageId;
            pageId = null;
        }
        var page = _manager!.GetPage(pageId);
        
        string targetDomain = domain?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(targetDomain) || targetDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || targetDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var urlToParse = !string.IsNullOrWhiteSpace(targetDomain) ? targetDomain : page.Url;
            if (Uri.TryCreate(urlToParse, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                targetDomain = uri.Host;
            }
            else if (!string.IsNullOrWhiteSpace(page.Url) && Uri.TryCreate(page.Url, UriKind.Absolute, out var pageUri) && !string.IsNullOrWhiteSpace(pageUri.Host))
            {
                targetDomain = pageUri.Host;
            }
        }
        else
        {
            // Strip port or path if user passed "eu.sprinto.com:8080/app"
            var slashIdx = targetDomain.IndexOf('/');
            if (slashIdx >= 0) targetDomain = targetDomain.Substring(0, slashIdx);
            var colonIdx = targetDomain.IndexOf(':');
            if (colonIdx >= 0) targetDomain = targetDomain.Substring(0, colonIdx);
        }

        if (string.IsNullOrWhiteSpace(targetDomain))
        {
            targetDomain = "localhost";
        }

        await page.Context.AddCookiesAsync(new[]
        {
            new Microsoft.Playwright.Cookie
            {
                Name = name ?? "",
                Value = value ?? "",
                Domain = targetDomain,
                Path = path
            }
        });
    }

    public static string GetDefaultStorageStatePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaywrightMCP");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, "default_storage_state.json");
    }

    private static async Task RestoreDefaultStorageStateAsync(IBrowserContext context)
    {
        var defaultPath = GetDefaultStorageStatePath();
        if (File.Exists(defaultPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(defaultPath);
                await ImportStorageStateToContextAsync(context, json);
            }
            catch { }
        }
    }

    internal static async Task ImportStorageStateToContextAsync(IBrowserContext context, string stateJson)
    {
        using var doc = JsonDocument.Parse(stateJson);
        var root = doc.RootElement;

        // 1. Restore cookies
        if (root.TryGetProperty("cookies", out var cookiesProp) && cookiesProp.ValueKind == JsonValueKind.Array)
        {
            var cookiesList = new List<Microsoft.Playwright.Cookie>();
            foreach (var c in cookiesProp.EnumerateArray())
            {
                try
                {
                    var cookie = new Microsoft.Playwright.Cookie
                    {
                        Name = c.GetProperty("name").GetString() ?? "",
                        Value = c.GetProperty("value").GetString() ?? "",
                        Domain = c.TryGetProperty("domain", out var d) ? (d.GetString() ?? "") : "",
                        Path = c.TryGetProperty("path", out var p) ? (p.GetString() ?? "/") : "/",
                        HttpOnly = c.TryGetProperty("httpOnly", out var h) && h.GetBoolean(),
                        Secure = c.TryGetProperty("secure", out var s) && s.GetBoolean()
                    };
                    if (c.TryGetProperty("expires", out var exp) && exp.TryGetDouble(out var expSec))
                    {
                        cookie.Expires = (float)expSec;
                    }
                    if (c.TryGetProperty("sameSite", out var ss))
                    {
                        var ssStr = ss.GetString()?.ToLowerInvariant();
                        cookie.SameSite = ssStr switch
                        {
                            "strict" => SameSiteAttribute.Strict,
                            "lax" => SameSiteAttribute.Lax,
                            "none" => SameSiteAttribute.None,
                            _ => null
                        };
                    }
                    cookiesList.Add(cookie);
                }
                catch { }
            }
            if (cookiesList.Count > 0)
            {
                await context.AddCookiesAsync(cookiesList);
            }
        }

        // 2. Restore origins / localStorage
        if (root.TryGetProperty("origins", out var originsProp) && originsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var origin in originsProp.EnumerateArray())
            {
                var originUrl = origin.TryGetProperty("origin", out var o) ? o.GetString() : null;
                if (string.IsNullOrWhiteSpace(originUrl)) continue;

                if (origin.TryGetProperty("localStorage", out var ls) && ls.ValueKind == JsonValueKind.Array)
                {
                    var pairs = new List<(string Name, string Value)>();
                    foreach (var item in ls.EnumerateArray())
                    {
                        var n = item.TryGetProperty("name", out var np) ? np.GetString() : null;
                        var v = item.TryGetProperty("value", out var vp) ? vp.GetString() : null;
                        if (!string.IsNullOrEmpty(n) && v != null)
                        {
                            pairs.Add((n, v));
                        }
                    }

                    if (pairs.Count > 0)
                    {
                        // Match or find page belonging to this origin, or fall back to any available page in context
                        var page = context.Pages.FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) && (p.Url.StartsWith(originUrl, StringComparison.OrdinalIgnoreCase) || originUrl.Contains(p.Url)))
                                   ?? context.Pages.FirstOrDefault();
                        if (page != null)
                        {
                            var jsSet = @"(items) => {
                                for (const item of items) {
                                    try { localStorage.setItem(item.name, item.value); } catch {}
                                }
                            }";
                            try
                            {
                                var payload = pairs.Select(p => new { name = p.Name, value = p.Value }).ToArray();
                                await page.EvaluateAsync(jsSet, payload);
                            }
                            catch { }
                        }
                    }
                }
            }
        }
    }

    [McpServerTool, Description("Exports the complete storage state (cookies, origins, and localStorage items) of the page's browser context. Can save directly to a specified filePath or return as a compact JSON string. Also caches to local storage for automatic restoration. pageId is optional and defaults to active page.")]
    public static async Task<string> ExportStorageState(string? pageId = null, string? filePath = null)
    {
        EnsureManager();
        if (filePath == null && !string.IsNullOrWhiteSpace(pageId) && (pageId.Contains("\\") || pageId.Contains("/") || pageId.EndsWith(".json")))
        {
            filePath = pageId;
            pageId = null;
        }
        var page = _manager!.GetPage(pageId);

        try
        {
            var options = new BrowserContextStorageStateOptions();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                options.Path = filePath;
            }

            var stateJson = await page.Context.StorageStateAsync(options);

            // Also persist to default cache file for automatic bridge reconnect recovery
            try
            {
                var defaultPath = GetDefaultStorageStatePath();
                await File.WriteAllTextAsync(defaultPath, stateJson);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return $"Successfully exported storage state to '{filePath}'";
            }

            return stateJson;
        }
        catch (Exception ex)
        {
            return $"Error exporting storage state: {ex.Message}";
        }
    }

    [McpServerTool, Description("Imports storage state (cookies, origins, and localStorage) into the page's browser context from a JSON string or file path. Enables seamless session resumption without re-authenticating. pageId is optional and defaults to active page.")]
    public static async Task<string> ImportStorageState(string? pageId = null, string? stateJson = null, string? filePath = null)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);

        try
        {
            string jsonContent = stateJson ?? "";
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!File.Exists(filePath))
                {
                    return $"Error: Storage state file '{filePath}' does not exist.";
                }
                jsonContent = await File.ReadAllTextAsync(filePath);
            }

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                // Fall back to default cache file if available
                var defaultPath = GetDefaultStorageStatePath();
                if (File.Exists(defaultPath))
                {
                    jsonContent = await File.ReadAllTextAsync(defaultPath);
                }
                else
                {
                    return "Error: Neither stateJson nor filePath was provided, and no default cached storage state exists.";
                }
            }

            await ImportStorageStateToContextAsync(page.Context, jsonContent);
            return "Successfully imported storage state into browser context.";
        }
        catch (Exception ex)
        {
            return $"Error importing storage state: {ex.Message}";
        }
    }

    [McpServerTool, Description("Navigates the page to the specified url. Optional waitUntil parameter supports 'domcontentloaded' (recommended default for SPAs to avoid 30s networkidle hangs), 'commit', 'load', or 'networkidle'. When bypassConsentBanners is true, automatically attempts to dismiss cookie consent dialogs (OneTrust, Cookiebot, etc.) after navigation settles. pageId is optional and defaults to active page.")]
    public static async Task<string> Navigate(string? pageId = null, string? url = null, string waitUntil = "domcontentloaded", float? timeoutMs = null, bool bypassConsentBanners = false)
    {
        EnsureManager();

        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(pageId) && (pageId.Contains("://") || pageId.StartsWith("data:") || pageId.StartsWith("about:")))
        {
            url = pageId;
            pageId = null;
        }

        if (string.IsNullOrWhiteSpace(url)) return "Error: url is required";

        var page = _manager!.GetPage(pageId);
        if (page == null) return "Error: Unknown pageId";

        EnsureContinuousCapture(pageId);

        try
        {
            if (page.IsClosed) return "Error: The requested page is already closed.";
        }
        catch { }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "file" && uri.Scheme != "about" && uri.Scheme != "data"))
        {
            if (Uri.TryCreate("http://" + url, UriKind.Absolute, out var tryUri))
            {
                url = tryUri.ToString();
            }
            else
            {
                return $"Error: Invalid URL: '{url}'";
            }
        }

        WaitUntilState targetWaitState = (waitUntil?.ToLowerInvariant()) switch
        {
            "networkidle" => WaitUntilState.NetworkIdle,
            "load" => WaitUntilState.Load,
            "commit" => WaitUntilState.Commit,
            _ => WaitUntilState.DOMContentLoaded
        };

        float navTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 
            ? timeoutMs.Value 
            : (targetWaitState == WaitUntilState.DOMContentLoaded ? 15000f : 30000f);

        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = targetWaitState, Timeout = navTimeout });
            _ = Task.Run(async () =>
            {
                try
                {
                    var defaultPath = GetDefaultStorageStatePath();
                    await page.Context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = defaultPath });
                }
                catch { }
            });

            if (bypassConsentBanners)
            {
                try
                {
                    await CloseOverlays(pageId, target: "consent", waitForSettle: false, timeoutMs: 1500);
                }
                catch { }
            }

            return "Success";
        }
        catch (TimeoutException)
        {
            if (targetWaitState != WaitUntilState.Commit)
            {
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 10000 });
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var defaultPath = GetDefaultStorageStatePath();
                            await page.Context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = defaultPath });
                        }
                        catch { }
                    });

                    if (bypassConsentBanners)
                    {
                        try
                        {
                            await CloseOverlays(pageId, target: "consent", waitForSettle: false, timeoutMs: 1500);
                        }
                        catch { }
                    }

                    return "Success";
                }
                catch (Exception commitEx)
                {
                    return $"Error: Navigation to '{url}' failed after progressive fallback: {commitEx.Message}";
                }
            }
            return $"Error: Navigation to '{url}' timed out.";
        }
        catch (Exception ex)
        {
            return $"Error: Navigation to '{url}' failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Gets the page content (outer HTML of document). Use GetAccessibilitySnapshot instead. Do not call this unless necessary as it takes more tokens. pageId is optional and defaults to active page.")]
    public static async Task<string> GetContent(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        var content = await page.ContentAsync();
        if (string.IsNullOrEmpty(content)) return content;

        // If content is small enough, return directly and remove any cached pages
        if (Encoding.UTF8.GetByteCount(content) <= DefaultContentChunkSize)
        {
            _contentPages.TryRemove(pageId, out var _);
            return content;
        }
        // Return content truncated to chunk size limit for immediate response
        //_contentPages.TryRemove(pageId, out var _);
        //return content.Substring(0, Math.Min(content.Length, DefaultContentChunkSize));

        // Otherwise split into pages and store; return a small JSON metadata object pointing to pagination
        PrepareContentPages(pageId, content);
        var meta = new { paginated = true, pageCount = GetContentPageCount(pageId) };
        return JsonSerializer.Serialize(meta);
    }

    // Helper to split and store content into chunks (preserving UTF8 boundaries heuristically)
    private static void PrepareContentPages(string pageId, string content)
    {
        // Estimate total bytes
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var pages = (int)Math.Ceiling((double)totalBytes / DefaultContentChunkSize);
        var map = new ConcurrentDictionary<int, string>();

        int charPos = 0;
        for (int i = 0; i < pages && charPos < content.Length; i++)
        {
            // Start with a heuristic char length: average 1 byte per char is optimistic; cap at remaining
            int take = Math.Min(content.Length - charPos, DefaultContentChunkSize);

            // Reduce take until byte count fits
            while (take > 0 && Encoding.UTF8.GetByteCount(content.AsSpan(charPos, Math.Min(take, content.Length - charPos))) > DefaultContentChunkSize)
            {
                take = Math.Max(1, take - 256);
            }

            var chunk = content.Substring(charPos, Math.Min(take, content.Length - charPos));
            map[i] = chunk;
            charPos += chunk.Length;
        }

        _contentPages[pageId] = map;
    }

    [McpServerTool, Description("Get number of content pages for a paginated page content.")]
    public static int GetContentPageCount(string pageId)
    {
        EnsureManager();
        if (!_contentPages.TryGetValue(pageId, out var map)) return 0;
        return map.Count;
    }

    [McpServerTool, Description("Get a specific content page (chunk) by zero-based index. Returns null if not available. Call this tool and process its results one at a time (You have a max of 1048576 input tokens so you don't exceed it, ensure you process that information before calling the next page)")]
    public static string? GetContentPage(string pageId, int pageIndex)
    {
        EnsureManager();
        if (!_contentPages.TryGetValue(pageId, out var map)) return null;
        if (!map.TryGetValue(pageIndex, out var chunk)) return null;
        return chunk;
    }

    private static async Task<string> DiagnoseSelectorFailureAsync(IPage page, string selector, string actionName, Exception originalException)
    {
        try
        {
            var js = @"(sel) => {
                try {
                    const matches = document.querySelectorAll(sel);
                    if (matches.length > 0) {
                        const el = matches[0];
                        const rect = el.getBoundingClientRect();
                        const style = window.getComputedStyle(el);
                        const isVisible = rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
                        const isAttached = document.body.contains(el);
                        return {
                            status: 'element_found_in_dom',
                            matchCount: matches.length,
                            isAttached,
                            isVisible,
                            display: style.display,
                            visibility: style.visibility,
                            opacity: style.opacity,
                            pointerEvents: style.pointerEvents,
                            dimensions: { width: Math.round(rect.width), height: Math.round(rect.height), top: Math.round(rect.top), left: Math.round(rect.left) },
                            snippet: el.outerHTML.substring(0, 250)
                        };
                    }

                    // Element not found - check for interactive elements or candidate matches
                    const cleanWords = sel.replace(/[^a-zA-Z0-9_-]/g, ' ').trim().split(/\s+/).filter(w => w.length > 2);
                    const interactives = Array.from(document.querySelectorAll('button, a, input, select, textarea, [role], [data-testid], [id]'));
                    const candidates = [];

                    for (const el of interactives) {
                        const text = (el.innerText || el.getAttribute('placeholder') || el.getAttribute('aria-label') || el.getAttribute('data-testid') || el.id || '').trim();
                        const tag = el.tagName.toLowerCase();
                        const role = el.getAttribute('role') || '';
                        const id = el.id ? '#' + el.id : '';
                        const testId = el.getAttribute('data-testid') ? `[data-testid=""${el.getAttribute('data-testid')}""]` : '';
                        const rect = el.getBoundingClientRect();
                        const visible = rect.width > 0 && rect.height > 0;

                        const matchesKeyword = cleanWords.some(w => 
                            text.toLowerCase().includes(w.toLowerCase()) || 
                            (el.className && typeof el.className === 'string' && el.className.toLowerCase().includes(w.toLowerCase()))
                        );

                        if (matchesKeyword) {
                            candidates.push({ tag, role, id, testId, text: text.substring(0, 40), visible });
                        }
                        if (candidates.length >= 6) break;
                    }

                    return {
                        status: 'element_not_found',
                        matchCount: 0,
                        candidates
                    };
                } catch (e) {
                    return { error: e.message };
                }
            }";

            var diag = await page.EvaluateAsync<JsonElement>(js, selector);
            return $"[DOM Diagnostics for '{selector}' during {actionName}]: {diag.GetRawText()}";
        }
        catch (Exception diagEx)
        {
            return $"[DOM Diagnostics unavailable: {diagEx.Message}]";
        }
    }

    [McpServerTool, Description("Locks or emulates document/window focus for the page. Useful for preventing popovers, dropdowns, and virtualized menus from closing due to window blur events. pageId is optional and defaults to active page.")]
    public static async Task<string> EmulateFocus(string? pageId = null, bool enabled = true)
    {
        EnsureManager();
        try
        {
            await _manager!.EnsureFocusAsync(pageId, enabled);
            return $"Focus emulation set to {enabled} for page {pageId ?? _manager.LastActivePageId}";
        }
        catch (Exception ex)
        {
            return $"Error enabling focus emulation: {ex.Message}";
        }
    }

    private static async Task<ILocator?> FindTopmostContainerAsync(IPage page)
    {
        try
        {
            var js = @"() => {
                const overlaySelectors = [
                    '.ant-drawer-open',
                    '.ant-drawer-content-wrapper',
                    '.ant-modal-wrap:not([style*=""display: none""])',
                    '.MuiDialog-root:not([aria-hidden=""true""])',
                    '.MuiDrawer-root:not([aria-hidden=""true""])',
                    '[data-radix-popper-content-wrapper]',
                    'dialog[open]'
                ];

                const candidates = [];
                for (const sel of overlaySelectors) {
                    const els = Array.from(document.querySelectorAll(sel));
                    for (const el of els) {
                        const rect = el.getBoundingClientRect();
                        const style = window.getComputedStyle(el);
                        if (rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden') {
                            const z = parseInt(style.zIndex, 10) || 0;
                            candidates.push({ el, z });
                        }
                    }
                }

                if (candidates.length === 0) return null;
                // Sort descending by z-index
                candidates.sort((a, b) => b.z - a.z);
                const top = candidates[0].el;
                if (top.id) return '#' + top.id;
                if (top.classList && top.classList.length > 0) {
                    const cls = Array.from(top.classList).join('.');
                    return '.' + cls;
                }
                return null;
            }";

            var topSel = await page.EvaluateAsync<string?>(js);
            if (!string.IsNullOrWhiteSpace(topSel))
            {
                var loc = page.Locator(topSel);
                if (await loc.CountAsync() > 0) return loc.Last;
            }
        }
        catch { }
        return null;
    }

    private static async Task<ILocator> ResolveTargetLocatorAsync(IPage page, string selector, int? index = null, bool autoFirst = true, bool topmostOnly = false, string? frameSelector = null)
    {
        // 1. If an explicit frameSelector is specified, pierce that frame first
        if (!string.IsNullOrWhiteSpace(frameSelector))
        {
            var frameLoc = page.FrameLocator(frameSelector);
            var loc = frameLoc.Locator(selector);
            if (index.HasValue) return loc.Nth(index.Value);
            return autoFirst ? loc.First : loc;
        }

        // 2. Topmost overlay scoping
        if (topmostOnly)
        {
            var top = await FindTopmostContainerAsync(page);
            if (top != null)
            {
                var scoped = top.Locator(selector);
                if (await scoped.CountAsync() > 0)
                {
                    return index.HasValue ? scoped.Nth(index.Value) : (autoFirst ? scoped.First : scoped);
                }
            }
        }

        var locator = page.Locator(selector);
        
        // 3. Auto-piercing check: if not found in main document, check if any iframe contains it
        try
        {
            if (await locator.CountAsync() == 0)
            {
                var iframeCount = await page.Locator("iframe").CountAsync();
                for (int f = 0; f < iframeCount; f++)
                {
                    var fLoc = page.FrameLocator($"iframe >> nth={f}");
                    var candidate = fLoc.Locator(selector);
                    if (await candidate.CountAsync() > 0)
                    {
                        return index.HasValue ? candidate.Nth(index.Value) : (autoFirst ? candidate.First : candidate);
                    }
                }
            }
        }
        catch { }

        if (index.HasValue)
        {
            return locator.Nth(index.Value);
        }

        if (autoFirst)
        {
            try
            {
                var count = await locator.CountAsync();
                if (count > 1)
                {
                    ILocator target = locator.First;
                    for (int i = 0; i < count; i++)
                    {
                        var item = locator.Nth(i);
                        if (await item.IsVisibleAsync())
                        {
                            target = item;
                            break;
                        }
                    }
                    return target;
                }
            }
            catch { }
        }

        return locator;
    }

    private static async Task WaitForElementStabilityAsync(ILocator locator, float? animationTimeoutMs = 250, bool waitForStable = true, float? timeoutMs = 10000)
    {
        if (!waitForStable) return;
        try
        {
            float actualWaitTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = actualWaitTimeout });
            var maxTimeout = animationTimeoutMs.HasValue && animationTimeoutMs.Value > 0 ? (int)animationTimeoutMs.Value : 250;

            var jsCheck = @"async (el, timeout) => {
                try {
                    const start = Date.now();
                    // 1. Check Web Animations API
                    if (el.getAnimations && typeof el.getAnimations === 'function') {
                        const anims = el.getAnimations({ subtree: true });
                        for (const a of anims) {
                            if (a.playState === 'running') {
                                try { await Promise.race([a.finished, new Promise(r => setTimeout(r, timeout))]); } catch {}
                            }
                        }
                    }

                    // 2. Sample bounding rect stability across two frames
                    let prevRect = el.getBoundingClientRect();
                    const pollInterval = 40;
                    while (Date.now() - start < timeout) {
                        await new Promise(r => setTimeout(r, pollInterval));
                        const currRect = el.getBoundingClientRect();
                        if (Math.abs(currRect.top - prevRect.top) < 0.5 &&
                            Math.abs(currRect.left - prevRect.left) < 0.5 &&
                            Math.abs(currRect.width - prevRect.width) < 0.5 &&
                            Math.abs(currRect.height - prevRect.height) < 0.5) {
                            break;
                        }
                        prevRect = currRect;
                    }
                    return true;
                } catch {
                    return false;
                }
            }";

            await locator.EvaluateAsync(jsCheck, maxTimeout);
        }
        catch { }
    }

    private static async Task ExecuteWithHttpMutationWaitAsync(IPage page, Func<Task> action, bool waitForHttpMutation, float timeoutMs = 3000f)
    {
        if (!waitForHttpMutation)
        {
            await action();
            return;
        }

        var mutatingInFlight = new ConcurrentDictionary<IRequest, byte>();
        void OnReq(object? s, IRequest req)
        {
            var m = req.Method?.ToUpperInvariant();
            if (m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH")
            {
                mutatingInFlight.TryAdd(req, 0);
            }
        }
        void OnReqDone(object? s, IRequest req)
        {
            mutatingInFlight.TryRemove(req, out _);
        }

        page.Request += OnReq;
        page.RequestFinished += OnReqDone;
        page.RequestFailed += OnReqDone;

        try
        {
            await action();

            var start = DateTime.UtcNow;
            var maxWait = TimeSpan.FromMilliseconds(timeoutMs > 0 ? Math.Min(timeoutMs, 3000f) : 3000);

            // Wait briefly for a request to be initiated (up to 200ms)
            while ((DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(200) && mutatingInFlight.IsEmpty)
            {
                await Task.Delay(50);
            }

            // Wait for all in-flight mutating requests to complete
            while (!mutatingInFlight.IsEmpty && (DateTime.UtcNow - start) < maxWait)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            page.Request -= OnReq;
            page.RequestFinished -= OnReqDone;
            page.RequestFailed -= OnReqDone;
        }
    }

    [McpServerTool, Description("Clicks on a selector on the page. Supports iframe piercing (frameSelector or auto-piercing), fast-fail action timeouts (timeoutMs, default 10000), topmost modal/drawer scoping (topmostOnly: true), multi-match disambiguation (autoFirst / index), pre-click animation stabilization (waitForStable: true), post-click transition delay (postClickDelayMs, e.g. 300), and waiting for background HTTP mutations to settle (waitForHttpMutation: true). pageId is optional and defaults to active page.")]
    public static async Task<string> Click(string? pageId = null, string? selector = null, int? index = null, bool autoFirst = true, bool waitForStable = true, float? animationTimeoutMs = 250, float? timeoutMs = 10000, bool topmostOnly = false, float? postClickDelayMs = null, string? frameSelector = null, bool waitForHttpMutation = false)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
        var clickOpts = new LocatorClickOptions { Timeout = actualTimeout };

        try 
        {
            var targetLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst, topmostOnly, frameSelector);
            await WaitForElementStabilityAsync(targetLocator, animationTimeoutMs, waitForStable, actualTimeout);

            await ExecuteWithHttpMutationWaitAsync(page, async () =>
            {
                try
                {
                    await targetLocator.ClickAsync(clickOpts);
                }
                catch (Exception clickEx) when (!topmostOnly && clickEx.Message.Contains("intercepts pointer events", StringComparison.OrdinalIgnoreCase))
                {
                    // Auto-fallback: retry inside topmost modal/drawer
                    var topLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst, topmostOnly: true);
                    await topLocator.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                }
            }, waitForHttpMutation, actualTimeout);

            if (postClickDelayMs.HasValue && postClickDelayMs.Value > 0)
            {
                await page.WaitForTimeoutAsync(postClickDelayMs.Value);
            }

            return "Success";
        } 
        catch (Exception ex) 
        {
            if (ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase) && autoFirst)
            {
                try
                {
                    var fallback = page.Locator(selector).First;
                    await WaitForElementStabilityAsync(fallback, animationTimeoutMs, waitForStable, actualTimeout);
                    await ExecuteWithHttpMutationWaitAsync(page, async () =>
                    {
                        await fallback.ClickAsync(clickOpts);
                    }, waitForHttpMutation, actualTimeout);

                    if (postClickDelayMs.HasValue && postClickDelayMs.Value > 0)
                    {
                        await page.WaitForTimeoutAsync(postClickDelayMs.Value);
                    }
                    return "Success";
                }
                catch { }
            }
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "Click", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Hovers over a selector on the page. Implicitly scrolls the element into view before hovering. Supports iframe piercing (frameSelector or auto-piercing), fast-fail action timeouts (timeoutMs, default 10000), topmost modal/drawer scoping (topmostOnly), multi-match disambiguation, and animation stabilization. pageId is optional and defaults to active page.")]
    public static async Task<string> Hover(string? pageId = null, string? selector = null, int? index = null, bool autoFirst = true, bool waitForStable = true, float? animationTimeoutMs = 250, float? timeoutMs = 10000, bool topmostOnly = false, string? frameSelector = null)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
        var hoverOpts = new LocatorHoverOptions { Timeout = actualTimeout };

        try 
        {
            var targetLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst, topmostOnly, frameSelector);
            await WaitForElementStabilityAsync(targetLocator, animationTimeoutMs, waitForStable, actualTimeout);
            await targetLocator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = actualTimeout });
            await targetLocator.HoverAsync(hoverOpts);
            return "Success";
        } 
        catch (Exception ex) 
        {
            if (ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase) && autoFirst)
            {
                try
                {
                    var fallback = page.Locator(selector).First;
                    await WaitForElementStabilityAsync(fallback, animationTimeoutMs, waitForStable, actualTimeout);
                    await fallback.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 });
                    await fallback.HoverAsync(hoverOpts);
                    return "Success";
                }
                catch { }
            }
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "Hover", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Drags the element at sourceSelector and drops it onto the targetSelector. Implicitly scrolls the elements into view. pageId is optional and defaults to active page.")]
    public static async Task<string> DragAndDrop(string? pageId = null, string? sourceSelector = null, string? targetSelector = null)
    {
        EnsureManager();
        if (targetSelector == null && sourceSelector != null && pageId != null)
        {
            targetSelector = sourceSelector;
            sourceSelector = pageId;
            pageId = null;
        }
        if (string.IsNullOrWhiteSpace(sourceSelector)) return "Error: sourceSelector is required";
        if (string.IsNullOrWhiteSpace(targetSelector)) return "Error: targetSelector is required";
        var page = _manager!.GetPage(pageId);
        try 
        {
            var source = page.Locator(sourceSelector);
            var target = page.Locator(targetSelector);
            
            await source.ScrollIntoViewIfNeededAsync();
            await source.DragToAsync(target);
            await target.ScrollIntoViewIfNeededAsync();
            return "Success";
        } 
        catch (Exception ex) 
        {
            var diag = await DiagnoseSelectorFailureAsync(page, sourceSelector, "DragAndDrop", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    public class TypeOptions
    {
        public bool PressEnter { get; set; }
        public float? Delay { get; set; }
    }

    [McpServerTool, Description("Types text into selector on the page, simulating real keyboard events. Supports fast-fail timeout (timeoutMs, default 10000). You can provide an options argument like {\"pressEnter\": true, \"delay\": 100}. pageId is optional and defaults to active page.")]
    public static async Task<string> Type(string? pageId = null, string? selector = null, string? text = null, TypeOptions? options = null, float? timeoutMs = 10000)
    {
        EnsureManager();
        if (text == null && selector != null && pageId != null)
        {
            text = selector;
            selector = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
        try 
        {
            var locator = page.Locator(selector);
            await locator.ClearAsync(new LocatorClearOptions { Timeout = actualTimeout });

            var seqOptions = new LocatorPressSequentiallyOptions { Timeout = actualTimeout };
            if (options?.Delay != null)
            {
                seqOptions.Delay = options.Delay;
            }

            await locator.PressSequentiallyAsync(text ?? "", seqOptions);
            if (options?.PressEnter == true)
            {
                await locator.PressAsync("Enter", new LocatorPressOptions { Timeout = actualTimeout });
            }
            return "Success";
        } 
        catch (Exception ex) 
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "Type", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Fills an input, textarea or contenteditable element with the specified value using native Playwright locator.FillAsync. Supports iframe piercing (frameSelector or auto-piercing), fast-fail timeout (timeoutMs, default 10000), topmost modal/drawer scoping (topmostOnly), multi-match disambiguation, and animation stabilization. pageId is optional and defaults to active page.")]
    public static async Task<string> Fill(string? pageId = null, string? selector = null, string? value = null, int? index = null, bool autoFirst = true, bool waitForStable = true, float? animationTimeoutMs = 250, float? timeoutMs = 10000, bool topmostOnly = false, string? frameSelector = null)
    {
        EnsureManager();
        if (value == null && selector != null && pageId != null)
        {
            value = selector;
            selector = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        value ??= "";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
        var fillOpts = new LocatorFillOptions { Timeout = actualTimeout };

        try
        {
            var targetLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst, topmostOnly, frameSelector);
            await WaitForElementStabilityAsync(targetLocator, animationTimeoutMs, waitForStable, actualTimeout);
            await targetLocator.FillAsync(value, fillOpts);
            return "Success";
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase) && autoFirst)
            {
                try
                {
                    var fallback = page.Locator(selector).First;
                    await WaitForElementStabilityAsync(fallback, animationTimeoutMs, waitForStable, actualTimeout);
                    await fallback.FillAsync(value, fillOpts);
                    return "Success";
                }
                catch { }
            }
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "Fill", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Fills a React controlled input, textarea, or contenteditable element. Uses Playwright locator.FillAsync with prototype descriptor fallback and synthetic event dispatching to ensure React state updates. Supports iframe piercing (frameSelector or auto-piercing), fast-fail timeout (timeoutMs, default 10000), topmost modal/drawer scoping (topmostOnly), multi-match disambiguation, animation stabilization, and waiting for background HTTP mutations to settle (waitForHttpMutation: true). pageId is optional and defaults to active page.")]
    public static async Task<string> FillInput(string? pageId = null, string? selector = null, string? value = null, int? index = null, bool autoFirst = true, bool waitForStable = true, float? animationTimeoutMs = 250, float? timeoutMs = 10000, bool topmostOnly = false, string? frameSelector = null, bool waitForHttpMutation = false)
    {
        EnsureManager();
        if (value == null && selector != null && pageId != null)
        {
            value = selector;
            selector = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        value ??= "";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;

        try
        {
            var targetLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst, topmostOnly, frameSelector);
            await WaitForElementStabilityAsync(targetLocator, animationTimeoutMs, waitForStable, actualTimeout);
            await targetLocator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = actualTimeout });
            await targetLocator.FocusAsync(new LocatorFocusOptions { Timeout = actualTimeout });

            JsonElement result = default;
            await ExecuteWithHttpMutationWaitAsync(page, async () =>
            {
                // 1. Try native Playwright FillAsync first
                try
                {
                    await targetLocator.FillAsync(value, new LocatorFillOptions { Timeout = Math.Min(3000, actualTimeout) });
                }
                catch { /* Fall through to descriptor fallback */ }

                // 2. Controlled component prototype descriptor setter and synthetic events
                var js = @"(params) => {
                    const { sel, idx, val } = params;
                    const matches = document.querySelectorAll(sel);
                    const el = (idx !== null && idx !== undefined && idx >= 0 && idx < matches.length) ? matches[idx] : matches[0];
                    if (!el) return { success: false, reason: 'element_not_found' };

                    el.focus();
                    if (el.isContentEditable) {
                        el.textContent = val;
                        el.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }));
                        el.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
                        return { success: true, method: 'contenteditable', value: el.textContent };
                    }

                    const isInput = el instanceof HTMLInputElement;
                    const isTextArea = el instanceof HTMLTextAreaElement;
                    if (isInput || isTextArea) {
                        const proto = isTextArea ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                        const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                        if (desc && desc.set) {
                            desc.set.call(el, val);
                        } else {
                            el.value = val;
                        }
                        el.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }));
                        el.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
                        return { success: true, method: 'prototype_setter', value: el.value };
                    }

                    // Fallback for custom components
                    el.value = val;
                    el.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
                    return { success: true, method: 'direct_property', value: el.value };
                }";

                result = await page.EvaluateAsync<JsonElement>(js, new { sel = selector, idx = index, val = value });
            }, waitForHttpMutation, actualTimeout);

            return $"Success: {result}";
        }
        catch (Exception ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "FillInput", ex);
            return $"Error: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Focuses an element (or active document) and presses a keyboard key or key combination (e.g., 'Enter', 'Escape', 'Tab', 'Backspace', 'ArrowDown', 'Control+A'). pageId is optional and defaults to active page.")]
    public static async Task<string> PressKey(string? pageId = null, string? key = null, string? selector = null)
    {
        EnsureManager();
        if (key == null && pageId != null)
        {
            key = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(key)) return "Error: key is required";
        var page = _manager!.GetPage(pageId);
        try
        {
            if (!string.IsNullOrWhiteSpace(selector))
            {
                var locator = page.Locator(selector);
                await locator.FocusAsync();
                await locator.PressAsync(key);
            }
            else
            {
                await page.Keyboard.PressAsync(key);
            }
            return "Success";
        }
        catch (Exception ex)
        {
            var diag = !string.IsNullOrWhiteSpace(selector) ? await DiagnoseSelectorFailureAsync(page, selector, "PressKey", ex) : "";
            return $"Error: {ex.Message}\n{diag}".TrimEnd();
        }
    }

    [McpServerTool, Description("Selects an option from modern UI dropdowns, virtualized select boxes (Ant Design, Material UI, React-Select), or native select elements. Supports iframe piercing (frameSelector or auto-piercing), fast-fail timeout (timeoutMs, default 10000), drawer/animation stabilization, and waiting for background HTTP mutations to settle (waitForHttpMutation: true). pageId is optional and defaults to active page.")]
    public static async Task<string> SelectOption(string? pageId = null, string? selector = null, string? option = null, string? searchInputSelector = null, string? dropdownSelector = null, bool waitForStable = true, float? animationTimeoutMs = 250, float? timeoutMs = 10000, string? frameSelector = null, bool waitForHttpMutation = false)
    {
        EnsureManager();
        if (option == null && selector != null && pageId != null)
        {
            option = selector;
            selector = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        if (string.IsNullOrWhiteSpace(option)) return "Error: option is required";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 10000f;
        try
        {
            var selectLoc = await ResolveTargetLocatorAsync(page, selector, frameSelector: frameSelector);
            // Check if native <select> first
            var isNativeSelect = await selectLoc.EvaluateAsync<bool>("el => el.tagName.toLowerCase() === 'select'").ConfigureAwait(false);
            if (isNativeSelect)
            {
                var selected = await selectLoc.SelectOptionAsync(new[] { new SelectOptionValue { Label = option } }, new LocatorSelectOptionOptions { Timeout = actualTimeout });
                if (selected != null && selected.Count > 0) return $"Selected native option: {option}";
                selected = await selectLoc.SelectOptionAsync(new[] { new SelectOptionValue { Value = option } }, new LocatorSelectOptionOptions { Timeout = actualTimeout });
                return $"Selected native option by value: {option}";
            }
        }
        catch { }

        try
        {
            // 1. Scroll trigger into view, wait for stability, and click to open dropdown
            var trigger = await ResolveTargetLocatorAsync(page, selector, frameSelector: frameSelector);
            await trigger.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = actualTimeout });
            await WaitForElementStabilityAsync(trigger, animationTimeoutMs, waitForStable);
            await trigger.ClickAsync(new LocatorClickOptions { Timeout = actualTimeout });

            // 2. Wait a moment for dropdown portal/popover to mount
            await page.WaitForTimeoutAsync(300);

            // 3. Auto-detect active floating portal / dropdown container
            string[] knownPortalSelectors = new[]
            {
                ".ant-select-dropdown:not(.ant-select-dropdown-hidden)",
                ".MuiAutocomplete-popper",
                ".MuiPopover-root:not([aria-hidden='true'])",
                "[data-radix-popper-content-wrapper]",
                "[class*='-menu']", // React-Select
                "dialog[open]",
                "[role='listbox']",
                ".rc-virtual-list"
            };

            ILocator? activePortal = null;
            if (!string.IsNullOrWhiteSpace(dropdownSelector))
            {
                var customPortal = page.Locator(dropdownSelector);
                if (await customPortal.CountAsync() > 0)
                {
                    activePortal = customPortal.First;
                }
            }
            else
            {
                foreach (var portalSel in knownPortalSelectors)
                {
                    var portalLoc = page.Locator(portalSel);
                    var count = await portalLoc.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var p = portalLoc.Nth(i);
                        if (await p.IsVisibleAsync())
                        {
                            activePortal = p;
                            break;
                        }
                    }
                    if (activePortal != null) break;
                }
            }

            // 4. If a search input selector is provided or an input exists inside trigger/portal, type to filter
            if (!string.IsNullOrWhiteSpace(searchInputSelector))
            {
                var searchInput = page.Locator(searchInputSelector);
                await searchInput.FillAsync(option);
                await page.WaitForTimeoutAsync(300);
            }
            else if (activePortal != null)
            {
                var portalInput = activePortal.Locator("input:not([type='hidden'])");
                if (await portalInput.CountAsync() > 0 && await portalInput.First.IsVisibleAsync())
                {
                    try
                    {
                        await portalInput.First.FillAsync(option);
                        await page.WaitForTimeoutAsync(300);
                    }
                    catch { }
                }
            }
            else
            {
                // Check if there is an active/visible input inside trigger
                var innerInput = trigger.Locator("input:not([type='hidden'])");
                if (await innerInput.CountAsync() > 0 && await innerInput.First.IsVisibleAsync())
                {
                    try
                    {
                        await innerInput.First.FillAsync(option);
                        await page.WaitForTimeoutAsync(300);
                    }
                    catch { }
                }
            }

            // 5. Locate the option element - check inside active portal first!
            ILocator? matched = null;
            if (activePortal != null)
            {
                var portalOptionCandidates = new[]
                {
                    activePortal.Locator($"[role=\"option\"]:has-text(\"{option}\")"),
                    activePortal.Locator($".ant-select-item-option:has-text(\"{option}\")"),
                    activePortal.Locator($".rc-virtual-list [role=\"option\"]:has-text(\"{option}\")"),
                    activePortal.Locator($".MuiAutocomplete-option:has-text(\"{option}\")"),
                    activePortal.Locator($"li:has-text(\"{option}\")"),
                    activePortal.Locator($"div:has-text(\"{option}\")"),
                    activePortal.Locator($"text=\"{option}\"")
                };

                foreach (var cand in portalOptionCandidates)
                {
                    if (await cand.CountAsync() > 0)
                    {
                        for (int i = 0; i < await cand.CountAsync(); i++)
                        {
                            var item = cand.Nth(i);
                            if (await item.IsVisibleAsync())
                            {
                                matched = item;
                                break;
                            }
                        }
                        if (matched != null) break;
                    }
                }
            }

            // Fallback to frame-level or document-level candidates
            if (matched == null && !string.IsNullOrWhiteSpace(frameSelector))
            {
                var fLoc = page.FrameLocator(frameSelector);
                var frameOptionCandidates = new[]
                {
                    fLoc.Locator($"[role=\"option\"]:has-text(\"{option}\")"),
                    fLoc.Locator($".ant-select-item-option:has-text(\"{option}\")"),
                    fLoc.Locator($".rc-virtual-list [role=\"option\"]:has-text(\"{option}\")"),
                    fLoc.Locator($".MuiAutocomplete-option:has-text(\"{option}\")"),
                    fLoc.Locator($"li:has-text(\"{option}\")"),
                    fLoc.Locator($"option:has-text(\"{option}\")"),
                    fLoc.Locator($"text=\"{option}\"")
                };

                foreach (var cand in frameOptionCandidates)
                {
                    if (await cand.CountAsync() > 0)
                    {
                        for (int i = 0; i < await cand.CountAsync(); i++)
                        {
                            var item = cand.Nth(i);
                            if (await item.IsVisibleAsync())
                            {
                                matched = item;
                                break;
                            }
                        }
                        if (matched != null) break;
                    }
                }
            }

            // Fallback to document-level candidates if portal wasn't found or option wasn't found inside it
            if (matched == null)
            {
                var docOptionCandidates = new[]
                {
                    page.Locator($"[role=\"option\"]:has-text(\"{option}\")"),
                    page.Locator($".ant-select-item-option:has-text(\"{option}\")"),
                    page.Locator($".rc-virtual-list [role=\"option\"]:has-text(\"{option}\")"),
                    page.Locator($".MuiAutocomplete-option:has-text(\"{option}\")"),
                    page.Locator($"li:has-text(\"{option}\")"),
                    page.Locator($"text=\"{option}\"")
                };

                foreach (var cand in docOptionCandidates)
                {
                    if (await cand.CountAsync() > 0)
                    {
                        for (int i = 0; i < await cand.CountAsync(); i++)
                        {
                            var item = cand.Nth(i);
                            if (await item.IsVisibleAsync())
                            {
                                matched = item;
                                break;
                            }
                        }
                        if (matched != null) break;
                    }
                }
            }

            // Auto-pierce check for options inside any iframe if still not found
            if (matched == null)
            {
                try
                {
                    var iframeCount = await page.Locator("iframe").CountAsync();
                    for (int f = 0; f < iframeCount; f++)
                    {
                        var fLoc = page.FrameLocator($"iframe >> nth={f}");
                        var frameCands = new[]
                        {
                            fLoc.Locator($"[role=\"option\"]:has-text(\"{option}\")"),
                            fLoc.Locator($"li:has-text(\"{option}\")"),
                            fLoc.Locator($"text=\"{option}\"")
                        };
                        foreach (var cand in frameCands)
                        {
                            if (await cand.CountAsync() > 0)
                            {
                                for (int i = 0; i < await cand.CountAsync(); i++)
                                {
                                    var item = cand.Nth(i);
                                    if (await item.IsVisibleAsync())
                                    {
                                        matched = item;
                                        break;
                                    }
                                }
                                if (matched != null) break;
                            }
                        }
                        if (matched != null) break;
                    }
                }
                catch { }
            }

            if (matched != null)
            {
                await matched.ScrollIntoViewIfNeededAsync();
                await WaitForElementStabilityAsync(matched, animationTimeoutMs, waitForStable, actualTimeout);
                await ExecuteWithHttpMutationWaitAsync(page, async () =>
                {
                    try
                    {
                        await matched.ClickAsync(new LocatorClickOptions { Timeout = actualTimeout });
                    }
                    catch
                    {
                        // Fallback to force click if backdrop mask intercepts pointer events
                        await matched.ClickAsync(new LocatorClickOptions { Force = true, Timeout = actualTimeout });
                    }
                }, waitForHttpMutation, actualTimeout);

                return $"Successfully selected option '{option}' for dropdown '{selector}'";
            }

            // 5. If not found, collect available visible options to provide rich diagnostics!
            var diagJs = @"() => {
                const options = Array.from(document.querySelectorAll('[role=""option""], .ant-select-item-option-content, .MuiAutocomplete-option, li[role=""option""]'))
                    .filter(el => {
                        const rect = el.getBoundingClientRect();
                        return rect.width > 0 && rect.height > 0;
                    })
                    .map(el => (el.innerText || el.textContent || '').trim())
                    .filter(t => t.length > 0);
                return Array.from(new Set(options)).slice(0, 20);
            }";
            var visibleOptions = await page.EvaluateAsync<string[]>(diagJs);
            var optionsList = visibleOptions != null && visibleOptions.Length > 0
                ? string.Join(", ", visibleOptions.Select(o => $"\"{o}\""))
                : "<none detected>";

            return $"Error: Option '{option}' not found in dropdown. Available visible options in DOM: [{optionsList}]";
        }
        catch (Exception ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "SelectOption", ex);
            return $"Error selecting option: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Uploads a file to an <input type=\"file\"> element or upload trigger. Automatically handles hidden inputs and multi-element disambiguation. Timeout is in milliseconds (default is 10000). pageId is optional and defaults to active page.")]
    public static async Task<string> UploadFile(string? pageId = null, string? selector = null, string? filePath = null, float? timeoutMs = 10000, int? index = null, bool autoFirst = true)
    {
        EnsureManager();
        if (filePath == null && selector != null && pageId != null)
        {
            filePath = selector;
            selector = pageId;
            pageId = null;
        }
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        if (string.IsNullOrWhiteSpace(filePath)) return "Error: filePath is required";
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs ?? 10000f;

        if (!File.Exists(filePath))
        {
            return $"Error: File '{filePath}' does not exist on the filesystem.";
        }

        try
        {
            var targetLocator = await ResolveTargetLocatorAsync(page, selector, index, autoFirst);
            await targetLocator.SetInputFilesAsync(filePath, new LocatorSetInputFilesOptions { Timeout = actualTimeout });
            return $"Successfully uploaded file '{filePath}' to '{selector}'";
        }
        catch (System.TimeoutException ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "UploadFile", ex);
            return $"Error: UploadFile timed out after {actualTimeout}ms for selector '{selector}'.\n{diag}";
        }
        catch (Exception ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "UploadFile", ex);
            return $"Error uploading file to '{selector}': {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Dismisses or closes active popups, cascading modal dialogs, sliding drawers, dropdown menus, and cookie consent banners (OneTrust, Cookiebot, etc.). Can target 'topmost', 'all', or 'consent'. Dispatches Escape, clicks close/accept buttons (.ant-drawer-close, .ant-modal-close, #onetrust-accept-btn-handler, etc.), and waits for backdrop masks to settle. pageId is optional and defaults to active page.")]
    public static async Task<string> CloseOverlays(string? pageId = null, string target = "topmost", bool waitForSettle = true, float? timeoutMs = 5000)
    {
        EnsureManager();
        if (pageId == "topmost" || pageId == "all" || pageId == "consent")
        {
            target = pageId;
            pageId = null;
        }
        var page = _manager!.GetPage(pageId);
        float actualTimeout = timeoutMs ?? 5000f;

        try
        {
            var jsDismiss = @"async (params) => {
                const { targetMode, maxWait } = params;
                const overlayContainers = [
                    '.ant-drawer-open',
                    '.ant-drawer-content-wrapper',
                    '.ant-modal-wrap:not([style*=""display: none""])',
                    '.MuiDialog-root:not([aria-hidden=""true""])',
                    '.MuiDrawer-root:not([aria-hidden=""true""])',
                    '.MuiPopover-root:not([aria-hidden=""true""])',
                    '[data-radix-popper-content-wrapper]',
                    'dialog[open]'
                ];

                const closeButtonSelectors = [
                    '.ant-drawer-close',
                    '.ant-modal-close',
                    '.ant-notification-notice-close',
                    'button[aria-label*=""close"" i]',
                    'button[aria-label*=""Close"" i]',
                    'button[title*=""close"" i]',
                    'button[title*=""Close"" i]',
                    '.MuiDialogTitle-root button',
                    '.MuiModal-root [aria-label=""close""]',
                    '.close-btn',
                    '.btn-close'
                ];

                const consentSelectors = [
                    '#onetrust-accept-btn-handler',
                    '.cc-btn.cc-dismiss',
                    'button[id*=""cookie"" i][id*=""accept"" i]',
                    'button[class*=""cookie"" i][class*=""accept"" i]',
                    'a[id*=""cookie"" i][id*=""accept"" i]',
                    'a[class*=""cookie"" i][class*=""accept"" i]',
                    '[aria-label*=""accept all"" i]',
                    '[aria-label*=""allow all"" i]',
                    'button[id*=""consent"" i][id*=""accept"" i]',
                    '#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll',
                    '#CybotCookiebotDialogBodyButtonAccept',
                    '.osano-cm-accept-all',
                    '#didomi-notice-agree-button'
                ];

                const consentContainers = [
                    '#onetrust-banner-sdk',
                    '#onetrust-consent-sdk',
                    '.cc-window',
                    '#CookiebotDialog',
                    '#CybotCookiebotDialog',
                    '.osano-cm-window',
                    '#didomi-notice',
                    '[class*=""cookie-banner"" i]',
                    '[id*=""cookie-banner"" i]',
                    '[class*=""consent-banner"" i]',
                    '[id*=""consent-banner"" i]'
                ];

                let closedCount = 0;

                // Handle explicit consent banner bypass
                if (targetMode === 'consent') {
                    for (const sel of consentSelectors) {
                        for (const btn of Array.from(document.querySelectorAll(sel))) {
                            const rect = btn.getBoundingClientRect();
                            const style = window.getComputedStyle(btn);
                            if (rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden') {
                                btn.click();
                                closedCount++;
                                break;
                            }
                        }
                    }
                    for (const cSel of consentContainers) {
                        for (const cEl of Array.from(document.querySelectorAll(cSel))) {
                            cEl.style.display = 'none';
                        }
                    }
                    return { closedCount, targetMode };
                }

                // If targetMode is 'all' or 'topmost', also check for and dismiss consent banners
                for (const sel of consentSelectors) {
                    for (const btn of Array.from(document.querySelectorAll(sel))) {
                        const rect = btn.getBoundingClientRect();
                        const style = window.getComputedStyle(btn);
                        if (rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden') {
                            btn.click();
                            closedCount++;
                            break;
                        }
                    }
                }

                const maxPasses = targetMode === 'all' ? 5 : 1;

                for (let pass = 0; pass < maxPasses; pass++) {
                    // Check active overlays
                    const activeOverlays = [];
                    for (const sel of overlayContainers) {
                        for (const el of Array.from(document.querySelectorAll(sel))) {
                            const rect = el.getBoundingClientRect();
                            const style = window.getComputedStyle(el);
                            if (rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden') {
                                const z = parseInt(style.zIndex, 10) || 0;
                                activeOverlays.push({ el, z });
                            }
                        }
                    }

                    // Check dropdowns / menus
                    const activeDropdowns = Array.from(document.querySelectorAll('.ant-select-dropdown:not(.ant-select-dropdown-hidden), .MuiAutocomplete-popper, [role=""listbox""], [role=""menu""]'))
                        .filter(el => {
                            const rect = el.getBoundingClientRect();
                            const style = window.getComputedStyle(el);
                            return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                        });

                    if (activeOverlays.length === 0 && activeDropdowns.length === 0) {
                        break;
                    }

                    // Sort overlays by z-index descending (topmost first)
                    activeOverlays.sort((a, b) => b.z - a.z);

                    let clicked = false;
                    if (activeOverlays.length > 0) {
                        const topOverlay = activeOverlays[0].el;
                        for (const btnSel of closeButtonSelectors) {
                            const btn = topOverlay.querySelector(btnSel);
                            if (btn && btn.offsetParent !== null) {
                                btn.click();
                                clicked = true;
                                closedCount++;
                                break;
                            }
                        }
                    }

                    // If no close button clicked, send Escape event
                    if (!clicked) {
                        const escEvtDown = new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true, cancelable: true });
                        const escEvtUp = new KeyboardEvent('keyup', { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true, cancelable: true });
                        document.dispatchEvent(escEvtDown);
                        document.dispatchEvent(escEvtUp);
                        if (document.activeElement) {
                            document.activeElement.dispatchEvent(escEvtDown);
                            document.activeElement.dispatchEvent(escEvtUp);
                        }
                        closedCount++;
                    }

                    // Give brief settle time between passes
                    await new Promise(r => setTimeout(r, 150));
                    if (targetMode !== 'all') break;
                }

                // If masks exist, wait briefly for them to unmount or fade
                const startWait = Date.now();
                while (Date.now() - startWait < Math.min(maxWait, 1000)) {
                    const mask = document.querySelector('.ant-drawer-mask, .ant-modal-mask, .MuiBackdrop-root:not([aria-hidden=""true""])');
                    if (!mask || window.getComputedStyle(mask).display === 'none' || window.getComputedStyle(mask).opacity === '0') {
                        break;
                    }
                    await new Promise(r => setTimeout(r, 50));
                }

                return { closedCount, targetMode };
            }";

            var res = await page.EvaluateAsync<JsonElement>(jsDismiss, new { targetMode = target.ToLowerInvariant(), maxWait = (int)actualTimeout });
            
            // Also issue Playwright Keyboard Escape if needed for full browser window synchronization
            if (target.ToLowerInvariant() != "consent")
            {
                try
                {
                    await page.Keyboard.PressAsync("Escape");
                }
                catch { }
            }

            if (waitForSettle)
            {
                await page.WaitForTimeoutAsync(250);
            }

            int count = res.TryGetProperty("closedCount", out var c) ? c.GetInt32() : 1;
            return $"Success: Dismissed overlays (mode: '{target}', dismissed count: {count})";
        }
        catch (Exception ex)
        {
            return $"Error dismissing overlays: {ex.Message}";
        }
    }

    private record AutoScrollResult(int Scrolls, int TotalHeight);

    private static async Task<AutoScrollResult> RunAutoScrollAsync(IPage page, string? containerSelector = null, int stepPixels = 600, int delayMs = 150, int maxScrolls = 30)
    {
        var jsScroll = @"async ([sel, step, delay, maxSteps]) => {
            const target = sel ? document.querySelector(sel) : (document.scrollingElement || document.documentElement);
            if (!target) return { scrolls: 0, totalHeight: 0 };
            let prevH = 0, scrolls = 0;
            while (scrolls < maxSteps) {
                const curH = target.scrollHeight;
                target.scrollBy({ top: step, behavior: 'smooth' });
                scrolls++;
                await new Promise(r => setTimeout(r, delay));
                if ((target.scrollTop + target.clientHeight >= target.scrollHeight - 10) && target.scrollHeight === prevH) break;
                prevH = curH;
            }
            target.scrollTo({ top: 0, behavior: 'instant' });
            await new Promise(r => setTimeout(r, 100));
            return { scrolls, totalHeight: target.scrollHeight };
        };";

        try
        {
            var res = await page.EvaluateAsync<JsonElement>(jsScroll, new object?[] { containerSelector, stepPixels, delayMs, maxScrolls });
            int scrolls = res.TryGetProperty("scrolls", out var s) ? s.GetInt32() : 0;
            int h = res.TryGetProperty("totalHeight", out var th) ? th.GetInt32() : 0;
            return new AutoScrollResult(scrolls, h);
        }
        catch
        {
            return new AutoScrollResult(0, 0);
        }
    }

    private static string EscapeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    [McpServerTool, Description("Extracts structured tabular data from <table> elements, Ant Design tables (.ant-table), Material UI tables (.MuiTable-root), or ARIA grids ([role='table'], [role='grid']). Supports pagination loops via nextPageSelector (maxPages, pageDelayMs), extracting from iframes via frameSelector or automatic iframe piercing. Can export directly to CSV (RFC 4180) or JSON on disk via filePath, auto-scroll virtual tables prior to extraction (scrollFirst), and filter columns or rows. pageId is optional and defaults to active page.")]
    public static async Task<string> ExtractTableData(
        string? pageId = null, 
        string? tableSelector = null, 
        string[]? columns = null, 
        int maxRows = 25, 
        string? frameSelector = null,
        string? filePath = null,
        string format = "json",
        bool scrollFirst = false,
        int scrollDelayMs = 150,
        int maxScrolls = 30,
        string? nextPageSelector = null,
        int maxPages = 1,
        int pageDelayMs = 400)
    {
        EnsureManager();
        if (tableSelector == null && !string.IsNullOrWhiteSpace(pageId))
        {
            if (pageId.StartsWith("#") || pageId.StartsWith(".") || pageId.StartsWith("//") || pageId.StartsWith("[") || pageId.Contains("table") || pageId.Contains("grid"))
            {
                tableSelector = pageId;
                pageId = null;
            }
        }
        var page = _manager!.GetPage(pageId);

        try
        {
            var jsExtract = @"(params) => {
                const { selector, requestedCols, maxRows } = params;
                
                // 1. Resolve table element
                let tbl = null;
                if (selector && selector.trim().length > 0) {
                    tbl = document.querySelector(selector);
                } else {
                    tbl = document.querySelector('.ant-table table, .ant-table-content table, .MuiTable-root, [role=""table""], [role=""grid""], table')
                       || document.querySelector('.ant-table, .rc-table, [role=""grid""]');
                }

                if (!tbl) {
                    return { error: 'table_not_found', message: 'No table or grid element found on page.' };
                }

                // 2. Extract column headers
                let headerTexts = [];
                const thElements = Array.from(tbl.querySelectorAll('thead th, [role=""columnheader""], th'));
                if (thElements.length > 0) {
                    headerTexts = thElements.map(th => (th.innerText || th.textContent || '').trim().replace(/\s+/g, ' '));
                }

                // Filter out empty headers or deduplicate empty names
                headerTexts = headerTexts.map((h, i) => h.length > 0 ? h : 'Col_' + (i + 1));

                // 3. Extract table body rows
                let rowElements = Array.from(tbl.querySelectorAll('tbody tr:not(.ant-table-measure-row), tr:not(thead tr), [role=""row""]'));
                // Filter out header row if accidentally captured
                rowElements = rowElements.filter(r => !r.querySelector('th') || r.querySelector('td'));

                const rows = [];
                const limit = Math.min(rowElements.length, maxRows > 0 ? maxRows : 25);

                for (let r = 0; r < limit; r++) {
                    const tr = rowElements[r];
                    const cells = Array.from(tr.querySelectorAll('td, [role=""cell""], [role=""gridcell""]'));
                    if (cells.length === 0) continue;

                    const rowObj = {};
                    for (let c = 0; c < cells.length; c++) {
                        const colName = c < headerTexts.length ? headerTexts[c] : 'Col_' + (c + 1);
                        
                        // Check if column is in requested column filter
                        if (requestedCols && requestedCols.length > 0) {
                            const isRequested = requestedCols.some(rc => rc.trim().toLowerCase() === colName.toLowerCase());
                            if (!isRequested) continue;
                        }

                        // Extract cell clean text content
                        const cell = cells[c];
                        const text = (cell.innerText || cell.textContent || '').trim().replace(/\s+/g, ' ');
                        rowObj[colName] = text;
                    }
                    rows.push(rowObj);
                }

                return {
                    headers: headerTexts,
                    totalRowsInDom: rowElements.length,
                    returnedRows: rows.length,
                    data: rows
                };
            }";

            int targetMaxPages = (!string.IsNullOrWhiteSpace(nextPageSelector) && maxPages > 1) ? maxPages : 1;
            var allDataRows = new List<Dictionary<string, string>>();
            var headersList = new List<string>();
            int totalDomRowsAcrossPages = 0;
            int pagesScraped = 0;

            bool isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrWhiteSpace(filePath) && filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            bool isCsvStreaming = isCsv && !string.IsNullOrWhiteSpace(filePath);
            StreamWriter? csvStreamWriter = null;
            int totalStreamedRows = 0;

            try
            {
                for (int pageIdx = 0; pageIdx < targetMaxPages; pageIdx++)
                {
                    pagesScraped++;
                    if (scrollFirst)
                    {
                        await RunAutoScrollAsync(page, tableSelector, 600, scrollDelayMs, maxScrolls);
                    }

                    int currentCollected = isCsvStreaming ? totalStreamedRows : allDataRows.Count;
                    int remainingRowsNeeded = maxRows > 0 ? (maxRows - currentCollected) : 50;
                    if (remainingRowsNeeded <= 0) break;

                    var extractParams = new {
                        selector = tableSelector,
                        requestedCols = columns,
                        maxRows = remainingRowsNeeded
                    };

                    JsonElement pageResult;

                    if (!string.IsNullOrWhiteSpace(frameSelector))
                    {
                        var frameLoc = page.FrameLocator(frameSelector);
                        pageResult = await frameLoc.Locator(":root").EvaluateAsync<JsonElement>(jsExtract, extractParams);
                    }
                    else
                    {
                        pageResult = await page.EvaluateAsync<JsonElement>(jsExtract, extractParams);

                        // Auto-pierce: if table was not found in top document, search inside available iframes
                        if (pageResult.TryGetProperty("error", out var errVal) && errVal.GetString() == "table_not_found")
                        {
                            try
                            {
                                var iframeCount = await page.Locator("iframe").CountAsync();
                                for (int f = 0; f < iframeCount; f++)
                                {
                                    var fLoc = page.FrameLocator($"iframe >> nth={f}");
                                    var frameRes = await fLoc.Locator(":root").EvaluateAsync<JsonElement>(jsExtract, extractParams);
                                    if (!frameRes.TryGetProperty("error", out _))
                                    {
                                        pageResult = frameRes;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (pageResult.TryGetProperty("error", out var err) && err.GetString() == "table_not_found")
                    {
                        if (pageIdx == 0)
                        {
                            var diag = await DiagnoseSelectorFailureAsync(page, tableSelector ?? "table", "ExtractTableData", new Exception("Table element not found"));
                            return $"Error: No table found on page matching '{tableSelector ?? "table"}'.\n{diag}";
                        }
                        break;
                    }

                    if (headersList.Count == 0 && pageResult.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var h in headersProp.EnumerateArray())
                        {
                            headersList.Add(h.GetString() ?? "");
                        }
                        if (columns != null && columns.Length > 0)
                        {
                            var filteredHeaders = columns.Where(c => headersList.Any(h => string.Equals(h, c, StringComparison.OrdinalIgnoreCase))).ToList();
                            if (filteredHeaders.Count > 0)
                            {
                                headersList = filteredHeaders;
                            }
                        }

                        if (isCsvStreaming && csvStreamWriter == null)
                        {
                            var dir = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            csvStreamWriter = new StreamWriter(filePath!, append: false, Encoding.UTF8);
                            await csvStreamWriter.WriteLineAsync(string.Join(",", headersList.Select(EscapeCsvCell)));
                            await csvStreamWriter.FlushAsync();
                        }
                    }

                    if (pageResult.TryGetProperty("totalRowsInDom", out var trProp))
                    {
                        totalDomRowsAcrossPages += trProp.GetInt32();
                    }

                    if (pageResult.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rowEl in dataProp.EnumerateArray())
                        {
                            var rowDict = new Dictionary<string, string>();
                            foreach (var prop in rowEl.EnumerateObject())
                            {
                                rowDict[prop.Name] = prop.Value.GetString() ?? "";
                            }

                            if (isCsvStreaming)
                            {
                                var rowValues = new List<string>();
                                foreach (var h in headersList)
                                {
                                    rowDict.TryGetValue(h, out var val);
                                    rowValues.Add(EscapeCsvCell(val ?? ""));
                                }
                                await csvStreamWriter!.WriteLineAsync(string.Join(",", rowValues));
                                totalStreamedRows++;
                                if (maxRows > 0 && totalStreamedRows >= maxRows) break;
                            }
                            else
                            {
                                allDataRows.Add(rowDict);
                                if (maxRows > 0 && allDataRows.Count >= maxRows) break;
                            }
                        }

                        if (isCsvStreaming && csvStreamWriter != null)
                        {
                            await csvStreamWriter.FlushAsync();
                        }
                    }

                    if (maxRows > 0 && (isCsvStreaming ? totalStreamedRows : allDataRows.Count) >= maxRows) break;

                    // Handle pagination click to advance to next page
                    if (pageIdx < targetMaxPages - 1 && !string.IsNullOrWhiteSpace(nextPageSelector))
                    {
                        var nextLoc = page.Locator(nextPageSelector).First;
                        var count = await nextLoc.CountAsync();
                        if (count == 0) break;
                        if (!await nextLoc.IsVisibleAsync()) break;
                        if (await nextLoc.IsDisabledAsync()) break;

                        var ariaDisabled = await nextLoc.GetAttributeAsync("aria-disabled");
                        if (string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase)) break;

                        var classAttr = (await nextLoc.GetAttributeAsync("class")) ?? "";
                        if (classAttr.Contains("disabled", StringComparison.OrdinalIgnoreCase)) break;

                        try
                        {
                            await nextLoc.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        }
                        catch
                        {
                            break;
                        }

                        if (pageDelayMs > 0)
                        {
                            await page.WaitForTimeoutAsync(pageDelayMs);
                        }

                        try
                        {
                            await Task.WhenAny(
                                page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 2000 }),
                                Task.Delay(1000)
                            );
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                if (csvStreamWriter != null)
                {
                    await csvStreamWriter.DisposeAsync();
                    csvStreamWriter = null;
                }
            }

            if (isCsvStreaming)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "success",
                    filePath = Path.GetFullPath(filePath!),
                    fileSize = new FileInfo(filePath!).Length,
                    format = "csv",
                    rowCount = totalStreamedRows,
                    pagesScraped = pagesScraped,
                    columnCount = headersList.Count,
                    headers = headersList,
                    streaming = true
                });
            }

            if (isCsv)
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", headersList.Select(EscapeCsvCell)));

                foreach (var rowDict in allDataRows)
                {
                    var rowValues = new List<string>();
                    foreach (var h in headersList)
                    {
                        rowDict.TryGetValue(h, out var val);
                        rowValues.Add(EscapeCsvCell(val ?? ""));
                    }
                    sb.AppendLine(string.Join(",", rowValues));
                }

                string csvContent = sb.ToString();

                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllTextAsync(filePath, csvContent, Encoding.UTF8);

                    return JsonSerializer.Serialize(new
                    {
                        status = "success",
                        filePath = Path.GetFullPath(filePath),
                        fileSize = new FileInfo(filePath).Length,
                        format = "csv",
                        rowCount = allDataRows.Count,
                        pagesScraped = pagesScraped,
                        columnCount = headersList.Count,
                        headers = headersList
                    });
                }

                return csvContent;
            }

            var finalResult = new
            {
                headers = headersList,
                totalRowsInDom = totalDomRowsAcrossPages,
                returnedRows = allDataRows.Count,
                pagesScraped = pagesScraped,
                data = allDataRows
            };

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string jsonContent = JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, jsonContent, Encoding.UTF8);

                return JsonSerializer.Serialize(new
                {
                    status = "success",
                    filePath = Path.GetFullPath(filePath),
                    fileSize = new FileInfo(filePath).Length,
                    format = "json",
                    rowCount = allDataRows.Count,
                    pagesScraped = pagesScraped,
                    columnCount = headersList.Count,
                    headers = headersList
                });
            }

            return JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, tableSelector ?? "table", "ExtractTableData", ex);
            return $"Error extracting table data: {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Executes arbitrary JavaScript in the browser page and returns the result as a JSON string. Use this to run batched logic locally without multiple round trips. pageId is optional and defaults to active page.")]
    public static async Task<string> EvaluateScript(string? pageId = null, string? script = null)
    {
        EnsureManager();
        if (script == null && pageId != null)
        {
            script = pageId;
            pageId = null;
        }
        if (string.IsNullOrWhiteSpace(script)) return JsonSerializer.Serialize(new { error = "script is required" });
        var page = _manager!.GetPage(pageId);
        try 
        {
            var result = await page.EvaluateAsync<object>(script);
            return JsonSerializer.Serialize(result);
        }
        catch(Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool, Description("Takes a screenshot of the page and returns a base64-encoded PNG. Use GetAccessibilitySnapshot instead. Do not call this unless necessary as it takes more tokens. pageId is optional and defaults to active page.")]
    public static async Task<string> Screenshot(string? pageId = null)
    {
        EnsureManager();
        try
        {
            var bytes = await _manager!.ScreenshotAsync(pageId);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Takes a screenshot of a specific element region by selector and returns a base64-encoded PNG. If multiple elements match, specify an optional index (e.g. 0), or it automatically defaults to the first visible element to avoid strict mode errors. pageId is optional and defaults to active page.")]
    public static async Task<string> ScreenshotRegion(string? pageId = null, string? selector = null, int? index = null)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        var page = _manager!.GetPage(pageId);
        var locator = page.Locator(selector);
        try
        {
            if (index.HasValue)
            {
                locator = locator.Nth(index.Value);
            }
            else
            {
                // Auto-scope to the first visible element if multiple exist to prevent strict mode violation
                try
                {
                    var count = await locator.CountAsync();
                    if (count > 1)
                    {
                        ILocator targetLocator = locator.First;
                        for (int i = 0; i < count; i++)
                        {
                            var item = locator.Nth(i);
                            if (await item.IsVisibleAsync())
                            {
                                targetLocator = item;
                                break;
                            }
                        }
                        locator = targetLocator;
                    }
                }
                catch { }
            }

            // Force scroll to trigger lazy loading and calculate actual dimensions before screenshotting
            await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 10000 });
            var bytes = await locator.ScreenshotAsync(new LocatorScreenshotOptions { Timeout = 15000 });
            return Convert.ToBase64String(bytes);
        }
        catch (System.TimeoutException)
        {
            return $"Error: Screenshot failed: Timed out waiting for selector '{selector}' to become visible. Ensure it is rendered and not obscured.";
        }
        catch (System.Exception ex)
        {
            if (ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var firstLoc = page.Locator(selector).First;
                    await firstLoc.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 });
                    var fallbackBytes = await firstLoc.ScreenshotAsync(new LocatorScreenshotOptions { Timeout = 10000 });
                    return Convert.ToBase64String(fallbackBytes);
                }
                catch { }
            }
            return $"Error: Screenshot failed for selector '{selector}'. The element might be hidden, 0x0 size, or missing entirely. Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Waits for a specific selector to appear on the page. Timeout is in milliseconds (default is 30000). pageId is optional and defaults to active page.")]
    public static async Task<string> WaitForSelector(string? pageId = null, string? selector = null, float? timeout = null)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        if (string.IsNullOrWhiteSpace(selector)) return "Error: selector is required";
        var page = _manager!.GetPage(pageId);
        var options = new PageWaitForSelectorOptions();
        if (timeout.HasValue) options.Timeout = timeout.Value;
        
        try
        {
            await page.WaitForSelectorAsync(selector, options);
            return "Success";
        }
        catch (System.TimeoutException ex)
        {
            float actualTimeout = timeout ?? 30000f;
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "WaitForSelector", ex);
            return $"Error: Selector '{selector}' timed out after {actualTimeout}ms. Ensure the selector is correct or the page has fully loaded.\n{diag}";
        }
        catch (System.Exception ex)
        {
            var diag = await DiagnoseSelectorFailureAsync(page, selector, "WaitForSelector", ex);
            return $"Error: Failed waiting for selector '{selector}': {ex.Message}\n{diag}";
        }
    }

    [McpServerTool, Description("Waits for the page to reach a specific load state: 'load', 'domcontentloaded', or 'networkidle' (default 'load'). Timeout is in milliseconds. pageId is optional and defaults to active page.")]
    public static async Task<string> WaitForLoadState(string? pageId = null, string state = "load", float? timeout = null)
    {
        EnsureManager();
        if (pageId == "load" || pageId == "domcontentloaded" || pageId == "networkidle")
        {
            state = pageId;
            pageId = null;
        }
        try 
        {
            var page = _manager!.GetPage(pageId);
            var loadState = state.ToLowerInvariant() switch
            {
                "domcontentloaded" => LoadState.DOMContentLoaded,
                "networkidle" => LoadState.NetworkIdle,
                _ => LoadState.Load
            };
            var options = new PageWaitForLoadStateOptions();
            if (timeout.HasValue) options.Timeout = timeout.Value;
            await page.WaitForLoadStateAsync(loadState, options);
            return "Success";
        } 
        catch (Exception ex) 
        {
            return $"Error: {ex.Message}";
        }
    }

    // --- Investigator-style capture tools ---

    // Thread-safe storage for captured data per page
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<CapturedConsoleLog>> _consoleLogs = new();
    // Chunked HTML/content storage: pageId -> (pageIndex -> chunk)
    private const int DefaultContentChunkSize = 512 * 2 * 1024; // 512 * 2 KB per chunk
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _contentPages = new();
    // DOM Diffing state
    private static readonly ConcurrentDictionary<string, string> _lastAriaSnapshots = new();
    // Visual Diffing state: cacheKey -> (base64, hash)
    private static readonly ConcurrentDictionary<string, VisualSnapshotRecord> _lastVisualSnapshots = new();
    // Network transactions: pageId -> (transactionId -> transaction)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NetworkTransaction>> _networkTransactions = new();
    // Preserve order of transaction ids per page
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _networkOrder = new();
    // map request object to transaction id for pairing
    private static readonly ConditionalWeakTable<object, string> _requestToTransactionId = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _sources = new(); // pageId -> (url -> base64)

    // Keep event handler references so we can unsubscribe
    private static readonly ConcurrentDictionary<string, EventHandler<Microsoft.Playwright.IConsoleMessage>> _consoleHandlers = new();
    private static readonly ConcurrentDictionary<string, EventHandler<Microsoft.Playwright.IRequest>> _requestHandlers = new();
    private static readonly ConcurrentDictionary<string, EventHandler<Microsoft.Playwright.IResponse>> _responseHandlers = new();

    // Internal transaction DTOs
    private class NetworkSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public int? Status { get; set; }
        public string? StatusText { get; set; }
        public long RequestTimestamp { get; set; }
        public long? ResponseTimestamp { get; set; }
        public string? RedirectedFromId { get; set; }
        public string? RedirectedToId { get; set; }
    }

    public class CapturedConsoleLog
    {
        public string? type { get; set; }
        public string? text { get; set; }
        public string? location { get; set; }
        public long timestamp { get; set; }
    }

    private class VisualSnapshotRecord
    {
        public string Base64 { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    private class NetworkTransaction
    {
        public string Id { get; set; } = string.Empty;
        // request side
        public string Url { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public Dictionary<string, string>? RequestHeaders { get; set; }
        public string? RequestBodyBase64 { get; set; }
        public long RequestTimestamp { get; set; }
        // response side
        public int? Status { get; set; }
        public string? StatusText { get; set; }
        public Dictionary<string, string>? ResponseHeaders { get; set; }
        public string? ResponseBodyBase64 { get; set; }
        public long? ResponseTimestamp { get; set; }
        public string? RedirectedFromId { get; set; }
        public string? RedirectedToId { get; set; }
    }

    [McpServerTool, Description("Start capturing console messages for the given page. pageId is optional and defaults to active page.")]
    public static void StartConsoleCapture(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        _consoleLogs.TryAdd(pageId, new ConcurrentQueue<CapturedConsoleLog>());
        if (_consoleHandlers.ContainsKey(pageId)) return; // already capturing

        EventHandler<Microsoft.Playwright.IConsoleMessage> handler = (s, msg) =>
        {
            var item = new CapturedConsoleLog
            {
                type = msg.Type,
                text = msg.Text,
                location = msg.Location?.ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (_consoleLogs.TryGetValue(pageId, out var q))
            {
                q.Enqueue(item);
                while (q.Count > MaxConsoleLogCapacity && q.TryDequeue(out _)) { }
            }
        };

        _consoleHandlers[pageId] = handler;
        page.Console += handler;
    }

    [McpServerTool, Description("Stop capturing console messages for the given page. pageId is optional and defaults to active page.")]
    public static void StopConsoleCapture(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        if (_consoleHandlers.TryRemove(pageId, out var handler))
        {
            page.Console -= handler;
        }
    }

    [McpServerTool, Description("Get captured console messages for the page as JSON array. Supports optional filtering by textPattern (regex or substring) and level (log, info, warn, error, debug, etc.). pageId is optional and defaults to active page.")]
    public static string GetConsoleMessages(string? pageId = null, string? textPattern = null, string? level = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        if (!_consoleLogs.TryGetValue(pageId, out var q)) return "[]";
        IEnumerable<CapturedConsoleLog> items = q.ToArray();

        if (!string.IsNullOrWhiteSpace(level))
        {
            var lvl = level.Trim();
            items = items.Where(x => string.Equals(x.type, lvl, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(textPattern))
        {
            Regex? regex = null;
            try
            {
                regex = new Regex(textPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // If invalid regex, fallback to substring
            }

            if (regex != null)
            {
                items = items.Where(x => x.text != null && (regex.IsMatch(x.text) || x.text.Contains(textPattern, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                items = items.Where(x => x.text != null && x.text.Contains(textPattern, StringComparison.OrdinalIgnoreCase));
            }
        }

        return JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("Clear captured console messages for the page. pageId is optional and defaults to active page.")]
    public static void ClearConsoleMessages(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        _consoleLogs[pageId] = new ConcurrentQueue<CapturedConsoleLog>();
    }

    [McpServerTool, Description("Start capturing network requests/responses for the given page. Captured responses will include bodies (base64) when available. pageId is optional and defaults to active page.")]
    public static void StartNetworkCapture(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        _networkTransactions.TryAdd(pageId, new ConcurrentDictionary<string, NetworkTransaction>());
        _networkOrder.TryAdd(pageId, new ConcurrentQueue<string>());
        _sources.TryAdd(pageId, new ConcurrentDictionary<string, string>());
        if (_requestHandlers.ContainsKey(pageId) || _responseHandlers.ContainsKey(pageId)) return;

        EventHandler<Microsoft.Playwright.IRequest> reqHandler = async (s, req) =>
        {
            string? bodyB64 = null;
            try
            {
                // Try to read post data if available. Playwright may expose PostDataAsync or PostData.
                try
                {
                    // Preferred: async API
                    var postDataMethod = req.GetType().GetMethod("PostDataAsync");
                    if (postDataMethod != null)
                    {
                        var task = (System.Threading.Tasks.Task)postDataMethod.Invoke(req, null)!;
                        await task.ConfigureAwait(false);
                        // Get Result property from Task<string>
                        var resultProp = task.GetType().GetProperty("Result");
                        var post = resultProp?.GetValue(task) as string;
                        if (!string.IsNullOrEmpty(post)) bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(post));
                    }
                    else
                    {
                        // Fallback: PostData property
                        var postDataProp = req.GetType().GetProperty("PostData");
                        if (postDataProp != null)
                        {
                            var post = postDataProp.GetValue(req) as string;
                            if (!string.IsNullOrEmpty(post)) bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(post));
                        }
                    }
                }
                catch { /* ignore reflection/read errors */ }
            }
            catch { /* swallow */ }

            // create a transaction and store request metadata
            var id = Guid.NewGuid().ToString();
            string? redirectedFromTxId = null;
            if (req.RedirectedFrom != null)
            {
                _requestToTransactionId.TryGetValue(req.RedirectedFrom, out redirectedFromTxId);
            }

            var tx = new NetworkTransaction
            {
                Id = id,
                Url = req.Url,
                Method = req.Method,
                ResourceType = req.ResourceType,
                RequestBodyBase64 = bodyB64,
                RequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RedirectedFromId = redirectedFromTxId
            };

            if (redirectedFromTxId != null)
            {
                _networkTransactions.TryGetValue(pageId, out var pMap);
                if (pMap != null && pMap.TryGetValue(redirectedFromTxId, out var parentTx))
                {
                    parentTx.RedirectedToId = id;
                }
            }

            // try to capture headers
            try
            {
                var headersProp = req.GetType().GetProperty("Headers");
                if (headersProp != null)
                {
                    var headersObj = headersProp.GetValue(req);
                    if (headersObj is IEnumerable<KeyValuePair<string, string>> kvs)
                        tx.RequestHeaders = kvs.ToDictionary(k => k.Key, v => v.Value);
                    else
                    {
                        // try to reflect into IDictionary<string,string>
                        var dict = new Dictionary<string, string>();
                        foreach (var p in (headersObj as System.Collections.IEnumerable) ?? Array.Empty<object>())
                        {
                            try
                            {
                                var k = p.GetType().GetProperty("Key")?.GetValue(p)?.ToString();
                                var v = p.GetType().GetProperty("Value")?.GetValue(p)?.ToString();
                                if (k != null) dict[k] = v ?? string.Empty;
                            }
                            catch { }
                        }
                        if (dict.Count > 0) tx.RequestHeaders = dict;
                    }
                }
            }
            catch { }

            _networkTransactions.TryGetValue(pageId, out var map);
            map ??= _networkTransactions.GetOrAdd(pageId, new ConcurrentDictionary<string, NetworkTransaction>());
            map[id] = tx;
            _networkOrder.TryGetValue(pageId, out var orderQ);
            orderQ ??= _networkOrder.GetOrAdd(pageId, new ConcurrentQueue<string>());
            orderQ.Enqueue(id);
            while (orderQ.Count > MaxNetworkTransactionCapacity && orderQ.TryDequeue(out var oldTxId))
            {
                if (map != null) map.TryRemove(oldTxId, out _);
            }

            // keep map from request object to transaction id for pairing on response
            try { _requestToTransactionId.AddOrUpdate(req, id); } catch { }

            // If body captured, store as a source (use a pseudo-url key to avoid overwriting real responses)
            if (bodyB64 != null)
            {
                _sources.TryGetValue(pageId, out var sMap);
                sMap ??= _sources.GetOrAdd(pageId, new ConcurrentDictionary<string, string>());
                try { sMap[$"request:{req.Url}"] = bodyB64; } catch { }
            }
        };

        EventHandler<Microsoft.Playwright.IResponse> respHandler = async (s, resp) =>
        {
            try
            {
                string? bodyB64 = null;
                // find matching transaction id via resp.Request
                string? txId = null;
                try
                {
                    var reqObj = resp.Request;
                    if (reqObj != null && _requestToTransactionId.TryGetValue(reqObj, out var id)) txId = id;
                }
                catch { }

                if (txId != null)
                {
                    _networkTransactions.TryGetValue(pageId, out var map);
                    if (map != null && map.TryGetValue(txId, out var tx))
                    {
                        tx.Status = resp.Status;
                        tx.StatusText = resp.StatusText;
                        tx.ResponseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        byte[]? body = null;
                        try { body = await resp.BodyAsync(); } catch { /* ignore body read errors */ }
                        bodyB64 = body is null ? null : Convert.ToBase64String(body);
                        tx.ResponseBodyBase64 = bodyB64;
                        try
                        {
                            var headersProp = resp.GetType().GetProperty("Headers");
                            if (headersProp != null)
                            {
                                var headersObj = headersProp.GetValue(resp);
                                var dict = new Dictionary<string, string>();
                                foreach (var p in (headersObj as System.Collections.IEnumerable) ?? Array.Empty<object>())
                                {
                                    try
                                    {
                                        var k = p.GetType().GetProperty("Key")?.GetValue(p)?.ToString();
                                        var v = p.GetType().GetProperty("Value")?.GetValue(p)?.ToString();
                                        if (k != null) dict[k] = v ?? string.Empty;
                                    }
                                    catch { }
                                }
                                if (dict.Count > 0) tx.ResponseHeaders = dict;
                            }
                        }
                        catch { }

                        // store source body for investigator use (if not too large)
                        if (bodyB64 != null)
                        {
                            _sources.TryGetValue(pageId, out var sMap);
                            sMap ??= _sources.GetOrAdd(pageId, new ConcurrentDictionary<string, string>());
                            sMap[resp.Url] = bodyB64;
                        }
                    }
                }
                else
                {
                    // no associated request found; create a loose transaction
                    var id = Guid.NewGuid().ToString();
                    var tx = new NetworkTransaction
                    {
                        Id = id,
                        Url = resp.Url,
                        RequestTimestamp = 0,
                        ResponseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Status = resp.Status,
                        StatusText = resp.StatusText,
                        ResponseBodyBase64 = bodyB64
                    };
                    _networkTransactions.TryGetValue(pageId, out var map);
                    map ??= _networkTransactions.GetOrAdd(pageId, new ConcurrentDictionary<string, NetworkTransaction>());
                    map[id] = tx;
                    _networkOrder.TryGetValue(pageId, out var orderQ);
                    orderQ ??= _networkOrder.GetOrAdd(pageId, new ConcurrentQueue<string>());
                    orderQ.Enqueue(id);
                    if (bodyB64 != null)
                    {
                        _sources.TryGetValue(pageId, out var sMap);
                        sMap ??= _sources.GetOrAdd(pageId, new ConcurrentDictionary<string, string>());
                        sMap[resp.Url] = bodyB64;
                    }
                }
            }
            catch { /* swallow */ }
        };

        _requestHandlers[pageId] = reqHandler;
        _responseHandlers[pageId] = respHandler;
        page.Request += reqHandler;
        page.Response += respHandler;
    }

    [McpServerTool, Description("Stop capturing network for the given page. pageId is optional and defaults to active page.")]
    public static void StopNetworkCapture(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        if (_requestHandlers.TryRemove(pageId, out var r)) page.Request -= r;
        if (_responseHandlers.TryRemove(pageId, out var s)) page.Response -= s;
    }

    [McpServerTool, Description("List captured network transactions (summaries) for the page as JSON array. Filter options: 'xhr_only', 'errors_only', or null for all. Set excludeStaticAssets=false to include images/fonts/css. Filter by domainFilter, urlPattern (regex/substring), minStatus/maxStatus (e.g. 400 to 599), or HTTP method (GET, POST, etc.). pageId is optional and defaults to active page.")]
    public static string ListNetworkTransactions(
        string? pageId = null,
        string? filter = null,
        bool excludeStaticAssets = true,
        string? domainFilter = null,
        string? urlPattern = null,
        int? minStatus = null,
        int? maxStatus = null,
        string? method = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "[]";
        _networkOrder.TryGetValue(pageId, out var orderQ);
        var ids = orderQ?.ToArray() ?? map.Keys.ToArray();
        var summaries = new List<NetworkSummary>();
        
        var normalizedFilter = filter?.ToLowerInvariant();

        Regex? urlRegex = null;
        if (!string.IsNullOrWhiteSpace(urlPattern))
        {
            try
            {
                urlRegex = new Regex(urlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
            }
            catch { }
        }

        foreach (var id in ids)
        {
            if (map.TryGetValue(id, out var tx))
            {
                var isAjax = string.Equals(tx.ResourceType, "xhr", StringComparison.OrdinalIgnoreCase) || 
                             string.Equals(tx.ResourceType, "fetch", StringComparison.OrdinalIgnoreCase);

                if (normalizedFilter == "xhr_only" && !isAjax) continue;
                if (normalizedFilter == "errors_only" && (tx.Status == null || tx.Status < 400)) continue;

                if (excludeStaticAssets)
                {
                    bool isStatic = string.Equals(tx.ResourceType, "image", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(tx.ResourceType, "stylesheet", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(tx.ResourceType, "font", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(tx.ResourceType, "media", StringComparison.OrdinalIgnoreCase);
                    if (isStatic) continue;
                }

                if (!string.IsNullOrWhiteSpace(domainFilter))
                {
                    if (tx.Url == null || !tx.Url.Contains(domainFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (!string.IsNullOrWhiteSpace(method))
                {
                    if (tx.Method == null || !string.Equals(tx.Method, method.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (minStatus.HasValue)
                {
                    if (tx.Status == null || tx.Status.Value < minStatus.Value) continue;
                }

                if (maxStatus.HasValue)
                {
                    if (tx.Status == null || tx.Status.Value > maxStatus.Value) continue;
                }

                if (!string.IsNullOrWhiteSpace(urlPattern))
                {
                    if (tx.Url == null) continue;
                    if (urlRegex != null)
                    {
                        if (!urlRegex.IsMatch(tx.Url) && !tx.Url.Contains(urlPattern, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else
                    {
                        if (!tx.Url.Contains(urlPattern, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                }

                summaries.Add(new NetworkSummary
                {
                    Id = id,
                    Url = tx.Url,
                    Method = tx.Method,
                    ResourceType = tx.ResourceType,
                    Status = tx.Status,
                    StatusText = tx.StatusText,
                    RequestTimestamp = tx.RequestTimestamp,
                    ResponseTimestamp = tx.ResponseTimestamp,
                    RedirectedFromId = tx.RedirectedFromId,
                    RedirectedToId = tx.RedirectedToId
                });
            }
        }
        return JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("Get detailed request+response for a captured network transaction by id. Returns JSON with headers and base64 bodies when available. pageId is optional and defaults to active page.")]
    public static string GetNetworkTransactionDetails(string? pageId = null, string? transactionId = null)
    {
        EnsureManager();
        if (string.IsNullOrEmpty(transactionId) && !string.IsNullOrEmpty(pageId) && pageId.Length > 20 && !pageId.StartsWith("page_"))
        {
            transactionId = pageId;
            pageId = null;
        }
        pageId = _manager!.ResolvePageId(pageId);
        if (string.IsNullOrEmpty(transactionId)) return "null";
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "null";
        if (!map.TryGetValue(transactionId, out var tx)) return "null";
        return JsonSerializer.Serialize(tx, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("Clear captured network entries for the page. pageId is optional and defaults to active page.")]
    public static void ClearNetworkEntries(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        _networkTransactions[pageId] = new ConcurrentDictionary<string, NetworkTransaction>();
        _networkOrder[pageId] = new ConcurrentQueue<string>();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
    }

    [McpServerTool, Description("List captured resource URLs for the page as JSON array. pageId is optional and defaults to active page.")]
    public static string ListCapturedSources(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        if (!_sources.TryGetValue(pageId, out var map)) return "[]";
        var arr = map.Keys.ToArray();
        return JsonSerializer.Serialize(arr);
    }

    [McpServerTool, Description("Get captured source/body by URL (base64). Returns null if not captured. pageId is optional and defaults to active page.")]
    public static string? GetCapturedSource(string? pageId = null, string? url = null)
    {
        EnsureManager();
        if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(pageId) && (pageId.Contains("://") || pageId.StartsWith("request:")))
        {
            url = pageId;
            pageId = null;
        }
        pageId = _manager!.ResolvePageId(pageId);
        if (string.IsNullOrEmpty(url)) return null;
        if (!_sources.TryGetValue(pageId, out var map)) return null;
        if (map.TryGetValue(url, out var bodyB64)) return bodyB64;
        return null;
    }

    [McpServerTool, Description("Clear captured sources for the page. pageId is optional and defaults to active page.")]
    public static void ClearCapturedSources(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        _sources[pageId] = new ConcurrentDictionary<string, string>();
    }

    [McpServerTool, Description("Get the Playwright accessibility snapshot (Aria Snapshot) for the page or scoped to a specific element selector. Returns a YAML string representation. Use this to understand page structure and element roles. Always use GetAccessibilitySnapshotDiff instead of this to detect changes in the DOM and save context. pageId is optional and defaults to active page.")]
    public static async Task<string> GetAccessibilitySnapshot(string? pageId = null, string? selector = null)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);

        try
        {
            pageId = _manager!.ResolvePageId(pageId);
            var page = _manager!.GetPage(pageId);
            var targetSelector = string.IsNullOrWhiteSpace(selector) ? "html" : selector;
            string ariaSnapshot = await page.Locator(targetSelector).AriaSnapshotAsync();

            string cacheKey = string.IsNullOrWhiteSpace(selector) ? pageId : $"{pageId}::{selector}";
            _lastAriaSnapshots[cacheKey] = ariaSnapshot;

            var payload = new { ariaSnapshot, scopedSelector = selector };
            var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            return JsonSerializer.Serialize(payload, opts);
        }
        catch (Exception ex)
        {
            IPage? page = null;
            try { page = _manager?.GetPage(pageId); } catch { }
            var diag = (!string.IsNullOrWhiteSpace(selector) && page != null) ? await DiagnoseSelectorFailureAsync(page, selector, "GetAccessibilitySnapshot", ex) : "";
            return JsonSerializer.Serialize(new { error = $"Accessibility Timeout or Error: {ex.Message}", diagnostics = diag });
        }
    }

    [McpServerTool, Description("Get a unified diff of the Accessibility Snapshot since the last time this tool or GetAccessibilitySnapshot was called. Can be scoped to a selector (e.g. modal, drawer, table) to save tokens. Set resetBaseline to true to clear any old state and just return the full raw snapshot. Use this instead of GetAccessibilitySnapshot to detect changes in the DOM and save context. pageId is optional and defaults to active page.")]
    public static async Task<string> GetAccessibilitySnapshotDiff(string? pageId = null, string? selector = null, bool resetBaseline = false)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);

        try
        {
            pageId = _manager!.ResolvePageId(pageId);
            var page = _manager!.GetPage(pageId);

            var targetSelector = string.IsNullOrWhiteSpace(selector) ? "body" : selector;
            var locator = page.Locator(targetSelector);
            string newSnapshot = await locator.AriaSnapshotAsync();

            string cacheKey = string.IsNullOrWhiteSpace(selector) ? pageId : $"{pageId}::{selector}";
            string? oldSnapshot = null;
            if (!resetBaseline && _lastAriaSnapshots.TryGetValue(cacheKey, out var existing))
            {
                oldSnapshot = existing;
            }

            _lastAriaSnapshots[cacheKey] = newSnapshot;

            if (string.IsNullOrEmpty(oldSnapshot))
            {
                var payload = new { status = "Initial State (No baseline to diff against)", diff = newSnapshot, scopedSelector = selector };
                var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                return JsonSerializer.Serialize(payload, opts);
            }

            var diff = InlineDiffBuilder.Diff(oldSnapshot, newSnapshot);
            var sb = new StringBuilder();
            bool hasChanges = false;
            foreach (var line in diff.Lines)
            {
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        sb.AppendLine($"+ {line.Text}");
                        hasChanges = true;
                        break;
                    case ChangeType.Deleted:
                        sb.AppendLine($"- {line.Text}");
                        hasChanges = true;
                        break;
                    case ChangeType.Modified:
                        sb.AppendLine($"* {line.Text}");
                        hasChanges = true;
                        break;
                    default:
                        break;
                }
            }

            if (!hasChanges)
            {
                var payload = new { status = "No DOM/Accessibility changes detected since last call.", diff = "", scopedSelector = selector };
                var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                return JsonSerializer.Serialize(payload, opts);
            }

            var resultPayload = new { status = "Differences found.", diff = sb.ToString(), scopedSelector = selector };
            return JsonSerializer.Serialize(resultPayload, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            IPage? page = null;
            try { page = _manager?.GetPage(pageId); } catch { }
            var diag = (!string.IsNullOrWhiteSpace(selector) && page != null) ? await DiagnoseSelectorFailureAsync(page, selector, "GetAccessibilitySnapshotDiff", ex) : "";
            return $"Error: Could not retrieve or compute accessibility diff: {ex.Message}\n{diag}".TrimEnd();
        }
    }

    [McpServerTool, Description("Performs visual diffing / element change detection on a selector (or the entire page/body). Compares current screenshot against previous state, returning hasChanged, changePercentage, and SHA-256 visual hashes without transferring large image payloads. pageId is optional and defaults to active page.")]
    public static async Task<string> ScreenshotRegionDiff(string? pageId = null, string? selector = null, int? index = null, bool resetBaseline = false)
    {
        EnsureManager();
        DisambiguatePageIdAndSelector(ref pageId, ref selector);
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);

        try
        {
            var targetSelector = string.IsNullOrWhiteSpace(selector) ? "body" : selector;
            var targetLocator = await ResolveTargetLocatorAsync(page, targetSelector, index, autoFirst: true);

            await targetLocator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 10000 });
            var bytes = await targetLocator.ScreenshotAsync(new LocatorScreenshotOptions { Timeout = 15000 });
            var newBase64 = Convert.ToBase64String(bytes);

            using var sha = SHA256.Create();
            var newHash = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();

            string cacheKey = string.IsNullOrWhiteSpace(selector) ? $"{pageId}::body::{index}" : $"{pageId}::{selector}::{index}";
            VisualSnapshotRecord? baseline = null;
            bool hasBaseline = !resetBaseline && _lastVisualSnapshots.TryGetValue(cacheKey, out baseline);
            _lastVisualSnapshots[cacheKey] = new VisualSnapshotRecord { Base64 = newBase64, Hash = newHash };

            if (!hasBaseline || baseline == null || string.IsNullOrEmpty(baseline.Base64))
            {
                var payload = new
                {
                    status = "Initial Baseline Captured",
                    hasChanged = false,
                    changePercentage = 0.0,
                    hash = newHash,
                    scopedSelector = selector,
                    index
                };
                return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            }

            if (string.Equals(baseline.Hash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    status = "No visual changes detected.",
                    hasChanged = false,
                    changePercentage = 0.0,
                    hash = newHash,
                    scopedSelector = selector,
                    index
                };
                return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            }

            // In-browser pixel-level comparison via HTML5 Canvas
            double changePct = 100.0;
            try
            {
                var diffJs = @"(params) => {
                    return new Promise((resolve) => {
                        const img1 = new Image();
                        const img2 = new Image();
                        let loaded = 0;
                        const onLoaded = () => {
                            loaded++;
                            if (loaded < 2) return;
                            try {
                                const w = Math.max(img1.width, img2.width);
                                const h = Math.max(img1.height, img2.height);
                                if (w === 0 || h === 0) return resolve(0.0);

                                const c1 = document.createElement('canvas');
                                c1.width = w; c1.height = h;
                                const ctx1 = c1.getContext('2d');
                                ctx1.drawImage(img1, 0, 0);

                                const c2 = document.createElement('canvas');
                                c2.width = w; c2.height = h;
                                const ctx2 = c2.getContext('2d');
                                ctx2.drawImage(img2, 0, 0);

                                const d1 = ctx1.getImageData(0, 0, w, h).data;
                                const d2 = ctx2.getImageData(0, 0, w, h).data;

                                let diffCount = 0;
                                const totalPixels = w * h;
                                for (let i = 0; i < d1.length; i += 4) {
                                    if (Math.abs(d1[i] - d2[i]) > 10 ||
                                        Math.abs(d1[i+1] - d2[i+1]) > 10 ||
                                        Math.abs(d1[i+2] - d2[i+2]) > 10 ||
                                        Math.abs(d1[i+3] - d2[i+3]) > 10) {
                                        diffCount++;
                                    }
                                }
                                const pct = (diffCount / totalPixels) * 100;
                                resolve(Math.round(pct * 100) / 100);
                            } catch (e) {
                                resolve(100.0);
                            }
                        };
                        img1.onload = onLoaded;
                        img1.onerror = () => resolve(100.0);
                        img2.onload = onLoaded;
                        img2.onerror = () => resolve(100.0);
                        img1.src = 'data:image/png;base64,' + params.b1;
                        img2.src = 'data:image/png;base64,' + params.b2;
                    });
                }";

                changePct = await page.EvaluateAsync<double>(diffJs, new { b1 = baseline.Base64, b2 = newBase64 });
            }
            catch { }

            var changeResult = new
            {
                status = "Visual difference detected.",
                hasChanged = true,
                changePercentage = changePct,
                previousHash = baseline.Hash,
                currentHash = newHash,
                scopedSelector = selector,
                index
            };
            return JsonSerializer.Serialize(changeResult, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            var diag = !string.IsNullOrWhiteSpace(selector) ? await DiagnoseSelectorFailureAsync(page, selector, "ScreenshotRegionDiff", ex) : "";
            return $"Error: Could not calculate screenshot diff: {ex.Message}\n{diag}".TrimEnd();
        }
    }

    [McpServerTool, Description("Clear all captured investigator data (console, network, sources, visual snapshots) for a page. pageId is optional and defaults to active page.")]
    public static void ClearAllCapturedData(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        _consoleLogs[pageId] = new ConcurrentQueue<CapturedConsoleLog>();
        _networkTransactions[pageId] = new ConcurrentDictionary<string, NetworkTransaction>();
        _networkOrder[pageId] = new ConcurrentQueue<string>();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
        _contentPages[pageId] = new ConcurrentDictionary<int, string>();
        _lastAriaSnapshots.TryRemove(pageId, out _);

        var keysToRemove = _lastVisualSnapshots.Keys.Where(k => k.StartsWith($"{pageId}::") || k == pageId).ToList();
        foreach (var k in keysToRemove)
        {
            _lastVisualSnapshots.TryRemove(k, out _);
        }
    }

    [McpServerTool, Description("List known context ids as JSON array. Useful to discover other contexts created by the manager.")]
    public static string ListContexts()
    {
        EnsureManager();
        try
        {
            var contexts = _manager!.ListContexts();
            return JsonSerializer.Serialize(contexts);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not list contexts: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("List open pages with their pageId, title, and url as a JSON array. If contextId is omitted or null, returns all pages across all active contexts.")]
    public static async Task<string> ListPages(string? contextId = null)
    {
        EnsureManager();
        try
        {
            var pages = await _manager!.ListPagesDetailsAsync(contextId);

            if (_relayServer != null)
            {
                var tabs = _relayServer.AttachedTabs.Values.ToList();
                for (int i = 0; i < pages.Count; i++)
                {
                    if (string.IsNullOrEmpty(pages[i].Title) || string.IsNullOrEmpty(pages[i].Url))
                    {
                        if (i < tabs.Count)
                        {
                            var t = tabs[i];
                            pages[i] = new ConnectionPageInfo(
                                pages[i].PageId,
                                !string.IsNullOrEmpty(pages[i].Title) ? pages[i].Title : t.Title,
                                !string.IsNullOrEmpty(pages[i].Url) ? pages[i].Url : t.Url
                            );
                        }
                    }
                }
            }

            return JsonSerializer.Serialize(pages, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not list pages for context '{contextId}': {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("Traces the redirect chain forwards and backwards for a given network transaction ID, returning the ordered lineage of requests. pageId is optional and defaults to active page.")]
    public static string TraceRedirectChain(string? pageId = null, string? transactionId = null)
    {
        EnsureManager();
        if (string.IsNullOrEmpty(transactionId) && !string.IsNullOrEmpty(pageId) && pageId.Length > 20 && !pageId.StartsWith("page_"))
        {
            transactionId = pageId;
            pageId = null;
        }
        pageId = _manager!.ResolvePageId(pageId);
        if (string.IsNullOrEmpty(transactionId)) return "[]";
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "[]";
        
        if (!map.TryGetValue(transactionId, out var tx)) return $"Error: Transaction {transactionId} not found.";

        var chain = new List<NetworkTransaction>();
        var curr = tx;
        while (curr.RedirectedFromId != null && map.TryGetValue(curr.RedirectedFromId, out var prev))
        {
            chain.Insert(0, prev);
            curr = prev;
        }

        chain.Add(tx);

        curr = tx;
        while (curr.RedirectedToId != null && map.TryGetValue(curr.RedirectedToId, out var next))
        {
            chain.Add(next);
            curr = next;
        }

        var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        return JsonSerializer.Serialize(chain, opts);
    }

    [McpServerTool, Description("Intercept network requests matching a URL pattern. You can abort, or inject mock JSON responses, or override headers/POST data securely. It will remain active until you call ClearInterceptions. pageId is optional and defaults to active page.")]
    public static async Task<string> InterceptAndModify(
        string? pageId = null, 
        string? urlPattern = null, 
        string? mockJsonResponse = null, 
        int? mockStatus = null, 
        Dictionary<string, string>? overrideHeaders = null, 
        string? overridePostData = null,
        bool abortRequest = false)
    {
        EnsureManager();
        if (urlPattern == null && pageId != null && (pageId.Contains("://") || pageId.StartsWith("*") || pageId.Contains("/")))
        {
            urlPattern = pageId;
            pageId = null;
        }
        pageId = _manager!.ResolvePageId(pageId);
        if (string.IsNullOrWhiteSpace(urlPattern)) return "Error: urlPattern is required";
        var page = _manager!.GetPage(pageId);
        try 
        {
            await page.RouteAsync(urlPattern, async route => 
            {
                var routeTask = Task.Run(async () =>
                {
                    if (abortRequest) 
                    {
                        await route.AbortAsync();
                        return;
                    }
                    
                    if (mockJsonResponse != null)
                    {
                        var opts = new RouteFulfillOptions { 
                            Status = mockStatus ?? 200, 
                            Body = mockJsonResponse, 
                            ContentType = "application/json" 
                        };
                        await route.FulfillAsync(opts);
                        return;
                    }

                    var continueOpts = new RouteContinueOptions();
                    if (overrideHeaders != null) continueOpts.Headers = overrideHeaders;
                    if (overridePostData != null) continueOpts.PostData = Encoding.UTF8.GetBytes(overridePostData);

                    await route.ContinueAsync(continueOpts);
                });

                // Safety timeout: 8s. Fall through to continue or fulfill 504 to prevent CDP deadlock in bridge mode
                if (await Task.WhenAny(routeTask, Task.Delay(8000)) != routeTask)
                {
                    try
                    {
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = 504,
                            Body = "{\"error\":\"Gateway Timeout: Interception handler timed out after 8s.\"}",
                            ContentType = "application/json"
                        });
                    }
                    catch
                    {
                        try { await route.ContinueAsync(); } catch { }
                    }
                }
            });
            return $"Success: Interceptor activated for '{urlPattern}'.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clears all active network interceptions on the page and forcibly flushes any stuck route handlers. pageId is optional and defaults to active page.")]
    public static async Task<string> ClearInterceptions(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        try
        {
            // Reset any pending callbacks in the extension bridge
            _relayServer?.ResetPendingCallbacks();

            var unrouteTask = page.UnrouteAllAsync();
            if (await Task.WhenAny(unrouteTask, Task.Delay(3000)) != unrouteTask)
            {
                try { await page.UnrouteAsync("**/*"); } catch { }
            }

            try
            {
                var cdp = await page.Context.NewCDPSessionAsync(page);
                await cdp.SendAsync("Fetch.disable");
            }
            catch { }

            return "Success: All network interceptions cleared and CDP fetch pipeline reset.";
        }
        catch(Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Intercept network requests matching a URL pattern globally across the ENTIRE context. Applies to all pages. You can abort, inject mock JSON, or override headers/POST data.")]
    public static async Task<string> InterceptContextAndModify(
        string contextId, 
        string urlPattern, 
        string? mockJsonResponse = null, 
        int? mockStatus = null, 
        Dictionary<string, string>? overrideHeaders = null, 
        string? overridePostData = null,
        bool abortRequest = false)
    {
        EnsureManager();
        var context = _manager!.GetContext(contextId);
        try 
        {
            await context.RouteAsync(urlPattern, async route => 
            {
                var routeTask = Task.Run(async () =>
                {
                    if (abortRequest) 
                    {
                        await route.AbortAsync();
                        return;
                    }
                    
                    if (mockJsonResponse != null)
                    {
                        var opts = new RouteFulfillOptions { 
                            Status = mockStatus ?? 200, 
                            Body = mockJsonResponse, 
                            ContentType = "application/json" 
                        };
                        await route.FulfillAsync(opts);
                        return;
                    }

                    var continueOpts = new RouteContinueOptions();
                    if (overrideHeaders != null) continueOpts.Headers = overrideHeaders;
                    if (overridePostData != null) continueOpts.PostData = Encoding.UTF8.GetBytes(overridePostData);

                    await route.ContinueAsync(continueOpts);
                });

                if (await Task.WhenAny(routeTask, Task.Delay(8000)) != routeTask)
                {
                    try
                    {
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = 504,
                            Body = "{\"error\":\"Gateway Timeout: Context interception handler timed out after 8s.\"}",
                            ContentType = "application/json"
                        });
                    }
                    catch
                    {
                        try { await route.ContinueAsync(); } catch { }
                    }
                }
            });
            return $"Success: Global Context Interceptor activated for '{urlPattern}'.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clears all active network interceptions across the entire context and forcibly flushes any stuck route handlers.")]
    public static async Task<string> ClearContextInterceptions(string contextId)
    {
        EnsureManager();
        var context = _manager!.GetContext(contextId);
        try
        {
            _relayServer?.ResetPendingCallbacks();

            var unrouteTask = context.UnrouteAllAsync();
            if (await Task.WhenAny(unrouteTask, Task.Delay(3000)) != unrouteTask)
            {
                try { await context.UnrouteAsync("**/*"); } catch { }
            }

            return "Success: All context-wide network interceptions cleared.";
        }
        catch(Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public const string AutoExpandStyleId = "__playwright_auto_expand_scroll_containers__";
    public const string AutoExpandCss = @"
@media print {
    html, body, main, [role='main'], [class*='layout'], [class*='content'], #root, #__next, #app {
        height: auto !important;
        min-height: 100% !important;
        max-height: none !important;
        overflow: visible !important;
        overflow-y: visible !important;
        position: static !important;
    }
    * {
        overflow: visible !important;
        max-height: none !important;
    }
    .ant-table-wrapper, .ant-table-container, .ant-table-content, .ant-table-body,
    .ant-layout, .ant-layout-content,
    .MuiTableContainer-root,
    [class*='table-container'], [class*='table-body'], [class*='scroll-container'], [class*='scrollable'],
    [class*='overflow-auto'], [class*='overflow-y-auto'],
    [style*='overflow'], [style*='max-height'] {
        height: auto !important;
        max-height: none !important;
        overflow: visible !important;
        position: static !important;
    }
    .ant-card, .ant-table-row, [role='row'], [class*='card'], .summary-card, [class*='summary'], [class*='kpi'] {
        break-inside: avoid !important;
        page-break-inside: avoid !important;
        height: auto !important;
        max-height: none !important;
        overflow: visible !important;
    }
}";

    public const string ScopedPrintStyleId = "__playwright_scoped_pdf_style__";

    [McpServerTool, Description("Exports the current page to a PDF document. Supports configuring format ('A4', 'Letter', etc.), printBackground, landscape, margins, scale, pageRanges, and writing directly to filePath or returning base64 PDF bytes. Supports displayHeaderFooter, headerTemplate, footerTemplate, and addComplianceAuditBanner (automatically stamps audit trail metadata with date, time, title, url, page numbers for SOC2/GRC audits). Works seamlessly across both headless sessions and headed extension bridge sessions via automatic headless transfer fallback. When autoExpandScrollContainers is true (default), automatically unconstrains SPA scroll containers (Ant Design, MUI, Tailwind, virtualized tables, summary cards) for complete multi-page printing without clipping. If selector is provided, scopes the PDF print specifically to that element (e.g. modal, drawer, summary card). If scrollFirst is true, auto-scrolls the page before printing to hydrate virtualized DOM. pageId is optional and defaults to active page.")]
    public static async Task<string> ExportPageToPdf(
        string? pageId = null, 
        string? filePath = null, 
        string format = "A4", 
        bool printBackground = true, 
        bool landscape = false,
        float? scale = null,
        string? pageRanges = null,
        string? marginTop = null,
        string? marginBottom = null,
        string? marginLeft = null,
        string? marginRight = null,
        bool autoExpandScrollContainers = true,
        bool forceFallback = false,
        string? selector = null,
        bool scrollFirst = false,
        bool displayHeaderFooter = false,
        string? headerTemplate = null,
        string? footerTemplate = null,
        bool addComplianceAuditBanner = false)
    {
        EnsureManager();
        if (filePath == null && !string.IsNullOrWhiteSpace(pageId))
        {
            if (pageId.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || pageId.Contains("\\") || pageId.Contains("/"))
            {
                filePath = pageId;
                pageId = null;
            }
        }
        var page = _manager!.GetPage(pageId);
        bool styleInjected = false;
        bool scopedStyleInjected = false;

        try
        {
            if (scrollFirst)
            {
                await RunAutoScrollAsync(page, selector);
            }

            if (!string.IsNullOrWhiteSpace(selector))
            {
                var scopedLoc = page.Locator(selector);
                if (await scopedLoc.CountAsync() == 0)
                {
                    return JsonSerializer.Serialize(new { status = "error", error = $"Scoped selector '{selector}' was not found on the page." });
                }

                try
                {
                    string scopedCss = $@"
@media print {{
    body * {{
        visibility: hidden !important;
    }}
    {selector}, {selector} * {{
        visibility: visible !important;
    }}
    {selector} {{
        position: absolute !important;
        left: 0 !important;
        top: 0 !important;
        width: 100% !important;
        margin: 0 !important;
        box-shadow: none !important;
        border: none !important;
    }}
}}";
                    await page.EvaluateAsync(@"([styleId, css]) => {
                        let el = document.getElementById(styleId);
                        if (!el) {
                            el = document.createElement('style');
                            el.id = styleId;
                            el.textContent = css;
                            (document.head || document.documentElement).appendChild(el);
                        }
                    }", new object[] { ScopedPrintStyleId, scopedCss });
                    scopedStyleInjected = true;
                }
                catch { }
            }

            if (autoExpandScrollContainers)
            {
                try
                {
                    await page.EvaluateAsync(@"([styleId, css]) => {
                        let el = document.getElementById(styleId);
                        if (!el) {
                            el = document.createElement('style');
                            el.id = styleId;
                            el.textContent = css;
                            (document.head || document.documentElement).appendChild(el);
                        }
                    }", new object[] { AutoExpandStyleId, AutoExpandCss });
                    styleInjected = true;
                }
                catch { }
            }

            var pdfOptions = new PagePdfOptions
            {
                Format = format,
                PrintBackground = printBackground,
                Landscape = landscape
            };

            if (addComplianceAuditBanner)
            {
                displayHeaderFooter = true;
                headerTemplate ??= @"<div style=""font-size: 8px; color: #555; width: 100%; display: flex; justify-content: space-between; padding: 0 10mm; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;""><span style=""font-weight: 600;"">EVIDENTIARY AUDIT RECORD &bull; GRC VERIFIED ARTIFACT</span><span>URL: <span class=""url""></span></span></div>";
                footerTemplate ??= @"<div style=""font-size: 8px; color: #666; width: 100%; display: flex; justify-content: space-between; padding: 0 10mm; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;""><span>Evidence Captured: <span class=""date""></span> <span class=""time""></span></span><span>URL: <span class=""url""></span></span><span>Page <span class=""pageNumber""></span> of <span class=""totalPages""></span></span></div>";
            }

            if (displayHeaderFooter)
            {
                pdfOptions.DisplayHeaderFooter = true;
                pdfOptions.HeaderTemplate = headerTemplate ?? "<div></div>";
                pdfOptions.FooterTemplate = footerTemplate ?? "<div></div>";

                // Headers and footers in Chromium require top and bottom margins to avoid clipping/overlap
                marginTop ??= "14mm";
                marginBottom ??= "14mm";
            }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                pdfOptions.Path = filePath;
            }

            if (scale.HasValue && scale.Value > 0)
            {
                pdfOptions.Scale = scale.Value;
            }

            if (!string.IsNullOrWhiteSpace(pageRanges))
            {
                pdfOptions.PageRanges = pageRanges;
            }

            if (!string.IsNullOrWhiteSpace(marginTop) || !string.IsNullOrWhiteSpace(marginBottom) ||
                !string.IsNullOrWhiteSpace(marginLeft) || !string.IsNullOrWhiteSpace(marginRight))
            {
                pdfOptions.Margin = new Microsoft.Playwright.Margin
                {
                    Top = marginTop,
                    Bottom = marginBottom,
                    Left = marginLeft,
                    Right = marginRight
                };
            }

            byte[] pdfBytes;
            string renderMethod = "native";

            try
            {
                if (forceFallback)
                {
                    throw new InvalidOperationException("Forced fallback requested for headed simulation");
                }

                pdfBytes = await page.PdfAsync(pdfOptions);
            }
            catch (Exception nativeEx)
            {
                // Headed session / CDP bridge does not support native Page.printToPDF.
                // Fall back to Headless Transfer: extract DOM and render in an ephemeral headless context
                try
                {
                    string html = await page.ContentAsync();
                    string currentUrl = page.Url;
                    string? storageJson = null;
                    try
                    {
                        storageJson = await page.Context.StorageStateAsync();
                    }
                    catch { }

                    ViewportSize? viewport = null;
                    try
                    {
                        var vp = page.ViewportSize;
                        if (vp != null)
                        {
                            viewport = new ViewportSize { Width = vp.Width, Height = vp.Height };
                        }
                    }
                    catch { }

                    pdfBytes = await _manager.RenderHtmlToPdfAsync(html, currentUrl, storageJson, pdfOptions, viewport, autoExpandScrollContainers);
                    renderMethod = "headless_transfer";
                }
                catch (Exception transferEx)
                {
                    // Fall back to screenshot converted to PDF
                    try
                    {
                        byte[] screenshotBytes;
                        if (!string.IsNullOrWhiteSpace(selector))
                        {
                            screenshotBytes = await page.Locator(selector).First.ScreenshotAsync(new LocatorScreenshotOptions
                            {
                                Type = ScreenshotType.Jpeg,
                                Quality = 85
                            });
                        }
                        else
                        {
                            screenshotBytes = await page.ScreenshotAsync(new PageScreenshotOptions
                            {
                                FullPage = true,
                                Type = ScreenshotType.Jpeg,
                                Quality = 85
                            });
                        }

                        pdfBytes = PdfHelper.CreatePdfFromJpeg(screenshotBytes);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            await File.WriteAllBytesAsync(filePath, pdfBytes);
                        }
                        renderMethod = "screenshot_fallback";
                    }
                    catch (Exception screenshotEx)
                    {
                        throw new AggregateException(
                            $"ExportPageToPdf failed across native, headless transfer, and screenshot fallbacks. Native: {nativeEx.Message}; Transfer: {transferEx.Message}; Screenshot: {screenshotEx.Message}",
                            nativeEx, transferEx, screenshotEx);
                    }
                }
            }

            // Ensure file is written if filePath was specified and file is missing or empty
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                {
                    await File.WriteAllBytesAsync(filePath, pdfBytes);
                }

                var fileInfo = new FileInfo(filePath);
                return JsonSerializer.Serialize(new
                {
                    status = "success",
                    filePath = Path.GetFullPath(filePath),
                    fileSize = fileInfo.Exists ? fileInfo.Length : pdfBytes.Length,
                    format = format,
                    landscape = landscape,
                    renderMethod = renderMethod,
                    selector = selector,
                    autoExpandScrollContainers = autoExpandScrollContainers,
                    displayHeaderFooter = displayHeaderFooter,
                    complianceAuditBanner = addComplianceAuditBanner,
                    note = renderMethod != "native" ? $"PDF generated via {renderMethod} fallback." : null
                });
            }
            else
            {
                string base64 = Convert.ToBase64String(pdfBytes);
                return JsonSerializer.Serialize(new
                {
                    status = "success",
                    dataBase64 = base64,
                    byteCount = pdfBytes.Length,
                    format = format,
                    landscape = landscape,
                    renderMethod = renderMethod,
                    selector = selector,
                    autoExpandScrollContainers = autoExpandScrollContainers,
                    displayHeaderFooter = displayHeaderFooter,
                    complianceAuditBanner = addComplianceAuditBanner,
                    note = renderMethod != "native" ? $"PDF generated via {renderMethod} fallback." : null
                });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
        finally
        {
            if (styleInjected)
            {
                try
                {
                    await page.EvaluateAsync(@"([styleId]) => {
                        const el = document.getElementById(styleId);
                        if (el) el.remove();
                    }", new object[] { AutoExpandStyleId });
                }
                catch { }
            }

            if (scopedStyleInjected)
            {
                try
                {
                    await page.EvaluateAsync(@"([styleId]) => {
                        const el = document.getElementById(styleId);
                        if (el) el.remove();
                    }", new object[] { ScopedPrintStyleId });
                }
                catch { }
            }
        }
    }

    [McpServerTool, Description("Progressively scrolls down a page or virtual scrollable container (e.g. Ant Design table body, MUI DataGrid, react-window) to trigger lazy-loading and DOM hydration of off-screen items, then scrolls back to top. pageId is optional and defaults to active page.")]
    public static async Task<string> AutoScrollPage(
        string? pageId = null,
        string? containerSelector = null,
        int stepPixels = 600,
        int delayMs = 150,
        int maxScrolls = 30)
    {
        EnsureManager();
        if (containerSelector == null && !string.IsNullOrWhiteSpace(pageId))
        {
            if (pageId.StartsWith("#") || pageId.StartsWith(".") || pageId.StartsWith("//") || pageId.StartsWith("[") || pageId.Contains("div") || pageId.Contains("table") || pageId.Contains("body") || pageId.Contains("container"))
            {
                containerSelector = pageId;
                pageId = null;
            }
        }
        var page = _manager!.GetPage(pageId);

        try
        {
            var res = await RunAutoScrollAsync(page, containerSelector, stepPixels, delayMs, maxScrolls);
            return JsonSerializer.Serialize(new
            {
                status = "success",
                containerSelector = containerSelector,
                scrolls = res.Scrolls,
                totalHeight = res.TotalHeight
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
    }

    [McpServerTool, Description("Switches active CDP focus and debugger attachment to a target browser tab by tabId, titlePattern, or urlPattern in an Extension Bridge session or standalone Playwright session.")]
    public static async Task<string> SwitchActiveTab(
        string? tabId = null,
        string? titlePattern = null,
        string? urlPattern = null)
    {
        EnsureManager();

        if (_relayServer != null)
        {
            try
            {
                var tabsResult = await _relayServer.SendExtensionCommand("chrome.tabs.query", new JsonArray { new JsonObject() });
                if (tabsResult is JsonArray tabsArray)
                {
                    JsonObject? matchedTab = null;

                    if (!string.IsNullOrWhiteSpace(tabId) && int.TryParse(tabId, out int targetIntId))
                    {
                        foreach (var t in tabsArray)
                        {
                            if (t is JsonObject obj && obj.ContainsKey("id") && obj["id"]?.GetValue<int>() == targetIntId)
                            {
                                matchedTab = obj;
                                break;
                            }
                        }
                    }
                    else
                    {
                        foreach (var t in tabsArray)
                        {
                            if (t is JsonObject obj)
                            {
                                string title = obj["title"]?.ToString() ?? "";
                                string url = obj["url"]?.ToString() ?? "";

                                bool titleMatches = string.IsNullOrWhiteSpace(titlePattern) ||
                                    Regex.IsMatch(title, titlePattern, RegexOptions.IgnoreCase) ||
                                    title.Contains(titlePattern, StringComparison.OrdinalIgnoreCase);

                                bool urlMatches = string.IsNullOrWhiteSpace(urlPattern) ||
                                    Regex.IsMatch(url, urlPattern, RegexOptions.IgnoreCase) ||
                                    url.Contains(urlPattern, StringComparison.OrdinalIgnoreCase);

                                if (titleMatches && urlMatches)
                                {
                                    matchedTab = obj;
                                    break;
                                }
                            }
                        }
                    }

                    if (matchedTab != null)
                    {
                        int selectedTabId = matchedTab["id"]!.GetValue<int>();
                        await _relayServer.SwitchToTabAsync(selectedTabId);

                        return JsonSerializer.Serialize(new
                        {
                            status = "success",
                            tabId = selectedTabId,
                            title = matchedTab["title"]?.ToString() ?? "",
                            url = matchedTab["url"]?.ToString() ?? ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Fall through to standalone Playwright search if extension command fails (e.g. unknown method chrome.tabs.query)
                CdpRelayServer.Log($"Extension switch tab attempt failed, falling through to standalone Playwright: {ex.Message}");
            }
        }

        // Standalone Playwright fallback: search contexts/pages
        try
        {
            var pageIds = _manager!.ListPages();
            foreach (var pid in pageIds)
            {
                var p = _manager.GetPage(pid);
                string title = "";
                try { title = await p.TitleAsync(); } catch { }
                string url = p.Url;

                bool idMatches = string.IsNullOrWhiteSpace(tabId) || pid.Equals(tabId, StringComparison.OrdinalIgnoreCase);

                bool titleMatches = string.IsNullOrWhiteSpace(titlePattern) ||
                    Regex.IsMatch(title, titlePattern, RegexOptions.IgnoreCase) ||
                    title.Contains(titlePattern, StringComparison.OrdinalIgnoreCase);

                bool urlMatches = string.IsNullOrWhiteSpace(urlPattern) ||
                    Regex.IsMatch(url, urlPattern, RegexOptions.IgnoreCase) ||
                    url.Contains(urlPattern, StringComparison.OrdinalIgnoreCase);

                if (idMatches && titleMatches && urlMatches)
                {
                    await p.BringToFrontAsync();
                    return JsonSerializer.Serialize(new
                    {
                        status = "success",
                        pageId = pid,
                        title = title,
                        url = url
                    });
                }
            }

            return JsonSerializer.Serialize(new
            {
                status = "error",
                message = $"No active page matched criteria (tabId: '{tabId}', titlePattern: '{titlePattern}', urlPattern: '{urlPattern}')."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    [McpServerTool, Description("Waits for a file download initiated by a subsequent user action or clicks an optional trigger selector, intercepts the download, and saves the file directly to destinationPath or a default downloads directory. Returns file metadata (path, fileName, fileSize, mimeType). pageId is optional and defaults to active page.")]
    public static async Task<string> WaitForDownload(
        string? pageId = null,
        string? triggerSelector = null,
        string? destinationPath = null,
        int timeoutMs = 30000)
    {
        EnsureManager();
        if (triggerSelector == null && !string.IsNullOrWhiteSpace(pageId))
        {
            if (pageId.StartsWith("#") || pageId.StartsWith(".") || pageId.StartsWith("//") || pageId.StartsWith("[") || pageId.Contains("button") || pageId.Contains("a") || pageId.Contains("download"))
            {
                triggerSelector = pageId;
                pageId = null;
            }
        }
        var page = _manager!.GetPage(pageId);

        try
        {
            IDownload download;
            if (!string.IsNullOrWhiteSpace(triggerSelector))
            {
                var downloadTask = page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = timeoutMs });
                await page.ClickAsync(triggerSelector);
                download = await downloadTask;
            }
            else
            {
                download = await page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = timeoutMs });
            }

            string suggestedFileName = download.SuggestedFilename;
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                destinationPath = Path.Combine(downloadsDir, suggestedFileName);
            }
            else if (Directory.Exists(destinationPath) || destinationPath.EndsWith("\\") || destinationPath.EndsWith("/"))
            {
                destinationPath = Path.Combine(destinationPath, suggestedFileName);
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await download.SaveAsAsync(destinationPath);

            var fileInfo = new FileInfo(destinationPath);
            return JsonSerializer.Serialize(new
            {
                status = "success",
                filePath = Path.GetFullPath(destinationPath),
                fileName = suggestedFileName,
                fileSize = fileInfo.Exists ? fileInfo.Length : 0,
                url = download.Url
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
    }

    [McpServerTool, Description("Configures how the next native JavaScript dialog (alert, confirm, prompt, beforeunload) on the page should be handled. Action can be 'accept' or 'dismiss', with optional promptText for prompt dialogs. Automatically attaches the dialog listener if not already active. pageId is optional and defaults to active page.")]
    public static Task<string> HandleNextDialog(string? pageId = null, string action = "accept", string? promptText = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);
        _manager.AttachDefaultDialogHandler(page, pageId);

        _manager.ConfigureNextDialog(pageId, action, promptText);
        var res = JsonSerializer.Serialize(new
        {
            status = "configured",
            pageId = pageId,
            action = action,
            promptText = promptText,
            message = $"Configured next JavaScript dialog to {action}" + (string.IsNullOrEmpty(promptText) ? "" : $" with text '{promptText}'")
        });
        return Task.FromResult(res);
    }

    [McpServerTool, Description("Gets details about the most recently handled JavaScript dialog (alert, confirm, prompt, beforeunload) on the page, including its type, message, and action taken. pageId is optional and defaults to active page.")]
    public static Task<string> GetLastDialog(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var info = _manager!.GetLastDialog(pageId);
        if (info == null)
        {
            return Task.FromResult(JsonSerializer.Serialize(new { status = "none", message = "No dialogs have been handled on this page yet." }));
        }
        var res = JsonSerializer.Serialize(new
        {
            status = "success",
            pageId = pageId,
            type = info.Type,
            message = info.Message,
            defaultPrompt = info.DefaultPrompt,
            actionTaken = info.ActionTaken
        });
        return Task.FromResult(res);
    }

    [McpServerTool, Description("Reads the current browser/system clipboard text. Automatically grants clipboard permissions and focuses page. Useful for capturing tokens, API keys, or text copied from UI 'Copy to Clipboard' actions. pageId is optional and defaults to active page.")]
    public static async Task<string> ReadClipboard(string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);

        try
        {
            await page.Context.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
        }
        catch { }

        try
        {
            await page.BringToFrontAsync();
        }
        catch { }

        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setFocusEmulationEnabled", new() { ["enabled"] = true });
        }
        catch { }

        try
        {
            var text = await page.EvaluateAsync<string>(@"async () => {
                try {
                    if (navigator.clipboard && navigator.clipboard.readText) {
                        return await navigator.clipboard.readText();
                    }
                    if (window.__playwright_clipboard !== undefined) {
                        return window.__playwright_clipboard;
                    }
                    return '';
                } catch (e) {
                    if (window.__playwright_clipboard !== undefined) {
                        return window.__playwright_clipboard;
                    }
                    return '__CLIPBOARD_ERROR__' + e.message;
                }
            }");

            if (text != null && text.StartsWith("__CLIPBOARD_ERROR__"))
            {
                return $"Error reading clipboard: {text.Substring(19)}";
            }

            return text ?? "";
        }
        catch (Exception ex)
        {
            return $"Error reading clipboard: {ex.Message}";
        }
    }

    [McpServerTool, Description("Writes text into the browser clipboard. Automatically grants clipboard permissions and focuses page. pageId is optional and defaults to active page.")]
    public static async Task<string> WriteClipboard(string text, string? pageId = null)
    {
        EnsureManager();
        pageId = _manager!.ResolvePageId(pageId);
        var page = _manager!.GetPage(pageId);

        try
        {
            await page.Context.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
        }
        catch { }

        try
        {
            await page.BringToFrontAsync();
        }
        catch { }

        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setFocusEmulationEnabled", new() { ["enabled"] = true });
        }
        catch { }

        try
        {
            await page.EvaluateAsync<bool>(@"async (val) => {
                try {
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        await navigator.clipboard.writeText(val);
                    }
                } catch (e) { }
                window.__playwright_clipboard = val;
                return true;
            }", text ?? "");

            return "Success";
        }
        catch (Exception ex)
        {
            return $"Error writing clipboard: {ex.Message}";
        }
    }

    [McpServerTool, Description("Executes an ordered pipeline of actions (navigate, click, hover, fill, fill_input, select_option, press_key, wait_for_selector, close_overlays, auto_scroll_page, wait_for_download, extract_table_data, export_page_to_pdf, read_clipboard, write_clipboard, handle_next_dialog, delay) in a single turn. Reduces agent round trips and latency for multi-step form and UI workflows. When autoDismissConsentBanners is true, automatically checks and dismisses cookie consent dialogs between action steps if any modal or banner appears. If abortOnError is true (default), halts execution immediately on failure. pageId is optional and defaults to active page.")]
    public static async Task<string> BatchActions(string? pageId = null, List<BatchActionItem>? actions = null, bool abortOnError = true, bool autoDismissConsentBanners = false)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);

        if (actions == null || actions.Count == 0)
        {
            return JsonSerializer.Serialize(new { status = "error", message = "No actions provided in actions list." });
        }

        if (autoDismissConsentBanners)
        {
            try
            {
                await CloseOverlays(pageId, target: "consent", waitForSettle: false, timeoutMs: 500);
            }
            catch { }
        }

        var results = new List<object>();
        bool hasFailure = false;

        for (int i = 0; i < actions.Count; i++)
        {
            var item = actions[i];
            var stepIndex = i;
            var actionType = item.Type?.Trim().ToLowerInvariant() ?? "";
            var stepResult = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = actionType,
                ["selector"] = item.Selector
            };

            try
            {
                switch (actionType)
                {
                    case "click":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for click action.");
                        var clickRes = await Click(
                            pageId: pageId,
                            selector: item.Selector,
                            index: item.Index,
                            autoFirst: item.AutoFirst ?? true,
                            waitForStable: item.WaitForStable ?? true,
                            animationTimeoutMs: item.AnimationTimeoutMs,
                            timeoutMs: item.TimeoutMs,
                            topmostOnly: item.TopmostOnly ?? false,
                            postClickDelayMs: item.Delay,
                            frameSelector: item.FrameSelector,
                            waitForHttpMutation: item.WaitForHttpMutation ?? false
                        );
                        stepResult["output"] = clickRes;
                        if (clickRes.StartsWith("Error")) throw new Exception(clickRes);
                        break;

                    case "hover":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for hover action.");
                        var hoverRes = await Hover(
                            pageId: pageId,
                            selector: item.Selector,
                            timeoutMs: item.TimeoutMs,
                            index: item.Index,
                            autoFirst: item.AutoFirst ?? true,
                            frameSelector: item.FrameSelector
                        );
                        stepResult["output"] = hoverRes;
                        if (hoverRes.StartsWith("Error")) throw new Exception(hoverRes);
                        break;

                    case "fill":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for fill action.");
                        var fillRes = await Fill(
                            pageId: pageId,
                            selector: item.Selector,
                            value: item.Value ?? item.Text ?? "",
                            index: item.Index,
                            autoFirst: item.AutoFirst ?? true,
                            waitForStable: item.WaitForStable ?? true,
                            animationTimeoutMs: item.AnimationTimeoutMs,
                            timeoutMs: item.TimeoutMs,
                            topmostOnly: item.TopmostOnly ?? false,
                            frameSelector: item.FrameSelector
                        );
                        stepResult["output"] = fillRes;
                        if (fillRes.StartsWith("Error")) throw new Exception(fillRes);
                        break;

                    case "fill_input":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for fill_input action.");
                        var fillInputRes = await FillInput(
                            pageId: pageId,
                            selector: item.Selector,
                            value: item.Value ?? item.Text ?? "",
                            index: item.Index,
                            autoFirst: item.AutoFirst ?? true,
                            waitForStable: item.WaitForStable ?? true,
                            animationTimeoutMs: item.AnimationTimeoutMs,
                            timeoutMs: item.TimeoutMs,
                            topmostOnly: item.TopmostOnly ?? false,
                            frameSelector: item.FrameSelector,
                            waitForHttpMutation: item.WaitForHttpMutation ?? false
                        );
                        if (item.PressEnter == true)
                        {
                            await PressKey(pageId, "Enter", item.Selector);
                        }
                        stepResult["output"] = fillInputRes;
                        if (fillInputRes.StartsWith("Error")) throw new Exception(fillInputRes);
                        break;

                    case "select_option":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for select_option action.");
                        var selOption = item.Option ?? item.Value ?? item.Text ?? "";
                        var selectRes = await SelectOption(
                            pageId: pageId,
                            selector: item.Selector,
                            option: selOption,
                            searchInputSelector: item.SearchInputSelector,
                            dropdownSelector: item.DropdownSelector,
                            waitForStable: item.WaitForStable ?? true,
                            animationTimeoutMs: item.AnimationTimeoutMs,
                            timeoutMs: item.TimeoutMs,
                            frameSelector: item.FrameSelector,
                            waitForHttpMutation: item.WaitForHttpMutation ?? false
                        );
                        stepResult["output"] = selectRes;
                        if (selectRes.StartsWith("Error")) throw new Exception(selectRes);
                        break;

                    case "press_key":
                        var keyToPress = item.Key ?? item.Value ?? "Enter";
                        var pressRes = await PressKey(
                            pageId: pageId,
                            key: keyToPress,
                            selector: item.Selector
                        );
                        stepResult["output"] = pressRes;
                        if (pressRes.StartsWith("Error")) throw new Exception(pressRes);
                        break;

                    case "wait_for_selector":
                        if (string.IsNullOrWhiteSpace(item.Selector)) throw new ArgumentException("Selector is required for wait_for_selector action.");
                        var stateStr = item.State?.ToLowerInvariant() ?? "visible";
                        WaitForSelectorState wfState = stateStr switch
                        {
                            "hidden" => WaitForSelectorState.Hidden,
                            "attached" => WaitForSelectorState.Attached,
                            "detached" => WaitForSelectorState.Detached,
                            _ => WaitForSelectorState.Visible
                        };
                        var waitLoc = !string.IsNullOrWhiteSpace(item.FrameSelector) 
                            ? page.FrameLocator(item.FrameSelector).Locator(item.Selector)
                            : page.Locator(item.Selector);
                        await waitLoc.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = wfState,
                            Timeout = item.TimeoutMs ?? 10000f
                        });
                        stepResult["output"] = $"Waited for '{item.Selector}' with state '{stateStr}'";
                        break;

                    case "close_overlays":
                    case "dismiss_consent":
                        var closeTarget = string.Equals(actionType, "dismiss_consent", StringComparison.OrdinalIgnoreCase)
                            ? "consent"
                            : (item.Target ?? "topmost");
                        var closeRes = await CloseOverlays(
                            pageId: pageId,
                            target: closeTarget,
                            waitForSettle: item.WaitForStable ?? true,
                            timeoutMs: item.TimeoutMs
                        );
                        stepResult["output"] = closeRes;
                        if (closeRes.StartsWith("Error")) throw new Exception(closeRes);
                        break;

                    case "auto_scroll_page":
                    case "scroll":
                        var scrollRes = await AutoScrollPage(
                            pageId: pageId,
                            containerSelector: item.Selector,
                            stepPixels: item.Index.HasValue && item.Index.Value > 0 ? item.Index.Value : 600,
                            delayMs: (int)(item.Delay ?? 150),
                            maxScrolls: item.ClickCount.HasValue && item.ClickCount.Value > 0 ? item.ClickCount.Value : 30
                        );
                        stepResult["output"] = scrollRes;
                        break;

                    case "wait_for_download":
                    case "download":
                        var downloadRes = await WaitForDownload(
                            pageId: pageId,
                            triggerSelector: item.Selector,
                            destinationPath: item.DestinationPath ?? item.FilePath,
                            timeoutMs: (int)(item.TimeoutMs ?? 30000)
                        );
                        stepResult["output"] = downloadRes;
                        if (downloadRes.Contains("\"error\"")) throw new Exception(downloadRes);
                        break;

                    case "extract_table_data":
                    case "extract_table":
                        var tableRes = await ExtractTableData(
                            pageId: pageId,
                            tableSelector: item.Selector,
                            columns: item.Columns,
                            maxRows: item.MaxRows ?? 25,
                            frameSelector: item.FrameSelector,
                            filePath: item.DestinationPath ?? item.FilePath,
                            format: item.Format ?? "json",
                            scrollFirst: item.ScrollFirst ?? false,
                            scrollDelayMs: item.ScrollDelayMs ?? 150,
                            maxScrolls: item.MaxScrolls ?? 30,
                            nextPageSelector: item.NextPageSelector,
                            maxPages: item.MaxPages ?? 1,
                            pageDelayMs: item.PageDelayMs ?? 400
                        );
                        stepResult["output"] = tableRes;
                        if (tableRes.StartsWith("Error")) throw new Exception(tableRes);
                        break;

                    case "read_clipboard":
                    case "clipboard":
                    case "get_clipboard":
                        var clipText = await ReadClipboard(pageId);
                        stepResult["output"] = clipText;
                        stepResult["clipboardText"] = clipText;
                        if (clipText.StartsWith("Error")) throw new Exception(clipText);
                        break;

                    case "write_clipboard":
                    case "set_clipboard":
                        var writeText = item.Value ?? item.Text ?? "";
                        var writeRes = await WriteClipboard(writeText, pageId);
                        stepResult["output"] = writeRes;
                        if (writeRes.StartsWith("Error")) throw new Exception(writeRes);
                        break;

                    case "handle_next_dialog":
                    case "dialog":
                        var dialogRes = await HandleNextDialog(pageId: pageId, action: item.Action ?? item.State ?? "accept", promptText: item.Value ?? item.Text);
                        stepResult["output"] = dialogRes;
                        break;

                    case "navigate":
                    case "goto":
                        var navUrl = item.Url ?? item.Value ?? item.Text;
                        if (string.IsNullOrWhiteSpace(navUrl)) throw new ArgumentException("Url (or value/text) is required for navigate action.");
                        var navRes = await Navigate(
                            pageId: pageId,
                            url: navUrl,
                            waitUntil: item.State ?? "domcontentloaded",
                            timeoutMs: item.TimeoutMs,
                            bypassConsentBanners: item.BypassConsentBanners ?? autoDismissConsentBanners
                        );
                        stepResult["output"] = navRes;
                        if (navRes.StartsWith("Error")) throw new Exception(navRes);
                        break;

                    case "export_page_to_pdf":
                    case "export_pdf":
                    case "pdf":
                        var pdfRes = await ExportPageToPdf(
                            pageId: pageId,
                            filePath: item.DestinationPath ?? item.FilePath,
                            format: item.Format ?? "A4",
                            landscape: item.Landscape ?? false,
                            selector: item.Selector,
                            autoExpandScrollContainers: item.AutoExpandScrollContainers ?? true,
                            scrollFirst: item.ScrollFirst ?? false,
                            displayHeaderFooter: item.DisplayHeaderFooter ?? false,
                            headerTemplate: item.HeaderTemplate,
                            footerTemplate: item.FooterTemplate,
                            addComplianceAuditBanner: item.AddComplianceAuditBanner ?? false
                        );
                        stepResult["output"] = pdfRes;
                        if (pdfRes.Contains("\"status\":\"error\"") || pdfRes.Contains("\"status\": \"error\"")) throw new Exception(pdfRes);
                        break;

                    case "delay":
                    case "sleep":
                    case "wait":
                        int delayDuration = (int)(item.DurationMs ?? item.TimeoutMs ?? 500);
                        if (delayDuration > 0)
                        {
                            await Task.Delay(delayDuration);
                        }
                        stepResult["output"] = $"Waited {delayDuration}ms";
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported batch action type '{item.Type}'. Supported types: navigate, click, hover, fill, fill_input, select_option, press_key, wait_for_selector, close_overlays, auto_scroll_page, wait_for_download, extract_table_data, export_page_to_pdf, read_clipboard, write_clipboard, handle_next_dialog, delay.");
                }

                stepResult["status"] = "success";

                // Post-delay if requested
                if (item.PostDelayMs.HasValue && item.PostDelayMs.Value > 0)
                {
                    await Task.Delay((int)item.PostDelayMs.Value);
                }

                if (autoDismissConsentBanners)
                {
                    try
                    {
                        await CloseOverlays(pageId, target: "consent", waitForSettle: false, timeoutMs: 500);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                hasFailure = true;
                stepResult["status"] = "error";
                stepResult["error"] = ex.Message;
                results.Add(stepResult);

                if (abortOnError)
                {
                    return JsonSerializer.Serialize(new
                    {
                        status = "aborted",
                        failedStepIndex = stepIndex,
                        actionCount = actions.Count,
                        completedCount = results.Count,
                        error = ex.Message,
                        steps = results
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                continue;
            }

            results.Add(stepResult);
        }

        return JsonSerializer.Serialize(new
        {
            status = hasFailure ? "completed_with_errors" : "success",
            actionCount = actions.Count,
            completedCount = results.Count,
            steps = results
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void EnsureManager()
    {
        if (_manager is null) throw new InvalidOperationException("PlaywrightManager not initialized. Ensure it's registered and created at startup.");
    }
}

public class BatchActionItem
{
    public string? Type { get; set; }
    public string? Action { get; set; }
    public string? Selector { get; set; }
    public string? Value { get; set; }
    public string? Text { get; set; }
    public string? Option { get; set; }
    public string? Key { get; set; }
    public string? State { get; set; }
    public string? Target { get; set; }
    public string? Button { get; set; }
    public int? ClickCount { get; set; }
    public float? Delay { get; set; }
    public bool? Force { get; set; }
    public float? TimeoutMs { get; set; }
    public float? DurationMs { get; set; }
    public float? PostDelayMs { get; set; }
    public int? Index { get; set; }
    public bool? AutoFirst { get; set; }
    public bool? WaitForStable { get; set; }
    public float? AnimationTimeoutMs { get; set; }
    public bool? TopmostOnly { get; set; }
    public bool? PressEnter { get; set; }
    public string? FrameSelector { get; set; }
    public string? SearchInputSelector { get; set; }
    public string? DropdownSelector { get; set; }
    public string? DestinationPath { get; set; }
    public string? FilePath { get; set; }
    public string[]? Columns { get; set; }
    public int? MaxRows { get; set; }
    public string? Format { get; set; }
    public bool? ScrollFirst { get; set; }
    public int? ScrollDelayMs { get; set; }
    public int? MaxScrolls { get; set; }
    public bool? WaitForHttpMutation { get; set; }
    public string? NextPageSelector { get; set; }
    public int? MaxPages { get; set; }
    public int? PageDelayMs { get; set; }
    public string? Url { get; set; }
    public bool? DisplayHeaderFooter { get; set; }
    public string? HeaderTemplate { get; set; }
    public string? FooterTemplate { get; set; }
    public bool? AddComplianceAuditBanner { get; set; }
    public bool? BypassConsentBanners { get; set; }
    public bool? Landscape { get; set; }
    public bool? AutoExpandScrollContainers { get; set; }
}

