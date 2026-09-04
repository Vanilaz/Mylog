import { initAnalytics, loadAnalytics } from './analytics.js';
import { initTimeFilter, resetTimeFilter } from './timeFilter.js';
import { initFlowTracker, exitFlow, resetFlowTracker, getActiveTraceId, isolateFlow } from './flowTracker.js';
import { initBookmarks, resetBookmarksForNewFile } from './bookmarks.js';
import { initMobileUx, updateMobileFileInfo, updateMobileStatsFromViewer, syncErrorsOnly, syncWordWrap, closeLineInspector } from './mobileUx.js';
import { $, api, responseError, showLoading, hideLoading, showPanel, setStatus, escapeHtml } from './utils.js';
import { state, store } from './state.js';
import { updateCustomRe, renderCustom, highlightMatches, renderOutput, renderSearchResults, highlightLine, reportError, renderChips, setAutoFormatJson, cancelPendingRenders, toggleJsonFormatting, updateFormatJsonButton } from './viewer.js';
import { renderFiles, loadFolder, initTree, updateSortIndicators, renderTreeNodes, setToggleState, toggleTreeNode, downloadCurrentFolder } from './browser.js';
import { initCluster, switchSidebarTab, searchCluster, downloadClusterZip, downloadClusterConcatLog, openClusterFile, browseClusterPath } from './cluster.js';
import { initRemote, loadRemoteProjects, browseRemotePath } from './remote.js';
import { tailFile, stopTail } from './hub.js';

export function resetFileViewerState(keepBookmarks = false) {
    cancelPendingRenders();
    closeLineInspector();
    resetTimeFilter();
    resetFlowTracker();
    if (!keepBookmarks) resetBookmarksForNewFile();
    if ($('content-search')) $('content-search').value = '';
    if ($('search-summary')) $('search-summary').textContent = '';
    if ($('search-matches')) $('search-matches').textContent = '';
    if ($('errors-only')) $('errors-only').checked = false;
    $('log-viewer')?.classList.remove('errors-only-mode');
    state.lastSearchData = null;
    state.lastSearchQuery = '';
}

async function openFile(path, keepBookmarks = false) {
    const token = ++state.openToken;
    resetFileViewerState(keepBookmarks);
    state.clusterMode = false;
    const clusterTabs = $('cluster-node-tabs');
    if (clusterTabs) clusterTabs.hidden = true;
    const clusterZip = $('download-cluster-zip-button');
    if (clusterZip) clusterZip.hidden = true;
    const clusterLog = $('download-cluster-log-button');
    if (clusterLog) clusterLog.hidden = true;
    const remoteDownload = $('download-remote-log-button');
    if (remoteDownload) remoteDownload.hidden = !state.remoteMode;
    showPanel('viewer');
    if (drawerQuery.matches) setSidebar(false);
    showLoading('OPENING FILE...');
    try {
        await stopTail();
        state.selectedFile = path || state.remoteFile;

        let url;
        if (state.remoteMode) {
            url = `${api('/api/remote/content')}?projectName=${encodeURIComponent(state.remoteProject)}&fileName=${encodeURIComponent(state.remoteFile)}`;
        } else {
            url = `${api('/api/files/content?path=')}${encodeURIComponent(state.selectedFile)}`;
        }

        const response = await fetch(url);
        if (!response.ok) throw await responseError(response, 'Unable to open file.');
        const data = await response.json();
        if (token !== state.openToken) return; // a newer open/tail superseded this one
        $('viewer-title').textContent = data.fileName;
        renderOutput(data.content);
        setStatus(data.isTruncated ? 'PARTIAL READ' : 'READY', false);
        $('pause-button').disabled = true;
        if (!$('analytics-dashboard')?.hidden) {
            loadAnalytics(state.selectedFile);
        }
        updateMobileFileInfo(data.fileName, false);
        updateMobileStatsFromViewer();
    } finally {
        hideLoading();
    }
}

