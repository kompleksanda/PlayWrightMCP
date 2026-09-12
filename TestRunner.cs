using System;
using System.Text.Json;
using System.Threading.Tasks;

public static class TestRunner
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("    RUNNING PLAYWRIGHT ENHANCEMENTS TEST SUITE    ");
        Console.WriteLine("==================================================");

        var mgr = await PlaywrightManager.CreateAsync(new PlaywrightOptions { Headless = true, UserDataDir = null });
        PlaywrightTools.SetManager(mgr);

        try
        {
            // 1. LaunchBrowser & Context & Page (with enableContinuousCapture: true)
            Console.WriteLine("[TEST 1] LaunchBrowser, NewContext, NewPage with Continuous Capture...");
            var browserId = await PlaywrightTools.LaunchBrowser(headless: true, enableContinuousCapture: true);
            var contextId = await PlaywrightTools.NewContext(browserId);
            var pageId = await PlaywrightTools.NewPage(contextId);
            Console.WriteLine($"[PASS] Created browser={browserId}, context={contextId}, page={pageId}");

            // 2. Test ListPages with optional/null contextId
            Console.WriteLine("[TEST 2] ListPages across all contexts (optional contextId)...");
            var pagesJson = await PlaywrightTools.ListPages();
            Console.WriteLine($"[PASS] ListPages() returned: {pagesJson}");
            if (!pagesJson.Contains(pageId)) throw new Exception("ListPages() did not return the newly created page!");

            // 3. Test EmulateFocus
            Console.WriteLine("[TEST 3] EmulateFocus...");
            var focusRes = await PlaywrightTools.EmulateFocus(pageId, true);
            Console.WriteLine($"[PASS] EmulateFocus: {focusRes}");

            // 4. Navigate to sample test HTML with input, custom dropdown, floating portal, multi-elements, and scoped container
            Console.WriteLine("[TEST 4] Navigate to HTML fixture...");
            var htmlRaw = @"<!DOCTYPE html>
<html>
<head><title>Test Page</title></head>
<body>
    <input id=""test-input"" placeholder=""Enter name"" value="""" />
    
    <!-- Standard dropdown -->
    <div id=""dropdown-trigger"" role=""combobox"" onclick=""document.getElementById('menu').style.display='block'"" style=""cursor:pointer;padding:8px;border:1px solid #ccc;"">Click to choose</div>
    <div id=""menu"" role=""listbox"" style=""display:none;padding:5px;border:1px solid #333;"">
        <div role=""option"" class=""ant-select-item-option"" onclick=""document.getElementById('dropdown-trigger').innerText=this.innerText;document.getElementById('menu').style.display='none';"" style=""padding:4px;"">Apple</div>
        <div role=""option"" class=""ant-select-item-option"" onclick=""document.getElementById('dropdown-trigger').innerText=this.innerText;document.getElementById('menu').style.display='none';"" style=""padding:4px;"">Banana (Target)</div>
    </div>

    <!-- Floating Portal (Ant Design / Drawer structure) -->
    <div id=""ant-portal-trigger"" role=""combobox"" onclick=""document.getElementById('ant-portal').style.display='block'"" style=""cursor:pointer;padding:8px;margin-top:10px;border:1px solid blue;"">Open Portal Select</div>
    <div id=""ant-portal"" class=""ant-select-dropdown"" style=""display:none;position:fixed;top:50px;left:50px;background:white;border:1px solid #666;z-index:9999;"">
        <div role=""option"" class=""ant-select-item-option"" onclick=""document.getElementById('ant-portal-trigger').innerText=this.innerText;document.getElementById('ant-portal').style.display='none';"" style=""padding:6px;"">Portal Item Alpha</div>
        <div role=""option"" class=""ant-select-item-option"" onclick=""document.getElementById('ant-portal-trigger').innerText=this.innerText;document.getElementById('ant-portal').style.display='none';"" style=""padding:6px;"">Portal Item Beta (Target)</div>
    </div>

    <!-- Multiple matching elements to test strict-mode region screenshot -->
    <div class=""multi-box"" style=""width:100px;height:40px;background:red;margin:4px;"">Box 1</div>
    <div class=""multi-box"" style=""width:100px;height:40px;background:green;margin:4px;"">Box 2</div>
    <div class=""multi-box"" style=""width:100px;height:40px;background:blue;margin:4px;"">Box 3</div>

    <div id=""scoped-card"" role=""region"" aria-label=""User Card"">
        <h4>User Profile</h4>
        <button id=""card-action"">Edit Details</button>
    </div>

    <!-- Animated element for stability testing -->
    <button id=""anim-btn"" style=""transition: transform 0.1s ease; transform: translateX(0px); padding:8px;"">Animated Button</button>

    <!-- Layered drawer/modal backdrop scenario for topmost modal scoping -->
    <div id=""bg-container"" style=""position:relative;z-index:100;padding:10px;"">
        <button id=""bg-btn"" onclick=""document.getElementById('bg-btn').innerText='Background Clicked'"">Background Manage</button>
    </div>
    <!-- Active foreground drawer with mask interception (opened dynamically in Test 20) -->
    <div id=""active-drawer"" style=""display:none;position:fixed;top:0;left:0;width:100%;height:100%;z-index:1000;"">
        <div class=""ant-drawer-mask"" style=""position:absolute;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.45);""></div>
        <div class=""ant-drawer-content-wrapper"" style=""position:absolute;right:0;top:0;width:300px;height:100%;background:white;z-index:1001;padding:20px;"">
            <div class=""ant-drawer-content"">
                <button class=""ant-drawer-close"" aria-label=""Close"" onclick=""document.getElementById('active-drawer').style.display='none';"" style=""float:right;"">X</button>
                <h3>Foreground Drawer</h3>
                <button id=""fg-btn"" onclick=""document.getElementById('fg-btn').innerText='Foreground Clicked'"">Manage</button>
            </div>
        </div>
    </div>

    <!-- Structured Table for extract_table_data -->
    <table id=""test-dashboard-table"" class=""ant-table"">
        <thead>
            <tr>
                <th>Check</th>
                <th>Task status</th>
                <th>Owner</th>
                <th>Action</th>
            </tr>
        </thead>
        <tbody>
            <tr>
                <td>Password policy check</td>
                <td><span>Passed</span></td>
                <td>Alexander</td>
                <td><button>View</button></td>
            </tr>
            <tr>
                <td>MFA Enforcement</td>
                <td><span>In Progress</span></td>
                <td>DevOps Team</td>
                <td><button>Audit</button></td>
            </tr>
            <tr>
                <td>Access Review Q3</td>
                <td><span>Pending</span></td>
                <td>Compliance</td>
                <td><button>Review</button></td>
            </tr>
        </tbody>
    </table>

    <!-- File upload elements -->
    <input type=""file"" id=""test-file-input"" style=""display:none;"" />
    <div class=""upload-target"" style=""margin-top:10px;"">
        <input type=""file"" class=""file-uploader"" />
    </div>
    <div class=""upload-target"" style=""margin-top:10px;"">
        <input type=""file"" class=""file-uploader"" />
    </div>

    <!-- Visual diff element -->
    <div id=""visual-target"" style=""width:120px;height:50px;background-color:#1890ff;color:white;padding:10px;"">Initial Visual</div>

    <!-- Virtual scroll container for auto_scroll_page -->
    <div id=""virtual-scroll-box"" style=""height:100px;overflow-y:auto;border:1px solid gray;"">
        <div style=""height:800px;padding:10px;"">Top virtual content ... Bottom virtual content</div>
    </div>

    <!-- Direct download anchor for wait_for_download -->
    <a id=""download-btn"" href=""data:application/octet-stream;base64,VGhpcyBpcyBhIHRlc3QgZG93bmxvYWQgZmlsZQ=="" download=""test_evidence.txt"">Download Report</a>

    <!-- Embedded iframe for iframe auto-piercing & frameSelector testing -->
    <iframe id=""test-frame"" name=""test-frame"" srcdoc=""<!DOCTYPE html><html><body><input id='frame-input' placeholder='Inside Frame' /><button id='frame-btn' onclick='document.getElementById(\&quot;frame-btn\&quot;).innerText=\&quot;Frame Clicked\&quot;'>Frame Button</button><table id='frame-table'><thead><tr><th>FramedCol</th></tr></thead><tbody><tr><td>FramedCell1</td></tr></tbody></table></body></html>"" style=""width:300px;height:200px;margin-top:10px;""></iframe>
</body>
</html>";
            var html = "data:text/html;charset=utf-8," + Uri.EscapeDataString(htmlRaw);
            var navRes = await PlaywrightTools.Navigate(pageId, html);
            Console.WriteLine($"[PASS] Navigate: {navRes}");

            // 5. Test Fill
            Console.WriteLine("[TEST 5] Fill (native Playwright locator.FillAsync)...");
            var fillRes = await PlaywrightTools.Fill(pageId, "#test-input", "Native Fill Text");
            Console.WriteLine($"[PASS] Fill: {fillRes}");

            // 6. Test FillInput (React controlled input helper with prototype descriptor setter)
            Console.WriteLine("[TEST 6] FillInput (React controlled setter fallback)...");
            var fillInputRes = await PlaywrightTools.FillInput(pageId, "#test-input", "React Controlled Value");
            Console.WriteLine($"[PASS] FillInput: {fillInputRes}");

            // 7. Test PressKey
            Console.WriteLine("[TEST 7] PressKey (Enter on input)...");
            var pressKeyRes = await PlaywrightTools.PressKey(pageId, "Enter", "#test-input");
            Console.WriteLine($"[PASS] PressKey: {pressKeyRes}");

            // 8. Test SelectOption (Standard dropdown)
            Console.WriteLine("[TEST 8] SelectOption on custom dropdown...");
            var selectRes = await PlaywrightTools.SelectOption(pageId, "#dropdown-trigger", "Banana (Target)");
            Console.WriteLine($"[PASS] SelectOption: {selectRes}");

            // 9. Test Scoped Accessibility Snapshot
            Console.WriteLine("[TEST 9] Scoped GetAccessibilitySnapshot...");
            var a11yScoped = await PlaywrightTools.GetAccessibilitySnapshot(pageId, "#scoped-card");
            Console.WriteLine($"[PASS] Scoped A11y Snapshot: {a11yScoped}");

            // 10. Test Scoped Accessibility Diff
            Console.WriteLine("[TEST 10] Scoped GetAccessibilitySnapshotDiff...");
            var a11yDiff = await PlaywrightTools.GetAccessibilitySnapshotDiff(pageId, "#scoped-card");
            Console.WriteLine($"[PASS] Scoped A11y Snapshot Diff: {a11yDiff}");

            // 11. Test DOM Diagnostics on selector timeout / failure
            Console.WriteLine("[TEST 11] Selector failure DOM diagnostics...");
            var failDiag = await PlaywrightTools.Click(pageId, "button.non-existent-action-button");
            Console.WriteLine($"[PASS] Selector failure diagnostic output:\n{failDiag}");
            if (!failDiag.Contains("DOM Diagnostics")) throw new Exception("Click failure did not include DOM Diagnostics!");

            // 12. Recommendation 1: Auto-Detect Floating Portal Container in SelectOption without dropdownSelector
            Console.WriteLine("[TEST 12] Recommendation 1: SelectOption with Auto-Detected Floating Portal (.ant-select-dropdown)...");
            var portalSelectRes = await PlaywrightTools.SelectOption(pageId, "#ant-portal-trigger", "Portal Item Beta (Target)");
            Console.WriteLine($"[PASS] Auto-Detected Floating Portal SelectOption: {portalSelectRes}");
            if (!portalSelectRes.Contains("Successfully selected")) throw new Exception("Portal auto-detection SelectOption failed!");

            // 13. Recommendation 3: ScreenshotRegion Multi-Match Auto-Scoping & Index
            Console.WriteLine("[TEST 13] Recommendation 3: ScreenshotRegion on generic selector (.multi-box) without strict mode error...");
            var screenAuto = await PlaywrightTools.ScreenshotRegion(pageId, ".multi-box");
            if (screenAuto.StartsWith("Error")) throw new Exception($"ScreenshotRegion multi-match failed: {screenAuto}");
            Console.WriteLine($"[PASS] ScreenshotRegion auto-scoped first element (base64 length: {screenAuto.Length})");
            var screenIndexed = await PlaywrightTools.ScreenshotRegion(pageId, ".multi-box", index: 1);
            if (screenIndexed.StartsWith("Error")) throw new Exception($"ScreenshotRegion with index failed: {screenIndexed}");
            Console.WriteLine($"[PASS] ScreenshotRegion with explicit index=1 (base64 length: {screenIndexed.Length})");

            // 14. Recommendation 4: Continuous Console & Network Capture Mode
            Console.WriteLine("[TEST 14] Recommendation 4: Continuous Capture Mode verification...");
            await PlaywrightTools.EvaluateScript(pageId, "console.warn('Continuous capture test warning');");
            for (int i = 0; i < 60; i++)
            {
                await PlaywrightTools.EvaluateScript(pageId, $"console.log('Rolling log message #{i}');");
            }
            var consoleLogs = PlaywrightTools.GetConsoleMessages(pageId);
            var logsDoc = JsonDocument.Parse(consoleLogs);
            int logCount = logsDoc.RootElement.GetArrayLength();
            Console.WriteLine($"[PASS] Continuous Console Capture auto-recorded logs. Rolling buffer length: {logCount} (capped at {PlaywrightTools.MaxConsoleLogCapacity})");
            if (logCount > PlaywrightTools.MaxConsoleLogCapacity) throw new Exception("Console log circular buffer exceeded max capacity!");

            // 15. Recommendation 2: Network Interception Timeout & Deadlock Safety
            Console.WriteLine("[TEST 15] Recommendation 2: InterceptAndModify & ClearInterceptions deadlock safety...");
            var routeMsg = await PlaywrightTools.InterceptAndModify(pageId, "**/api/test-route", mockJsonResponse: "{\"status\":\"mocked\"}");
            Console.WriteLine($"[PASS] InterceptAndModify: {routeMsg}");
            var clearMsg = await PlaywrightTools.ClearInterceptions(pageId);
            Console.WriteLine($"[PASS] ClearInterceptions: {clearMsg}");
            if (!clearMsg.Contains("Success")) throw new Exception($"ClearInterceptions failed: {clearMsg}");

            // 16. New Recommendation 1: Auto-Disambiguate Generic Action Targets (Click, Hover, Fill, FillInput on multi-match)
            Console.WriteLine("[TEST 16] Recommendation 1: Generic Action Targets Multi-Match Disambiguation...");
            var clickMultiRes = await PlaywrightTools.Click(pageId, ".multi-box");
            if (clickMultiRes.StartsWith("Error")) throw new Exception($"Click multi-match failed: {clickMultiRes}");
            Console.WriteLine($"[PASS] Click on multi-element selector (.multi-box) without strict mode violation: {clickMultiRes}");

            var clickIndexedRes = await PlaywrightTools.Click(pageId, ".multi-box", index: 2);
            if (clickIndexedRes.StartsWith("Error")) throw new Exception($"Click indexed failed: {clickIndexedRes}");
            Console.WriteLine($"[PASS] Click on multi-element selector with explicit index=2: {clickIndexedRes}");

            var hoverMultiRes = await PlaywrightTools.Hover(pageId, ".multi-box");
            if (hoverMultiRes.StartsWith("Error")) throw new Exception($"Hover multi-match failed: {hoverMultiRes}");
            Console.WriteLine($"[PASS] Hover on multi-element selector without strict mode violation: {hoverMultiRes}");

            // 17. New Recommendation 2: Auto-Wait for Element Stability on Fast Transitions / Animations
            Console.WriteLine("[TEST 17] Recommendation 2: Auto-Wait for Element Stability on Fast Transitions...");
            // Start a brief CSS animation
            await PlaywrightTools.EvaluateScript(pageId, "document.getElementById('anim-btn').style.transform = 'translateX(20px)';");
            var animClickRes = await PlaywrightTools.Click(pageId, "#anim-btn", waitForStable: true, animationTimeoutMs: 300);
            if (animClickRes.StartsWith("Error")) throw new Exception($"Click with waitForStable failed: {animClickRes}");
            Console.WriteLine($"[PASS] Click with animation/drawer stabilization: {animClickRes}");

            // 18. New Recommendation 3: Smart Cookie Auto-Domain Detection in inject_cookie
            Console.WriteLine("[TEST 18] Recommendation 3: Smart Cookie Auto-Domain Detection in InjectCookie...");
            // Inject with null domain (should auto-detect or tolerate)
            await PlaywrightTools.InjectCookie(pageId, "session_test", "12345", domain: null);
            // Inject with full URL as domain
            await PlaywrightTools.InjectCookie(pageId, "auth_token", "abcdef", domain: "https://eu.sprinto.com/app");
            Console.WriteLine("[PASS] InjectCookie cleanly handled null domain and full URL domain parsing without exception");

            // 19. New Recommendation 4: Direct Visual-Diff / Element Change Detection (ScreenshotRegionDiff)
            Console.WriteLine("[TEST 19] Recommendation 4: Direct Visual-Diff / ScreenshotRegionDiff...");
            // Initial baseline
            var baselineDiffStr = await PlaywrightTools.ScreenshotRegionDiff(pageId, "#visual-target", resetBaseline: true);
            var baselineDoc = JsonDocument.Parse(baselineDiffStr);
            if (baselineDoc.RootElement.GetProperty("hasChanged").GetBoolean())
                throw new Exception("Initial visual diff reported changes!");
            Console.WriteLine($"[PASS] ScreenshotRegionDiff baseline established: {baselineDiffStr}");

            // Check diff before visual change (should be false)
            var noChangeDiffStr = await PlaywrightTools.ScreenshotRegionDiff(pageId, "#visual-target");
            var noChangeDoc = JsonDocument.Parse(noChangeDiffStr);
            if (noChangeDoc.RootElement.GetProperty("hasChanged").GetBoolean())
                throw new Exception("Identical visual state reported changes!");
            Console.WriteLine($"[PASS] ScreenshotRegionDiff identical comparison: hasChanged=false");

            // Mutate visual appearance
            await PlaywrightTools.EvaluateScript(pageId, "document.getElementById('visual-target').style.backgroundColor = '#ff4d4f'; document.getElementById('visual-target').innerText = 'Changed Visual';");
            var changedDiffStr = await PlaywrightTools.ScreenshotRegionDiff(pageId, "#visual-target");
            var changedDoc = JsonDocument.Parse(changedDiffStr);
            if (!changedDoc.RootElement.GetProperty("hasChanged").GetBoolean())
                throw new Exception("ScreenshotRegionDiff failed to detect visual color and text change!");
            double pct = changedDoc.RootElement.GetProperty("changePercentage").GetDouble();
            Console.WriteLine($"[PASS] ScreenshotRegionDiff successfully detected visual change: hasChanged=true, changePercentage={pct}%");

            // 20. Recommendation 1: Auto-Disambiguate Background Modals & Drawers on click (topmostOnly: true / fallback)
            Console.WriteLine("[TEST 20] Recommendation 1: Topmost modal/drawer scoping and pointer interception auto-fallback...");
            // Open the drawer dynamically
            await PlaywrightTools.EvaluateScript(pageId, "const d = document.getElementById('active-drawer'); d.style.display = 'block'; d.className = 'ant-drawer-open';");
            // Click button inside foreground drawer using topmostOnly: true
            var topmostClick = await PlaywrightTools.Click(pageId, "button:has-text(\"Manage\")", topmostOnly: true);
            if (topmostClick.StartsWith("Error")) throw new Exception($"Topmost click failed: {topmostClick}");
            var fgBtnText = await PlaywrightTools.EvaluateScript(pageId, "document.getElementById('fg-btn').innerText");
            if (!fgBtnText.Contains("Foreground Clicked")) throw new Exception($"Expected foreground button to be clicked, got: {fgBtnText}");
            Console.WriteLine($"[PASS] Topmost click scoped accurately inside active foreground drawer: {topmostClick}");

            // 21. Recommendation 2: Fast-Fail Actionability Timeouts for AI Agents (timeoutMs)
            Console.WriteLine("[TEST 21] Recommendation 2: Fast-Fail Actionability Timeout (timeoutMs = 1200ms)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fastFailRes = await PlaywrightTools.Click(pageId, "#non-existent-button-for-timeout", timeoutMs: 1200);
            sw.Stop();
            if (!fastFailRes.StartsWith("Error")) throw new Exception("Expected click on non-existent element to fail!");
            if (sw.ElapsedMilliseconds > 6000) throw new Exception($"Click timeout took too long: {sw.ElapsedMilliseconds}ms (expected ~1200ms)");
            Console.WriteLine($"[PASS] Fast-fail timeout triggered in {sw.ElapsedMilliseconds}ms with actionable diagnostics: {fastFailRes.Split('\n')[0]}");

            // 22. Recommendation 3: Post-Click Transition Delay (postClickDelayMs)
            Console.WriteLine("[TEST 22] Recommendation 3: Post-Click Transition Delay (postClickDelayMs = 350ms)...");
            var swPostDelay = System.Diagnostics.Stopwatch.StartNew();
            var delayClickRes = await PlaywrightTools.Click(pageId, "#fg-btn", postClickDelayMs: 350);
            swPostDelay.Stop();
            if (delayClickRes.StartsWith("Error")) throw new Exception($"Click with postClickDelayMs failed: {delayClickRes}");
            if (swPostDelay.ElapsedMilliseconds < 300) throw new Exception($"postClickDelayMs did not delay execution! Elapsed: {swPostDelay.ElapsedMilliseconds}ms");
            Console.WriteLine($"[PASS] Post-click transition delay successfully held execution for {swPostDelay.ElapsedMilliseconds}ms");

            // 23. Recommendation 4: File Uploads (upload_file)
            Console.WriteLine("[TEST 23] Recommendation 4: Direct Support for File Uploads (UploadFile)...");
            var tempFilePath = Path.Combine(Path.GetTempPath(), $"playwright_test_upload_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFilePath, "Sample upload file payload for test runner verification");
            try
            {
                // Test hidden input upload
                var uploadRes = await PlaywrightTools.UploadFile(pageId, "#test-file-input", tempFilePath);
                if (uploadRes.StartsWith("Error")) throw new Exception($"UploadFile failed on hidden file input: {uploadRes}");
                Console.WriteLine($"[PASS] UploadFile succeeded on hidden input: {uploadRes}");

                // Test multi-element upload input with autoFirst
                var uploadMultiRes = await PlaywrightTools.UploadFile(pageId, ".file-uploader", tempFilePath, autoFirst: true);
                if (uploadMultiRes.StartsWith("Error")) throw new Exception($"UploadFile failed on multi-match selector: {uploadMultiRes}");
                Console.WriteLine($"[PASS] UploadFile autoFirst disambiguation succeeded on multi-match input: {uploadMultiRes}");
            }
            finally
            {
                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            }

            // 24. New Recommendation 1: Auto-Close / Dismiss Overlays Primitive (CloseOverlays)
            Console.WriteLine("[TEST 24] Recommendation 1: CloseOverlays (topmost and all overlays)...");
            // Ensure drawer is open first
            await PlaywrightTools.EvaluateScript(pageId, "const d = document.getElementById('active-drawer'); d.style.display = 'block'; d.className = 'ant-drawer-open';");
            var closeRes = await PlaywrightTools.CloseOverlays(pageId, target: "topmost", waitForSettle: true);
            if (closeRes.StartsWith("Error")) throw new Exception($"CloseOverlays failed: {closeRes}");
            var drawerDisplay = await PlaywrightTools.EvaluateScript(pageId, "document.getElementById('active-drawer').style.display");
            if (drawerDisplay.Contains("block") && !drawerDisplay.Contains("none"))
                throw new Exception($"Drawer was expected to be dismissed/hidden, but was: {drawerDisplay}");
            Console.WriteLine($"[PASS] CloseOverlays successfully dismissed foreground drawer: {closeRes}");

            // 25. New Recommendation 2: Auto-Extract Structured Table Data (ExtractTableData)
            Console.WriteLine("[TEST 25] Recommendation 2: ExtractTableData with column filtering and row parsing...");
            // Extract all rows and columns
            var tableJson = await PlaywrightTools.ExtractTableData(pageId, tableSelector: "#test-dashboard-table");
            if (tableJson.StartsWith("Error")) throw new Exception($"ExtractTableData failed: {tableJson}");
            var tableDoc = JsonDocument.Parse(tableJson);
            int returnedRows = tableDoc.RootElement.GetProperty("returnedRows").GetInt32();
            if (returnedRows != 3) throw new Exception($"Expected 3 rows in table, got: {returnedRows}");
            
            // Extract with column filter
            var filteredJson = await PlaywrightTools.ExtractTableData(pageId, tableSelector: "#test-dashboard-table", columns: new[] { "Check", "Owner" }, maxRows: 2);
            var filteredDoc = JsonDocument.Parse(filteredJson);
            var firstRow = filteredDoc.RootElement.GetProperty("data")[0];
            if (!firstRow.TryGetProperty("Check", out _) || !firstRow.TryGetProperty("Owner", out _) || firstRow.TryGetProperty("Action", out _))
                throw new Exception($"Column filter failed: {filteredJson}");
            Console.WriteLine($"[PASS] ExtractTableData cleanly mapped tabular structure with column filtering: {filteredJson}");

            // 26. New Recommendation 3: Smart Cookie / LocalStorage State Preservation (ExportStorageState & ImportStorageState)
            Console.WriteLine("[TEST 26] Recommendation 3: ExportStorageState and ImportStorageState preservation...");
            // Navigate to an http origin so window.localStorage is accessible
            await PlaywrightTools.InterceptAndModify(pageId, "http://localhost:9999/dashboard", mockJsonResponse: "{\"status\":\"ok\"}", mockStatus: 200);
            await PlaywrightTools.Navigate(pageId, "http://localhost:9999/dashboard");

            // Inject distinct cookie and set localStorage
            await PlaywrightTools.InjectCookie(pageId, "session_restore_test", "token_xyz987", domain: "localhost");
            await PlaywrightTools.EvaluateScript(pageId, "localStorage.setItem('app_theme', 'dark'); localStorage.setItem('user_role', 'security_admin');");
            
            // Export storage state to temp file
            var stateTempPath = Path.Combine(Path.GetTempPath(), $"playwright_storage_state_{Guid.NewGuid():N}.json");
            try
            {
                var exportRes = await PlaywrightTools.ExportStorageState(pageId, stateTempPath);
                if (exportRes.StartsWith("Error")) throw new Exception($"ExportStorageState failed: {exportRes}");
                if (!File.Exists(stateTempPath)) throw new Exception("ExportStorageState did not write file to disk!");
                var fileContent = await File.ReadAllTextAsync(stateTempPath);
                if (!fileContent.Contains("session_restore_test")) throw new Exception("Exported storage state missing injected cookie!");
                Console.WriteLine($"[PASS] ExportStorageState exported context state: {exportRes}");

                // Clear context cookies and localStorage
                await PlaywrightTools.EvaluateScript(pageId, "localStorage.clear();");
                var lsCleared = await PlaywrightTools.EvaluateScript(pageId, "localStorage.getItem('user_role')");
                if (!string.IsNullOrWhiteSpace(lsCleared) && !lsCleared.Equals("null", StringComparison.OrdinalIgnoreCase) && lsCleared != "\"\"")
                    throw new Exception($"Failed to clear localStorage for test! Got: '{lsCleared}'");

                // Import storage state back
                var importRes = await PlaywrightTools.ImportStorageState(pageId, filePath: stateTempPath);
                if (importRes.StartsWith("Error")) throw new Exception($"ImportStorageState failed: {importRes}");

                // Verify restored state
                var lsRestored = await PlaywrightTools.EvaluateScript(pageId, "localStorage.getItem('user_role')");
                if (!lsRestored.Contains("security_admin")) throw new Exception($"Failed to restore localStorage: {lsRestored}");
                Console.WriteLine($"[PASS] ImportStorageState successfully restored storage state: {importRes} (localStorage value: {lsRestored.Trim()})");
            }
            finally
            {
                await PlaywrightTools.ClearInterceptions(pageId);
                if (File.Exists(stateTempPath)) File.Delete(stateTempPath);
            }

            // Re-navigate to the local HTML fixture for subsequent tests (since Test 26 navigated to http://localhost:9999/dashboard)
            await PlaywrightTools.Navigate(pageId, html);

            // 27. Test BatchActions: Unified multi-step action execution pipeline
            Console.WriteLine("[TEST 27] Frontier 1: BatchActions multi-step action pipeline...");
            var batchList = new List<BatchActionItem>
            {
                new BatchActionItem { Type = "fill_input", Selector = "#test-input", Value = "Batch Pipeline User" },
                new BatchActionItem { Type = "click", Selector = "#dropdown-trigger" },
                new BatchActionItem { Type = "delay", DurationMs = 200 },
                new BatchActionItem { Type = "select_option", Selector = "#dropdown-trigger", Option = "Apple" },
                new BatchActionItem { Type = "close_overlays", Target = "topmost" }
            };

            var batchJson = await PlaywrightTools.BatchActions(pageId, batchList, abortOnError: true);
            var batchDoc = JsonDocument.Parse(batchJson);
            var batchStatus = batchDoc.RootElement.GetProperty("status").GetString();
            if (batchStatus != "success") throw new Exception($"BatchActions failed: {batchJson}");
            var stepsCount = batchDoc.RootElement.GetProperty("completedCount").GetInt32();
            if (stepsCount != 5) throw new Exception($"Expected 5 completed steps in batch, got {stepsCount}");
            Console.WriteLine($"[PASS] BatchActions executed 5 sequential actions in a single turn: {batchStatus}");

            // 28. Test iframe handling: Auto-piercing & explicit frameSelector
            Console.WriteLine("[TEST 28] Frontier 2: iFrame auto-piercing and frameSelector...");
            // Test 28a: Explicit frameSelector on fill & click
            var frameFillRes = await PlaywrightTools.Fill(pageId, "#frame-input", "Framed Value", frameSelector: "#test-frame");
            if (frameFillRes.StartsWith("Error")) throw new Exception($"Frame Fill failed: {frameFillRes}");
            var frameClickRes = await PlaywrightTools.Click(pageId, "#frame-btn", frameSelector: "#test-frame");
            if (frameClickRes.StartsWith("Error")) throw new Exception($"Frame Click failed: {frameClickRes}");

            // Test 28b: Auto-pierce on table extraction inside iframe
            var frameTableJson = await PlaywrightTools.ExtractTableData(pageId, tableSelector: "#frame-table");
            if (frameTableJson.StartsWith("Error")) throw new Exception($"Auto-pierce ExtractTableData failed: {frameTableJson}");
            var frameTableDoc = JsonDocument.Parse(frameTableJson);
            if (!frameTableJson.Contains("FramedCell1")) throw new Exception($"Auto-pierce failed to extract iframe table rows: {frameTableJson}");
            Console.WriteLine($"[PASS] iFrame auto-piercing & frameSelector succeeded: {frameTableJson}");

            // 29. Test ExportPageToPdf: PDF generation in headless mode + headed transfer fallback + screenshot fallback
            Console.WriteLine("[TEST 29] Frontier 3: ExportPageToPdf (Native + Headed Fallback + Screenshot Fallback)...");
            var pdfTempPath = Path.Combine(Path.GetTempPath(), $"playwright_export_{Guid.NewGuid():N}.pdf");
            var fallbackTempPath = Path.Combine(Path.GetTempPath(), $"playwright_fallback_{Guid.NewGuid():N}.pdf");
            var screenshotTempPath = Path.Combine(Path.GetTempPath(), $"playwright_screenshot_{Guid.NewGuid():N}.pdf");
            try
            {
                // 29a: Native PDF export in headless mode
                var pdfRes = await PlaywrightTools.ExportPageToPdf(pageId, filePath: pdfTempPath, format: "A4", printBackground: true);
                if (pdfRes.Contains("\"status\":\"error\"") || pdfRes.Contains("\"error\""))
                    throw new Exception($"ExportPageToPdf native failed: {pdfRes}");
                if (!File.Exists(pdfTempPath))
                    throw new Exception($"ExportPageToPdf did not write file to {pdfTempPath}");
                var pdfInfo = new FileInfo(pdfTempPath);
                if (pdfInfo.Length < 100)
                    throw new Exception($"ExportPageToPdf file size too small: {pdfInfo.Length} bytes");

                // Also test base64 export without filePath
                var pdfBase64Res = await PlaywrightTools.ExportPageToPdf(pageId, format: "Letter");
                var pdfDoc = JsonDocument.Parse(pdfBase64Res);
                if (!pdfDoc.RootElement.TryGetProperty("dataBase64", out var base64Prop) || string.IsNullOrWhiteSpace(base64Prop.GetString()))
                    throw new Exception($"ExportPageToPdf base64 output missing dataBase64 property: {pdfBase64Res}");

                Console.WriteLine($"[PASS] ExportPageToPdf native mode generated valid PDF ({pdfInfo.Length} bytes) and base64 string");

                // 29b: Headless transfer fallback (simulating headed / extension bridge mode where native printToPDF fails)
                var fallbackRes = await PlaywrightTools.ExportPageToPdf(pageId, filePath: fallbackTempPath, format: "A4", printBackground: true, forceFallback: true);
                if (fallbackRes.Contains("\"status\":\"error\"") || fallbackRes.Contains("\"error\""))
                    throw new Exception($"ExportPageToPdf fallback failed: {fallbackRes}");
                var fallbackDoc = JsonDocument.Parse(fallbackRes);
                var renderMethod = fallbackDoc.RootElement.GetProperty("renderMethod").GetString();
                if (renderMethod != "headless_transfer")
                    throw new Exception($"Expected renderMethod 'headless_transfer', got '{renderMethod}'");
                if (!File.Exists(fallbackTempPath))
                    throw new Exception($"ExportPageToPdf fallback did not write file to {fallbackTempPath}");
                var fallbackInfo = new FileInfo(fallbackTempPath);
                if (fallbackInfo.Length < 100)
                    throw new Exception($"ExportPageToPdf fallback file size too small: {fallbackInfo.Length} bytes");

                Console.WriteLine($"[PASS] ExportPageToPdf headless transfer fallback generated valid PDF ({fallbackInfo.Length} bytes, renderMethod={renderMethod})");

                // 29c: Direct Screenshot-to-PDF engine test
                var jpegBytes = await mgr.GetPage(pageId).ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    FullPage = true,
                    Type = Microsoft.Playwright.ScreenshotType.Jpeg,
                    Quality = 85
                });
                var pdfFromJpegBytes = PdfHelper.CreatePdfFromJpeg(jpegBytes);
                await File.WriteAllBytesAsync(screenshotTempPath, pdfFromJpegBytes);
                var screenshotPdfHeader = System.Text.Encoding.ASCII.GetString(pdfFromJpegBytes, 0, 8);
                if (!screenshotPdfHeader.StartsWith("%PDF-1.4"))
                    throw new Exception($"PdfHelper did not produce valid PDF header: {screenshotPdfHeader}");
                Console.WriteLine($"[PASS] PdfHelper screenshot-to-PDF fallback produced valid PDF ({pdfFromJpegBytes.Length} bytes, header={screenshotPdfHeader.Trim()})");

                // 29d: Test autoExpandScrollContainers: verify SPA scroll container unconstraining and post-export DOM cleanup
                var autoExpandRes = await PlaywrightTools.ExportPageToPdf(pageId, format: "A4", autoExpandScrollContainers: true);
                var autoExpandDoc = JsonDocument.Parse(autoExpandRes);
                if (!autoExpandDoc.RootElement.GetProperty("autoExpandScrollContainers").GetBoolean())
                    throw new Exception("Expected autoExpandScrollContainers=true in response");

                // Verify that the injected unconstraining style was cleanly removed from the live DOM
                var styleCheck = await mgr.GetPage(pageId).EvaluateAsync<bool>($"() => document.getElementById('{PlaywrightTools.AutoExpandStyleId}') !== null");
                if (styleCheck)
                    throw new Exception("Injected unconstraining style was NOT cleaned up from live DOM after export!");
                Console.WriteLine($"[PASS] autoExpandScrollContainers unconstrained SPA containers and verified complete post-export DOM cleanup");
            }
            finally
            {
                if (File.Exists(pdfTempPath)) File.Delete(pdfTempPath);
                if (File.Exists(fallbackTempPath)) File.Delete(fallbackTempPath);
                if (File.Exists(screenshotTempPath)) File.Delete(screenshotTempPath);
            }

            // 30. Test Bridge Resilience: Auto-refresh GetPage on stale/closed handle
            Console.WriteLine("[TEST 30] Bridge Resilience: Auto-refresh GetPage on stale/closed handle...");
            var stalePageId = await PlaywrightTools.NewPage(contextId);
            var initialPageHandle = mgr.GetPage(stalePageId);
            
            // Spawn a replacement live page in the context, then close the old page to simulate navigation/stale tab
            var replacementPage = await mgr.GetContext(contextId).NewPageAsync();
            await replacementPage.SetContentAsync("<h1>Live Replaced Page</h1>");
            await initialPageHandle.CloseAsync();

            if (!initialPageHandle.IsClosed)
                throw new Exception("Initial page handle should be marked closed!");

            // Calling GetPage with the stale pageId should auto-detect that it is closed,
            // query context.Pages, and automatically rebind stalePageId to the live replacementPage!
            var autoRefreshedPage = mgr.GetPage(stalePageId);
            if (autoRefreshedPage.IsClosed)
                throw new Exception("GetPage returned a closed page despite live pages existing in context!");
            
            // Verify actions work seamlessly on the auto-refreshed handle
            var heading = await autoRefreshedPage.InnerTextAsync("h1");
            if (heading != "Live Replaced Page")
                throw new Exception($"Expected heading 'Live Replaced Page', got '{heading}'");

            Console.WriteLine($"[PASS] Auto-Refresh GetPage successfully rebound stale handle '{stalePageId}' to active live page (h1='{heading}')");

            // 31. Test AutoScrollPage: Progressive scrolling for lazy-loading and DOM hydration
            Console.WriteLine("[TEST 31] Enterprise 1: AutoScrollPage virtual container hydration...");
            var scrollRes = await PlaywrightTools.AutoScrollPage(pageId, containerSelector: "#virtual-scroll-box", stepPixels: 200, delayMs: 50, maxScrolls: 5);
            var scrollDoc = JsonDocument.Parse(scrollRes);
            if (scrollDoc.RootElement.GetProperty("status").GetString() != "success")
                throw new Exception($"AutoScrollPage failed: {scrollRes}");
            Console.WriteLine($"[PASS] AutoScrollPage successfully hydrated virtual container: {scrollRes}");

            // 32. Test ExtractTableData with Direct RFC 4180 CSV Export and Compact Metadata Output
            Console.WriteLine("[TEST 32] Enterprise 2: Direct RFC 4180 CSV Export in ExtractTableData...");
            var csvTempPath = Path.Combine(Path.GetTempPath(), $"table_export_{Guid.NewGuid():N}.csv");
            try
            {
                // Test 32a: Direct CSV export to disk
                var csvExportRes = await PlaywrightTools.ExtractTableData(pageId, tableSelector: "#test-dashboard-table", filePath: csvTempPath, format: "csv", scrollFirst: true);
                var csvDoc = JsonDocument.Parse(csvExportRes);
                if (csvDoc.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"ExtractTableData CSV export failed: {csvExportRes}");
                if (!File.Exists(csvTempPath))
                    throw new Exception($"ExtractTableData did not write CSV file to {csvTempPath}");
                
                var csvLines = await File.ReadAllLinesAsync(csvTempPath);
                if (csvLines.Length < 4)
                    throw new Exception($"Expected at least 4 CSV lines, got {csvLines.Length}");
                if (!csvLines[0].Contains("Check") || !csvLines[0].Contains("Task status"))
                    throw new Exception($"Unexpected CSV headers: {csvLines[0]}");
                
                Console.WriteLine($"[PASS] ExtractTableData directly wrote RFC 4180 CSV file ({new FileInfo(csvTempPath).Length} bytes, {csvLines.Length} lines)");

                // Test 32b: Return raw CSV text when filePath is omitted
                var rawCsvText = await PlaywrightTools.ExtractTableData(pageId, tableSelector: "#test-dashboard-table", columns: new[] { "Check", "Owner" }, format: "csv");
                if (!rawCsvText.Contains("Check,Owner") || !rawCsvText.Contains("Alexander"))
                    throw new Exception($"Unexpected raw CSV output: {rawCsvText}");
                Console.WriteLine($"[PASS] ExtractTableData raw CSV string output verified: {rawCsvText.Trim().Replace("\r\n", " | ")}");
            }
            finally
            {
                if (File.Exists(csvTempPath)) File.Delete(csvTempPath);
            }

            // 33. Test Scoped Region-to-PDF (ExportPageToPdf with selector)
            Console.WriteLine("[TEST 33] Enterprise 3: Scoped Region-to-PDF (ExportPageToPdf with selector)...");
            var scopedPdfPath = Path.Combine(Path.GetTempPath(), $"scoped_pdf_{Guid.NewGuid():N}.pdf");
            try
            {
                var scopedPdfRes = await PlaywrightTools.ExportPageToPdf(pageId, filePath: scopedPdfPath, selector: "#test-dashboard-table", scrollFirst: true);
                var scopedDoc = JsonDocument.Parse(scopedPdfRes);
                if (scopedDoc.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"Scoped ExportPageToPdf failed: {scopedPdfRes}");
                if (!File.Exists(scopedPdfPath))
                    throw new Exception($"Scoped ExportPageToPdf did not write file to {scopedPdfPath}");
                var scopedInfo = new FileInfo(scopedPdfPath);
                if (scopedInfo.Length < 100)
                    throw new Exception($"Scoped PDF file size too small: {scopedInfo.Length} bytes");
                
                // Also verify scoped print style was cleaned up from the DOM
                var styleCheck = await mgr.GetPage(pageId).EvaluateAsync<bool>($"() => document.getElementById('{PlaywrightTools.ScopedPrintStyleId}') !== null");
                if (styleCheck)
                    throw new Exception("Scoped print style was NOT removed from DOM after export!");

                Console.WriteLine($"[PASS] Scoped ExportPageToPdf generated valid scoped PDF ({scopedInfo.Length} bytes) and cleaned up scoped styling");
            }
            finally
            {
                if (File.Exists(scopedPdfPath)) File.Delete(scopedPdfPath);
            }

            // 34. Test Multi-Tab Switcher (SwitchActiveTab)
            Console.WriteLine("[TEST 34] Enterprise 4: SwitchActiveTab across multiple pages...");
            var secondPage = await mgr.GetContext(contextId).NewPageAsync();
            await secondPage.SetContentAsync("<title>Audit Tab Secondary</title><h2>Audit Findings</h2>");
            try
            {
                var switchRes = await PlaywrightTools.SwitchActiveTab(titlePattern: "Audit Tab Secondary");
                var switchDoc = JsonDocument.Parse(switchRes);
                if (switchDoc.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"SwitchActiveTab failed: {switchRes}");
                
                Console.WriteLine($"[PASS] SwitchActiveTab switched focus successfully: {switchRes}");
            }
            finally
            {
                await secondPage.CloseAsync();
            }

            // 35. Test Deterministic Download Interception (WaitForDownload)
            Console.WriteLine("[TEST 35] Enterprise 5: WaitForDownload deterministic interception...");
            var downloadDest = Path.Combine(Path.GetTempPath(), $"intercepted_evidence_{Guid.NewGuid():N}.txt");
            try
            {
                var dlRes = await PlaywrightTools.WaitForDownload(pageId, triggerSelector: "#download-btn", destinationPath: downloadDest, timeoutMs: 10000);
                var dlDoc = JsonDocument.Parse(dlRes);
                if (dlDoc.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"WaitForDownload failed: {dlRes}");
                if (!File.Exists(downloadDest))
                    throw new Exception($"WaitForDownload did not save file to {downloadDest}");
                var content = await File.ReadAllTextAsync(downloadDest);
                if (!content.Contains("This is a test download file"))
                    throw new Exception($"Downloaded file content mismatch: {content}");
                Console.WriteLine($"[PASS] WaitForDownload intercepted download successfully ({new FileInfo(downloadDest).Length} bytes): {dlRes}");
            }
            finally
            {
                if (File.Exists(downloadDest)) File.Delete(downloadDest);
            }

            // 36. Test Default/Omitted pageId Resolution Across Element Tools
            Console.WriteLine("[TEST 36] Calling Element Tools with Default/Omitted pageId...");
            // Test Fill without pageId
            var fillOmittedRes = await PlaywrightTools.Fill(selector: "#test-input", value: "Default PageId Tested");
            if (fillOmittedRes != "Success") throw new Exception($"Fill without pageId failed: {fillOmittedRes}");
            
            // Test Click without pageId
            var clickOmittedRes = await PlaywrightTools.Click(selector: "#anim-btn");
            if (clickOmittedRes != "Success") throw new Exception($"Click without pageId failed: {clickOmittedRes}");

            // Test ExtractTableData without pageId
            var tableOmittedRes = await PlaywrightTools.ExtractTableData(tableSelector: "#test-dashboard-table", maxRows: 5);
            using var tDocOmitted = JsonDocument.Parse(tableOmittedRes);
            if (!tDocOmitted.RootElement.TryGetProperty("returnedRows", out var rRows) || rRows.GetInt32() < 1)
                throw new Exception($"ExtractTableData without pageId failed: {tableOmittedRes}");

            // Test Screenshot without pageId
            var screenshotOmittedRes = await PlaywrightTools.Screenshot();
            if (string.IsNullOrEmpty(screenshotOmittedRes) || screenshotOmittedRes.StartsWith("Error"))
                throw new Exception($"Screenshot without pageId failed: {screenshotOmittedRes}");

            Console.WriteLine("[PASS] Verified Fill, Click, ExtractTableData, Screenshot all execute seamlessly with default omitted pageId!");

            // 37. Test Resilient Navigation with waitUntil: domcontentloaded
            Console.WriteLine("[TEST 37] Resilient Navigation with waitUntil domcontentloaded & fast fallback...");
            var navDlRes = await PlaywrightTools.Navigate(url: "about:blank", waitUntil: "domcontentloaded");
            if (navDlRes != "Success") throw new Exception($"Navigate with domcontentloaded failed: {navDlRes}");
            // Navigate back to fixture
            await PlaywrightTools.Navigate(url: html, waitUntil: "domcontentloaded");
            Console.WriteLine("[PASS] Resilient navigation with waitUntil domcontentloaded passed!");

            // 38. Test BatchActions with extract_table_data and wait_for_download
            Console.WriteLine("[TEST 38] BatchActions pipeline with extract_table_data and wait_for_download...");
            var batchDownloadDest = Path.Combine(Path.GetTempPath(), $"batch_download_{Guid.NewGuid():N}.txt");
            try
            {
                var batchRes = await PlaywrightTools.BatchActions(actions: new List<BatchActionItem>
                {
                    new BatchActionItem { Type = "click", Selector = "#anim-btn" },
                    new BatchActionItem { Type = "fill", Selector = "#test-input", Value = "Batch Pipeline Value" },
                    new BatchActionItem { Type = "extract_table_data", Selector = "#test-dashboard-table", MaxRows = 3 },
                    new BatchActionItem { Type = "wait_for_download", Selector = "#download-btn", DestinationPath = batchDownloadDest, TimeoutMs = 10000 }
                });

                using var bDoc38 = JsonDocument.Parse(batchRes);
                if (bDoc38.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"BatchActions with extract_table_data and download failed: {batchRes}");
                
                if (!File.Exists(batchDownloadDest))
                    throw new Exception($"BatchActions wait_for_download did not write file to {batchDownloadDest}");

                Console.WriteLine($"[PASS] BatchActions pipeline successfully executed click, fill, extract_table_data, and wait_for_download in a single turn!");
            }
            finally
            {
                if (File.Exists(batchDownloadDest)) File.Delete(batchDownloadDest);
            }

            // 39. Test Auto-Dismiss JS Dialogs & HandleNextDialog / GetLastDialog
            Console.WriteLine("[TEST 39] Native JS Dialog Auto-Dismiss & HandleNextDialog...");
            // Test default auto-accept of native alert
            await PlaywrightTools.EvaluateScript("() => { alert('Automated Alert Test'); return true; }");
            await Task.Delay(150);
            var lastAlertRes = await PlaywrightTools.GetLastDialog();
            using var alertDoc = JsonDocument.Parse(lastAlertRes);
            if (!alertDoc.RootElement.TryGetProperty("type", out var aType) || aType.GetString() != "alert")
                throw new Exception($"Default auto-accept of alert failed: {lastAlertRes}");
            Console.WriteLine($"[PASS] Auto-accepted alert: {lastAlertRes}");

            // Test explicit HandleNextDialog with dismiss on confirm
            await PlaywrightTools.HandleNextDialog(action: "dismiss");
            await PlaywrightTools.EvaluateScript("() => { confirm('Do you confirm?'); return true; }");
            await Task.Delay(150);
            var lastConfirmRes = await PlaywrightTools.GetLastDialog();
            using var confirmDoc = JsonDocument.Parse(lastConfirmRes);
            if (confirmDoc.RootElement.GetProperty("actionTaken").GetString() != "dismiss")
                throw new Exception($"HandleNextDialog dismiss failed: {lastConfirmRes}");
            Console.WriteLine($"[PASS] Configured dismiss on confirm: {lastConfirmRes}");

            // Test explicit HandleNextDialog with promptText on prompt
            await PlaywrightTools.HandleNextDialog(action: "accept", promptText: "SecretAnswer42");
            var promptResult = await PlaywrightTools.EvaluateScript(script: "() => prompt('Enter code:')");
            await Task.Delay(150);
            if (promptResult?.Trim('"') != "SecretAnswer42")
                throw new Exception($"HandleNextDialog promptText failed, got: '{promptResult}'");
            Console.WriteLine($"[PASS] Configured prompt accepted with text '{promptResult}'");

            // 40. Test Native Storage State Auto-Sync
            Console.WriteLine("[TEST 40] Native Storage State Auto-Sync...");
            await mgr.AutoSyncStorageStateAsync();
            var defaultStoragePath = PlaywrightTools.GetDefaultStorageStatePath();
            if (!File.Exists(defaultStoragePath) || new FileInfo(defaultStoragePath).Length == 0)
                throw new Exception($"Storage state auto-sync failed: file missing or empty at {defaultStoragePath}");
            Console.WriteLine($"[PASS] Storage state auto-synced to disk ({new FileInfo(defaultStoragePath).Length} bytes at {defaultStoragePath})");

            // 41. Test Direct DOM-to-Clipboard & BatchActions Clipboard Pipeline
            Console.WriteLine("[TEST 41] Direct DOM-to-Clipboard & BatchActions Pipeline...");
            // Standalone write and read
            var testSecret = "enterprise-api-key-" + Guid.NewGuid().ToString("N");
            var writeRes = await PlaywrightTools.WriteClipboard(testSecret);
            if (writeRes != "Success") throw new Exception($"WriteClipboard failed: {writeRes}");
            var readText = await PlaywrightTools.ReadClipboard();
            if (readText != testSecret) throw new Exception($"ReadClipboard mismatch: expected '{testSecret}', got '{readText}'");
            Console.WriteLine($"[PASS] Direct WriteClipboard and ReadClipboard matched: {readText}");

            // BatchActions clipboard pipeline
            var batchClipSecret = "batch-copied-token-998877";
            var batchClipRes = await PlaywrightTools.BatchActions(actions: new List<BatchActionItem>
            {
                new BatchActionItem { Type = "write_clipboard", Value = batchClipSecret },
                new BatchActionItem { Type = "read_clipboard" }
            });
            using var bClipDoc = JsonDocument.Parse(batchClipRes);
            if (bClipDoc.RootElement.GetProperty("status").GetString() != "success")
                throw new Exception($"BatchActions clipboard pipeline failed: {batchClipRes}");
            var lastStepOutput = bClipDoc.RootElement.GetProperty("steps")[1].GetProperty("clipboardText").GetString();
            if (lastStepOutput != batchClipSecret)
                throw new Exception($"BatchActions read_clipboard mismatch: expected '{batchClipSecret}', got '{lastStepOutput}'");
            Console.WriteLine($"[PASS] BatchActions clipboard pipeline executed in a single turn: {lastStepOutput}");

            // 42. Test Multi-Page Pagination Loop in ExtractTableData & BatchActions
            Console.WriteLine("[TEST 42] Multi-Page Pagination Loop in ExtractTableData & BatchActions...");
            await PlaywrightTools.EvaluateScript(script: @"() => {
                const el = document.getElementById('paged-container');
                if (el) el.remove();
                document.body.innerHTML += `
                    <div id='paged-container'>
                        <table id='paged-table'>
                            <thead><tr><th>ID</th><th>User</th></tr></thead>
                            <tbody id='paged-tbody'>
                                <tr><td>1</td><td>Alice</td></tr>
                                <tr><td>2</td><td>Bob</td></tr>
                            </tbody>
                        </table>
                        <button id='next-page-btn' onclick=""
                            document.getElementById('paged-tbody').innerHTML = '<tr><td>3</td><td>Charlie</td></tr><tr><td>4</td><td>David</td></tr>';
                            this.disabled = true;
                        "">Next</button>
                    </div>
                `;
                return true;
            }");

            var pagedRes = await PlaywrightTools.ExtractTableData(
                tableSelector: "#paged-table",
                nextPageSelector: "#next-page-btn",
                maxPages: 2,
                pageDelayMs: 100
            );
            using var pagedDoc = JsonDocument.Parse(pagedRes);
            var pagesScraped = pagedDoc.RootElement.GetProperty("pagesScraped").GetInt32();
            var pagedReturnedRows = pagedDoc.RootElement.GetProperty("returnedRows").GetInt32();
            if (pagesScraped != 2 || pagedReturnedRows != 4)
                throw new Exception($"Multi-page table extraction failed: pagesScraped={pagesScraped}, returnedRows={pagedReturnedRows}, output: {pagedRes}");

            var dataArr = pagedDoc.RootElement.GetProperty("data");
            if (dataArr[0].GetProperty("User").GetString() != "Alice" || dataArr[3].GetProperty("User").GetString() != "David")
                throw new Exception($"Multi-page table data content mismatch: {pagedRes}");
            Console.WriteLine($"[PASS] ExtractTableData aggregated 4 rows across 2 pages seamlessly: {pagedReturnedRows} rows");

            // 43. Test Smart Waiting on In-Flight Requests (waitForHttpMutation)
            Console.WriteLine("[TEST 43] Smart Waiting on In-Flight Requests (waitForHttpMutation)...");
            var testPage = mgr.GetPage(pageId);
            await testPage.RouteAsync("**/api/save-record", async route =>
            {
                await Task.Delay(250);
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"saved\":true}"
                });
            });

            await PlaywrightTools.EvaluateScript(script: @"() => {
                const el = document.getElementById('mutation-btn');
                if (el) el.remove();
                document.body.innerHTML += `
                    <button id='mutation-btn' onclick=""
                        fetch('/api/save-record', { method: 'POST', body: JSON.stringify({ audit: 1 }) });
                    "">Save Mutation</button>
                `;
                return true;
            }");

            var swMutation = System.Diagnostics.Stopwatch.StartNew();
            var clickMutationRes = await PlaywrightTools.Click(pageId: pageId, selector: "#mutation-btn", waitForHttpMutation: true);
            swMutation.Stop();
            if (clickMutationRes.StartsWith("Error"))
                throw new Exception($"Click with waitForHttpMutation failed: {clickMutationRes}");
            if (swMutation.ElapsedMilliseconds < 200)
                throw new Exception($"Click did not wait for mutating HTTP request, elapsed only {swMutation.ElapsedMilliseconds}ms");
            Console.WriteLine($"[PASS] Click with waitForHttpMutation waited {swMutation.ElapsedMilliseconds}ms for in-flight POST request to complete: {clickMutationRes}");

            // Also test BatchActions with waitForHttpMutation
            var batchMutationRes = await PlaywrightTools.BatchActions(actions: new List<BatchActionItem>
            {
                new BatchActionItem
                {
                    Type = "click",
                    Selector = "#mutation-btn",
                    WaitForHttpMutation = true
                }
            });
            using var bMutDoc = JsonDocument.Parse(batchMutationRes);
            if (bMutDoc.RootElement.GetProperty("status").GetString() != "success")
                throw new Exception($"BatchActions with waitForHttpMutation failed: {batchMutationRes}");
            Console.WriteLine($"[PASS] BatchActions with waitForHttpMutation completed successfully: {batchMutationRes}");

            // 44. Test Console Log & Network Transaction Text Pattern & Status Filtering
            Console.WriteLine("[TEST 44] Console Log & Network Transaction Text Pattern & Status Filtering...");
            // 44a: Console filtering
            PlaywrightTools.StartConsoleCapture(pageId);
            PlaywrightTools.ClearConsoleMessages(pageId);
            await PlaywrightTools.EvaluateScript(script: @"() => {
                console.log('[AUDIT] Normal compliance trace');
                console.warn('[SECURITY] Vulnerable library detected');
                console.error('[FATAL] Remote database unreachable: 503');
                console.info('Service heartbeat ok');
                return true;
            }");

            // Filter by level "error"
            var errLogsJson = PlaywrightTools.GetConsoleMessages(pageId, level: "error");
            using var errDoc = JsonDocument.Parse(errLogsJson);
            if (errDoc.RootElement.GetArrayLength() != 1 || !errLogsJson.Contains("FATAL"))
                throw new Exception($"Console filter by level 'error' failed: {errLogsJson}");

            // Filter by textPattern regex
            var secLogsJson = PlaywrightTools.GetConsoleMessages(pageId, textPattern: @"\[SECURITY\].*Vulnerable");
            using var secDoc = JsonDocument.Parse(secLogsJson);
            if (secDoc.RootElement.GetArrayLength() != 1 || !secLogsJson.Contains("Vulnerable"))
                throw new Exception($"Console filter by textPattern regex failed: {secLogsJson}");

            Console.WriteLine($"[PASS] GetConsoleMessages successfully filtered by level and regex text pattern!");

            // 44b: Network filtering
            PlaywrightTools.StartNetworkCapture(pageId);
            PlaywrightTools.ClearNetworkEntries(pageId);

            await testPage.RouteAsync("**/api/audit/logs", async route => await route.FulfillAsync(new() { Status = 200, Body = "[]" }));
            await testPage.RouteAsync("**/api/audit/create", async route => await route.FulfillAsync(new() { Status = 201, Body = "{}" }));
            await testPage.RouteAsync("**/api/audit/denied", async route => await route.FulfillAsync(new() { Status = 403, Body = "Forbidden" }));
            await testPage.RouteAsync("**/api/audit/broken", async route => await route.FulfillAsync(new() { Status = 500, Body = "Server Error" }));

            await PlaywrightTools.EvaluateScript(pageId: pageId, script: @"async () => {
                await fetch('https://audit.test/api/audit/logs');
                await fetch('https://audit.test/api/audit/create', { method: 'POST', body: '{}' });
                try { await fetch('https://audit.test/api/audit/denied'); } catch (e) {}
                try { await fetch('https://audit.test/api/audit/broken'); } catch (e) {}
                return true;
            }");

            await Task.Delay(250);

            // Filter minStatus 400
            var netErrorsJson = PlaywrightTools.ListNetworkTransactions(pageId, minStatus: 400);
            using var netErrDoc = JsonDocument.Parse(netErrorsJson);
            if (netErrDoc.RootElement.GetArrayLength() != 2)
                throw new Exception($"Network filter minStatus=400 expected 2 error transactions, got: {netErrorsJson}");

            // Filter method POST
            var netPostJson = PlaywrightTools.ListNetworkTransactions(pageId, method: "POST");
            using var netPostDoc = JsonDocument.Parse(netPostJson);
            if (netPostDoc.RootElement.GetArrayLength() != 1 || !netPostJson.Contains("/api/audit/create"))
                throw new Exception($"Network filter method=POST failed: {netPostJson}");

            // Filter urlPattern "broken"
            var netPatternJson = PlaywrightTools.ListNetworkTransactions(pageId, urlPattern: "broken");
            using var netPatDoc = JsonDocument.Parse(netPatternJson);
            if (netPatDoc.RootElement.GetArrayLength() != 1 || !netPatternJson.Contains("broken"))
                throw new Exception($"Network filter urlPattern='broken' failed: {netPatternJson}");

            Console.WriteLine($"[PASS] ListNetworkTransactions filtered by minStatus: 400, method: POST, and urlPattern: 'broken'!");

            // 45. Test Evidentiary Headers/Footers & Tamper-Resistant Compliance Audit Banner in ExportPageToPdf
            Console.WriteLine("[TEST 45] Evidentiary Headers/Footers & Compliance Audit Banner in ExportPageToPdf...");
            var auditPdfPath = Path.Combine(Path.GetTempPath(), $"compliance_audit_{Guid.NewGuid():N}.pdf");
            try
            {
                // 45a: Export to file with addComplianceAuditBanner: true
                var auditPdfRes = await PlaywrightTools.ExportPageToPdf(pageId, filePath: auditPdfPath, addComplianceAuditBanner: true);
                using var auditDoc = JsonDocument.Parse(auditPdfRes);
                if (auditDoc.RootElement.GetProperty("status").GetString() != "success")
                    throw new Exception($"ExportPageToPdf with compliance banner failed: {auditPdfRes}");
                if (!auditDoc.RootElement.GetProperty("complianceAuditBanner").GetBoolean())
                    throw new Exception("Expected complianceAuditBanner: true in response");
                if (!auditDoc.RootElement.GetProperty("displayHeaderFooter").GetBoolean())
                    throw new Exception("Expected displayHeaderFooter: true in response");
                if (!File.Exists(auditPdfPath))
                    throw new Exception($"Audit PDF file not found at {auditPdfPath}");
                
                var pdfBytes = await File.ReadAllBytesAsync(auditPdfPath);
                if (pdfBytes.Length < 100 || !System.Text.Encoding.ASCII.GetString(pdfBytes.Take(5).ToArray()).StartsWith("%PDF"))
                    throw new Exception("Exported file is not a valid PDF document");
                Console.WriteLine($"[PASS] ExportPageToPdf with compliance audit banner wrote valid PDF ({pdfBytes.Length} bytes)");

                // 45b: Export to base64 with custom header/footer templates
                var customHeaderRes = await PlaywrightTools.ExportPageToPdf(
                    pageId, 
                    displayHeaderFooter: true, 
                    headerTemplate: "<div style='font-size:9px; width:100%; text-align:center;'>CONFIDENTIAL AUDIT REPORT</div>",
                    footerTemplate: "<div style='font-size:9px; width:100%; text-align:center;'><span class='pageNumber'></span> of <span class='totalPages'></span></div>"
                );
                using var customDoc = JsonDocument.Parse(customHeaderRes);
                if (customDoc.RootElement.GetProperty("status").GetString() != "success" || string.IsNullOrEmpty(customDoc.RootElement.GetProperty("dataBase64").GetString()))
                    throw new Exception($"Custom header/footer PDF export failed: {customHeaderRes}");
                Console.WriteLine($"[PASS] ExportPageToPdf with custom headerTemplate and footerTemplate generated base64 PDF ({customDoc.RootElement.GetProperty("byteCount").GetInt32()} bytes)");

                // 45c: BatchActions with export_page_to_pdf and compliance audit banner
                var batchAuditPdfPath = Path.Combine(Path.GetTempPath(), $"batch_audit_{Guid.NewGuid():N}.pdf");
                try
                {
                    var batchPdfRes = await PlaywrightTools.BatchActions(actions: new List<BatchActionItem>
                    {
                        new BatchActionItem
                        {
                            Type = "export_page_to_pdf",
                            DestinationPath = batchAuditPdfPath,
                            AddComplianceAuditBanner = true
                        }
                    });
                    using var bPdfDoc = JsonDocument.Parse(batchPdfRes);
                    if (bPdfDoc.RootElement.GetProperty("status").GetString() != "success")
                        throw new Exception($"BatchActions export_page_to_pdf failed: {batchPdfRes}");
                    if (!File.Exists(batchAuditPdfPath) || new FileInfo(batchAuditPdfPath).Length == 0)
                        throw new Exception($"BatchActions did not write audit PDF to {batchAuditPdfPath}");
                    Console.WriteLine($"[PASS] BatchActions export_page_to_pdf successfully created compliance PDF ({new FileInfo(batchAuditPdfPath).Length} bytes)");
                }
                finally
                {
                    if (File.Exists(batchAuditPdfPath)) File.Delete(batchAuditPdfPath);
                }
            }
            finally
            {
                if (File.Exists(auditPdfPath)) File.Delete(auditPdfPath);
            }

            // 46. Test Auto-Dismiss / Bypass for Cookie Consent Banners
            Console.WriteLine("[TEST 46] Cookie Consent Banner Auto-Dismiss / Bypass...");
            // 46a: Test target: "consent" with OneTrust banner
            await PlaywrightTools.EvaluateScript(pageId: pageId, script: @"() => {
                const old = document.getElementById('onetrust-banner-sdk');
                if (old) old.remove();
                const banner = document.createElement('div');
                banner.id = 'onetrust-banner-sdk';
                banner.innerHTML = `
                    <p>We use cookies to improve your compliance tracking experience.</p>
                    <button id='onetrust-accept-btn-handler' onclick=""this.closest('#onetrust-banner-sdk').style.display = 'none'"">Accept All Cookies</button>
                `;
                document.body.appendChild(banner);
                return true;
            }");

            var bannerVisibleBefore = await testPage.IsVisibleAsync("#onetrust-banner-sdk");
            if (!bannerVisibleBefore) throw new Exception("Expected OneTrust banner to be visible before dismissal");

            var closeConsentRes = await PlaywrightTools.CloseOverlays(pageId, target: "consent");
            if (!closeConsentRes.Contains("Dismissed"))
                throw new Exception($"CloseOverlays(target: 'consent') failed: {closeConsentRes}");

            var bannerVisibleAfter = await testPage.IsVisibleAsync("#onetrust-banner-sdk");
            if (bannerVisibleAfter) throw new Exception("Expected OneTrust banner to be dismissed/hidden after CloseOverlays");
            Console.WriteLine($"[PASS] CloseOverlays(target: 'consent') successfully dismissed OneTrust banner: {closeConsentRes}");

            // 46b: Test Cookiebot dismissal via BatchActions
            await PlaywrightTools.EvaluateScript(pageId: pageId, script: @"() => {
                const old = document.getElementById('CookiebotDialog');
                if (old) old.remove();
                const banner = document.createElement('div');
                banner.id = 'CookiebotDialog';
                banner.innerHTML = `
                    <p>Cookiebot notice</p>
                    <button id='CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll' onclick=""this.closest('#CookiebotDialog').style.display = 'none'"">Allow all</button>
                `;
                document.body.appendChild(banner);
                return true;
            }");

            var batchConsentRes = await PlaywrightTools.BatchActions(actions: new List<BatchActionItem>
            {
                new BatchActionItem
                {
                    Type = "close_overlays",
                    Target = "consent"
                }
            });
            using var bConsentDoc = JsonDocument.Parse(batchConsentRes);
            if (bConsentDoc.RootElement.GetProperty("status").GetString() != "success")
                throw new Exception($"BatchActions close_overlays consent failed: {batchConsentRes}");

            var cookiebotVisibleAfter = await testPage.IsVisibleAsync("#CookiebotDialog");
            if (cookiebotVisibleAfter) throw new Exception("Expected Cookiebot dialog to be dismissed/hidden after batch close_overlays");
            Console.WriteLine($"[PASS] BatchActions close_overlays (consent) successfully dismissed Cookiebot dialog");

            // 46c: Test Navigate with bypassConsentBanners: true
            var navConsentHtml = "data:text/html," + Uri.EscapeDataString(@"
                <html><body>
                    <h1>Page with Consent</h1>
                    <div id='onetrust-consent-sdk'>
                        <button id='onetrust-accept-btn-handler' onclick=""this.parentElement.style.display='none'"">Accept</button>
                    </div>
                </body></html>");
            var navConsentRes = await PlaywrightTools.Navigate(url: navConsentHtml, bypassConsentBanners: true);
            if (navConsentRes != "Success")
                throw new Exception($"Navigate with bypassConsentBanners failed: {navConsentRes}");

            var navBannerVisible = await mgr.GetPage().IsVisibleAsync("#onetrust-consent-sdk");
            if (navBannerVisible) throw new Exception("Expected consent banner to be automatically dismissed during Navigate");
            Console.WriteLine($"[PASS] Navigate with bypassConsentBanners: true automatically dismissed consent banner upon arrival");

            // 47. Test Direct CSV Streaming in extract_table_data (Zero Memory Overhead)
            Console.WriteLine("[TEST 47] Direct CSV Streaming in ExtractTableData (Zero Memory Overhead)...");
            var streamCsvPath = Path.Combine(Path.GetTempPath(), $"streaming_table_{Guid.NewGuid():N}.csv");
            try
            {
                await PlaywrightTools.EvaluateScript(script: @"() => {
                    const el = document.getElementById('stream-container');
                    if (el) el.remove();
                    document.body.innerHTML += `
                        <div id='stream-container'>
                            <table id='stream-table'>
                                <thead><tr><th>TransactionId</th><th>Amount</th><th>Status</th></tr></thead>
                                <tbody id='stream-tbody'>
                                    <tr><td>TX_1001</td><td>$150.00</td><td>COMPLETED</td></tr>
                                    <tr><td>TX_1002</td><td>$2,450.50</td><td>PENDING</td></tr>
                                </tbody>
                            </table>
                            <button id='stream-next-btn' onclick=""
                                document.getElementById('stream-tbody').innerHTML = '<tr><td>TX_1003</td><td>$99.99</td><td>COMPLETED</td></tr><tr><td>TX_1004</td><td>$410.20</td><td>FAILED</td></tr>';
                                this.disabled = true;
                            "">Next</button>
                        </div>
                    `;
                    return true;
                }");

                var streamResultJson = await PlaywrightTools.ExtractTableData(
                    tableSelector: "#stream-table",
                    nextPageSelector: "#stream-next-btn",
                    maxPages: 2,
                    filePath: streamCsvPath,
                    format: "csv",
                    pageDelayMs: 100
                );

                using var streamDoc = JsonDocument.Parse(streamResultJson);
                var streamRoot = streamDoc.RootElement;
                if (streamRoot.GetProperty("status").GetString() != "success")
                    throw new Exception($"ExtractTableData streaming failed: {streamResultJson}");
                if (!streamRoot.GetProperty("streaming").GetBoolean())
                    throw new Exception("Expected streaming: true in result JSON");
                if (streamRoot.GetProperty("rowCount").GetInt32() != 4)
                    throw new Exception($"Expected rowCount 4, got {streamRoot.GetProperty("rowCount").GetInt32()}");
                if (streamRoot.GetProperty("pagesScraped").GetInt32() != 2)
                    throw new Exception($"Expected pagesScraped 2, got {streamRoot.GetProperty("pagesScraped").GetInt32()}");

                if (!File.Exists(streamCsvPath))
                    throw new Exception($"Streaming CSV was not written to {streamCsvPath}");

                var csvLines = await File.ReadAllLinesAsync(streamCsvPath);
                if (csvLines.Length != 5)
                    throw new Exception($"Expected 5 CSV lines (1 header + 4 rows), got {csvLines.Length}");
                if (csvLines[0] != "TransactionId,Amount,Status")
                    throw new Exception($"Unexpected CSV header line: {csvLines[0]}");
                if (!csvLines[2].Contains("\"$2,450.50\""))
                    throw new Exception($"Expected RFC 4180 quotes around amount with comma, got: {csvLines[2]}");

                Console.WriteLine($"[PASS] Direct CSV Streaming successfully wrote 4 rows across 2 pages directly to disk ({new FileInfo(streamCsvPath).Length} bytes)");
            }
            finally
            {
                if (File.Exists(streamCsvPath)) File.Delete(streamCsvPath);
            }

            // 48. Test autoDismissConsentBanners: true in batch_actions
            Console.WriteLine("[TEST 48] autoDismissConsentBanners in BatchActions between dynamic steps...");
            await PlaywrightTools.EvaluateScript(script: @"() => {
                const el = document.getElementById('dyn-container');
                if (el) el.remove();
                document.body.innerHTML += `
                    <div id='dyn-container'>
                        <button id='action-step-1' onclick=""
                            const b = document.createElement('div');
                            b.id = 'onetrust-banner-sdk';
                            b.innerHTML = '<p>Dynamic consent popup</p><button id=\'onetrust-accept-btn-handler\' onclick=\'this.parentElement.remove()\'>Accept</button>';
                            document.body.appendChild(b);
                        "">Step 1</button>
                        <button id='action-step-2' onclick=""
                            document.getElementById('action-result').innerText = 'Step 2 Complete';
                        "">Step 2</button>
                        <div id='action-result'>Pending</div>
                    </div>
                `;
                return true;
            }");

            var batchDynamicConsentRes = await PlaywrightTools.BatchActions(
                actions: new List<BatchActionItem>
                {
                    new BatchActionItem { Type = "click", Selector = "#action-step-1" },
                    new BatchActionItem { Type = "click", Selector = "#action-step-2" }
                },
                autoDismissConsentBanners: true
            );

            using var bDynDoc = JsonDocument.Parse(batchDynamicConsentRes);
            if (bDynDoc.RootElement.GetProperty("status").GetString() != "success")
                throw new Exception($"BatchActions with autoDismissConsentBanners failed: {batchDynamicConsentRes}");

            var resultText = await mgr.GetPage().InnerTextAsync("#action-result");
            if (resultText != "Step 2 Complete")
                throw new Exception($"Expected 'Step 2 Complete', got '{resultText}'");

            var bannerStillExists = await mgr.GetPage().Locator("#onetrust-banner-sdk").CountAsync() > 0;
            if (bannerStillExists)
                throw new Exception("Dynamic consent banner was NOT dismissed by autoDismissConsentBanners!");

            Console.WriteLine($"[PASS] autoDismissConsentBanners in BatchActions dynamically dismissed inter-step cookie banner!");

            Console.WriteLine("\n==================================================");
            Console.WriteLine("    ALL 48 ENTERPRISE TESTS PASSED SUCCESSFULLY!  ");
            Console.WriteLine("==================================================");
        }
        finally
        {
            await mgr.DisposeAsync();
        }
    }
}
