# Changelog

All notable changes to this project will be documented in this file.

## [1.5.0] - 2026-09-03

### Added
- **Enterprise Cluster & Load Balancer Log Aggregation**:
  - **Unified Chronological Timeline**: Merges logs across all machines behind a Load Balancer in parallel (`Task.WhenAll`), parsing timestamps and interleaving them in exact chronological order.
  - **Dynamic Node Switcher Tabs**: Seamlessly toggle between `[🌐 All Nodes (Merged Timeline)]` or drill down into specific nodes (`[💻 Node 1]`, `[💻 Node 2]`) in one click.
  - **Node Identity Badges**: Color-coded inline chips (`[App Server A (LB-01)]`, `[App Server B (LB-02)]`) indicate which instance processed each transaction line.
  - **Cross-Node Scatter-Gather Search**: Searches keywords or Trace IDs across all nodes in the cluster simultaneously, tagging matched lines by node.
  - **Zero-RAM Temporary File ZIP Downloads**: Merges logs from all nodes into a structured ZIP (`node-name/app.log`) using temporary streaming with `DeleteOnClose`, keeping memory overhead near zero.
  - **Node Reachability Health Probing**: Automatic 2-second timeout probe with status indicators (online green dot / offline red dot) so unreachable nodes never hang the UI.
  - **Deep Linking Support**: Direct URL navigation to cluster logs (`/?cluster=service-cluster-01&file=app.log&node=all`).
  - **Flexible Dual Transport**: Works with both local/UNC file paths (e.g. `\\192.168.1.10\d$\WSProjectLogs`) and remote HTTP API gateways.

## [1.4.1] - 2026-09-03

### Performance & Scalability
- **Large Log DOM Protection**: Initial file loads now cap rendering at `MAX_VIEWER_LINES` (3,000) with a notification header and real line numbers preserved, preventing browser freezing on 50,000+ line logs.
- **Incremental Stats Counters**: Replaced full O(N) DOM scans on live tail with O(1) incremental state counters (`errCount`, `wrnCount`).
- **Memory Pressure Relief**:
  - Remote ZIP downloads now stream directly to disk with `FileOptions.DeleteOnClose` instead of allocating 100MB `MemoryStream` on the Large Object Heap.
  - Used `ArrayPool<byte>.Shared` in `FileBrowserService` line counting buffer to eliminate 1MB LOH allocations.
  - Capped folder cache at 50 directories with FIFO eviction.
- **Eliminated Layout Thrashing**: Batched DOM reads and writes in `timeFilter.js` and JSON formatting.
- **CSS Selector Optimization**: Replaced expensive `:not(:has())` selector with fast class-based selector.
- **Search Input Debounce**: Added 200ms debounce to prevent re-rendering file lists on every keystroke.

### Bug Fixes & Reliability
- **Stream Lifecycle Safety**: Added `leaveOpen: true` to `ZipArchive` in `FilesController` to prevent closing `Response.Body`.
- **Resource Cleanup**: Properly disposed `CancellationTokenSource` in `LogTailService`.
- **Race Condition Prevention**: Added request token concurrency checks in `searchContent`, `loadFolder`, and `loadAnalytics` to prevent stale asynchronous data from overwriting UI state.
- **Midnight Wrap-Around Filter**: Fixed time filter bug when filtering across midnight (e.g. 23:00 to 01:00).
- **Download Folder URL**: Used `api()` helper to ensure correct base paths.
- **Memory Leak Cleanups**: Cleared DOM element references in mobile line inspector on dismissal and file change.
- **Accessibility & Z-Index**: Added `for="logs-only"` attribute on filter checkbox label and organized z-index layer hierarchy.

## [1.4.0] - 2026-09-03

### Added
- **Mobile-First UX Field Console Architecture**:
  - **Sticky Bottom Command Bar (`#mobile-command-bar`)**:
    - Positioned in the mobile "Thumb Zone" for comfortable one-handed incident triage on smartphones.
    - **`🚨 Errors Only` Pill**: 1-tap instant client-side toggle to filter out INFO lines and isolate failures in 1 second.
    - **`🔍 Quick Search`**: 1-tap quick scroll and focus to the search input.
    - **`⭐ Bookmarks Counter Badge`**: Real-time counter badge with 1-tap drawer access to bookmarked lines.
    - **`📊 Quick Analytics`**: 1-tap drawer toggle for log distribution histogram and KPI summary.
    - **`⬇️ Auto-Scroll / Live Jump`**: 1-tap scroll to the latest live log line.
  - **Tap-to-Inspect Bottom Sheet (`#line-inspector-sheet`)**:
    - Tapping any log line on mobile slides up a clean inspection drawer.
    - Auto-detects and displays severity badge, line number, timestamp, and transaction GUIDs.
    - 1-tap **`🔗 Track Flow`** to isolate the full transaction lifecycle immediately.
    - 1-tap **`📋 Copy GUID`**, **`📋 Copy Full Line`**, and **`⭐ Add Bookmark`**.
  - **Landscape "Pro Terminal" Console Mode & Word Wrap**:
    - Auto-adjusts layout when rotating device to landscape, maximizing horizontal width (760px+) with edge-to-edge rendering.
    - **1-Tap Quick `↩️` Word Wrap Toggle**: Switch between responsive wrapped view (full log lines visible without horizontal cut-off) and single-line terminal view with smooth horizontal scrolling.
    - Floating translucent action capsule with sufficient bottom clearance (`75px` padding) so lines are never obscured beneath buttons.
  - **1-Tap Errors Only Filter**:
    - Solid red active state illumination on both capsule pill and top stats ribbon badge.
    - Supports Serilog `EROR`, `CRIT`, `FATAL`, `TIMEOUT`, and automatically preserves multiline continuation stack traces (`   at ...`).
  - **Severity Accent Left Stripes**:
    - Added colored left border stripes on all log lines (Red = Error, Amber = Warn, Blue = Info) for effortless visual scanning.