let currentSearchToken = 0;

async function searchContent() {
    if (!state.selectedFile) throw new Error('Open or tail a file before searching.');
    const query = $('content-search').value.trim();
    if (getActiveTraceId() && query !== getActiveTraceId()) {
        resetFlowTracker();
    }

    if (!query) {
        $('search-summary').textContent = '';
        state.lastSearchData = null;
        if (state.tailing) await tailFile(state.selectedFile);
        else await openFile(state.selectedFile);
        return;
    }

    const token = ++currentSearchToken;
    showLoading('SEARCHING LOGS...');
    try {
        const regex = $('search-mode').value === 'regex';
        const context = $('search-context').value;

        if (state.clusterMode) {
            const handled = await searchCluster(query, regex, context);
            if (handled) return;
        }

        let url;
        if (state.remoteMode) {
            url = `${api('/api/remote/search')}?projectName=${encodeURIComponent(state.remoteProject)}&fileName=${encodeURIComponent(state.remoteFile)}`
                + `&query=${encodeURIComponent(query)}&regex=${regex}&context=${encodeURIComponent(context)}`;
        } else {
            url = `${api('/api/files/search')}?path=${encodeURIComponent(state.selectedFile)}`
                + `&query=${encodeURIComponent(query)}&regex=${regex}&context=${encodeURIComponent(context)}`;
        }

        const response = await fetch(url);
        if (token !== currentSearchToken) return;
        if (!response.ok) throw await responseError(response, 'Unable to search file content.');
        const data = await response.json();
        if (token !== currentSearchToken) return;

        renderSearchResults(data, query, regex, {
            source: state.remoteMode ? 'remote' : 'local',
            emptyMessage: 'No matching lines.'
        });
    } finally {
        hideLoading();
    }
}

// Tree events
document.addEventListener('click', async event => {
    const toggle = event.target.closest('.tree-toggle');
    if (toggle) {
        event.stopPropagation();
        event.stopImmediatePropagation();
        await toggleTreeNode(toggle);
    }
});

// UI Events
const drawerQuery = window.matchMedia('(max-width: 900px)');

function setSidebar(open) {
    document.querySelector('.main-content').classList.toggle('sidebar-collapsed', !open);
    $('toggle-sidebar').setAttribute('aria-expanded', String(open));
    $('sidebar-backdrop').hidden = !(open && drawerQuery.matches);
}

const sidebarOpen = () => !document.querySelector('.main-content').classList.contains('sidebar-collapsed');

document.addEventListener('click', async event => {
    const lineLink = event.target.closest('.line-number');
    if (lineLink) {
        event.preventDefault();
        const lineNumber = lineLink.dataset.line;
        highlightLine(lineNumber);
        
        const lineEl = document.getElementById(`L${lineNumber}`);
        if (lineEl && lineEl.lastElementChild) {
            const text = lineEl.lastElementChild.textContent;
            try {
                await navigator.clipboard.writeText(text);
                const originalText = lineLink.textContent;
                lineLink.textContent = 'Copied!';
                lineLink.style.color = '#10b981';
                setTimeout(() => {
                    lineLink.textContent = originalText;
                    lineLink.style.color = '';
                }, 1500);
            } catch (err) {
                console.error('Failed to copy text', err);
            }
        }
        return;
    }

    if (event.target.closest('.tree-toggle')) return;

    const target = event.target.closest('[data-folder], [data-action]');
    if (!target) return;
    try {
        if (target.dataset.folder) {
            document.querySelectorAll('.tree-row.active, .tree-label.active').forEach(el => el.classList.remove('active'));
            const row = target.closest('.tree-row') || target;
            row.classList.add('active');
            if (drawerQuery.matches) setSidebar(false);
            await loadFolder(target.dataset.folder);

            const toggle = row.querySelector('.tree-toggle');
            if (toggle && toggle.getAttribute('aria-expanded') !== 'true') {
                await toggleTreeNode(toggle, true);
            }
        } else if (target.dataset.action === 'navigate-folder') {
            if (state.remoteMode) {
                await browseRemotePath(target.dataset.path);
            } else if (state.clusterMode) {
                await browseClusterPath(state.clusterId, target.dataset.path);
            } else {
                await loadFolder(target.dataset.path);
            }
        } else if (target.dataset.action === 'open') {
            if (state.remoteMode) {
                state.remoteFile = target.dataset.path;
                await openFile();
            } else if (state.clusterMode) {
                await openClusterFile(state.clusterId, state.clusterPath, target.dataset.path, 'all');
            } else {
                await openFile(target.dataset.path);
            }
        } else if (target.dataset.action === 'tail') {
            resetTimeFilter();
            resetFlowTracker();
            resetBookmarksForNewFile();
            if (state.clusterMode) {
                await tailFile(target.dataset.path, {
                    clusterId: state.clusterId,
                    relativePath: state.clusterPath,
                    fileName: target.dataset.path,
                    nodeId: state.clusterNodeId || 'all'
                });
            } else {
                await tailFile(target.dataset.path);
            }
        }
    } catch (error) {
        reportError(error);
    }
});

