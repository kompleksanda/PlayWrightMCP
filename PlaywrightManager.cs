using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

public record ConnectionPageInfo(string PageId, string Title, string Url);
public record ConnectionContextInfo(string ContextId, List<ConnectionPageInfo> Pages);
public record ConnectionResult(string BrowserId, List<ConnectionContextInfo> Contexts);

public record DialogHandledInfo(string Type, string Message, string? DefaultPrompt, string ActionTaken);

public class DialogHandlingConfig
{
    public string Action { get; set; } = "accept";
    public string? PromptText { get; set; }
    public TaskCompletionSource<DialogHandledInfo>? CompletionSource { get; set; }
}

public sealed class PlaywrightManager : IAsyncDisposable
{
    private readonly IPlaywright _pw;
    private readonly IBrowserType _chromium;
    private readonly PlaywrightOptions _options;
    private readonly ConcurrentDictionary<string, IBrowser> _browsers = new();
    private readonly ConcurrentDictionary<string, IBrowserContext> _contexts = new();
    // map pageId -> contextId and contextId -> collection of pageIds
    private readonly ConcurrentDictionary<string, string> _pageToContext = new();
    // contextId -> (pageId -> dummy) to allow removal when pages close
    private readonly ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> _contextToPages = new();
    // When we launch a persistent context (via userDataDir) we return a browserId but
    // actually hold the persistent IBrowserContext. This map lets us translate that
    // browserId back to the context id so callers of NewContext can get a valid context.
    private readonly ConcurrentDictionary<string, string> _persistentBrowserToContext = new();
    private readonly ConcurrentDictionary<string, IPage> _pages = new();

    private readonly ConcurrentDictionary<string, ConcurrentQueue<DialogHandlingConfig>> _pageDialogConfigs = new();
    private readonly ConcurrentDictionary<string, DialogHandledInfo> _lastDialogInfo = new();
    private readonly ConditionalWeakTable<IPage, object> _dialogAttachedPages = new();

