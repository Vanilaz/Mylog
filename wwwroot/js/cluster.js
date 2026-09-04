import { $, api, responseError, showLoading, hideLoading, showPanel, setStatus, escapeHtml, getFileExtension } from './utils.js';
import { state } from './state.js';
import { renderClusterOutput, renderLine, renderSearchResults, reportError } from './viewer.js';
import { updateMobileFileInfo, updateMobileStatsFromViewer } from './mobileUx.js';
import { renderFiles } from './browser.js';

let _clusters = [];
let _activeCluster = null;
let _currentBrowsePath = '';
let _onFileOpening = null;
let _onFileOpened = null;

export const getCurrentBrowsePath = () => _currentBrowsePath;

export async function initCluster(options = {}) {
    _onFileOpening = options.onFileOpening;
    _onFileOpened = options.onFileOpened;
    const navCluster = $('nav-cluster');
    if (navCluster) {
        navCluster.addEventListener('click', () => switchSidebarTab('cluster'));
    }

    const refreshBtn = $('refresh-clusters');
    if (refreshBtn) {
        refreshBtn.addEventListener('click', loadClusters);
    }

    const clusterSelect = $('cluster-select');
    if (clusterSelect) {
        clusterSelect.addEventListener('change', async (e) => {
            const id = e.target.value;
            if (!id) {
                _activeCluster = null;
                $('cluster-nodes-status').innerHTML = '';
                $('cluster-file-tree').innerHTML = '';
                if (state.clusterMode) {
                    state.folders = [];
                    state.files = [];
                    renderFiles();
                }
                return;
            }
            _activeCluster = _clusters.find(c => c.clusterId === id);
            renderClusterNodesStatus(_activeCluster);
            showPanel('files');
            await browseClusterPath(id, '');
        });
    }

    await loadClusters();
}

export function switchSidebarTab(tab) {
    const navLocal = $('nav-local');
    const navRemote = $('nav-remote');
    const navCluster = $('nav-cluster');
    const localSection = $('local-sidebar-section');
    const remoteSection = $('remote-sidebar-section');
    const clusterSection = $('cluster-sidebar-section');

    navLocal?.classList.toggle('active', tab === 'local');
    navRemote?.classList.toggle('active', tab === 'remote');
    navCluster?.classList.toggle('active', tab === 'cluster');

    if (localSection) localSection.hidden = (tab !== 'local');
    if (remoteSection) remoteSection.hidden = (tab !== 'remote');
    if (clusterSection) clusterSection.hidden = (tab !== 'cluster');

    if (tab === 'cluster' && !_clusters.length) {
        loadClusters();
    }
}

export async function loadClusters() {
    const select = $('cluster-select');
    if (!select) return;

    try {
        const res = await fetch(api('/api/cluster/list'));
        if (!res.ok) return;
        _clusters = await res.json();

        select.innerHTML = '<option value="">-- Select Cluster / LB --</option>';
        for (const c of _clusters) {
            const opt = document.createElement('option');
            opt.value = c.clusterId;
            const shortName = c.clusterName.replace(/\s*\((?:Load Balancer|Node[^\)]*)\)/gi, '').trim();
            opt.textContent = shortName;
            opt.title = `${c.clusterName} (${c.nodes.length} nodes)`;
            select.appendChild(opt);
        }

        // Auto-select if 1 cluster exists
        if (_clusters.length === 1 && !select.value) {
            select.value = _clusters[0].clusterId;
            select.dispatchEvent(new Event('change'));
        }
    } catch (err) {
        console.warn('Failed to load clusters:', err);
    }
}

function renderClusterNodesStatus(cluster) {
    const container = $('cluster-nodes-status');
    if (!container || !cluster || !cluster.nodes) return;

    container.innerHTML = cluster.nodes.map(n => `
        <span class="cluster-node-chip ${n.isReachable ? 'online' : 'offline'}" title="${escapeHtml(n.statusMessage || (n.isReachable ? 'Online' : 'Offline'))}">
            <span class="node-dot"></span>
            ${escapeHtml(n.nodeName)} ${n.isReachable ? '' : '(Offline)'}
        </span>
    `).join('');
}

export async function browseClusterPath(clusterId, relativePath = '') {
    const tree = $('cluster-file-tree');
    if (!tree) return;

    _currentBrowsePath = relativePath;
    tree.innerHTML = '<div class="cluster-loading">Scanning nodes...</div>';

    try {
        const url = `${api(`/api/cluster/${encodeURIComponent(clusterId)}/browse`)}?path=${encodeURIComponent(relativePath)}`;
        const res = await fetch(url);
        if (!res.ok) throw await responseError(res, 'Failed to browse cluster files.');
        const data = await res.json();

        renderClusterFileList(data);
        syncClusterMainTable(data);
        if (data.nodes && data.nodes.length) {
            renderClusterNodesStatus({ nodes: data.nodes });
        }
    } catch (err) {
        tree.innerHTML = `<div class="cluster-error">⚠️ ${escapeHtml(err.message)}</div>`;
        if (state.clusterMode) {
            state.folders = [];
            state.files = [];
            renderFiles();
            const emptyEl = $('empty-state');
            if (emptyEl) {
                emptyEl.hidden = false;
                emptyEl.textContent = `Cluster error: ${err.message}`;
            }
        }
    }
}