const navigate = () => {
    if (state.clusterMode) {
        if (state.clusterId) browseClusterPath(state.clusterId, '').catch(reportError);
    } else if (state.remoteMode) {
        loadRemoteProjects(true).catch(reportError);
    } else {
        loadFolder($('current-path').value.trim(), { force: true }).catch(reportError);
    }
};

$('go-button').addEventListener('click', navigate);
$('current-path').addEventListener('keydown', event => { if (event.key === 'Enter') navigate(); });
$('refresh-button').addEventListener('click', () => {
    if (state.clusterMode) {
        if (state.clusterId) browseClusterPath(state.clusterId, state.clusterPath || '').catch(reportError);
    } else if (state.remoteMode) {
        if (state.remoteProject) browseRemotePath(state.remoteProject).catch(reportError);
        else loadRemoteProjects(true).catch(reportError);
    } else {
        loadFolder(state.path, { force: true }).catch(reportError);
    }
});
$('reset-root').addEventListener('click', () => { $('current-path').value = ''; navigate(); });
let _searchDebounce;
$('search-input').addEventListener('input', () => {
    clearTimeout(_searchDebounce);
    _searchDebounce = setTimeout(renderFiles, 200);
});
$('logs-only').addEventListener('change', renderFiles);
$('download-zip-button').addEventListener('click', () => {
    downloadCurrentFolder();
});
$('clear-button').addEventListener('click', () => renderOutput(''));
$('search-button').addEventListener('click', () => {
    searchContent().catch(error => { $('search-summary').textContent = error.message; });
});
$('content-search').addEventListener('keydown', event => { if (event.key === 'Enter') $('search-button').click(); });

$('quick-filter-select')?.addEventListener('change', async event => {
    const val = event.target.value;
    if (!val) return;

    let query = '';
    let isRegex = true;
    let errorsOnly = false;

    switch (val) {
        case 'errors':
            query = '(?:\\|\\s*(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\s*\\||\\[(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\]|\\b(?:ERROR|EROR|FATAL|FTL|CRITICAL):|Exception:|\\b[A-Za-z0-9_.]+Exception\\b)';
            errorsOnly = true;
            break;
        case 'warnings':
            query = '(?:\\|\\s*(?:WARN|WARNING|WRN)\\s*\\||\\[(?:WARN|WARNING|WRN)\\]|\\b(?:WARN|WARNING|WRN):)';
            errorsOnly = true;
            break;
        case 'http':
            query = '\\b[45]\\d{2}\\b|Status(?:Code)?[:=]\\s*[45]\\d{2}';
            break;
        case 'slow':
            query = '\\b[1-9]\\d{3,}\\s*ms\\b|(?:Elapsed|Duration)[:=]\\s*\\d{4,}ms';
            break;
        case 'guid':
            query = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
            break;
        case 'timeout':
            query = '(?:TimeoutException|SocketException|TaskCanceledException|Connection refused)';
            errorsOnly = true;
            break;
    }

    $('content-search').value = query;
    $('search-mode').value = isRegex ? 'regex' : 'text';
    $('errors-only').checked = errorsOnly;
    $('log-viewer').classList.toggle('errors-only-mode', errorsOnly);

    event.target.value = '';
    try { await searchContent(); } catch (err) { reportError(err); }
});

