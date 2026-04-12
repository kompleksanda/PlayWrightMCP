using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Playwright;
using System.Runtime.CompilerServices;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

[McpServerToolType]
public static class PlaywrightTools
{
    private static PlaywrightManager? _manager;

    // Called by DI on startup to set manager instance
    public static void SetManager(PlaywrightManager manager) => _manager = manager;

    // --- existing basic helpers ---
    [McpServerTool, Description("Launches a Chromium browser and returns a browserId. You should only call this once and reuse the browserId for multiple contexts/pages. Call the new context tool next.")]
    public static async Task<string> LaunchBrowser(bool headless = true)
    {
        EnsureManager();
        return await _manager!.LaunchBrowserAsync(headless);
    }

    [McpServerTool, Description("Creates a new browser context for given browserId and returns contextId. Call the new page tool next.")]
    public static async Task<string> NewContext(string browserId)
    {
        EnsureManager();
        return await _manager!.NewContextAsync(browserId);
    }

    [McpServerTool, Description("Creates a new page in the given context and returns pageId.")]
    public static async Task<string> NewPage(string contextId)
    {
        EnsureManager();
        return await _manager!.NewPageAsync(contextId);
    }

    [McpServerTool, Description("Injects a session token or cookie into the browser context. This allows you to simulate logged-in sessions by passing a cookie name, value, and domain (e.g., '.kuda.com').")]
    public static async Task InjectCookie(string pageId, string name, string value, string domain, string path = "/")
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        await page.Context.AddCookiesAsync(new[]
        {
            new Microsoft.Playwright.Cookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = path
            }
        });
    }

    [McpServerTool, Description("Navigates the page to the specified url.")]
    public static async Task<string> Navigate(string pageId, string url)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(pageId)) return "Error: pageId is required";
        if (string.IsNullOrWhiteSpace(url)) return "Error: url is required";

        var page = _manager!.GetPage(pageId);
        if (page == null) return "Error: Unknown pageId";

        try
        {
            if (page.IsClosed) return "Error: The requested page is already closed.";
        }
        catch { }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "file" && uri.Scheme != "about"))
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

        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
            return "Success";
        }
        catch (TimeoutException firstTimeout)
        {
            try
            {
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
                return "Success";
            }
            catch (TimeoutException secondTimeout)
            {
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 15000 });
                    return "Success";
                }
                catch (Exception finalEx)
                {
                    return $"Error: Navigation to '{url}' failed after multiple attempts: final error={finalEx.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            return $"Error: Navigation to '{url}' failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Gets the page content (outer HTML of document). Use GetAccessibilitySnapshot instead. Do not call this unless necessary as it takes more tokens.")]
    public static async Task<string> GetContent(string pageId)
    {
        EnsureManager();
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

    [McpServerTool, Description("Clicks on a selector on the page.")]
    public static async Task<string> Click(string pageId, string selector)
    {
        EnsureManager();
        try 
        {
            var page = _manager!.GetPage(pageId);
            await page.ClickAsync(selector);
            return "Success";
        } 
        catch (Exception ex) 
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Hovers over a selector on the page. Implicitly scrolls the element into view before hovering.")]
    public static async Task<string> Hover(string pageId, string selector)
    {
        EnsureManager();
        try 
        {
            var page = _manager!.GetPage(pageId);
            var locator = page.Locator(selector);
            await locator.ScrollIntoViewIfNeededAsync();
            await locator.HoverAsync();
            return "Success";
        } 
        catch (Exception ex) 
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Drags the element at sourceSelector and drops it onto the targetSelector. Implicitly scrolls the elements into view.")]
    public static async Task<string> DragAndDrop(string pageId, string sourceSelector, string targetSelector)
    {
        EnsureManager();
        try 
        {
            var page = _manager!.GetPage(pageId);
            var source = page.Locator(sourceSelector);
            var target = page.Locator(targetSelector);
            
            await source.ScrollIntoViewIfNeededAsync();
            
            await source.DragToAsync(target);
            
            await target.ScrollIntoViewIfNeededAsync();
            return "Success";
        } 
        catch (Exception ex) 
        {
            return $"Error: {ex.Message}";
        }
    }

    public class TypeOptions
    {
        public bool PressEnter { get; set; }
        public float? Delay { get; set; }
    }

    [McpServerTool, Description("Types text into selector on the page, simulating real keyboard events. You can provide an options argument like {\"pressEnter\": true, \"delay\": 100} to press Enter after typing or add a typing delay in milliseconds.")]
    public static async Task<string> Type(string pageId, string selector, string text, TypeOptions? options = null)
    {
        EnsureManager();
        try 
        {
            var page = _manager!.GetPage(pageId);
            var locator = page.Locator(selector);
            await locator.ClearAsync();

            var seqOptions = new LocatorPressSequentiallyOptions();
            if (options?.Delay != null)
            {
                seqOptions.Delay = options.Delay;
            }

            await locator.PressSequentiallyAsync(text, seqOptions);
            if (options?.PressEnter == true)
            {
                await locator.PressAsync("Enter");
            }
            return "Success";
        } 
        catch (Exception ex) 
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Executes arbitrary JavaScript in the browser page and returns the result as a JSON string. Use this to run batched logic locally without multiple round trips.")]
    public static async Task<string> EvaluateScript(string pageId, string script)
    {
        EnsureManager();
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

    [McpServerTool, Description("Takes a screenshot of the page and returns a base64-encoded PNG. Use GetAccessibilitySnapshot instead. Do not call this unless necessary as it takes more tokens.")]
    public static async Task<string> Screenshot(string pageId)
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

    [McpServerTool, Description("Takes a screenshot of a specific element region by selector and returns a base64-encoded PNG. This is much more token-efficient than a full page screenshot.")]
    public static async Task<string> ScreenshotRegion(string pageId, string selector)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        var locator = page.Locator(selector);
        try
        {
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
            return $"Error: Screenshot failed for selector '{selector}'. The element might be hidden, 0x0 size, or missing entirely. Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Waits for a specific selector to appear on the page. Timeout is in milliseconds (default is 30000).")]
    public static async Task<string> WaitForSelector(string pageId, string selector, float? timeout = null)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        var options = new PageWaitForSelectorOptions();
        if (timeout.HasValue) options.Timeout = timeout.Value;
        
        try
        {
            await page.WaitForSelectorAsync(selector, options);
            return "Success";
        }
        catch (System.TimeoutException)
        {
            float actualTimeout = timeout ?? 30000f;
            return $"Error: Selector '{selector}' timed out after {actualTimeout}ms. Ensure the selector is correct or the page has fully loaded.";
        }
        catch (System.Exception ex)
        {
            return $"Error: Failed waiting for selector '{selector}': {ex.Message}";
        }
    }

    [McpServerTool, Description("Waits for the page to reach a specific load state: 'load', 'domcontentloaded', or 'networkidle' (default 'load'). Timeout is in milliseconds.")]
    public static async Task<string> WaitForLoadState(string pageId, string state = "load", float? timeout = null)
    {
        EnsureManager();
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
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<object>> _consoleLogs = new();
    // Chunked HTML/content storage: pageId -> (pageIndex -> chunk)
    private const int DefaultContentChunkSize = 512 * 2 * 1024; // 512 * 2 KB per chunk
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _contentPages = new();
    // DOM Diffing state
    private static readonly ConcurrentDictionary<string, string> _lastAriaSnapshots = new();
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

    [McpServerTool, Description("Start capturing console messages for the given page.")]
    public static void StartConsoleCapture(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        _consoleLogs.TryAdd(pageId, new ConcurrentQueue<object>());
        if (_consoleHandlers.ContainsKey(pageId)) return; // already capturing

        EventHandler<Microsoft.Playwright.IConsoleMessage> handler = (s, msg) =>
        {
            var item = new
            {
                type = msg.Type,
                text = msg.Text,
                location = msg.Location?.ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (_consoleLogs.TryGetValue(pageId, out var q)) q.Enqueue(item);
        };

        _consoleHandlers[pageId] = handler;
        page.Console += handler;
    }

    [McpServerTool, Description("Stop capturing console messages for the given page.")]
    public static void StopConsoleCapture(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        if (_consoleHandlers.TryRemove(pageId, out var handler))
        {
            page.Console -= handler;
        }
    }

    [McpServerTool, Description("Get captured console messages for the page as JSON array.")]
    public static string GetConsoleMessages(string pageId)
    {
        EnsureManager();
        if (!_consoleLogs.TryGetValue(pageId, out var q)) return "[]";
        var arr = q.ToArray();
        return JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("Clear captured console messages for the page.")]
    public static void ClearConsoleMessages(string pageId)
    {
        EnsureManager();
        _consoleLogs[pageId] = new ConcurrentQueue<object>();
    }

    [McpServerTool, Description("Start capturing network requests/responses for the given page. Captured responses will include bodies (base64) when available.")]
    public static void StartNetworkCapture(string pageId)
    {
        EnsureManager();
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
                byte[]? body = null;
                try { body = await resp.BodyAsync(); } catch { /* ignore body read errors */ }
                string? bodyB64 = body is null ? null : Convert.ToBase64String(body);

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

    [McpServerTool, Description("Stop capturing network for the given page.")]
    public static void StopNetworkCapture(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        if (_requestHandlers.TryRemove(pageId, out var r)) page.Request -= r;
        if (_responseHandlers.TryRemove(pageId, out var s)) page.Response -= s;
    }

    [McpServerTool, Description("List captured network transactions (summaries) for the page as JSON array. Filter options: 'xhr_only', 'errors_only', or null for all. Set excludeStaticAssets=false to include images/fonts/css. Use domainFilter (e.g. 'api.kuda.com') to constrain results.")]
    public static string ListNetworkTransactions(string pageId, string? filter = null, bool excludeStaticAssets = true, string? domainFilter = null)
    {
        EnsureManager();
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "[]";
        _networkOrder.TryGetValue(pageId, out var orderQ);
        var ids = orderQ?.ToArray() ?? map.Keys.ToArray();
        var summaries = new List<NetworkSummary>();
        
        var normalizedFilter = filter?.ToLowerInvariant();

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

    [McpServerTool, Description("Get detailed request+response for a captured network transaction by id. Returns JSON with headers and base64 bodies when available.")]
    public static string GetNetworkTransactionDetails(string pageId, string transactionId)
    {
        EnsureManager();
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "null";
        if (!map.TryGetValue(transactionId, out var tx)) return "null";
        return JsonSerializer.Serialize(tx, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("Clear captured network entries for the page.")]
    public static void ClearNetworkEntries(string pageId)
    {
        EnsureManager();
        _networkTransactions[pageId] = new ConcurrentDictionary<string, NetworkTransaction>();
        _networkOrder[pageId] = new ConcurrentQueue<string>();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
    }

    [McpServerTool, Description("List captured resource URLs for the page as JSON array.")]
    public static string ListCapturedSources(string pageId)
    {
        EnsureManager();
        if (!_sources.TryGetValue(pageId, out var map)) return "[]";
        var arr = map.Keys.ToArray();
        return JsonSerializer.Serialize(arr);
    }

    [McpServerTool, Description("Get captured source/body by URL (base64). Returns null if not captured.")]
    public static string? GetCapturedSource(string pageId, string url)
    {
        EnsureManager();
        if (!_sources.TryGetValue(pageId, out var map)) return null;
        if (map.TryGetValue(url, out var bodyB64)) return bodyB64;
        return null;
    }

    [McpServerTool, Description("Clear captured sources for the page.")]
    public static void ClearCapturedSources(string pageId)
    {
        EnsureManager();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
    }

    [McpServerTool, Description("Get the Playwright accessibility snapshot (AX tree) (Prefer this to GetContent or screenshot) for the page. Returns JSON representation of the accessibility tree. Set interestingOnly to true to filter to nodes Playwright considers interesting.")]
    public static async Task<string> GetAccessibilitySnapshot(string pageId, bool interestingOnly = true)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(pageId)) throw new ArgumentException("pageId is required", nameof(pageId));

        var page = _manager!.GetPage(pageId);
        if (page == null) throw new ArgumentException("Unknown pageId", nameof(pageId));

        try
        {
            // Use locator-based aria snapshot (recommended replacement for page.Accessibility)
            // Capture the aria snapshot for the document body. Playwright returns a YAML string.
            var locator = page.Locator("body");
            string ariaSnapshot = await locator.AriaSnapshotAsync();

            _lastAriaSnapshots[pageId] = ariaSnapshot;

            var payload = new { ariaSnapshot };
            var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            return JsonSerializer.Serialize(payload, opts);
        }
        catch (Exception ex)
        {
            var err = new { error = $"Could not retrieve accessibility snapshot: {ex.Message}" };
            return JsonSerializer.Serialize(err, new JsonSerializerOptions { WriteIndented = false });
        }
    }

    [McpServerTool, Description("Get a unified diff of the Accessibility Snapshot since the last time this tool or GetAccessibilitySnapshot was called. Set resetBaseline to true to clear any old state and just return the full raw snapshot.")]
    public static async Task<string> GetAccessibilityDiff(string pageId, bool resetBaseline = false)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(pageId)) return "Error: pageId is required";

        var page = _manager!.GetPage(pageId);
        if (page == null) return "Error: Unknown pageId";

        try
        {
            var locator = page.Locator("body");
            string newSnapshot = await locator.AriaSnapshotAsync();

            string? oldSnapshot = null;
            if (!resetBaseline && _lastAriaSnapshots.TryGetValue(pageId, out var existing))
            {
                oldSnapshot = existing;
            }

            _lastAriaSnapshots[pageId] = newSnapshot;

            if (string.IsNullOrEmpty(oldSnapshot))
            {
                var payload = new { status = "Initial State (No baseline to diff against)", diff = newSnapshot };
                var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                return JsonSerializer.Serialize(payload, opts);
            }

            var diff = InlineDiffBuilder.Diff(oldSnapshot, newSnapshot);
            var sb = new StringBuilder();
            bool hasChanges = false;
            foreach (var line in diff.Lines)
            {
                if (line.Type == ChangeType.Inserted) 
                {
                    sb.AppendLine($"+ {line.Text}");
                    hasChanges = true;
                }
                else if (line.Type == ChangeType.Deleted) 
                {
                    sb.AppendLine($"- {line.Text}");
                    hasChanges = true;
                }
                else if (line.Type == ChangeType.Unchanged) 
                {
                    sb.AppendLine($"  {line.Text}");
                }
            }

            if (!hasChanges)
            {
                var payload = new { status = "No DOM/Accessibility changes detected since last call.", diff = "" };
                var opts = new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                return JsonSerializer.Serialize(payload, opts);
            }

            var resultPayload = new { status = "Differences found.", diff = sb.ToString() };
            return JsonSerializer.Serialize(resultPayload, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            return $"Error: Could not retrieve or compute accessibility diff: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clear all captured investigator data (console, network, sources) for a page.")]
    public static void ClearAllCapturedData(string pageId)
    {
        EnsureManager();
        _consoleLogs[pageId] = new ConcurrentQueue<object>();
        _networkTransactions[pageId] = new ConcurrentDictionary<string, NetworkTransaction>();
        _networkOrder[pageId] = new ConcurrentQueue<string>();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
        _contentPages[pageId] = new ConcurrentDictionary<int, string>();
        _lastAriaSnapshots.TryRemove(pageId, out _);
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

    [McpServerTool, Description("List page ids for a given context as JSON array.")]
    public static string ListPages(string contextId)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(contextId)) throw new ArgumentException("contextId is required", nameof(contextId));
        try
        {
            var pages = _manager!.ListPages(contextId);
            return JsonSerializer.Serialize(pages);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not list pages for context '{contextId}': {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("Traces the redirect chain forwards and backwards for a given network transaction ID, returning the ordered lineage of requests.")]
    public static string TraceRedirectChain(string pageId, string transactionId)
    {
        EnsureManager();
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

    [McpServerTool, Description("Intercept network requests matching a URL pattern. You can abort, or inject mock JSON responses, or override headers/POST data securely. It will remain active until you call ClearInterceptions.")]
    public static async Task<string> InterceptAndModify(
        string pageId, 
        string urlPattern, 
        string? mockJsonResponse = null, 
        int? mockStatus = null, 
        Dictionary<string, string>? overrideHeaders = null, 
        string? overridePostData = null,
        bool abortRequest = false)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        try 
        {
            await page.RouteAsync(urlPattern, async route => 
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
            return $"Success: Interceptor activated for '{urlPattern}'.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clears all active network interceptions on the page.")]
    public static async Task<string> ClearInterceptions(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        try
        {
            await page.UnrouteAllAsync();
            return "Success: All network interceptions cleared.";
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
            return $"Success: Global Context Interceptor activated for '{urlPattern}'.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Clears all active network interceptions across the entire context.")]
    public static async Task<string> ClearContextInterceptions(string contextId)
    {
        EnsureManager();
        var context = _manager!.GetContext(contextId);
        try
        {
            await context.UnrouteAllAsync();
            return "Success: All context-wide network interceptions cleared.";
        }
        catch(Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static void EnsureManager()
    {
        if (_manager is null) throw new InvalidOperationException("PlaywrightManager not initialized. Ensure it's registered and created at startup.");
    }
}
