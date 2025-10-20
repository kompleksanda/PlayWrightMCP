using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Playwright;

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

    [McpServerTool, Description("Navigates the page to the specified url.")]
    public static async Task Navigate(string pageId, string url)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(pageId)) throw new ArgumentException("pageId is required", nameof(pageId));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url is required", nameof(url));

        var page = _manager!.GetPage(pageId);
        if (page == null) throw new ArgumentException("Unknown pageId", nameof(pageId));

        // If the page is closed, surface a clear error
        try
        {
            // IPage exposes IsClosed in Playwright .NET
            if (page.IsClosed)
                throw new InvalidOperationException("The requested page is already closed.");
        }
        catch
        {
            // If introspection fails, continue and let GotoAsync report the error
        }

        // Normalize/validate URL: if no scheme is provided, try http:// prefix
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "file" && uri.Scheme != "about"))
        {
            if (Uri.TryCreate("http://" + url, UriKind.Absolute, out var tryUri))
            {
                url = tryUri.ToString();
            }
            else
            {
                throw new ArgumentException($"Invalid URL: '{url}'", nameof(url));
            }
        }

        // Try a few sensible navigation strategies when timeouts occur on slow or resource-heavy sites.
        try
        {
            // Primary attempt: wait for network idle (most complete state) with a 30s timeout
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
            return;
        }
        catch (TimeoutException firstTimeout)
        {
            // Fallback 1: wait for full load with a longer timeout (some pages take longer to load resources)
            try
            {
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
                return;
            }
            catch (TimeoutException secondTimeout)
            {
                // Final fallback: navigate and return once navigation is committed (don't wait for network)
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 15000 });
                    return;
                }
                catch (Exception finalEx)
                {
                    throw new InvalidOperationException($"Navigation to '{url}' failed after multiple attempts: first timeout={firstTimeout.Message}; second timeout={secondTimeout.Message}; final error={finalEx.Message}", finalEx);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-timeout error: provide context
            throw new InvalidOperationException($"Navigation to '{url}' failed: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("Gets the page content (outer HTML of document).")]
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
        _contentPages.TryRemove(pageId, out var _);
        return content.Substring(0, Math.Min(content.Length, DefaultContentChunkSize));

        // Otherwise split into pages and store; return a small JSON metadata object pointing to pagination
        //PrepareContentPages(pageId, content);
        //var meta = new { paginated = true, pageCount = GetContentPageCount(pageId) };
        //return JsonSerializer.Serialize(meta);
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

    [McpServerTool, Description("Types text into selector on the page.")]
    public static async Task Type(string pageId, string selector, string text)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        await page.FillAsync(selector, text);
    }

    [McpServerTool, Description("Takes a screenshot of the page and returns a base64-encoded PNG.")]
    public static async Task<string> Screenshot(string pageId)
    {
        EnsureManager();
        var bytes = await _manager!.ScreenshotAsync(pageId);
        return Convert.ToBase64String(bytes);
    }

    // --- Investigator-style capture tools ---

    // Thread-safe storage for captured data per page
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<object>> _consoleLogs = new();
    // Chunked HTML/content storage: pageId -> (pageIndex -> chunk)
    private const int DefaultContentChunkSize = 512 * 2 * 1024; // 512 * 2 KB per chunk
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _contentPages = new();
    // Network transactions: pageId -> (transactionId -> transaction)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NetworkTransaction>> _networkTransactions = new();
    // Preserve order of transaction ids per page
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _networkOrder = new();
    // map request object to transaction id for pairing
    private static readonly ConcurrentDictionary<object, string> _requestToTransactionId = new();
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
        public int? Status { get; set; }
        public string? StatusText { get; set; }
        public long RequestTimestamp { get; set; }
        public long? ResponseTimestamp { get; set; }
    }

    private class NetworkTransaction
    {
        public string Id { get; set; } = string.Empty;
        // request side
        public string Url { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public Dictionary<string, string>? RequestHeaders { get; set; }
        public string? RequestBodyBase64 { get; set; }
        public long RequestTimestamp { get; set; }
        // response side
        public int? Status { get; set; }
        public string? StatusText { get; set; }
        public Dictionary<string, string>? ResponseHeaders { get; set; }
        public string? ResponseBodyBase64 { get; set; }
        public long? ResponseTimestamp { get; set; }
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
            var tx = new NetworkTransaction
            {
                Id = id,
                Url = req.Url,
                Method = req.Method,
                RequestBodyBase64 = bodyB64,
                RequestTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

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
            try { _requestToTransactionId[req] = id; } catch { }

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

    [McpServerTool, Description("List captured network transactions (summaries) for the page as JSON array.")]
    public static string ListNetworkTransactions(string pageId)
    {
        EnsureManager();
        if (!_networkTransactions.TryGetValue(pageId, out var map)) return "[]";
        _networkOrder.TryGetValue(pageId, out var orderQ);
        var ids = orderQ?.ToArray() ?? map.Keys.ToArray();
        var summaries = new List<NetworkSummary>();
        foreach (var id in ids)
        {
            if (map.TryGetValue(id, out var tx))
            {
                summaries.Add(new NetworkSummary
                {
                    Id = id,
                    Url = tx.Url,
                    Method = tx.Method,
                    Status = tx.Status,
                    StatusText = tx.StatusText,
                    RequestTimestamp = tx.RequestTimestamp,
                    ResponseTimestamp = tx.ResponseTimestamp
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

    [McpServerTool, Description("Clear all captured investigator data (console, network, sources) for a page.")]
    public static void ClearAllCapturedData(string pageId)
    {
        EnsureManager();
        _consoleLogs[pageId] = new ConcurrentQueue<object>();
        _networkTransactions[pageId] = new ConcurrentDictionary<string, NetworkTransaction>();
        _networkOrder[pageId] = new ConcurrentQueue<string>();
        _sources[pageId] = new ConcurrentDictionary<string, string>();
        _contentPages[pageId] = new ConcurrentDictionary<int, string>();
    }

    private static void EnsureManager()
    {
        if (_manager is null) throw new InvalidOperationException("PlaywrightManager not initialized. Ensure it's registered and created at startup.");
    }
}