// Folder Search Implementation
function getCurrentFolderPath() {
    if (state.clusterMode) return state.clusterPath || '';
    if (state.path) return state.path;
    if (state.selectedFile) {
        const lastSlash = Math.max(state.selectedFile.lastIndexOf('/'), state.selectedFile.lastIndexOf('\\'));
        if (lastSlash > 0) return state.selectedFile.substring(0, lastSlash);
    }
    return '';
}

async function runFolderSearch(query, isRegex, countEl, listEl, resultsBox) {
    if (!query) return;
    const isCluster = state.clusterMode && state.clusterId;
    const folderPath = getCurrentFolderPath();
    if (!isCluster && !folderPath) {
        reportError(new Error('No active folder selected.'));
        return;
    }

    resultsBox.hidden = false;
    countEl.textContent = 'Searching folder logs...';
    listEl.innerHTML = '<div class="searching-spinner" style="padding: 12px; color: var(--muted);">Searching all logs in current folder...</div>';

    try {
        let url;
        if (isCluster) {
            url = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/search-folder`)}?path=${encodeURIComponent(folderPath)}&query=${encodeURIComponent(query)}&regex=${isRegex}`;
        } else {
            url = `${api('/api/files/search-folder')}?path=${encodeURIComponent(folderPath)}&query=${encodeURIComponent(query)}&regex=${isRegex}`;
        }

        const res = await fetch(url);
        if (!res.ok) throw await responseError(res, 'Folder search failed.');
        const data = await res.json();

        countEl.textContent = `${data.length} match${data.length === 1 ? '' : 'es'} found in folder`;

        if (!data.length) {
            listEl.innerHTML = '<div class="empty-hint" style="padding: 12px;">No matches found in folder logs.</div>';
            return;
        }

        listEl.innerHTML = data.map(item => `
            <div class="folder-match-item" data-path="${escapeHtml(item.path || '')}" data-line="${item.lineNumber}" data-filename="${escapeHtml(item.fileName || '')}">
                <div class="match-meta">
                    <span class="match-filename">📄 ${escapeHtml(item.fileName)}</span>
                    ${item.nodeName ? `<span class="match-node-badge" style="background: var(--surface-2); border: 1px solid var(--border); padding: 1px 6px; border-radius: 4px; font-size: 0.75rem; margin-left: 6px;">💻 ${escapeHtml(item.nodeName)}</span>` : ''}
                    <span class="match-line-badge">Line ${item.lineNumber}</span>
                </div>
                <div class="match-snippet">${highlightMatches(item.line, query, isRegex)}</div>
            </div>
        `).join('');

        listEl.querySelectorAll('.folder-match-item').forEach(el => {
            el.addEventListener('click', async () => {
                const targetPath = el.dataset.path;
                const targetLine = el.dataset.line;
                const fileName = el.dataset.filename;
                resultsBox.hidden = true;
                if (isCluster) {
                    await openClusterFile(state.clusterId, folderPath, fileName, 'all');
                } else {
                    await openFile(targetPath);
                }
                highlightLine(targetLine);
                document.getElementById('L' + targetLine)?.scrollIntoView({ block: 'center' });
            });
        });
    } catch (err) {
        countEl.textContent = 'Error during search';
        listEl.innerHTML = `<div class="empty-hint" style="color: var(--error); padding: 12px;">${escapeHtml(err.message)}</div>`;
    }
}