export function syncClusterMainTable(data) {
    if (!state.clusterMode) return;
    state.path = data.currentPath || '';
    state.clusterPath = data.currentPath || '';
    state.clusterId = data.clusterId;
    state.parentPath = data.parentPath || '';
    state.folders = (data.folders || []).map(f => ({
        name: f.name || f.Name,
        path: f.relativePath || f.RelativePath
    }));

    const totalNodesCount = (data.nodes || []).length || 2;
    state.files = (data.files || []).map(f => {
        const nodeNames = f.availableNodeNames || f.AvailableNodeNames || [];
        const isAll = nodeNames.length >= totalNodesCount || nodeNames.length > 1;
        let badgeText = 'All Nodes';
        if (!isAll) {
            const raw = nodeNames[0] || 'Node';
            const m = raw.match(/(?:Node\s*\d+|LB-\d+|Server\s*[A-Z])/i);
            badgeText = m ? m[0] : (raw.length > 10 ? raw.slice(0, 8) + '..' : raw);
        }
        const fileName = f.name || f.Name || '';
        const ext = f.extension || f.Extension || getFileExtension(fileName);
        return {
            name: fileName,
            path: fileName,
            relativePath: f.relativePath || f.RelativePath,
            size: f.maxSize || f.MaxSize || 0,
            lastModified: f.lastModified || f.LastModified || new Date().toISOString(),
            extension: ext || (fileName.toLowerCase().endsWith('.log') ? '.log' : ''),
            nodeBadge: badgeText,
            isAllNodes: isAll
        };
    });

    const locationInput = $('current-path');
    if (locationInput) {
        const clusterName = _activeCluster ? _activeCluster.clusterName : data.clusterId;
        locationInput.value = data.currentPath 
            ? `Cluster: ${clusterName} / ${data.currentPath}` 
            : `Cluster: ${clusterName}`;
    }
    renderFiles();
}

function renderClusterFileList(data) {
    const tree = $('cluster-file-tree');
    if (!tree) return;

    let html = '';

    if (data.parentPath !== null && data.currentPath) {
        html += `<div class="cluster-file-item folder cluster-folder-row" data-cluster-folder="${escapeHtml(data.parentPath)}">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📁</span>
                <span class="cluster-file-name">.. (Parent)</span>
            </div>
        </div>`;
    }

    for (const folder of data.folders) {
        html += `<div class="cluster-file-item folder cluster-folder-row" data-cluster-folder="${escapeHtml(folder.relativePath)}">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📁</span>
                <span class="cluster-file-name">${escapeHtml(folder.Name || folder.name)}</span>
            </div>
        </div>`;
    }

    const totalNodesCount = (data.nodes || []).length || 2;
    for (const file of data.files) {
        const isAll = file.availableNodeNames.length >= totalNodesCount || file.availableNodeNames.length > 1;
        let badgeText = 'All Nodes';
        let badgeClass = 'cluster-file-pill all';
        let badgeTitle = `Present on all nodes (${file.availableNodeNames.join(', ')})`;

        if (!isAll) {
            const raw = file.availableNodeNames[0] || 'Node';
            const m = raw.match(/(?:Node\s*\d+|LB-\d+|Server\s*[A-Z])/i);
            badgeText = m ? m[0] : (raw.length > 10 ? raw.slice(0, 8) + '..' : raw);
            badgeClass = 'cluster-file-pill single';
            badgeTitle = `Present only on ${raw}`;
        }

        const activeClass = state.clusterFile === file.name ? 'active' : '';

        html += `<div class="cluster-file-item file cluster-file-row ${activeClass}" data-cluster-file="${escapeHtml(file.name)}" data-cluster-rel="${escapeHtml(file.relativePath)}">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📄</span>
                <span class="cluster-file-name" title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</span>
            </div>
            <span class="${badgeClass}" title="${escapeHtml(badgeTitle)}">${escapeHtml(badgeText)}</span>
        </div>`;
    }

    if (!data.folders.length && !data.files.length) {
        html = '<div class="cluster-empty">No log files found in this path across reachable nodes.</div>';
    }

    tree.innerHTML = html;

    // Attach click events
    tree.querySelectorAll('.cluster-folder-row').forEach(el => {
        el.addEventListener('click', () => browseClusterPath(data.clusterId, el.dataset.clusterFolder));
    });

    tree.querySelectorAll('.cluster-file-row').forEach(el => {
        el.addEventListener('click', () => {
            tree.querySelectorAll('.cluster-file-row').forEach(r => r.classList.remove('active'));
            el.classList.add('active');
            const fileName = el.dataset.clusterFile;
            openClusterFile(data.clusterId, data.currentPath, fileName, 'all');
        });
    });
}