    private PlaywrightManager(IPlaywright pw, PlaywrightOptions? options = null)
    {
        _pw = pw;
        _chromium = _pw.Chromium;
        _options = options ?? new PlaywrightOptions();

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                var defaultPath = PlaywrightTools.GetDefaultStorageStatePath();
                foreach (var ctx in _contexts.Values)
                {
                    try
                    {
                        var task = ctx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = defaultPath });
                        task.Wait(1500);
                        return;
                    }
                    catch { }
                }
            }
            catch { }
        };
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

    public async Task<ConnectionResult> ConnectBrowserAsync(string endpointUrl)
    {
        var browser = await _chromium.ConnectOverCDPAsync(endpointUrl);
        var id = Guid.NewGuid().ToString();
        _browsers[id] = browser;

        var resultContexts = new List<ConnectionContextInfo>();

        foreach (var ctx in browser.Contexts.ToArray())
        {
            try
            {
                await ctx.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
            }
            catch { }

            var ctxId = Guid.NewGuid().ToString();
            _contexts[ctxId] = ctx;

            _persistentBrowserToContext[id] = ctxId;

            // If no pages yet, wait a moment for the initial page
            if (ctx.Pages.Count == 0)
            {
                var tcs = new TaskCompletionSource<IPage>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPage(object? s, IPage p) => tcs.TrySetResult(p);
                ctx.Page += OnPage;
                await Task.WhenAny(tcs.Task, Task.Delay(3000));
                ctx.Page -= OnPage;
            }

            var resultPages = new List<ConnectionPageInfo>();

            foreach (var page in ctx.Pages.ToArray())
            {
                var pageId = Guid.NewGuid().ToString();
                _pages[pageId] = page;
                _lastActivePageId = pageId;
                _pageToContext[pageId] = ctxId;
                var map = _contextToPages.GetOrAdd(ctxId, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
                map.TryAdd(pageId, 0);

                AttachDefaultDialogHandler(page, pageId);

                string title = page.Url;
                try
                {
                    var titleTask = page.TitleAsync();
                    title = await Task.WhenAny(titleTask, Task.Delay(1500)) == titleTask ? await titleTask : page.Url;
                }
                catch { }

                resultPages.Add(new ConnectionPageInfo(pageId, title, page.Url));
            }

            resultContexts.Add(new ConnectionContextInfo(ctxId, resultPages));
        }
        
        return new ConnectionResult(id, resultContexts);
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
            var persistentOptions = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = useHeadless,
                Permissions = new[] { "clipboard-read", "clipboard-write" }
            };
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

        var contextOptions = new BrowserNewContextOptions
        {
            Permissions = new[] { "clipboard-read", "clipboard-write" }
        };
        try
        {
            var defaultStoragePath = PlaywrightTools.GetDefaultStorageStatePath();
            if (File.Exists(defaultStoragePath))
            {
                contextOptions.StorageStatePath = defaultStoragePath;
            }
        }
        catch { }

        var context = await browser.NewContextAsync(contextOptions);
        var id = Guid.NewGuid().ToString();
        _contexts[id] = context;
        return id;
    }

    public async Task<string> NewPageAsync(string contextId)
    {
        if (!_contexts.TryGetValue(contextId, out var context))
            throw new ArgumentException("Unknown contextId");
        var page = await context.NewPageAsync();
        try
        {
            await page.BringToFrontAsync();
        }
        catch { }

        var id = Guid.NewGuid().ToString();
        _pages[id] = page;
        AttachDefaultDialogHandler(page, id);
        // record mapping from page to context and add to context->pages map
        try
        {
            _pageToContext[id] = contextId;
            var map = _contextToPages.GetOrAdd(contextId, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
            map.TryAdd(id, 0);
        }
        catch { }
        _lastActivePageId = id;
        return id;
    }

    private string? _lastActivePageId;
    public string? LastActivePageId
    {
        get => _lastActivePageId;
        set => _lastActivePageId = value;
    }

    public void SetActivePage(string pageId)
    {
        if (!string.IsNullOrWhiteSpace(pageId))
        {
            _lastActivePageId = pageId;
        }
    }

    public IPage GetPage(string? pageId = null)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            pageId = _lastActivePageId;
            if (string.IsNullOrWhiteSpace(pageId) || !_pages.ContainsKey(pageId))
            {
                var livePages = ListPages();
                pageId = livePages.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new InvalidOperationException("No active browser pages found. Launch a browser or open a page first.");
            }
        }

        if (_pages.TryGetValue(pageId, out var page))
        {
            bool isClosed = false;
            try { isClosed = page.IsClosed; } catch { isClosed = true; }
            if (!isClosed)
            {
                _lastActivePageId = pageId;
                AttachDefaultDialogHandler(page, pageId);
                return page;
            }
        }

        // Auto-refresh stale or closed handle from live context.Pages collection
        var refreshed = TryRefreshAndBindLivePage(pageId);
        if (refreshed != null)
        {
            _lastActivePageId = pageId;
            return refreshed;
        }

        if (page != null)
        {
            _lastActivePageId = pageId;
            AttachDefaultDialogHandler(page, pageId);
            return page;
        }

        // Resilient fallback: If requested pageId was not found, check if there is an active live page
        var fallbackPageId = _lastActivePageId ?? ListPages().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallbackPageId) && _pages.TryGetValue(fallbackPageId, out var fallbackPage))
        {
            bool fallbackClosed = false;
            try { fallbackClosed = fallbackPage.IsClosed; } catch { fallbackClosed = true; }
            if (!fallbackClosed)
            {
                _lastActivePageId = fallbackPageId;
                return fallbackPage;
            }
        }

        throw new ArgumentException($"Unknown or closed pageId: '{pageId}'");
    }

    public string ResolvePageId(string? pageId = null)
    {
        var page = GetPage(pageId);
        foreach (var kvp in _pages)
        {
            if (kvp.Value == page) return kvp.Key;
        }
        return _lastActivePageId ?? _pages.Keys.FirstOrDefault() ?? pageId ?? "";
    }

    public IPage? TryRefreshAndBindLivePage(string pageId)
    {
        IBrowserContext? targetContext = null;
        if (_pageToContext.TryGetValue(pageId, out var contextId) && _contexts.TryGetValue(contextId, out var ctx))
        {
            targetContext = ctx;
        }

        var contextsToCheck = new List<IBrowserContext>();
        if (targetContext != null) contextsToCheck.Add(targetContext);
        foreach (var c in _contexts.Values)
        {
            if (!contextsToCheck.Contains(c)) contextsToCheck.Add(c);
        }

        foreach (var context in contextsToCheck)
        {
            try
            {
                var pages = context.Pages;
                for (int i = pages.Count - 1; i >= 0; i--)
                {
                    var p = pages[i];
                    if (p == null) continue;

                    bool closed = false;
                    try { closed = p.IsClosed; } catch { closed = true; }

                    if (!closed)
                    {
                        // Found an active live page! Rebind pageId to this page
                        _pages[pageId] = p;
                        AttachDefaultDialogHandler(p, pageId);

                        string matchedContextId = _contexts.FirstOrDefault(x => object.ReferenceEquals(x.Value, context)).Key ?? contextId ?? "";
                        if (!string.IsNullOrEmpty(matchedContextId))
                        {
                            _pageToContext[pageId] = matchedContextId;
                            var map = _contextToPages.GetOrAdd(matchedContextId, _ => new ConcurrentDictionary<string, byte>());
                            map.TryAdd(pageId, 0);
                        }

                        return p;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public static bool IsTargetClosedException(Exception ex)
    {
        if (ex == null) return false;
        var msg = ex.Message;
        return msg.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("has been closed", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<T> ExecuteWithLivePageAsync<T>(string pageId, Func<IPage, Task<T>> action)
    {
        var page = GetPage(pageId);
        try
        {
            return await action(page);
        }
        catch (Exception ex) when (IsTargetClosedException(ex))
        {
            var livePage = TryRefreshAndBindLivePage(pageId);
            if (livePage != null && !object.ReferenceEquals(livePage, page))
            {
                return await action(livePage);
            }
            throw;
        }
    }

    public async Task ExecuteWithLivePageAsync(string pageId, Func<IPage, Task> action)
    {
        var page = GetPage(pageId);
        try
        {
            await action(page);
        }
        catch (Exception ex) when (IsTargetClosedException(ex))
        {
            var livePage = TryRefreshAndBindLivePage(pageId);
            if (livePage != null && !object.ReferenceEquals(livePage, page))
            {
                await action(livePage);
                return;
            }
            throw;
        }
    }

    public IBrowserContext GetContext(string contextId)
    {
        if (!_contexts.TryGetValue(contextId, out var context))
            throw new ArgumentException("Unknown contextId");
        return context;
    }

    public async Task<byte[]> ScreenshotAsync(string? pageId = null, PageScreenshotOptions? options = null)
    {
        var page = GetPage(pageId);
        var stream = await page.ScreenshotAsync(options ?? new PageScreenshotOptions { FullPage = true });
        return stream;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await AutoSyncStorageStateAsync();
        }
        catch { }

        foreach (var p in _pages.Values)
            try { await p.CloseAsync(); } catch { }
        foreach (var c in _contexts.Values)
            try { await c.CloseAsync(); } catch { }
        foreach (var b in _browsers.Values)
            try { await b.CloseAsync(); } catch { }
        try { _pw?.Dispose(); } catch { }
    }

    // Return all known context ids
    public string[] ListContexts()
    {
        return _contexts.Keys.ToArray();
    }

    // Locks or emulates document/window focus for a page
    public async Task EnsureFocusAsync(string? pageId = null, bool enabled = true)
    {
        var page = GetPage(pageId);
        if (!string.IsNullOrWhiteSpace(pageId)) _lastActivePageId = pageId;
        try
        {
            await page.BringToFrontAsync();
        }
        catch { }

        try
        {
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setFocusEmulationEnabled", new() { ["enabled"] = enabled });
        }
        catch { }
    }

    // Return page ids for a given context id, or across all contexts if contextId is null/empty
    public string[] ListPages(string? contextId = null)
    {
        if (string.IsNullOrEmpty(contextId))
        {
            var allPages = new List<string>();
            foreach (var cid in _contexts.Keys)
            {
                allPages.AddRange(ListPages(cid));
            }
            return allPages.Distinct().ToArray();
        }

        // If we don't have the context, return empty
        if (!_contexts.TryGetValue(contextId, out var context)) return Array.Empty<string>();

        var livePageIds = new List<string>();

        // Enumerate actual pages from the context to detect externally-created pages
        try
        {
            var pages = context.Pages.ToArray(); // Snapshot of pages
            foreach (var p in pages)
            {
                if (p is null) continue;

                // Skip closed pages but still clean up recorded entries later
                bool isClosed = false;
                try { isClosed = p.IsClosed; } catch { }

                if (isClosed) continue;

                // Try to find existing pageId for this IPage instance
                string? foundId = null;
                foreach (var kv in _pages)
                {
                    if (object.ReferenceEquals(kv.Value, p)) { foundId = kv.Key; break; }
                }

                if (foundId is null)
                {
                    // New external page - register it
                    var newId = Guid.NewGuid().ToString();
                    _pages[newId] = p;
                    _pageToContext[newId] = contextId;
                    var map = _contextToPages.GetOrAdd(contextId, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
                    map.TryAdd(newId, 0);
                    AttachDefaultDialogHandler(p, newId);
                    foundId = newId;
                }

                if (foundId is not null) livePageIds.Add(foundId);
            }
        }
        catch { /* ignore Playwright introspection errors */ }

        // Cleanup recorded pages for the context that no longer exist or are closed
        if (_contextToPages.TryGetValue(contextId, out var recorded))
        {
            var recordedIds = recorded.Keys.ToArray();
            foreach (var pid in recordedIds)
            {
                // if recorded id not found in livePageIds, remove
                if (!livePageIds.Contains(pid))
                {
                    recorded.TryRemove(pid, out var _);
                    _pages.TryRemove(pid, out var _);
                    _pageToContext.TryRemove(pid, out var _);
                }
            }
        }

        return livePageIds.ToArray();
    }

    public async Task<List<ConnectionPageInfo>> ListPagesDetailsAsync(string? contextId = null)
    {
        var pageIds = ListPages(contextId);
        var list = new List<ConnectionPageInfo>();
        foreach (var pid in pageIds)
        {
            try
            {
                if (_pages.TryGetValue(pid, out var page))
                {
                    bool isClosed = false;
                    try { isClosed = page.IsClosed; } catch { isClosed = true; }
                    if (isClosed) continue;

                    string title = "";
                    try
                    {
                        var titleTask = page.TitleAsync();
                        title = await Task.WhenAny(titleTask, Task.Delay(500)) == titleTask ? await titleTask : "";
                    }
                    catch { }

                    string url = "";
                    try
                    {
                        url = page.Url ?? "";
                    }
                    catch { }

                    list.Add(new ConnectionPageInfo(pid, title, url));
                }
            }
            catch { }
        }
        return list;
    }

    public IBrowserType Chromium => _chromium;

    /// <summary>
    /// Renders an HTML string with optional base URL and storage state into a vector PDF document
    /// using an ephemeral headless Chromium instance. Used as a transparent fallback when ExportPageToPdf
    /// is called on headed sessions (e.g. extension bridge tabs) where Chromium restricts Page.printToPDF.
    /// </summary>
    public async Task<byte[]> RenderHtmlToPdfAsync(
        string html,
        string? baseUrl,
        string? storageStateJson,
        PagePdfOptions pdfOptions,
        ViewportSize? viewport = null,
        bool autoExpandScrollContainers = true)
    {
        var launchOptions = new BrowserTypeLaunchOptions { Headless = true };
        var headlessBrowser = await _chromium.LaunchAsync(launchOptions);
        try
        {
            var contextOptions = new BrowserNewContextOptions();
            if (viewport != null)
            {
                contextOptions.ViewportSize = new ViewportSize
                {
                    Width = viewport.Width,
                    Height = viewport.Height
                };
            }

            if (!string.IsNullOrWhiteSpace(baseUrl) &&
                (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var uri = new Uri(baseUrl);
                    contextOptions.BaseURL = uri.GetLeftPart(UriPartial.Authority);
                }
                catch { }
            }

            var context = await headlessBrowser.NewContextAsync(contextOptions);
            try
            {
                if (!string.IsNullOrWhiteSpace(storageStateJson))
                {
                    try
                    {
                        await PlaywrightTools.ImportStorageStateToContextAsync(context, storageStateJson);
                    }
                    catch { }
                }

                var tempPage = await context.NewPageAsync();
                try
                {
                    var preparedHtml = html;
                    if (!string.IsNullOrWhiteSpace(baseUrl) &&
                        (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                        !preparedHtml.Contains("<base ", StringComparison.OrdinalIgnoreCase))
                    {
                        int headIndex = preparedHtml.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                        if (headIndex >= 0)
                        {
                            preparedHtml = preparedHtml.Insert(headIndex + 6, $"\n<base href=\"{System.Security.SecurityElement.Escape(baseUrl)}\">\n");
                        }
                    }

                    if (autoExpandScrollContainers && !preparedHtml.Contains(PlaywrightTools.AutoExpandStyleId))
                    {
                        int headIndex = preparedHtml.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                        if (headIndex >= 0)
                        {
                            preparedHtml = preparedHtml.Insert(headIndex + 6, $"\n<style id=\"{PlaywrightTools.AutoExpandStyleId}\">{PlaywrightTools.AutoExpandCss}</style>\n");
                        }
                        else
                        {
                            preparedHtml = $"<style id=\"{PlaywrightTools.AutoExpandStyleId}\">{PlaywrightTools.AutoExpandCss}</style>\n" + preparedHtml;
                        }
                    }

                    try
                    {
                        await tempPage.SetContentAsync(preparedHtml, new PageSetContentOptions
                        {
                            WaitUntil = WaitUntilState.Load,
                            Timeout = 10000
                        });
                    }
                    catch
                    {
                        await tempPage.SetContentAsync(preparedHtml, new PageSetContentOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 10000
                        });
                    }

                    try
                    {
                        await Task.WhenAny(
                            tempPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3000 }),
                            Task.Delay(1000)
                        );
                    }
                    catch { }

                    return await tempPage.PdfAsync(pdfOptions);
                }
                finally
                {
                    try { await tempPage.CloseAsync(); } catch { }
                }
            }
            finally
            {
                try { await context.CloseAsync(); } catch { }
            }
        }
        finally
        {
            try { await headlessBrowser.CloseAsync(); } catch { }
        }
    }

    public async Task AutoSyncStorageStateAsync()
    {
        try
        {
            var defaultPath = PlaywrightTools.GetDefaultStorageStatePath();
            foreach (var ctx in _contexts.Values)
            {
                try
                {
                    await ctx.StorageStateAsync(new BrowserContextStorageStateOptions
                    {
                        Path = defaultPath
                    });
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    public void AttachDefaultDialogHandler(IPage page, string pageId)
    {
        if (page == null) return;
        lock (_dialogAttachedPages)
        {
            if (_dialogAttachedPages.TryGetValue(page, out _)) return;
            _dialogAttachedPages.Add(page, new object());
        }

        page.Dialog += async (_, dialog) =>
        {
            try
            {
                DialogHandlingConfig? config = null;
                if (_pageDialogConfigs.TryGetValue(pageId, out var queue) && queue.TryDequeue(out var dequeued))
                {
                    config = dequeued;
                }

                string actionTaken = "accept";
                if (config != null)
                {
                    actionTaken = string.Equals(config.Action, "dismiss", StringComparison.OrdinalIgnoreCase) ? "dismiss" : "accept";
                    if (actionTaken == "dismiss")
                    {
                        await dialog.DismissAsync();
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(config.PromptText))
                        {
                            await dialog.AcceptAsync(config.PromptText);
                        }
                        else
                        {
                            await dialog.AcceptAsync();
                        }
                    }

                    var info = new DialogHandledInfo(dialog.Type, dialog.Message, dialog.DefaultValue, actionTaken);
                    _lastDialogInfo[pageId] = info;
                    config.CompletionSource?.TrySetResult(info);
                }
                else
                {
                    // Default auto-dismiss/accept:
                    // Alerts, confirms, prompts, and beforeunload automatically accepted so navigation never freezes
                    await dialog.AcceptAsync();
                    var info = new DialogHandledInfo(dialog.Type, dialog.Message, dialog.DefaultValue, "accept (auto)");
                    _lastDialogInfo[pageId] = info;
                }

                Console.Error.WriteLine($"[Dialog Handled] Page: {pageId}, Type: {dialog.Type}, Action: {actionTaken}, Message: '{dialog.Message}'");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Dialog Handler Error] Page: {pageId}: {ex.Message}");
            }
        };
    }

    public void ConfigureNextDialog(string pageId, string action = "accept", string? promptText = null)
    {
        var queue = _pageDialogConfigs.GetOrAdd(pageId, _ => new ConcurrentQueue<DialogHandlingConfig>());
        queue.Enqueue(new DialogHandlingConfig
        {
            Action = action,
            PromptText = promptText
        });
    }

    public DialogHandledInfo? GetLastDialog(string pageId)
    {
        _lastDialogInfo.TryGetValue(pageId, out var info);
        return info;
    }
}

