$targetDir = "C:\Users\alexander.akinyomola\.gemini\antigravity-cli\mcp\playwright"

# 1. First remove pageId from required across ALL tools
$jsonFiles = Get-ChildItem -Path $targetDir -Filter "*.json"
foreach ($file in $jsonFiles) {
    $raw = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $obj = $raw | ConvertFrom-Json
    if ($obj.parameters -and $obj.parameters.required) {
        $req = @($obj.parameters.required)
        if ($req -contains "pageId") {
            $newReq = @($req | Where-Object { $_ -ne "pageId" })
            $obj.parameters.required = $newReq
            $updatedJson = ConvertTo-Json -InputObject $obj -Depth 10 -Compress
            [System.IO.File]::WriteAllText($file.FullName, $updatedJson, [System.Text.Encoding]::UTF8)
            Write-Host "Removed pageId requirement from $($file.Name)"
        }
    }
}

# 2. Update navigate.json
$navigateJson = @'
{
  "name": "navigate",
  "description": "Navigates the page to the specified url. Optional waitUntil parameter supports 'domcontentloaded' (recommended default for SPAs to avoid 30s networkidle hangs), 'commit', 'load', or 'networkidle'. When bypassConsentBanners is true, automatically attempts to dismiss cookie consent dialogs (OneTrust, Cookiebot, etc.) after navigation settles. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to the last active page."
      },
      "url": {
        "type": "string",
        "description": "The target URL to navigate to."
      },
      "waitUntil": {
        "type": "string",
        "enum": [
          "domcontentloaded",
          "load",
          "networkidle",
          "commit"
        ],
        "default": "domcontentloaded",
        "description": "When to consider navigation successful. Default is 'domcontentloaded' for resilient SPA navigation."
      },
      "timeoutMs": {
        "type": [
          "number",
          "null"
        ],
        "default": 30000,
        "description": "Navigation timeout in milliseconds."
      },
      "bypassConsentBanners": {
        "type": "boolean",
        "default": false,
        "description": "If true, automatically attempts to dismiss cookie consent dialogs (OneTrust, Cookiebot, etc.) after navigation settles."
      }
    },
    "required": [
      "url"
    ],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "navigate.json"), ($navigateJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated navigate.json"

# 3. Update batch_actions.json
$batchJson = @'
{
  "name": "batch_actions",
  "description": "Executes an ordered pipeline of actions (navigate, click, hover, fill, fill_input, select_option, press_key, wait_for_selector, close_overlays, auto_scroll_page, wait_for_download, extract_table_data, export_page_to_pdf, read_clipboard, write_clipboard, handle_next_dialog, delay) in a single turn. Reduces agent round trips and latency for multi-step form and UI workflows. When autoDismissConsentBanners is true, automatically checks and dismisses cookie consent dialogs between action steps if any modal or banner appears. If abortOnError is true (default), halts execution immediately on failure. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to active page."
      },
      "abortOnError": {
        "default": true,
        "type": "boolean",
        "description": "If true, stops pipeline execution on the first error and reports the failed step."
      },
      "autoDismissConsentBanners": {
        "default": false,
        "type": "boolean",
        "description": "If true, automatically checks and dismisses cookie consent dialogs between action steps if any modal or banner appears."
      },
      "actions": {
        "items": {
          "type": "object",
          "properties": {
            "type": { "type": "string", "description": "Action type: navigate, click, hover, fill, fill_input, select_option, press_key, wait_for_selector, close_overlays, auto_scroll_page, wait_for_download, extract_table_data, export_page_to_pdf, read_clipboard, write_clipboard, handle_next_dialog, delay" },
            "action": { "type": "string", "description": "Action mode for dialogs: accept, dismiss" },
            "selector": { "type": "string" },
            "url": { "type": "string" },
            "value": { "type": "string" },
            "text": { "type": "string" },
            "option": { "type": "string" },
            "key": { "type": "string" },
            "state": { "type": "string" },
            "target": { "type": "string" },
            "button": { "type": "string" },
            "clickCount": { "type": "integer" },
            "delay": { "type": "number" },
            "force": { "type": "boolean" },
            "timeoutMs": { "type": "number" },
            "durationMs": { "type": "number" },
            "postDelayMs": { "type": "number" },
            "index": { "type": "integer" },
            "autoFirst": { "type": "boolean" },
            "waitForStable": { "type": "boolean" },
            "animationTimeoutMs": { "type": "number" },
            "topmostOnly": { "type": "boolean" },
            "pressEnter": { "type": "boolean" },
            "frameSelector": { "type": "string" },
            "searchInputSelector": { "type": "string" },
            "dropdownSelector": { "type": "string" },
            "destinationPath": { "type": "string" },
            "filePath": { "type": "string" },
            "columns": { "type": "array", "items": { "type": "string" } },
            "maxRows": { "type": "integer" },
            "format": { "type": "string" },
            "scrollFirst": { "type": "boolean" },
            "scrollDelayMs": { "type": "integer" },
            "maxScrolls": { "type": "integer" },
            "waitForHttpMutation": { "type": "boolean" },
            "nextPageSelector": { "type": "string" },
            "maxPages": { "type": "integer" },
            "pageDelayMs": { "type": "integer" },
            "displayHeaderFooter": { "type": "boolean" },
            "headerTemplate": { "type": "string" },
            "footerTemplate": { "type": "string" },
            "addComplianceAuditBanner": { "type": "boolean" },
            "bypassConsentBanners": { "type": "boolean" },
            "landscape": { "type": "boolean" },
            "autoExpandScrollContainers": { "type": "boolean" }
          },
          "required": ["type"]
        },
        "type": "array",
        "description": "Ordered list of action items to execute sequentially."
      }
    },
    "required": [
      "actions"
    ],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "batch_actions.json"), ($batchJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated batch_actions.json"

# 4. Add handle_next_dialog.json
$handleDialogJson = @'
{
  "name": "handle_next_dialog",
  "description": "Configures how the next native JavaScript dialog (alert, confirm, prompt, beforeunload) on the page should be handled. Action can be 'accept' or 'dismiss', with optional promptText for prompt dialogs. Automatically attaches the dialog listener if not already active. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to active page."
      },
      "action": {
        "default": "accept",
        "type": "string",
        "description": "How to handle the dialog: 'accept' (default) or 'dismiss'."
      },
      "promptText": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional text to supply if the dialog is a JavaScript prompt."
      }
    },
    "required": [],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "handle_next_dialog.json"), ($handleDialogJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated handle_next_dialog.json"

# 5. Add get_last_dialog.json
$getLastDialogJson = @'
{
  "name": "get_last_dialog",
  "description": "Gets details about the most recently handled JavaScript dialog (alert, confirm, prompt, beforeunload) on the page, including its type, message, and action taken. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to active page."
      }
    },
    "required": [],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "get_last_dialog.json"), ($getLastDialogJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated get_last_dialog.json"

# 6. Add read_clipboard.json
$readClipboardJson = @'
{
  "name": "read_clipboard",
  "description": "Reads the current browser/system clipboard text. Automatically grants clipboard permissions and focuses page. Useful for capturing tokens, API keys, or text copied from UI 'Copy to Clipboard' actions. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to active page."
      }
    },
    "required": [],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "read_clipboard.json"), ($readClipboardJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated read_clipboard.json"

# 7. Add write_clipboard.json
$writeClipboardJson = @'
{
  "name": "write_clipboard",
  "description": "Writes text into the browser clipboard. Automatically grants clipboard permissions and focuses page. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "text": {
        "type": "string",
        "description": "The text to copy into the clipboard."
      },
      "pageId": {
        "type": [
          "string",
          "null"
        ],
        "description": "Optional page ID. If omitted, defaults to active page."
      }
    },
    "required": [
      "text"
    ],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "write_clipboard.json"), ($writeClipboardJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated write_clipboard.json"

# 8. Update extract_table_data.json
$extractTableJson = @'
{
  "name": "extract_table_data",
  "description": "Extracts structured tabular data from <table> elements, Ant Design tables (.ant-table), Material UI tables (.MuiTable-root), or ARIA grids ([role='table'], [role='grid']). Supports multi-page pagination loop (nextPageSelector, maxPages, pageDelayMs), extracting from iframes via frameSelector or automatic iframe piercing. Can export directly to CSV (RFC 4180) or JSON on disk via filePath, auto-scroll virtual tables prior to extraction (scrollFirst), and filter columns or rows. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "tableSelector": { "type": ["string", "null"], "description": "CSS selector for the table element." },
      "columns": { "type": ["array", "null"], "items": { "type": "string" }, "description": "Optional list of column header names to extract." },
      "maxRows": { "type": ["integer", "null"], "default": 25, "description": "Maximum rows to return per page." },
      "frameSelector": { "type": ["string", "null"], "description": "Optional iframe selector." },
      "filePath": { "type": ["string", "null"], "description": "Destination file path to save extracted table as CSV or JSON." },
      "format": { "type": "string", "default": "json", "enum": ["json", "csv"], "description": "Format for output file ('json' or 'csv')." },
      "scrollFirst": { "type": "boolean", "default": false, "description": "Whether to auto-scroll virtual scroll containers before extraction." },
      "scrollDelayMs": { "type": "integer", "default": 150, "description": "Delay in ms between incremental scrolls." },
      "maxScrolls": { "type": "integer", "default": 30, "description": "Max scrolls when scrollFirst is enabled." },
      "nextPageSelector": { "type": ["string", "null"], "description": "Optional CSS selector for pagination 'Next' button (.ant-pagination-next, button:has-text('Next')). When provided with maxPages > 1, automatically loops across pages and aggregates all rows." },
      "maxPages": { "type": ["integer", "null"], "default": 1, "description": "Maximum number of pages to scrape when nextPageSelector is provided. Defaults to 1." },
      "pageDelayMs": { "type": ["integer", "null"], "default": 400, "description": "Delay in milliseconds after clicking nextPageSelector to allow table rows to load and settle." }
    },
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "extract_table_data.json"), ($extractTableJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated extract_table_data.json"

# 9. Update click.json
$clickJson = @'
{
  "name": "click",
  "description": "Clicks on a selector on the page. Supports iframe piercing (frameSelector or auto-piercing), fast-fail action timeouts (timeoutMs, default 10000), topmost modal/drawer scoping (topmostOnly: true), multi-match disambiguation (autoFirst / index), pre-click animation stabilization (waitForStable: true), post-click transition delay (postClickDelayMs, e.g. 300), and smart waiting for in-flight HTTP mutations (waitForHttpMutation: true). pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "selector": { "type": "string", "description": "CSS or text selector for the target element." },
      "timeoutMs": { "type": ["number", "null"], "default": 10000, "description": "Action timeout in milliseconds." },
      "postClickDelayMs": { "type": ["number", "null"], "description": "Optional delay in ms after clicking." },
      "index": { "type": ["integer", "null"], "description": "Index of element if selector matches multiple." },
      "autoFirst": { "type": "boolean", "default": true, "description": "Whether to default to the first matching element." },
      "waitForStable": { "type": "boolean", "default": true, "description": "Wait for element to stabilize before clicking." },
      "animationTimeoutMs": { "type": ["number", "null"], "default": 250, "description": "Stabilization timeout in ms." },
      "topmostOnly": { "type": "boolean", "default": false, "description": "Only click elements inside topmost modal/drawer." },
      "frameSelector": { "type": ["string", "null"], "description": "Optional iframe selector." },
      "waitForHttpMutation": { "type": "boolean", "default": false, "description": "If true, monitors in-flight HTTP mutating requests (POST, PUT, DELETE, PATCH) triggered by the click and waits up to 3000ms for them to settle before completing." }
    },
    "required": ["selector"],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "click.json"), ($clickJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated click.json"

# 10. Update fill_input.json
$fillInputJson = @'
{
  "name": "fill_input",
  "description": "Fills text into an input field using React controlled input prototype setters and native input events. Resilient against React synthetic events and custom frameworks. Supports waitForHttpMutation to wait for asynchronous form validation / autosave requests. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "selector": { "type": "string", "description": "CSS selector for the input element." },
      "value": { "type": "string", "description": "Text value to fill." },
      "timeoutMs": { "type": ["number", "null"], "default": 10000, "description": "Timeout in milliseconds." },
      "index": { "type": ["integer", "null"], "description": "Index if selector matches multiple." },
      "autoFirst": { "type": "boolean", "default": true, "description": "Default to first match." },
      "waitForStable": { "type": "boolean", "default": true, "description": "Wait for input to stabilize." },
      "animationTimeoutMs": { "type": ["number", "null"], "default": 250, "description": "Stabilization timeout." },
      "topmostOnly": { "type": "boolean", "default": false, "description": "Target only inputs in topmost modal/drawer." },
      "frameSelector": { "type": ["string", "null"], "description": "Optional iframe selector." },
      "waitForHttpMutation": { "type": "boolean", "default": false, "description": "If true, monitors in-flight HTTP mutating requests (POST, PUT, DELETE, PATCH) triggered by the input and waits up to 3000ms for them to settle." }
    },
    "required": ["selector", "value"],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "fill_input.json"), ($fillInputJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated fill_input.json"

# 11. Update select_option.json
$selectOptionJson = @'
{
  "name": "select_option",
  "description": "Selects an option from a dropdown. Supports standard native HTML <select>, Ant Design, Material UI, and custom div-based dropdowns. Supports search filtering, custom dropdown selectors, and waitForHttpMutation. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "selector": { "type": "string", "description": "Selector for the dropdown trigger or select element." },
      "option": { "type": "string", "description": "The option text or value to select." },
      "searchInputSelector": { "type": ["string", "null"], "description": "Optional search input selector inside dropdown." },
      "dropdownSelector": { "type": ["string", "null"], "description": "Optional dropdown menu selector." },
      "waitForStable": { "type": "boolean", "default": true, "description": "Wait for dropdown to stabilize." },
      "animationTimeoutMs": { "type": ["number", "null"], "default": 250, "description": "Stabilization timeout." },
      "timeoutMs": { "type": ["number", "null"], "default": 10000, "description": "Timeout in milliseconds." },
      "frameSelector": { "type": ["string", "null"], "description": "Optional iframe selector." },
      "waitForHttpMutation": { "type": "boolean", "default": false, "description": "If true, monitors in-flight HTTP mutating requests triggered by the selection and waits up to 3000ms for them to settle." }
    },
    "required": ["selector", "option"],
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "select_option.json"), ($selectOptionJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated select_option.json"

# 12. Update get_console_messages.json
$consoleMessagesJson = @'
{
  "name": "get_console_messages",
  "description": "Get captured console messages for the page as JSON array. Supports optional filtering by textPattern (regex or substring) and level (log, info, warn, error, debug). pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "textPattern": { "type": ["string", "null"], "description": "Optional regex pattern or substring to filter console messages by text." },
      "level": { "type": ["string", "null"], "description": "Optional log level to filter by ('log', 'info', 'warn', 'error', 'debug')." }
    },
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "get_console_messages.json"), ($consoleMessagesJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated get_console_messages.json"

# 13. Update list_network_transactions.json
$listNetworkJson = @'
{
  "name": "list_network_transactions",
  "description": "List captured network transactions (summaries) for the page as JSON array. Filter options: 'xhr_only', 'errors_only', or null for all. Set excludeStaticAssets=false to include images/fonts/css. Filter by domainFilter, urlPattern (regex/substring), minStatus/maxStatus (e.g. 400 to 599), or HTTP method (GET, POST, etc.). pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "description": "Optional page ID. If omitted, defaults to active page." },
      "filter": { "type": ["string", "null"], "description": "Filter options: 'xhr_only', 'errors_only', or null for all." },
      "excludeStaticAssets": { "type": "boolean", "default": true, "description": "Whether to exclude static assets like stylesheets, images, and fonts." },
      "domainFilter": { "type": ["string", "null"], "description": "Optional domain filter substring (e.g. 'api.kuda.com')." },
      "urlPattern": { "type": ["string", "null"], "description": "Optional regex pattern or substring to filter transactions by URL." },
      "minStatus": { "type": ["integer", "null"], "description": "Optional minimum HTTP status code (e.g. 400 for errors)." },
      "maxStatus": { "type": ["integer", "null"], "description": "Optional maximum HTTP status code (e.g. 499 for client errors)." },
      "method": { "type": ["string", "null"], "description": "Optional HTTP method filter ('GET', 'POST', 'PUT', 'DELETE', etc.)." }
    },
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "list_network_transactions.json"), ($listNetworkJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated list_network_transactions.json"

# 14. Update export_page_to_pdf.json
$exportPdfJson = @'
{
  "name": "export_page_to_pdf",
  "description": "Exports the current page to a PDF document. Supports configuring format ('A4', 'Letter', etc.), printBackground, landscape, margins, scale, pageRanges, and writing directly to filePath or returning base64 PDF bytes. Supports displayHeaderFooter, headerTemplate, footerTemplate, and addComplianceAuditBanner (automatically stamps audit trail metadata with date, time, title, url, page numbers for SOC2/GRC audits). Works seamlessly across both headless sessions and headed extension bridge sessions via automatic headless transfer fallback. When autoExpandScrollContainers is true (default), automatically unconstrains SPA scroll containers (Ant Design, MUI, Tailwind, virtualized tables, summary cards) for complete multi-page printing without clipping. If selector is provided, scopes the PDF print specifically to that element (e.g. modal, drawer, summary card). If scrollFirst is true, auto-scrolls the page before printing to hydrate virtualized DOM. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "default": null, "description": "Optional page ID. If omitted, defaults to active page." },
      "filePath": { "type": ["string", "null"], "default": null, "description": "Target file path to save the PDF. If omitted, base64 PDF bytes are returned." },
      "format": { "type": "string", "default": "A4", "description": "Paper format (e.g. 'A4', 'Letter', 'Legal', 'A3', 'Tabloid'). Default is 'A4'." },
      "printBackground": { "type": "boolean", "default": true, "description": "Print background graphics and colors. Default is true." },
      "landscape": { "type": "boolean", "default": false, "description": "Paper orientation: true for landscape, false for portrait." },
      "scale": { "type": ["number", "null"], "default": null, "description": "Scale of the webpage rendering. Defaults to 1." },
      "pageRanges": { "type": ["string", "null"], "default": null, "description": "Paper ranges to print, e.g., '1-5', '8', '11-13'." },
      "marginTop": { "type": ["string", "null"], "default": null, "description": "Top margin, accepts values labeled with units (e.g. '10mm', '0.5in')." },
      "marginBottom": { "type": ["string", "null"], "default": null, "description": "Bottom margin, accepts values labeled with units." },
      "marginLeft": { "type": ["string", "null"], "default": null, "description": "Left margin, accepts values labeled with units." },
      "marginRight": { "type": ["string", "null"], "default": null, "description": "Right margin, accepts values labeled with units." },
      "autoExpandScrollContainers": { "type": "boolean", "default": true, "description": "When true, injects print rules that unconstrain SPA scroll containers for full multi-page printing." },
      "forceFallback": { "type": "boolean", "default": false, "description": "If true, bypasses native PDF generation and immediately uses headless transfer rendering." },
      "selector": { "type": ["string", "null"], "default": null, "description": "CSS selector to scope the PDF export to a specific element/container." },
      "scrollFirst": { "type": "boolean", "default": false, "description": "If true, auto-scrolls down the page/container to hydrate virtualized DOM before printing." },
      "displayHeaderFooter": { "type": "boolean", "default": false, "description": "Whether to display evidentiary header and footer templates." },
      "headerTemplate": { "type": ["string", "null"], "default": null, "description": "HTML template for the print header. CSS classes: date, title, url, pageNumber, totalPages." },
      "footerTemplate": { "type": ["string", "null"], "default": null, "description": "HTML template for the print footer. CSS classes: date, title, url, pageNumber, totalPages." },
      "addComplianceAuditBanner": { "type": "boolean", "default": false, "description": "When true, automatically stamps GRC compliance audit metadata (date, time, url, pageNumber, totalPages)." }
    },
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "export_page_to_pdf.json"), ($exportPdfJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated export_page_to_pdf.json"

# 15. Update close_overlays.json
$closeOverlaysJson = @'
{
  "name": "close_overlays",
  "description": "Dismisses or closes active popups, cascading modal dialogs, sliding drawers, dropdown menus, and cookie consent banners (OneTrust, Cookiebot, etc.). Can target 'topmost', 'all', or 'consent'. Dispatches Escape, clicks close/accept buttons (.ant-drawer-close, .ant-modal-close, #onetrust-accept-btn-handler, etc.), and waits for backdrop masks to settle. pageId is optional and defaults to active page.",
  "parameters": {
    "properties": {
      "pageId": { "type": ["string", "null"], "default": null, "description": "Optional page ID. If omitted, defaults to active page." },
      "target": { "type": "string", "default": "topmost", "description": "Overlay target mode: 'topmost' to close the topmost overlay, 'all' to close all cascading overlays, or 'consent' to dismiss cookie/compliance consent dialogs." },
      "waitForSettle": { "type": "boolean", "default": true, "description": "Whether to wait for modals and mask backdrops to completely animate out and unmount." },
      "timeoutMs": { "type": ["number", "null"], "default": 5000, "description": "Timeout in milliseconds." }
    },
    "type": "object"
  }
}
'@
[System.IO.File]::WriteAllText((Join-Path $targetDir "close_overlays.json"), ($closeOverlaysJson | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
Write-Host "Updated close_overlays.json"

