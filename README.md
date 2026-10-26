# MyLog - Enterprise Log Viewer & Analytics Console

**MyLog** is a lightweight, high-performance, and offline-ready log viewer and analytics platform built on ASP.NET Core 8. It is engineered specifically for banking and enterprise systems (SIT, UAT, and Production) to monitor, inspect, search, and analyze application logs without installing heavy agents or relying on external cloud/CDN dependencies.

📖 **[อ่านคู่มือการใช้งานระบบภาษาไทย (User Manual)](./USER_MANUAL.md)**

---

## 🌟 Key Features

### 1. 📊 Log Analytics & Dashboard
- **Instant KPI Metrics:** Error/Critical, Warning, Info, and Total Lines Scanned counts.
- **Hourly Distribution Histogram:** High-DPI Canvas-based dual-color bar chart comparing error vs. warning frequencies over time to spot error spikes immediately.
- **Top 5 Error Patterns:** Automatic extraction and clustering of the most frequent exception types and error messages.
- **Interactive Filtering:** Click any KPI card, chart bar, or error badge to instantly filter the log viewer to matching lines.

### 2. ⚡ Pre-built Quick Filters
- Instant dropdown presets for rapid troubleshooting without typing regular expressions manually:
  - 💥 **Errors & Exceptions Only:** Precise severity filtering (excludes JSON fields like `'error':null` in INFO logs).
  - ⚠️ **Warnings:** Isolates warning lines.
  - 🌐 **HTTP 4xx / 5xx:** Detects HTTP client/server status failure codes.
  - ⏱️ **Slow Requests (>1s):** Detects operations taking >1,000ms.
  - 🆔 **Trace ID / GUID:** Standard UUID/GUID pattern matching.
  - 🔌 **Timeouts & Sockets:** Captures timeout and network connection failures.

### 3. 🔍 Folder-Level Search (Grep in Folder)
- Search keywords, Transaction IDs, or Trace IDs across **all log files** in the current directory concurrently.
- Accessible directly from the Log Viewer (`📁 In Folder` button) and the File Browser (`🔍 Search in folder`).
- Click any search result to open that file and jump directly to the matched line (`#L{lineNumber}`).

### 4. ⏱️ Time-Range Filter
- Filter log output within a precise time window by specifying `Start Time` and `End Time` (e.g. `02:00:00` to `02:45:00`).
- Seamlessly isolates incidents while preserving stack trace context.

### 5. ⚡ Live Tailing & Advanced Viewer
- **Real-time Streaming:** Low-overhead live log tailing powered by ASP.NET Core SignalR.
- **Context Lines:** View matching lines with `±1`, `±2`, `±3`, `±5`, or `±10` surrounding context lines.
- **JSON Formatting:** Auto-formats inline JSON payloads for readability.
- **Quick Copy & Bookmarks:** Click any line number to copy the line directly to clipboard with instant confirmation.
- **Custom Highlighting:** Comma-separated keyword highlighter with distinct colors.

### 6. 🌐 Remote Log Gateway Integration
- Browse and read remote logs from SIT/UAT server gateways securely via configured API endpoints.

### 7. 🚀 High-Speed Downloads & ZIP Packaging
- High-throughput streaming in 1MB chunks bypassing Kestrel's 64KB buffer limit.
- Download entire directories as uncompressed ZIP archives on-the-fly with `FileShare.ReadWrite` support.

### 8. 🔗 One-Click Request Flow Tracker
- Automatically detects `RequestUid`, `CorrelationId`, and GUIDs in log output and transforms them into interactive clickable chips.
- Click any chip to isolate all steps of that transaction across the file with a floating flow banner and 1-click clipboard export.

### 9. ⭐ Log Bookmarks & Pinned Lines
- Pin critical log lines with a single click (`☆`/`⭐`).
- Slide-out Bookmarks Drawer displaying pinned lines, timestamps, and severity levels.
- 1-click jump to any bookmarked line, and 1-click formatted incident summary export for JIRA/Teams.

### 10. 📱 Mobile-First UX Field Console
- **Sticky Bottom Command Bar:** Ergonomically placed in the mobile Thumb Zone for effortless one-handed triage. Includes 1-tap **`🚨 Errors Only`** filter, **`↩️ Word Wrap`** toggle, **`🔍 Quick Search`**, **`⭐ Bookmarks`** badge, **`📊 Analytics`**, and **`⬇️ Live Scroll`**.
- **Tap-to-Inspect Bottom Sheet:** Tapping any log line on mobile opens an inspection drawer with timestamp, severity badge, detected GUIDs, and 1-tap buttons for **`🔗 Track Flow`**, **`📋 Copy GUID`**, and **`⭐ Bookmark`**.
- **Landscape Pro Terminal Mode & Word Wrap:** Automatically adapts to landscape orientation with edge-to-edge rendering, floating translucent action capsule, and 1-tap **`↩️`** Word Wrap toggle to switch between fully wrapped readable lines and single-line terminal view with smooth horizontal scrolling.
- **Severity Accent Left Stripes:** Distinct colored edge stripes on all log lines (Red = Error, Amber = Warn, Blue = Info) for instantaneous visual incident scanning.

### 11. 🌐 Enterprise Cluster & Load Balancer Log Aggregation
- **Unified Chronological Timeline:** Merges and interleaves log streams from multiple servers behind a Load Balancer in parallel (`Task.WhenAll`), sorted precisely by timestamp.
- **Node Switcher Tabs:** Seamlessly toggle between `[🌐 All Nodes (Merged Timeline)]` or focus on individual nodes (`[💻 Node 1]`, `[💻 Node 2]`) in 1 click.
- **Node Identity Badges:** Color-coded inline tags (`[App Server A]`, `[App Server B]`) indicate which server instance handled each step.
- **Cross-Cluster Scatter-Gather Search:** Query Trace IDs across all nodes in the cluster simultaneously.
- **Zero-RAM Temporary File ZIP Downloads:** Bundles logs from all cluster machines into a structured ZIP without Large Object Heap memory spikes.
- **Node Reachability Probing:** Auto-probes node health with a 2-second timeout and live status indicators.

---

## ⚙️ Configuration (`appsettings.json`)

```json
{
  "AllowedFolders": [
    "D:\\WSProjectLogs\\SIT",
    "C:\\Logs"
  ],
  "DownloadLimits": {
    "MaxZipSizeMB": 100,
    "MaxExtractedSizeMB": 300
  },
  "RemoteLogApi": {
    "BaseUrl": "https://gateway-url/",
    "ApiKey": "",
    "TimeoutSeconds": 15
  }
}
```

---

## 🚀 Getting Started

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

### Run Locally
```bash
# คัดลอกคอนฟิกตัวอย่างสำหรับใช้งาน
cp appsettings.example.json appsettings.json

dotnet restore
dotnet run
```
Open your browser at `http://localhost:5000` (or the configured port).

### Build for Production / UAT
```bash
dotnet publish -c Release -o ./publish
```

---

## 🛡️ Security & Enterprise Compliance
- **Offline / Intranet-Ready:** Zero external CDN dependencies (all styles, scripts, and charts are fully self-contained in `wwwroot`).
- **Path Traversal Protection:** All file and directory accesses are strictly validated against `AllowedFolders` using canonical paths to prevent `../` directory escapes.