// Viewer Folder Search Wiring
$('folder-search-viewer-btn')?.addEventListener('click', () => {
    const query = $('content-search')?.value.trim();
    if (!query) {
        $('content-search')?.focus();
        return;
    }
    const isRegex = $('search-mode')?.value === 'regex';
    runFolderSearch(
        query,
        isRegex,
        $('folder-search-count-viewer'),
        $('folder-search-list-viewer'),
        $('folder-search-results-viewer')
    );
});

$('folder-search-viewer-toggle')?.addEventListener('click', () => {
    const resultsBox = $('folder-search-results-viewer');
    if (!resultsBox) return;
    const query = $('content-search')?.value.trim();
    if (query) {
        $('folder-search-viewer-btn')?.click();
    } else {
        $('content-search')?.focus();
        $('content-search')?.setAttribute('placeholder', 'Enter text to search in folder, then click 📁 In Folder');
    }
});

$('folder-search-close-viewer')?.addEventListener('click', () => {
    $('folder-search-results-viewer').hidden = true;
});

// File Browser Folder Search Wiring
$('folder-search-toggle')?.addEventListener('click', () => {
    const bar = $('folder-search-bar');
    if (!bar) return;
    bar.hidden = !bar.hidden;
    if (!bar.hidden) $('folder-search-input')?.focus();
});

$('folder-search-close')?.addEventListener('click', () => {
    $('folder-search-bar').hidden = true;
    $('folder-search-results').hidden = true;
});

$('folder-search-btn')?.addEventListener('click', () => {
    const query = $('folder-search-input')?.value.trim();
    const isRegex = $('folder-search-regex')?.checked || false;
    runFolderSearch(
        query,
        isRegex,
        $('folder-search-count'),
        $('folder-search-list'),
        $('folder-search-results')
    );
});

$('folder-search-input')?.addEventListener('keydown', e => {
    if (e.key === 'Enter') $('folder-search-btn')?.click();
});

$('theme-button').addEventListener('click', () => {
    const dark = document.documentElement.classList.toggle('dark');
    store.set('theme', dark ? 'dark' : 'light');
    $('theme-button').setAttribute('aria-pressed', String(dark));
});

$('toggle-sidebar').addEventListener('click', () => setSidebar(!sidebarOpen()));
$('sidebar-backdrop').addEventListener('click', () => setSidebar(false));

document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && drawerQuery.matches && sidebarOpen()) setSidebar(false);
});

drawerQuery.addEventListener('change', event => setSidebar(!event.matches));

$('custom-highlight').addEventListener('change', () => {
    updateCustomRe();
    if (!state.selectedFile) return;
    if (state.lastSearchData) searchContent().catch(reportError);
    else if (state.tailing) tailFile(state.selectedFile).catch(reportError);
    else openFile(state.selectedFile).catch(reportError);
});

$('auto-format-json').addEventListener('change', event => {
    const isChecked = event.target.checked;
    store.set('autoFormatJson', String(isChecked));
    setAutoFormatJson(isChecked);
    const query = $('content-search')?.value.trim() || '';
    const isRegex = $('search-mode')?.value === 'regex';
    toggleJsonFormatting(isChecked, query, isRegex);
});

const bindToggle = (id, storageKey, className, target = () => $('log-viewer')) =>
    $(id).addEventListener('change', event => {
        store.set(storageKey, String(event.target.checked));
        target().classList.toggle(className, event.target.checked);
    });

bindToggle('show-lines', 'showLines', 'show-lines');
$('errors-only').addEventListener('change', () => {
    state.errorsOnly = $('errors-only').checked;
    $('log-viewer').classList.toggle('errors-only-mode', state.errorsOnly);
    store.set('errorsOnly', state.errorsOnly);
    syncErrorsOnly(state.errorsOnly);
});
$('word-wrap').addEventListener('change', event => {
    store.set('wordWrap', String(event.target.checked));
    $('log-viewer').classList.toggle('nowrap', !event.target.checked);
    syncWordWrap(event.target.checked);
});