export async function openClusterFile(clusterId, relativePath, fileName, nodeId = 'all', viewMode = 'timeline') {
    _onFileOpening?.();
    showPanel('viewer');
    const isMerged = nodeId === 'all';
    const modeLabel = isMerged ? (viewMode === 'concat' ? 'CONCATENATED' : 'TIMELINE') : nodeId;
    showLoading(`LOADING CLUSTER LOGS (${modeLabel})...`);

    state.clusterMode = true;
    state.remoteMode = false;
    state.clusterId = clusterId;
    state.clusterPath = relativePath || '';
    state.clusterFile = fileName;
    state.clusterNodeId = nodeId;
    state.clusterViewMode = viewMode;
    state.selectedFile = fileName;

    try {
        const url = `${api(`/api/cluster/${encodeURIComponent(clusterId)}/content`)}?path=${encodeURIComponent(relativePath || '')}&fileName=${encodeURIComponent(fileName)}&nodeId=${encodeURIComponent(nodeId)}&viewMode=${encodeURIComponent(viewMode)}`;
        const res = await fetch(url);
        if (!res.ok) throw await responseError(res, 'Failed to fetch cluster file content.');
        const data = await res.json();

        // Update Viewer UI
        const viewLabel = isMerged
            ? (viewMode === 'concat' ? 'Cluster Concat' : 'Cluster Timeline')
            : nodeId;
        $('viewer-title').textContent = `${fileName} [${viewLabel}]`;
        setStatus('CLUSTER READY', false);

        // Render Node Switcher Tabs
        renderNodeTabs(data);

        const zipBtn = $('download-cluster-zip-button');
        if (zipBtn) zipBtn.hidden = false;
        const logBtn = $('download-cluster-log-button');
        if (logBtn) logBtn.hidden = false;

        // Render Lines
        renderClusterOutput(data);

        _onFileOpened?.(fileName);

        updateMobileFileInfo(fileName, false);
        updateMobileStatsFromViewer();
    } catch (err) {
        reportError(err);
    } finally {
        hideLoading();
    }
}

function renderNodeTabs(data) {
    const container = $('cluster-node-tabs');
    const pillGroup = $('cluster-node-pill-group');
    if (!container || !pillGroup) return;

    container.hidden = false;

    const isTimelineActive = data.selectedNodeId === 'all' && (data.viewMode !== 'concat');
    const isConcatActive = data.selectedNodeId === 'all' && data.viewMode === 'concat';

    let pills = `
        <button type="button" class="cluster-pill ${isTimelineActive ? 'active' : ''}" data-node-id="all" data-view-mode="timeline" title="Interleave lines by timestamp chronologically">
            🌐 All Nodes (Timeline)
        </button>
        <button type="button" class="cluster-pill ${isConcatActive ? 'active' : ''}" data-node-id="all" data-view-mode="concat" title="Concatenate logs node-by-node sequentially">
            📑 All Nodes (ต่อกัน / Concat)
        </button>
    `;

    for (const node of data.nodes) {
        pills += `
            <button type="button" class="cluster-pill ${data.selectedNodeId === node.nodeId ? 'active' : ''} ${!node.isReachable ? 'unreachable' : ''}" data-node-id="${node.nodeId}" data-view-mode="timeline" ${!node.isReachable ? 'disabled' : ''}>
                💻 ${escapeHtml(node.nodeName)} ${!node.isReachable ? '(Offline)' : ''}
            </button>
        `;
    }

    pillGroup.innerHTML = pills;

    pillGroup.querySelectorAll('.cluster-pill').forEach(btn => {
        btn.addEventListener('click', () => {
            const nid = btn.dataset.nodeId;
            const vm = btn.dataset.viewMode || 'timeline';
            if (nid && (nid !== state.clusterNodeId || vm !== state.clusterViewMode)) {
                openClusterFile(state.clusterId, state.clusterPath, state.clusterFile, nid, vm);
            }
        });
    });
}

export async function searchCluster(query, regex = false, context = 0) {
    if (!state.clusterMode || !state.clusterId || !state.clusterFile) return false;

    showLoading('SEARCHING ACROSS CLUSTER NODES...');
    try {
        const url = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/search`)}?path=${encodeURIComponent(state.clusterPath)}&fileName=${encodeURIComponent(state.clusterFile)}&query=${encodeURIComponent(query)}&regex=${regex}&context=${context}`;
        const res = await fetch(url);
        if (!res.ok) throw await responseError(res, 'Cluster search failed.');
        const data = await res.json();

        renderSearchResults(data, query, regex, {
            source: 'cluster',
            emptyMessage: 'No matching lines across cluster nodes.'
        });
        return true;
    } finally {
        hideLoading();
    }
}

export function downloadClusterZip() {
    if (!state.clusterMode || !state.clusterId || !state.clusterFile) return false;
    const url = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/download-file`)}?path=${encodeURIComponent(state.clusterPath)}&fileName=${encodeURIComponent(state.clusterFile)}&format=zip`;
    window.location.href = url;
    return true;
}

export function downloadClusterConcatLog() {
    if (!state.clusterMode || !state.clusterId || !state.clusterFile) return false;
    const url = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/download-file`)}?path=${encodeURIComponent(state.clusterPath)}&fileName=${encodeURIComponent(state.clusterFile)}&format=log`;
    window.location.href = url;
    return true;
}
