using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

[McpServerToolType]
public static class PlaywrightTools
{
    private static PlaywrightManager? _manager;

    // Called by DI on startup to set manager instance
    public static void SetManager(PlaywrightManager manager) => _manager = manager;

    // --- existing basic helpers ---
    [McpServerTool, Description("Launches a Chromium browser and returns a browserId.")]
    public static async Task<string> LaunchBrowser(bool headless = true)
    {
        EnsureManager();
        return await _manager!.LaunchBrowserAsync(headless);
    }

    [McpServerTool, Description("Creates a new browser context for given browserId and returns contextId.")]
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
        var page = _manager!.GetPage(pageId);
        await page.GotoAsync(url);
    }

    [McpServerTool, Description("Gets the page content (outer HTML of document).")]
    public static async Task<string> GetContent(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        return await page.ContentAsync();
    }

    [McpServerTool, Description("Takes an accessibility snapshot of the page and returns it as a JSON string.")]
    public static async Task<string> GetAccessibilitySnapshot(string pageId)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);

        try
        {
            // Prefer the strongly-typed API if available: page.Accessibility.SnapshotAsync()
            var accessibilityProp = page.GetType().GetProperty("Accessibility");
            if (accessibilityProp != null)
            {
                var accessibilityObj = accessibilityProp.GetValue(page);
                if (accessibilityObj != null)
                {
                    var snapshotMethod = accessibilityObj.GetType().GetMethod("SnapshotAsync", new Type[] { });
                    if (snapshotMethod != null)
                    {
                        var task = (System.Threading.Tasks.Task)snapshotMethod.Invoke(accessibilityObj, null)!;
                        await task.ConfigureAwait(false);
                        // Task<TResult> -> get Result
                        var resultProp = task.GetType().GetProperty("Result");
                        var result = resultProp?.GetValue(task);
                        // Serialize to JSON using System.Text.Json
                        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
                    }
                }
            }

            // Fallback: try invoking page.EvaluateAsync to call accessibility snapshot in-page
            try
            {
                var evalMethod = page.GetType().GetMethod("EvaluateAsync", new Type[] { typeof(string) });
                if (evalMethod != null)
                {
                    // This fallback attempts to run a small script to collect accessible name/role tree — limited but better than nothing.
                    string script = @"(()=>{
                        function nodeToObj(n){
                            const obj={role:n.role, name:n.name, value:n.value, children:[]};
                            if(n.children) for(const c of n.children) obj.children.push(nodeToObj(c));
                            return obj;
                        }
                        try{ const root = (window.__playwright_accessibility_snapshot && window.__playwright_accessibility_snapshot()) || null; return root; }catch(e){ return null; }
                    })()";
                    var task = (System.Threading.Tasks.Task)evalMethod.Invoke(page, new object[] { script })!;
                    await task.ConfigureAwait(false);
                    var resProp = task.GetType().GetProperty("Result");
                    var res = resProp?.GetValue(task);
                    return JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = false });
                }
            }
            catch { /* swallow fallback errors */ }
        }
        catch { /* swallow */ }

        // As a last resort, return an empty JSON object
        return "{}";
    }

    [McpServerTool, Description("Takes an accessibility snapshot of the page and saves it to the given file path. Returns the file path on success.")]
    public static async Task<string> SaveAccessibilitySnapshot(string pageId, string filePath)
    {
        EnsureManager();
        var json = await GetAccessibilitySnapshot(pageId);
        try
        {
            System.IO.File.WriteAllText(filePath, json, Encoding.UTF8);
            return filePath;
        }
        catch (System.Exception ex)
        {
            throw new System.InvalidOperationException($"Failed to write accessibility snapshot to file: {ex.Message}");
        }
    }

    [McpServerTool, Description("Clicks a selector on the page.")]
    public static async Task Click(string pageId, string selector)
    {
        EnsureManager();
        var page = _manager!.GetPage(pageId);
        await page.ClickAsync(selector);
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
    }

    private static void EnsureManager()
    {
        if (_manager is null) throw new InvalidOperationException("PlaywrightManager not initialized. Ensure it's registered and created at startup.");
    }
}