$('copy-button').addEventListener('click', async () => {
    const button = $('copy-button');
    try {
        const text = Array.from($('log-viewer').querySelectorAll('.log-line'))
            .filter(el => !el.classList.contains('divider'))
            .map(el => (el.lastElementChild || el).textContent)
            .join('\n');
        await navigator.clipboard.writeText(text);
        button.textContent = 'Copied!';
    } catch {
        button.textContent = 'Copy failed';
    }
    setTimeout(() => { button.textContent = 'Copy'; }, 2000);
});

$('export-search-button').addEventListener('click', () => {
    if (!state.lastSearchData) return;
    const text = state.lastSearchData.map(row => row.line).join('\n');
    const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `search_${Date.now()}.txt`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
});

$('format-json-button').addEventListener('click', () => {
    const query = $('content-search')?.value.trim() || '';
    const isRegex = $('search-mode')?.value === 'regex';
    toggleJsonFormatting(null, query, isRegex);
});

$('close-file-button').addEventListener('click', async () => {
    state.openToken++;
    await stopTail();
    state.selectedFile = null;
    const wasCluster = state.clusterMode;
    const savedClusterId = state.clusterId;
    const savedClusterPath = state.clusterPath;
    if (!wasCluster) {
        state.clusterMode = false;
        state.clusterId = null;
    }
    state.clusterFile = null;
    const clusterTabs = $('cluster-node-tabs');
    if (clusterTabs) clusterTabs.hidden = true;
    const clusterZip = $('download-cluster-zip-button');
    if (clusterZip) clusterZip.hidden = true;
    resetFileViewerState();
    setStatus('OFFLINE', false);
    showPanel('files');
    if (wasCluster && savedClusterId) {
        browseClusterPath(savedClusterId, savedClusterPath || '');
    } else if (state.remoteMode) {
        browseRemotePath(state.remoteProject || '');
    } else {
        history.replaceState(null, '', window.location.pathname);
    }
});

$('options-toggle').addEventListener('click', () => {
    const open = $('viewer-toggles').classList.toggle('open');
    $('options-toggle').setAttribute('aria-expanded', String(open));
});

const closeOptions = () => {
    $('viewer-toggles').classList.remove('open');
    $('options-toggle').setAttribute('aria-expanded', 'false');
};

$('options-close').addEventListener('click', closeOptions);

document.addEventListener('click', event => {
    if (!$('viewer-toggles').classList.contains('open')) return;
    if (event.target.closest('#viewer-toggles, #options-toggle')) return;
    closeOptions();
});

document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && $('viewer-toggles').classList.contains('open')) closeOptions();
});

$('pause-button').addEventListener('click', () => {
    state.paused = !state.paused;
    $('pause-button').textContent = state.paused ? 'Resume' : 'Pause';
});

const applyFontSize = () => {
    store.set('fontSize', String(state.fontSize));
    document.documentElement.style.setProperty('--log-font-size', `${state.fontSize}px`);
};
$('font-inc').addEventListener('click', () => { state.fontSize = Math.min(28, state.fontSize + 1); applyFontSize(); });
$('font-dec').addEventListener('click', () => { state.fontSize = Math.max(8, state.fontSize - 1); applyFontSize(); });

document.querySelectorAll('th[data-sort]').forEach(header => header.addEventListener('click', () => {
    state.ascending = state.sort === header.dataset.sort ? !state.ascending : true;
    state.sort = header.dataset.sort;
    updateSortIndicators();
    renderFiles();
}));

window.addEventListener('beforeunload', () => { if (state.connection) state.connection.stop(); });

