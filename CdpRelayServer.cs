using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private JsonObject? _connectedTabInfo;
    
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _extensionConnectedTcs = new TaskCompletionSource<bool>();

    public string CdpEndpoint => $"{_wsHost}{_cdpPath}";

    public async Task StartAndConnectAsync(string token)
    {
        // 1. Find a free port
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

        _ = Task.Run(AcceptConnectionsLoop);

        // 2. Spawn Chrome Extension with specific URL
        var mcpRelayEndpoint = $"{_wsHost}{_extensionPath}";
        var connectUrl = $"chrome-extension://mmlmfjhmonkocbjadbfplnigmagldckm/connect.html?mcpRelayUrl={Uri.EscapeDataString(mcpRelayEndpoint)}";
        if (!string.IsNullOrEmpty(token)) {
            connectUrl += $"&token={Uri.EscapeDataString(token)}";
        }
        connectUrl += "&protocolVersion=1.3";
        // Pass a mock client param to pass extension JSON.parse requirements
        connectUrl += $"&client={Uri.EscapeDataString("{\"name\":\"C# Playwright Agent\",\"version\":\"1.0.0\"}")}";

        string chromePath = GetChromePath();
        Process.Start(new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = $"\"{connectUrl}\"",
            UseShellExecute = false
        });

        // 3. Wait for the extension to dial back to our WebSocket
        var timeoutTask = Task.Delay(15000);
        if (await Task.WhenAny(_extensionConnectedTcs.Task, timeoutTask) == timeoutTask)
        {
            throw new Exception("Timed out waiting for the Playwright MCP Bridge extension to connect back. Is the extension installed and enabled in Chrome?");
        }
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

        if (path == _cdpPath)
        {
            if (_playwrightSocket != null) {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Playwright already connected", CancellationToken.None);
                return;
            }
            _playwrightSocket = ws;
            _ = PumpPlaywrightMessages();
        }
        else if (path == _extensionPath)
        {
            if (_extensionSocket != null) {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Extension already connected", CancellationToken.None);
                return;
            }
            _extensionSocket = ws;
            _extensionConnectedTcs.TrySetResult(true);
            _ = PumpExtensionMessages();
        }
        else
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid path", CancellationToken.None);
        }
    }

    private async Task PumpPlaywrightMessages()
    {
        var buffer = new byte[81920];
        try
        {
            while (_playwrightSocket!.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _playwrightSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
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
            Console.Error.WriteLine($"[Relay] Playwright Pump Error: {ex.Message}");
        }
    }

    private async Task PumpExtensionMessages()
    {
        var buffer = new byte[81920];
        try
        {
            while (_extensionSocket!.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _extensionSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
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
            Console.Error.WriteLine($"[Relay] Extension Pump Error: {ex.Message}");
        }
    }

    private int _msgId = 0;
    // Simple callback tracking for attached response mock
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _extensionCallbacks = new();

    private async Task HandleExtensionMessage(string messageData)
    {
        try
        {
            var node = JsonNode.Parse(messageData)?.AsObject();
            if (node == null) return;

            // Fulfill pending internal extension calls
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
                    return; // Intercepted
                }
            }

            // Fallback: it's a notification from the extension (forwardCDPEvent)
            if (node.ContainsKey("method") && node["method"]!.GetValue<string>() == "forwardCDPEvent")
            {
                var paramObj = node["params"]?.AsObject();
                if (paramObj != null)
                {
                    string mcpSessionId = paramObj.ContainsKey("sessionId") ? paramObj["sessionId"]!.GetValue<string>() : _sessionId!;
                    
                    var outMsg = new JsonObject
                    {
                        ["sessionId"] = mcpSessionId,
                        ["method"] = paramObj["method"]?.GetValue<string>(),
                        ["params"] = paramObj["params"]?.DeepClone()
                    };
                    await SendToPlaywright(outMsg.ToJsonString());
                }
                return;
            }

            // Normal unknown message? Just send it to Playwright directly? 
            // The NodeJS implementation ONLY expects forwardCDPEvent from the extension.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Relay] Extension Message Handling Error: {ex.Message}\nData: {messageData.Substring(0, Math.Min(messageData.Length, 500))}");
        }
    }

    private async Task HandlePlaywrightMessage(string messageData)
    {
        JsonObject? node = null;
        try 
        {
            node = JsonNode.Parse(messageData)?.AsObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Relay] Playwright JSON Parse Error: {ex.Message}\nData: {messageData.Substring(0, Math.Min(messageData.Length, 500))}");
            return;
        }

        if (node == null) return;

        int? reqId = node.ContainsKey("id") ? node["id"]!.GetValue<int>() : null;
        string? method = node.ContainsKey("method") ? node["method"]!.GetValue<string>() : null;
        string? reqSessionId = node.ContainsKey("sessionId") ? node["sessionId"]!.GetValue<string>() : null;

        if (method == "Browser.getVersion")
        {
            await SendToPlaywright(new JsonObject {
                ["id"] = reqId,
                ["sessionId"] = reqSessionId,
                ["result"] = new JsonObject {
                    ["protocolVersion"] = "1.3",
                    ["product"] = "Chrome/Extension-Bridge",
                    ["userAgent"] = "CDP-Bridge-Server/1.0.0"
                }
            }.ToJsonString());
            return;
        }
        
        if (method == "Browser.setDownloadBehavior")
        {
            await SendToPlaywright(new JsonObject {
                ["id"] = reqId,
                ["sessionId"] = reqSessionId,
                ["result"] = new JsonObject()
            }.ToJsonString());
            return;
        }

        if (method == "Target.setAutoAttach")
        {
            if (!string.IsNullOrEmpty(reqSessionId)) {
                try {
                    var result = await ForwardToExtension(method, node["params"]?.AsObject(), reqSessionId);
                    await SendToPlaywright(new JsonObject {
                        ["id"] = reqId, ["sessionId"] = reqSessionId, ["result"] = result?.DeepClone() ?? new JsonObject()
                    }.ToJsonString());
                } catch (Exception ex) {
                    await SendToPlaywright(new JsonObject {
                        ["id"] = reqId, ["sessionId"] = reqSessionId, ["error"] = new JsonObject { ["message"] = ex.Message }
                    }.ToJsonString());
                }
                return;
            }

            // Simulate attaching to the specific tab via our extension
            try {
                var extRes = await SendExtensionInternal("attachToTab", new JsonObject());
                _connectedTabInfo = extRes?["targetInfo"]?.AsObject();
                _sessionId = $"pw-tab-{Interlocked.Increment(ref _nextSessionId)}";

                var tInfo = _connectedTabInfo?.DeepClone().AsObject();
                if (tInfo != null) tInfo["attached"] = true;

                await SendToPlaywright(new JsonObject {
                    ["method"] = "Target.attachedToTarget",
                    ["params"] = new JsonObject {
                        ["sessionId"] = _sessionId,
                        ["targetInfo"] = tInfo ?? new JsonObject(),
                        ["waitingForDebugger"] = false
                    }
                }.ToJsonString());

                await SendToPlaywright(new JsonObject {
                    ["id"] = reqId,
                    ["result"] = new JsonObject()
                }.ToJsonString());
            } catch (Exception ex) {
                await SendToPlaywright(new JsonObject { ["id"] = reqId, ["error"] = new JsonObject { ["message"] = ex.Message } }.ToJsonString());
            }
            return;
        }

        if (method == "Target.getTargetInfo")
        {
            await SendToPlaywright(new JsonObject {
                ["id"] = reqId,
                ["sessionId"] = reqSessionId,
                ["result"] = new JsonObject {
                    ["targetInfo"] = _connectedTabInfo?.DeepClone()
                }
            }.ToJsonString());
            return;
        }

        // Forward all others
        try {
            var result = await ForwardToExtension(method!, node["params"]?.AsObject(), reqSessionId);
            await SendToPlaywright(new JsonObject {
                ["id"] = reqId,
                ["sessionId"] = reqSessionId,
                ["result"] = result?.DeepClone() ?? new JsonObject()
            }.ToJsonString());
        } catch (Exception ex) {
            await SendToPlaywright(new JsonObject {
                ["id"] = reqId,
                ["sessionId"] = reqSessionId,
                ["error"] = new JsonObject { ["message"] = ex.Message }
            }.ToJsonString());
        }
    }

    private async Task<JsonNode?> ForwardToExtension(string method, JsonObject? args, string? sessionId)
    {
        string finalSessionId = (sessionId == _sessionId) ? null! : sessionId!;
        return await SendExtensionInternal("forwardCDPCommand", new JsonObject {
            ["sessionId"] = finalSessionId,
            ["method"] = method,
            ["params"] = args?.DeepClone()
        });
    }

    private async Task<JsonNode?> SendExtensionInternal(string method, JsonObject args)
    {
        var id = Interlocked.Increment(ref _msgId);
        var tcs = new TaskCompletionSource<JsonNode?>();
        _extensionCallbacks[id] = tcs;

        var extMsg = new JsonObject {
            ["id"] = id,
            ["method"] = method,
            ["params"] = args.DeepClone()
        };

        var bytes = Encoding.UTF8.GetBytes(extMsg.ToJsonString());
        await _extensionSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);

        return await tcs.Task;
    }

    private async Task SendToPlaywright(string payload)
    {
        if (_playwrightSocket == null || _playwrightSocket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _playwrightSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
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