## [1.3.0] - 2026-09-03

### Added
- **One-Click Request Flow / Correlation Tracker (🔗 Trace Chips & Flow Banner)**:
  - Automatic detection of `RequestUid`, `CorrelationId`, and GUIDs in log output rendered as interactive clickable chips.
  - Clicking any trace chip immediately isolates and tracks all lifecycle events for that single request.
  - Floating `#flow-banner` with total event counter, 1-click full flow clipboard export (`📋 Copy Flow`), and seamless `✕ Exit Flow`.
- **Log Bookmarks & Pinned Lines (⭐ Bookmarks)**:
  - Star icon (`☆`/`⭐`) on every log line to pin critical lines.
  - Bookmarked lines visually accented with a left gold border.
  - Dedicated expandable drawer (`#bookmarks-panel`) showing all pinned lines with line numbers, timestamps, and severity badges.
  - 1-click jump to any bookmarked line, and 1-click formatted incident summary export (`📋 Copy All`).

## [1.2.0] - 2026-09-03

### Added
- **Log Analytics & Dashboard (📊 Analytics)**:
  - Interactive frequency analytics with Error/Critical, Warning, Info, and Total Scanned KPI stat cards.
  - Hourly distribution histogram (Canvas-based, zero CDN dependency) comparing Error vs Warning spikes.
  - Top 5 recurring exception patterns / error clusters.
  - Interactive click-to-filter: clicking on KPI cards or error patterns automatically searches and highlights matching entries.
- **Pre-built Regex / Quick Filters (⚡ Quick Filters)**:
  - Dropdown presets for enterprise log analysis: Errors & Exceptions, Warnings, HTTP 4xx/5xx, Slow Requests (>1s), GUID/TraceID, and Timeouts & Socket Errors.
- **Folder-Level Search (🔍 In Folder)**:
  - Search any keyword or Trace ID across all `.log` and `.txt` files in the current folder.
  - Accessible directly inside the Log Viewer next to `[ Search ]` and on the File Browser toolbar.
  - Displays match previews with file names and line numbers; clicking any match opens the file and scrolls to the exact line (`#L{lineNumber}`).
- **Time-Range Filter (⏱️ Time)**:
  - Filter log lines within a specific `Start Time` and `End Time` window (e.g. `02:00:00` to `02:45:00`).
  - Dynamically isolates matching log lines while keeping stack trace context intact.

### Fixed
- **Analytics Error Filter Isolation**:
  - Excluded `INFO` log lines containing `'error':null` or variable names like `LastError=` from being mistakenly treated as Error lines.
- **Search Context Lines**:
  - Expanded search context dropdown to include `±1 line` and `±3 lines` options in addition to `±2`, `±5`, and `±10`.

## [1.1.0] - 2026-09-02

### Added
- **Copy to Clipboard**: Clicking on a line number in the log viewer now automatically copies the entire line's text to the clipboard and shows a "Copied!" confirmation.

### Changed
- **Removed Virtual Scrolling**: Reverted the virtual scrolling feature to resolve issues with jumping scrollbars and inability to reach the bottom of large logs.

### Fixed
- **ZIP Download 0KB Issue**: Fixed a fatal crash during ZIP creation caused by Kestrel blocking synchronous IO.
- **ZIP Access Denied Error**: Custom folder traversal now gracefully skips hidden/restricted system folders (e.g., System Volume Information) instead of aborting the entire ZIP download.
- **Active File ZIP Error**: Added FileShare.ReadWrite to allow ZIPing of log files even while they are actively being written to by other applications.

### Performance
- **Single File Download Speed**: Bypassed ASP.NET Core's default 64KB HTTP buffer limit. Files are now streamed directly to the network socket using 1MB chunks to achieve maximum connection speed.
- **ZIP Download Speed**: Wrapped the ZipArchive output in a 1MB buffer and disabled CPU compression to maximize throughput on Gigabit/10G networks.
- **Live Tail (SignalR)**: Reduced JSON payload sizes and string allocations to fix browser freezing when opening large, active logs.