function restorePreferences() {
    const storedSize = parseInt(store.get('fontSize'), 10);
    state.fontSize = Number.isFinite(storedSize) ? Math.min(28, Math.max(8, storedSize)) : 13;
    applyFontSize();

    $('theme-button').setAttribute('aria-pressed', String(document.documentElement.classList.contains('dark')));

    setSidebar(!drawerQuery.matches);

    const restore = (id, storageKey, defaultOn) => {
        const stored = store.get(storageKey);
        const on = stored === null ? defaultOn : stored === 'true';
        $(id).checked = on;
        return on;
    };
    const autoFormatJsonOn = restore('auto-format-json', 'autoFormatJson', false);
    setAutoFormatJson(autoFormatJsonOn);
    updateFormatJsonButton(autoFormatJsonOn);
    $('log-viewer').classList.toggle('show-lines', restore('show-lines', 'showLines', true));
    const errorsOnlyOn = restore('errors-only', 'errorsOnly', false);
    syncErrorsOnly(errorsOnlyOn);
    const wordWrapOn = restore('word-wrap', 'wordWrap', true);
    $('log-viewer').classList.toggle('nowrap', !wordWrapOn);
    syncWordWrap(wordWrapOn);
}

// ---- Tab Navigation & Remote Integration ----

$('nav-local').addEventListener('click', () => {
    switchSidebarTab('local');
    state.remoteMode = false;
    state.clusterMode = false;
    showPanel('files');
    loadFolder(state.localPath || '').catch(reportError);
});

$('nav-cluster')?.addEventListener('click', () => {
    switchSidebarTab('cluster');
    state.remoteMode = false;
    state.clusterMode = true;
    showPanel('files');
    const clusterSelect = $('cluster-select');
    if (clusterSelect && clusterSelect.value) {
        browseClusterPath(clusterSelect.value, state.clusterPath || '');
    } else {
        state.folders = [];
        state.files = [];
        renderFiles();
        const emptyEl = $('empty-state');
        if (emptyEl) {
            emptyEl.hidden = false;
            emptyEl.textContent = 'Please select a cluster from the dropdown to browse its files.';
        }
        const locationInput = $('current-path');
        if (locationInput) locationInput.value = 'Cluster: Load Balance Mode';
    }
});

$('nav-remote').addEventListener('click', () => {
    switchSidebarTab('remote');
    state.remoteMode = true;
    state.clusterMode = false;
    showPanel('files');
    loadRemoteProjects();
});

$('download-cluster-zip-button')?.addEventListener('click', () => {
    downloadClusterZip();
});

$('download-cluster-log-button')?.addEventListener('click', () => {
    downloadClusterConcatLog();
});

$('download-remote-log-button')?.addEventListener('click', () => {
    if (state.remoteMode && state.remoteProject && state.selectedFile) {
        window.location.href = `${api('/api/remote/download')}?projectName=${encodeURIComponent(state.remoteProject)}&fileName=${encodeURIComponent(state.selectedFile)}`;
    }
});

// ---- Boot ----

