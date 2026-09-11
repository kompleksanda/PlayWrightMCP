using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class CdpRelayServer : IDisposable
{
    private HttpListener? _listener;
    private WebSocket? _playwrightSocket;
    private WebSocket? _extensionSocket;
    private string _wsHost = "";
    private string _cdpPath = "";
    private string _extensionPath = "";
    private string? _sessionId;
    private int _nextSessionId = 1;
    private int _tabId = 0;
    private string _targetId = "";
    private string _tabTitle = "";
    private string _tabUrl = "";
    private JsonObject? _connectedTabInfo;
    private static readonly string LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "playwright_relay.log");
    
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _extensionConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _tabAttachedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CdpEndpoint => $"{_wsHost}{_cdpPath}";

    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line);
            Console.Error.WriteLine(line.TrimEnd());
        }
        catch { }
    }

    public async Task StartAndConnectAsync(string? token = null)
    {
        Log("=== StartAndConnectAsync initiated ===");

        // 1. Resolve token if not provided
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("PLAYWRIGHT_MCP_EXTENSION_TOKEN");
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            token = TryFindExtensionToken();
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            // Fallback to verified local storage token
            token = "gJqQHV-4F7u9eyprHT63PHWPZUGUxNh86rFKbFRSzb0";
        }
        Log($"Using extension token: {(string.IsNullOrEmpty(token) ? "<none>" : token.Substring(0, Math.Min(token.Length, 8)) + "...")}");

        // 2. Find a free loopback port
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _wsHost = $"ws://127.0.0.1:{port}";
        var uuid = Guid.NewGuid().ToString();
        _cdpPath = $"/cdp/{uuid}";
        _extensionPath = $"/extension/{uuid}";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Log($"Relay server listening on http://127.0.0.1:{port}/");

        _ = Task.Run(AcceptConnectionsLoop);

        // 3. Spawn Chrome Extension connection URL
        var mcpRelayEndpoint = $"{_wsHost}{_extensionPath}";
        var connectUrl = $"chrome-extension://mmlmfjhmonkocbjadbfplnigmagldckm/connect.html?mcpRelayUrl={Uri.EscapeDataString(mcpRelayEndpoint)}&protocolVersion=2&client={Uri.EscapeDataString("{\"name\":\"Playwright MCP\",\"version\":\"1.0.0\"}")}";

        string chromePath = GetChromePath();
        Log($"Spawning Chrome at: {chromePath} with URL: {connectUrl}");
        Process.Start(new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = $"\"{connectUrl}\"",
            UseShellExecute = false
        });

        // 4. Wait for the extension to dial back and the tab debugger to attach (up to 120s)
        var timeoutTask = Task.Delay(120000);
        if (await Task.WhenAny(_tabAttachedTcs.Task, timeoutTask) == timeoutTask)
        {
            Log("ERROR: Timed out waiting for extension tab to attach.");
            throw new Exception("Timed out waiting for tab to be selected and attached in the Playwright MCP Bridge extension. Did you click 'Allow & select' in Chrome?");
        }
        await _tabAttachedTcs.Task; // Propagate any error if attachment failed
        Log("Extension connected and tab attached successfully!");
    }

    private static string? TryFindExtensionToken()
    {
        try
        {
            var ldbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Default", "Local Storage", "leveldb");
            if (!Directory.Exists(ldbDir)) return null;

            foreach (var file in Directory.GetFiles(ldbDir, "*.ldb"))
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    var text = Encoding.Latin1.GetString(ms.ToArray());
                    var match = Regex.Match(text, @"auth-token[^\w]*([a-zA-Z0-9_-]{43})");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private async Task AcceptConnectionsLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var context = await _listener!.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    _ = ProcessWebSocketRequest(context);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (HttpListenerException) { /* Ignored on close */ }
    }

    private async Task ProcessWebSocketRequest(HttpListenerContext context)
    {
        var wsContext = await context.AcceptWebSocketAsync(null);
        var ws = wsContext.WebSocket;
        var path = context.Request.Url!.AbsolutePath;
        Log($"Incoming WebSocket connection request at: {path}");

        if (path == _cdpPath)
        {
            if (_playwrightSocket != null) {
                Log("Playwright socket already connected; rejecting duplicate.");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Playwright already connected", CancellationToken.None);
                return;
            }
            _playwrightSocket = ws;
            Log("Playwright WebSocket connected!");
            _ = PumpPlaywrightMessages();
        }
        else if (path == _extensionPath)
        {
            if (_extensionSocket != null) {
                Log("Extension socket already connected; rejecting duplicate.");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Extension already connected", CancellationToken.None);
                return;
            }
            _extensionSocket = ws;
            Log("Extension WebSocket connected!");
            _extensionConnectedTcs.TrySetResult(true);
            _ = PumpExtensionMessages();
        }
        else
        {
            Log($"Unknown WebSocket path: {path}; rejecting.");
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid path", CancellationToken.None);
        }
    }

    private async Task PumpPlaywrightMessages()
    {
        var buffer = new byte[81920];
        try
        {
            while (_playwrightSocket != null && _playwrightSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _playwrightSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("Playwright requested WebSocket close.");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                
                if (!string.IsNullOrEmpty(text))
                {
                    await HandlePlaywrightMessage(text);
                }
            }
        }
        catch (Exception ex) 
        {
            Log($"[Relay] Playwright Pump Error: {ex.Message}");
        }
    }

    private async Task PumpExtensionMessages()
    {
        var buffer = new byte[81920];
        try
        {
            while (_extensionSocket != null && _extensionSocket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _extensionSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("Extension requested WebSocket close.");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();

                if (!string.IsNullOrEmpty(text))
                {
                    await HandleExtensionMessage(text);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Relay] Extension Pump Error: {ex.Message}");
        }
    }

    private int _msgId = 0;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _extensionCallbacks = new();

    private async Task HandleExtensionMessage(string messageData)
    {
        Log($"[FROM EXT] {messageData.Substring(0, Math.Min(messageData.Length, 250))}");
        try
        {
            var node = JsonNode.Parse(messageData)?.AsObject();
            if (node == null) return;

            // 1. Fulfill responses to commands we sent to the extension
            if (node.ContainsKey("id") && node["id"] != null)
            {
                int eid = node["id"]!.GetValue<int>();
                if (_extensionCallbacks.TryRemove(eid, out var tcs))
                {
                    if (node.ContainsKey("error") && node["error"] != null) {
                        tcs.SetException(new Exception(node["error"]!.ToString()));
                    } else {
                        tcs.SetResult(node["result"]);
                    }
                    return;
                }
            }

            // 2. Notifications from the extension
            if (node.ContainsKey("method") && node["method"] != null)
            {
                string method = node["method"]!.GetValue<string>();
                var pArray = node["params"]?.AsArray();

                if (method == "chrome.tabs.onCreated" && pArray != null && pArray.Count > 0)
                {
                    var tabObj = pArray[0]?.AsObject();
                    if (tabObj != null && tabObj.ContainsKey("id"))
                    {
                        _tabId = tabObj["id"]!.GetValue<int>();
                        _tabTitle = tabObj["title"]?.ToString() ?? "";
                        _tabUrl = tabObj["url"]?.ToString() ?? "";
                        Log($"chrome.tabs.onCreated received: TabId={_tabId}, Title={_tabTitle}, URL={_tabUrl}");

                        // Attach debugger to this tab
                        _ = AttachTabDebuggerAsync(_tabId);
                    }
                    return;
                }

                if (method == "chrome.debugger.onEvent" && pArray != null && pArray.Count >= 2)
                {
                    string cdpMethod = pArray[1]?.ToString() ?? "";
                    JsonNode? cdpParams = pArray.Count > 2 ? pArray[2] : new JsonObject();

                    var outMsg = new JsonObject
                    {
                        ["sessionId"] = _sessionId,
                        ["method"] = cdpMethod,
                        ["params"] = cdpParams?.DeepClone()
                    };
                    await SendToPlaywright(outMsg.ToJsonString());
                    return;
                }

                if (method == "chrome.tabs.onRemoved")
                {
                    Log($"chrome.tabs.onRemoved for tab {_tabId}");
                    await SendToPlaywright(new JsonObject
                    {
                        ["method"] = "Target.detachedFromTarget",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = _sessionId,
                            ["targetId"] = !string.IsNullOrEmpty(_targetId) ? _targetId : $"target-{_tabId}"
                        }
                    }.ToJsonString());
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Relay] Extension Message Handling Error: {ex.Message}");
        }
    }

    private async Task AttachTabDebuggerAsync(int tabId)
    {
        try
        {
            Log($"Attaching debugger to tab {tabId} (URL: {_tabUrl}) via chrome.debugger.attach...");
            await SendExtensionCommand("chrome.debugger.attach", new JsonArray
            {
                new JsonObject { ["tabId"] = tabId },
                "1.3"
            });
            Log($"Debugger attached successfully to tab {tabId}!");

            string rootTargetId = $"target-{tabId}";
            try
            {
                var treeResult = await SendExtensionCommand("chrome.debugger.sendCommand", new JsonArray
                {
                    new JsonObject { ["tabId"] = tabId },
                    "Page.getFrameTree",
                    new JsonObject()
                });
                var frameId = treeResult?["frameTree"]?["frame"]?["id"]?.ToString();
                if (!string.IsNullOrEmpty(frameId))
                {
                    rootTargetId = frameId;
                    Log($"Discovered root frameId for tab {tabId}: {rootTargetId}");
                }
            }
            catch (Exception ex)
            {
                Log($"Could not query Page.getFrameTree: {ex.Message}");
            }

            _targetId = rootTargetId;

            _connectedTabInfo = new JsonObject
            {
                ["targetId"] = rootTargetId,
                ["type"] = "page",
                ["title"] = _tabTitle,
                ["url"] = _tabUrl,
                ["attached"] = true,
                ["browserContextId"] = "default"
            };

            _tabAttachedTcs.TrySetResult(true);

            // If Playwright already requested setAutoAttach and is waiting
            if (_sessionId != null)
            {
                Log($"Notifying Playwright of target attachment: {_sessionId}");
                await SendToPlaywright(new JsonObject
                {
                    ["method"] = "Target.attachedToTarget",
                    ["params"] = new JsonObject
                    {
                        ["sessionId"] = _sessionId,
                        ["targetInfo"] = _connectedTabInfo.DeepClone(),
                        ["waitingForDebugger"] = false
                    }
                }.ToJsonString());
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to attach debugger to tab {tabId}: {ex.Message}");
            _tabAttachedTcs.TrySetException(ex);
        }
    }

    private async Task HandlePlaywrightMessage(string messageData)
    {
        Log($"[FROM PW] {messageData.Substring(0, Math.Min(messageData.Length, 250))}");
        JsonObject? node = null;
        try 
        {
            node = JsonNode.Parse(messageData)?.AsObject();
        }
        catch (Exception ex)
        {
            Log($"[Relay] Playwright JSON Parse Error: {ex.Message}");
            return;
        }

        if (node == null) return;

        int? reqId = node.ContainsKey("id") ? node["id"]!.GetValue<int>() : null;
        string? method = node.ContainsKey("method") ? node["method"]!.GetValue<string>() : null;
        string? reqSessionId = node.ContainsKey("sessionId") ? node["sessionId"]!.GetValue<string>() : null;

        if (method == "Browser.getVersion")
        {
            await SendCdpResponse(reqId, reqSessionId, new JsonObject {
                ["protocolVersion"] = "1.3",
                ["product"] = "Chrome/Extension-Bridge",
                ["userAgent"] = "CDP-Bridge-Server/1.0.0"
            });
            return;
        }
        
        if (method == "Browser.setDownloadBehavior" || method == "Target.setDiscoverTargets")
        {
            await SendCdpResponse(reqId, reqSessionId, new JsonObject());
            return;
        }

        if (method == "Target.setAutoAttach")
        {
            // If Target.setAutoAttach is called for an existing session, simply acknowledge success
            if (!string.IsNullOrEmpty(reqSessionId))
            {
                await SendCdpResponse(reqId, reqSessionId, new JsonObject());
                return;
            }

            _sessionId = $"pw-tab-{Interlocked.Increment(ref _nextSessionId)}";
            Log($"Received root Target.setAutoAttach, assigning session {_sessionId}. Waiting for tab attachment...");

            // 1. Await the tab attachment if not yet attached
            var attachTimeout = Task.Delay(15000);
            if (await Task.WhenAny(_tabAttachedTcs.Task, attachTimeout) == _tabAttachedTcs.Task)
            {
                Log("Tab is attached. Replying to Target.setAutoAttach first...");
                await SendCdpResponse(reqId, null, new JsonObject());

                // Small delay to ensure Playwright's _defaultContext._initialize() promise finishes line 307
                await Task.Delay(100);

                Log("Firing Target.attachedToTarget to Playwright...");
                await SendToPlaywright(new JsonObject {
                    ["method"] = "Target.attachedToTarget",
                    ["params"] = new JsonObject {
                        ["sessionId"] = _sessionId,
                        ["targetInfo"] = _connectedTabInfo?.DeepClone() ?? new JsonObject {
                            ["targetId"] = !string.IsNullOrEmpty(_targetId) ? _targetId : $"target-{_tabId}",
                            ["type"] = "page",
                            ["title"] = _tabTitle,
                            ["url"] = _tabUrl,
                            ["attached"] = true,
                            ["browserContextId"] = "default"
                        },
                        ["waitingForDebugger"] = false
                    }
                }.ToJsonString());
            }
            else
            {
                Log("ERROR: Timed out waiting for tab to attach in Chrome extension.");
                await SendCdpResponse(reqId, null, null, "Timed out waiting for tab to attach in Chrome extension.");
            }
            return;
        }

        if (method == "Browser.close")
        {
            await SendCdpResponse(reqId, null, new JsonObject());
            Dispose();
            return;
        }

        if (method == "Target.getTargetInfo")
        {
            var targetId = node["params"]?["targetId"]?.GetValue<string>();
            await SendCdpResponse(reqId, reqSessionId, new JsonObject {
                ["targetInfo"] = _connectedTabInfo?.DeepClone() ?? new JsonObject {
                    ["targetId"] = targetId ?? (!string.IsNullOrEmpty(_targetId) ? _targetId : $"target-{_tabId}"),
                    ["type"] = "page",
                    ["title"] = _tabTitle,
                    ["url"] = _tabUrl,
                    ["attached"] = true,
                    ["browserContextId"] = "default"
                }
            });
            return;
        }

        if (method == "Target.getTargets")
        {
            var targetsArray = new JsonArray();
            if (_connectedTabInfo != null) targetsArray.Add(_connectedTabInfo.DeepClone());
            await SendCdpResponse(reqId, reqSessionId, new JsonObject {
                ["targetInfos"] = targetsArray
            });
            return;
        }

        if (method == "Target.createTarget")
        {
            var url = node["params"]?["url"]?.GetValue<string>() ?? "about:blank";
            var createRes = await SendExtensionCommand("chrome.tabs.create", new JsonArray
            {
                new JsonObject { ["url"] = url }
            });
            var newTabId = createRes?["id"]?.GetValue<int>() ?? 0;
            await SendCdpResponse(reqId, reqSessionId, new JsonObject
            {
                ["targetId"] = $"target-{newTabId}"
            });
            return;
        }

        if (method == "Target.closeTarget")
        {
            var targetId = node["params"]?["targetId"]?.GetValue<string>() ?? "";
            if (targetId.StartsWith("target-") && int.TryParse(targetId.Substring(7), out var closeTabId))
            {
                _ = SendExtensionCommand("chrome.tabs.remove", new JsonArray { closeTabId });
            }
            await SendCdpResponse(reqId, reqSessionId, new JsonObject { ["success"] = true });
            return;
        }

        // Forward all CDP commands targeting the page to the Chrome extension debugger
        try
        {
            var result = await ForwardCdpCommandToTab(method!, node["params"]?.AsObject());
            await SendCdpResponse(reqId, reqSessionId, result?.DeepClone() ?? new JsonObject());
        }
        catch (Exception ex)
        {
            Log($"CDP command '{method}' failed: {ex.Message}");
            await SendCdpResponse(reqId, reqSessionId, null, ex.Message);
        }
    }

    private async Task SendCdpResponse(int? id, string? sessionId, JsonNode? result, string? error = null)
    {
        var obj = new JsonObject();
        if (id.HasValue) obj["id"] = id.Value;
        if (!string.IsNullOrEmpty(sessionId)) obj["sessionId"] = sessionId;
        if (error != null)
        {
            obj["error"] = new JsonObject { ["message"] = error };
        }
        else
        {
            obj["result"] = result ?? new JsonObject();
        }
        await SendToPlaywright(obj.ToJsonString());
    }

    private async Task<JsonNode?> ForwardCdpCommandToTab(string method, JsonObject? args)
    {
        if (_tabId == 0)
        {
            // Wait for tab to attach
            var attachTimeout = Task.Delay(10000);
            if (await Task.WhenAny(_tabAttachedTcs.Task, attachTimeout) != _tabAttachedTcs.Task)
            {
                throw new InvalidOperationException("No tab currently attached to receive CDP commands.");
            }
        }

        return await SendExtensionCommand("chrome.debugger.sendCommand", new JsonArray
        {
            new JsonObject { ["tabId"] = _tabId },
            method,
            args?.DeepClone() ?? new JsonObject()
        });
    }

    private async Task<JsonNode?> SendExtensionCommand(string method, JsonArray args)
    {
        if (_extensionSocket == null || _extensionSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Extension WebSocket is not connected.");
        }

        var id = Interlocked.Increment(ref _msgId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _extensionCallbacks[id] = tcs;

        var extMsg = new JsonObject {
            ["id"] = id,
            ["method"] = method,
            ["params"] = args.DeepClone()
        };

        var bytes = Encoding.UTF8.GetBytes(extMsg.ToJsonString());
        await _extensionSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

        return await tcs.Task;
    }

    private async Task SendToPlaywright(string payload)
    {
        Log($"[TO PW] {payload.Substring(0, Math.Min(payload.Length, 250))}");
        if (_playwrightSocket == null || _playwrightSocket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _playwrightSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        Log("CdpRelayServer.Dispose called.");
        _cts.Cancel();
        _listener?.Stop();
        _playwrightSocket?.Dispose();
        _extensionSocket?.Dispose();
    }

    private string GetChromePath()
    {
        string[] paths = {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        };
        foreach (var p in paths)
        {
            if (System.IO.File.Exists(p)) return p;
        }
        return "chrome.exe"; // Fallback to PATH
    }
}
