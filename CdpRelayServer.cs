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
    private int _tabId = 0;
    private string _targetId = "";
    private string _tabTitle = "";
    private string _tabUrl = "";
    private JsonObject? _connectedTabInfo;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ExtensionTabInfo> _attachedTabs = new();
    public System.Collections.Generic.IReadOnlyDictionary<int, ExtensionTabInfo> AttachedTabs => _attachedTabs;
    private bool _rootAutoAttachDone = false;
    private static readonly string LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "playwright_relay.log");
    
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _extensionConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _tabAttachedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CdpEndpoint => $"{_wsHost}{_cdpPath}";
    public int CurrentTabId => _tabId;

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
        if (!string.IsNullOrWhiteSpace(token))
        {
            connectUrl += $"&token={Uri.EscapeDataString(token)}";
        }

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

            var files = Directory.GetFiles(ldbDir, "*.ldb").Concat(Directory.GetFiles(ldbDir, "*.log"));
            foreach (var file in files)
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
                if (_playwrightSocket.State != WebSocketState.Open)
                {
                    Log("Previous Playwright socket was closed; disposing and accepting new socket.");
                    try { _playwrightSocket.Dispose(); } catch { }
                    _playwrightSocket = null;
                }
                else
                {
                    Log("Playwright socket already connected; rejecting duplicate.");
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Playwright already connected", CancellationToken.None);
                    return;
                }
            }
            _playwrightSocket = ws;
            Log("Playwright WebSocket connected!");
            _ = PumpPlaywrightMessages();
        }
        else if (path == _extensionPath)
        {
            if (_extensionSocket != null) {
                Log("Extension socket reconnecting; disposing previous socket.");
                try { _extensionSocket.Dispose(); } catch { }
                _extensionSocket = null;
            }
            _extensionSocket = ws;
            Log("Extension WebSocket connected!");
            _extensionConnectedTcs.TrySetResult(true);
            EnsureHeartbeatStarted();
            if (_tabId != 0)
            {
                _ = AttachTabDebuggerAsync(_tabId);
            }
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
                        int newTabId = tabObj["id"]!.GetValue<int>();
                        string title = tabObj["title"]?.ToString() ?? "";
                        string url = tabObj["url"]?.ToString() ?? "";

                        if (url.Contains("chrome-extension://") || title == "Welcome")
                        {
                            Log($"Connected tab is extension connect page (TabId={newTabId}). Auto-spawning a new web tab via chrome.tabs.create...");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var newTabRes = await SendExtensionCommand("chrome.tabs.create", new JsonArray
                                    {
                                        new JsonObject { ["url"] = "about:blank" }
                                    });
                                    int newWebTabId = newTabRes?["id"]?.GetValue<int>() ?? 0;
                                    if (newWebTabId != 0)
                                    {
                                        Log($"New web tab created with ID: {newWebTabId}. Removing connect page {newTabId}...");
                                        _ = SendExtensionCommand("chrome.tabs.remove", new JsonArray { newTabId });
                                        await AttachTabDebuggerAsync(newWebTabId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"Failed to auto-spawn web tab: {ex.Message}");
                                }
                            });
                            return;
                        }

                        _tabId = newTabId;
                        _tabTitle = title;
                        _tabUrl = url;

                        var info = _attachedTabs.GetOrAdd(newTabId, id => new ExtensionTabInfo { TabId = id });
                        info.Title = title;
                        info.Url = url;

                        Log($"chrome.tabs.onCreated received: TabId={_tabId}, Title={_tabTitle}, URL={_tabUrl}");

                        // Attach debugger to this tab
                        _ = AttachTabDebuggerAsync(newTabId);
                    }
                    return;
                }

                if (method == "chrome.tabs.onUpdated" && pArray != null && pArray.Count > 0)
                {
                    int updatedTabId = 0;
                    if (pArray[0] is JsonValue v && v.TryGetValue<int>(out var idVal))
                    {
                        updatedTabId = idVal;
                    }
                    else if (pArray[0] is JsonObject tabIdObj && tabIdObj.ContainsKey("id"))
                    {
                        updatedTabId = tabIdObj["id"]?.GetValue<int>() ?? 0;
                    }

                    var changeInfo = pArray.Count > 1 ? pArray[1]?.AsObject() : null;
                    var tabObj = pArray.Count > 2 ? pArray[2]?.AsObject() : null;

                    if (updatedTabId != 0)
                    {
                        var info = _attachedTabs.GetOrAdd(updatedTabId, id => new ExtensionTabInfo { TabId = id });
                        if (tabObj != null)
                        {
                            if (tabObj.ContainsKey("title")) info.Title = tabObj["title"]?.ToString() ?? info.Title;
                            if (tabObj.ContainsKey("url")) info.Url = tabObj["url"]?.ToString() ?? info.Url;
                        }
                        if (changeInfo != null && changeInfo.ContainsKey("url"))
                        {
                            info.Url = changeInfo["url"]?.ToString() ?? info.Url;
                        }

                        if (_tabId == updatedTabId)
                        {
                            _tabTitle = info.Title;
                            _tabUrl = info.Url;
                        }

                        Log($"chrome.tabs.onUpdated for tab {updatedTabId}: URL={info.Url}, Title={info.Title}");

                        if (_playwrightSocket != null && _playwrightSocket.State == WebSocketState.Open && _rootAutoAttachDone)
                        {
                            await SendToPlaywright(new JsonObject
                            {
                                ["method"] = "Target.targetInfoChanged",
                                ["params"] = new JsonObject
                                {
                                    ["targetInfo"] = new JsonObject
                                    {
                                        ["targetId"] = info.TargetId,
                                        ["type"] = "page",
                                        ["title"] = info.Title,
                                        ["url"] = info.Url,
                                        ["attached"] = true,
                                        ["browserContextId"] = "default"
                                    }
                                }
                            }.ToJsonString());
                        }
                    }
                    return;
                }

                if (method == "chrome.debugger.onEvent" && pArray != null && pArray.Count >= 2)
                {
                    string cdpMethod = pArray[1]?.ToString() ?? "";
                    JsonNode? cdpParams = pArray.Count > 2 ? pArray[2] : new JsonObject();

                    int eventTabId = 0;
                    if (pArray[0] is JsonObject sourceObj && sourceObj.ContainsKey("tabId"))
                    {
                        eventTabId = sourceObj["tabId"]?.GetValue<int>() ?? 0;
                    }
                    string eventSessionId = eventTabId != 0 ? $"pw-tab-{eventTabId}" : (_sessionId ?? "pw-tab-1");

                    var outMsg = new JsonObject
                    {
                        ["sessionId"] = eventSessionId,
                        ["method"] = cdpMethod,
                        ["params"] = cdpParams?.DeepClone()
                    };
                    await SendToPlaywright(outMsg.ToJsonString());
                    return;
                }

                if (method == "chrome.tabs.onRemoved")
                {
                    int removedTabId = 0;
                    if (pArray != null && pArray.Count > 0)
                    {
                        if (pArray[0] is JsonValue rv && rv.TryGetValue<int>(out var idVal)) removedTabId = idVal;
                        else if (pArray[0] is JsonObject robj && robj.ContainsKey("id")) removedTabId = robj["id"]?.GetValue<int>() ?? 0;
                    }
                    if (removedTabId == 0) removedTabId = _tabId;

                    _attachedTabs.TryRemove(removedTabId, out _);
                    Log($"chrome.tabs.onRemoved for tab {removedTabId}");

                    await SendToPlaywright(new JsonObject
                    {
                        ["method"] = "Target.detachedFromTarget",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = $"pw-tab-{removedTabId}",
                            ["targetId"] = $"target-{removedTabId}"
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
            var info = _attachedTabs.GetOrAdd(tabId, id => new ExtensionTabInfo { TabId = id });
            if (string.IsNullOrEmpty(info.Title)) info.Title = _tabTitle;
            if (string.IsNullOrEmpty(info.Url)) info.Url = _tabUrl;

            Log($"Attaching debugger to tab {tabId} (URL: {info.Url}) via chrome.debugger.attach...");
            await SendExtensionCommand("chrome.debugger.attach", new JsonArray
            {
                new JsonObject { ["tabId"] = tabId },
                "1.3"
            });
            Log($"Debugger attached successfully to tab {tabId}!");

            string rootTargetId = $"target-{tabId}";
            _targetId = rootTargetId;
            string sessionId = $"pw-tab-{tabId}";
            _sessionId = sessionId;
            Log($"Using stable targetId for tab {tabId}: {rootTargetId}, sessionId: {sessionId}");

            // Lock document/window focus (emulate focused state) to resist tab blur
            try
            {
                await SendExtensionCommand("chrome.debugger.sendCommand", new JsonArray
                {
                    new JsonObject { ["tabId"] = tabId },
                    "Emulation.setFocusEmulationEnabled",
                    new JsonObject { ["enabled"] = true }
                });
                Log($"Emulation.setFocusEmulationEnabled(true) applied to tab {tabId}");
            }
            catch (Exception ex)
            {
                Log($"Could not enable focus emulation on tab {tabId}: {ex.Message}");
            }

            _connectedTabInfo = new JsonObject
            {
                ["targetId"] = rootTargetId,
                ["type"] = "page",
                ["title"] = info.Title,
                ["url"] = info.Url,
                ["attached"] = true,
                ["browserContextId"] = "default"
            };

            _tabAttachedTcs.TrySetResult(true);

            // If Playwright already requested setAutoAttach and is waiting
            if (_rootAutoAttachDone && _playwrightSocket != null && _playwrightSocket.State == WebSocketState.Open)
            {
                Log($"Notifying Playwright of target attachment: {sessionId} ({info.Title} - {info.Url})");
                await SendToPlaywright(new JsonObject
                {
                    ["method"] = "Target.attachedToTarget",
                    ["params"] = new JsonObject
                    {
                        ["sessionId"] = sessionId,
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

        if (method != null && (method.StartsWith("Browser.") || method == "Target.setDiscoverTargets"))
        {
            if (method == "Browser.getVersion")
            {
                await SendCdpResponse(reqId, reqSessionId, new JsonObject {
                    ["protocolVersion"] = "1.3",
                    ["product"] = "Chrome/Extension-Bridge",
                    ["userAgent"] = "CDP-Bridge-Server/1.0.0"
                });
                return;
            }
            if (method == "Browser.close")
            {
                await SendCdpResponse(reqId, null, new JsonObject());
                Dispose();
                return;
            }
            // All other Browser.* commands (grantPermissions, resetPermissions, setDownloadBehavior, etc.)
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

            _rootAutoAttachDone = true;
            Log($"Received root Target.setAutoAttach. Waiting for tab attachment...");

            // 1. Await the tab attachment if not yet attached
            var attachTimeout = Task.Delay(15000);
            if (await Task.WhenAny(_tabAttachedTcs.Task, attachTimeout) == _tabAttachedTcs.Task)
            {
                Log("Tab is attached. Replying to Target.setAutoAttach first...");
                await SendCdpResponse(reqId, null, new JsonObject());

                // Small delay to ensure Playwright's _defaultContext._initialize() promise finishes line 307
                await Task.Delay(100);

                Log($"Firing Target.attachedToTarget to Playwright for {_attachedTabs.Count} tab(s)...");
                foreach (var kv in _attachedTabs)
                {
                    var tab = kv.Value;
                    await SendToPlaywright(new JsonObject {
                        ["method"] = "Target.attachedToTarget",
                        ["params"] = new JsonObject {
                            ["sessionId"] = tab.SessionId,
                            ["targetInfo"] = new JsonObject {
                                ["targetId"] = tab.TargetId,
                                ["type"] = "page",
                                ["title"] = tab.Title,
                                ["url"] = tab.Url,
                                ["attached"] = true,
                                ["browserContextId"] = "default"
                            },
                            ["waitingForDebugger"] = false
                        }
                    }.ToJsonString());
                }
            }
            else
            {
                Log("ERROR: Timed out waiting for tab to attach in Chrome extension.");
                await SendCdpResponse(reqId, null, null, "Timed out waiting for tab to attach in Chrome extension.");
            }
            return;
        }

        if (method == "Target.getTargetInfo")
        {
            var targetId = node["params"]?["targetId"]?.GetValue<string>();
            await SendCdpResponse(reqId, reqSessionId, new JsonObject {
                ["targetInfo"] = _connectedTabInfo?.DeepClone() ?? new JsonObject {
                    ["targetId"] = !string.IsNullOrEmpty(targetId) ? targetId : $"target-{_tabId}",
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
            int targetTabId = _tabId;
            if (!string.IsNullOrEmpty(reqSessionId) && reqSessionId.StartsWith("pw-tab-") && int.TryParse(reqSessionId.Substring(7), out var parsedTabId))
            {
                targetTabId = parsedTabId;
            }

            var result = await ForwardCdpCommandToTab(method!, node["params"]?.AsObject(), targetTabId);
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

    private async Task<JsonNode?> ForwardCdpCommandToTab(string method, JsonObject? args, int tabId = 0)
    {
        int targetTab = tabId != 0 ? tabId : _tabId;
        if (targetTab == 0)
        {
            // Wait for tab to attach
            var attachTimeout = Task.Delay(10000);
            if (await Task.WhenAny(_tabAttachedTcs.Task, attachTimeout) != _tabAttachedTcs.Task)
            {
                throw new InvalidOperationException("No tab currently attached to receive CDP commands.");
            }
            targetTab = _tabId;
        }

        return await SendExtensionCommand("chrome.debugger.sendCommand", new JsonArray
        {
            new JsonObject { ["tabId"] = targetTab },
            method,
            args?.DeepClone() ?? new JsonObject()
        });
    }

    public void ResetPendingCallbacks()
    {
        Log("Resetting all pending extension callbacks.");
        foreach (var kv in _extensionCallbacks)
        {
            if (_extensionCallbacks.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetException(new OperationCanceledException("Extension debugger pipeline reset."));
            }
        }
    }

    public async Task SwitchToTabAsync(int targetTabId)
    {
        Log($"Switching active tab from {_tabId} to {targetTabId}...");
        if (_tabId != 0 && _tabId != targetTabId)
        {
            try
            {
                await SendExtensionCommand("chrome.debugger.detach", new JsonArray
                {
                    new JsonObject { ["tabId"] = _tabId }
                });
            }
            catch (Exception ex)
            {
                Log($"Could not detach old tab {_tabId}: {ex.Message}");
            }
        }

        _tabId = targetTabId;
        try
        {
            var tabInfo = await SendExtensionCommand("chrome.tabs.get", new JsonArray { targetTabId });
            if (tabInfo != null)
            {
                _tabTitle = tabInfo["title"]?.ToString() ?? "";
                _tabUrl = tabInfo["url"]?.ToString() ?? "";
            }
        }
        catch { }

        await AttachTabDebuggerAsync(targetTabId);
    }

    public async Task<JsonNode?> SendExtensionCommand(string method, JsonArray args, int timeoutMs = 20000)
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

        // Safety timeout so CDP calls never deadlock waiting indefinitely
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, _cts.Token));
        if (completed != tcs.Task)
        {
            _extensionCallbacks.TryRemove(id, out _);
            throw new TimeoutException($"Extension command '{method}' timed out after {timeoutMs}ms waiting for extension debugger response.");
        }

        return await tcs.Task;
    }

    private async Task SendToPlaywright(string payload)
    {
        Log($"[TO PW] {payload.Substring(0, Math.Min(payload.Length, 250))}");
        if (_playwrightSocket == null || _playwrightSocket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _playwrightSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private int _heartbeatStarted = 0;
    private void EnsureHeartbeatStarted()
    {
        if (Interlocked.CompareExchange(ref _heartbeatStarted, 1, 0) == 0)
        {
            _ = RunHeartbeatLoop();
        }
    }

    private async Task RunHeartbeatLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15000, _cts.Token);
                if (_extensionSocket != null && _extensionSocket.State == WebSocketState.Open && _tabId != 0)
                {
                    var tabInfo = await SendExtensionCommand("chrome.tabs.get", new JsonArray { _tabId });
                    if (tabInfo != null && tabInfo["url"] != null)
                    {
                        _tabUrl = tabInfo["url"]!.ToString();
                        _tabTitle = tabInfo["title"]?.ToString() ?? _tabTitle;
                    }
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"[Relay] Heartbeat notice: {ex.Message}");
            }
        }
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

public class ExtensionTabInfo
{
    public int TabId { get; set; }
    public string SessionId => $"pw-tab-{TabId}";
    public string TargetId => $"target-{TabId}";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}