async function boot() {
    restorePreferences();
    updateCustomRe();
    updateSortIndicators();

    const params = new URLSearchParams(window.location.search);
    const deepLink = params.get('path');
    const errorsOnlyParam = params.get('errorsOnly');
    if (errorsOnlyParam === 'true') {
        state.errorsOnly = true;
        syncErrorsOnly(true);
    }

    if (!deepLink && !params.get('cluster') && params.get('tab') !== 'remote' && !params.get('remoteProject')) {
        await loadFolder().catch(reportError);
    }
    initTree();
    initTimeFilter();
    initBookmarks();
    initMobileUx({
        onTrackFlow: async (traceId) => {
            await isolateFlow(traceId);
        }
    });

    initFlowTracker({
        onSearchFlow: async (traceId) => {
            $('content-search').value = traceId;
            $('search-mode').value = 'text';
            try { await searchContent(); } catch (err) { reportError(err); }
        },
        onExitFlow: async () => {
            $('content-search').value = '';
            $('search-summary').textContent = '';
            $('export-search-button').hidden = true;
            try { await openFile(state.selectedFile, true); } catch (err) { reportError(err); }
        }
    });

    initAnalytics({
        onFilterKeyword: async (keyword, isRegex = false, errorsOnly = false) => {
            $('content-search').value = keyword;
            $('search-mode').value = isRegex ? 'regex' : 'text';
            $('errors-only').checked = errorsOnly;
            $('log-viewer').classList.toggle('errors-only-mode', errorsOnly);
            try { await searchContent(); } catch (err) { reportError(err); }
        },
        onFilterHour: async (hour, errorsOnly = false) => {
            $('content-search').value = hour;
            $('search-mode').value = 'text';
            $('errors-only').checked = errorsOnly;
            $('log-viewer').classList.toggle('errors-only-mode', errorsOnly);
            try { await searchContent(); } catch (err) { reportError(err); }
        }
    });

    initCluster({
        onFileOpening: () => {
            resetFileViewerState();
            if (drawerQuery.matches) setSidebar(false);
        },
        onFileOpened: (fileName) => {
            if (!$('analytics-dashboard')?.hidden) {
                loadAnalytics(fileName);
            }
        }
    });
    initRemote({
        onOpenFile: async (project, file) => {
            try {
                await openFile();
            } catch (err) {
                reportError(err);
            }
        }
    });

    const tabParam = params.get('tab');
    if (tabParam === 'remote' && !params.get('remoteProject')) {
        switchSidebarTab('remote');
        state.remoteMode = true;
        state.clusterMode = false;
        loadRemoteProjects();
    } else if (tabParam === 'cluster' && !params.get('cluster')) {
        switchSidebarTab('cluster');
        state.remoteMode = false;
        state.clusterMode = true;
        showPanel('files');
        const clusterSelect = $('cluster-select');
        if (clusterSelect && clusterSelect.value) {
            browseClusterPath(clusterSelect.value, '');
        }
    }

    const remoteProjectParam = params.get('remoteProject');
    if (remoteProjectParam) {
        switchSidebarTab('remote');
        state.remoteMode = true;
        state.clusterMode = false;
        try {
            await browseRemotePath(remoteProjectParam);
            const remoteFileParam = params.get('remoteFile');
            if (remoteFileParam) {
                state.remoteProject = remoteProjectParam;
                state.remoteFile = remoteFileParam;
                await openFile();
                if (params.get('analytics') === 'true') {
                    $('analytics-button')?.click();
                }
            }
        } catch (err) {
            reportError(err);
        }
        return;
    }

    const clusterParam = params.get('cluster');
    const clusterFileParam = params.get('file');
    if (clusterParam && clusterFileParam) {
        switchSidebarTab('cluster');
        try {
            await openClusterFile(clusterParam, params.get('rel') || '', clusterFileParam, params.get('node') || 'all');
            if (params.get('analytics') === 'true') {
                $('analytics-button')?.click();
            }
        } catch (err) {
            reportError(err);
        }
        return;
    } else if (clusterParam) {
        switchSidebarTab('cluster');
        state.remoteMode = false;
        state.clusterMode = true;
        showPanel('files');
        const clusterSelect = $('cluster-select');
        if (clusterSelect) {
            clusterSelect.value = clusterParam;
            browseClusterPath(clusterParam, params.get('rel') || '');
        }
    }

    if (!deepLink) return;
    try {
        if (!/\.\w+$/.test(deepLink)) {
            await loadFolder(deepLink);
            return;
        }
        await openFile(deepLink);
    } catch (error) {
        reportError(error);
        return;
    }

    try {
        if (window.location.hash.startsWith('#L')) {
            const lineNumber = window.location.hash.slice(2);
            highlightLine(lineNumber);
            const lineEl = document.getElementById(`L${lineNumber}`);
            lineEl?.scrollIntoView?.({ block: 'center' });
            if (window.innerWidth <= 768) {
                lineEl?.click();
            }
        }
    } catch (error) {
        console.error('Could not scroll to the linked line', error);
    }
}

boot();
