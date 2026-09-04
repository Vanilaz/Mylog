import { $, api, responseError, escapeHtml, showLoading, hideLoading, showFilesError, showPanel, getFileExtension, isArchiveFile, getArchiveLabel } from './utils.js';
import { state } from './state.js';
import { formatSize, formatDate } from './format.js';

export function getDownloadFileUrl(file) {
    if (state.clusterMode && state.clusterId) {
        return `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/download-file`)}?path=${encodeURIComponent(state.clusterPath || '')}&fileName=${encodeURIComponent(file.name)}`;
    }
    if (state.remoteMode && state.remoteProject) {
        return `${api('/api/remote/download')}?projectName=${encodeURIComponent(state.remoteProject)}&fileName=${encodeURIComponent(file.name)}`;
    }
    return `${api('/api/files/download')}?path=${encodeURIComponent(file.path)}`;
}

export function downloadCurrentFolder() {
    if (state.clusterMode && state.clusterId) {
        window.location.href = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/download-folder`)}?path=${encodeURIComponent(state.clusterPath || '')}`;
        return;
    }
    if (state.remoteMode && state.remoteProject) {
        window.location.href = `${api('/api/remote/download-folder')}?projectName=${encodeURIComponent(state.remoteProject)}`;
        return;
    }
    window.location.href = `${api('/api/files/download-folder')}?path=${encodeURIComponent(state.path || '')}`;
}

export function renderFiles() {
    const folderSearchBtn = $('folder-search-toggle');
    if (folderSearchBtn) folderSearchBtn.hidden = state.remoteMode;
    const downloadZipBtn = $('download-zip-button');
    if (downloadZipBtn) {
        if (state.remoteMode) {
            downloadZipBtn.hidden = !state.remoteProject;
            downloadZipBtn.title = state.remoteProject ? `Download all logs in project ${state.remoteProject} as ZIP` : 'Download ZIP';
        } else if (state.clusterMode) {
            downloadZipBtn.hidden = !state.clusterId;
            downloadZipBtn.title = 'Download cluster folder logs across all nodes as ZIP';
        } else {
            downloadZipBtn.hidden = !state.path;
            downloadZipBtn.title = 'Download current folder as ZIP';
        }
    }

    const query = $('search-input').value.toLowerCase();
    const logsOnly = $('logs-only').checked;

    const visibleFolders = (!logsOnly && state.folders) ? state.folders.filter(f =>
        !query || f.name.toLowerCase().includes(query)) : [];

    const visibleFiles = state.files.filter(file =>
        (!query || file.name.toLowerCase().includes(query)) &&
        (!logsOnly || (file.extension || '').toLowerCase() === '.log'));

    const key = state.sort;
    visibleFiles.sort((a, b) => {
        let result;
        if (key === 'size') result = a.size - b.size;
        else if (key === 'modified') result = new Date(a.lastModified) - new Date(b.lastModified);
        else result = a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
        return result * (state.ascending ? 1 : -1);
    });

    let html = '';

    // If there is a parent path and no query, show ".." (Parent Folder)
    if (!logsOnly && state.parentPath && !query) {
        html += `<tr class="folder-row parent-row" data-action="navigate-folder" data-path="${escapeHtml(state.parentPath)}">`
              + `<td><span class="folder-cell"><span class="folder-icon">📁</span> <span class="folder-name">.. (Parent Folder)</span></span></td>`
              + `<td class="file-meta" data-label="Size">-</td>`
              + `<td class="file-meta" data-label="Modified">-</td>`
              + `<td class="file-meta" data-label="Type"><span class="folder-type-badge parent">Parent</span></td>`
              + `<td class="actions"><span class="row-actions"><button class="text-button" data-action="navigate-folder" data-path="${escapeHtml(state.parentPath)}">Up</button></span></td>`
              + `</tr>`;
    }

    // Subfolders list
    for (const folder of visibleFolders) {
        const path = escapeHtml(folder.path);
        html += `<tr class="folder-row" data-action="navigate-folder" data-path="${path}">`
              + `<td><span class="folder-cell"><span class="folder-icon">📁</span> <span class="folder-name">${escapeHtml(folder.name)}</span></span></td>`
              + `<td class="file-meta" data-label="Size">-</td>`
              + `<td class="file-meta" data-label="Modified">-</td>`
              + `<td class="file-meta" data-label="Type"><span class="folder-type-badge">Folder</span></td>`
              + `<td class="actions"><span class="row-actions"><button class="text-button btn-action-open" data-action="navigate-folder" data-path="${path}">Open</button></span></td>`
              + `</tr>`;
    }

    // Files list (capped to 300 to protect UI thread on huge folders)
    const maxRender = 300;
    const filesToRender = visibleFiles.slice(0, maxRender);
    html += filesToRender.map(file => {
        const path = escapeHtml(file.path);
        let actionsHtml = `<button class="text-button btn-action-open" data-action="open" data-path="${path}">Open</button>`;
        const isArchive = isArchiveFile(file.name, file.extension);
        if (!state.remoteMode && !isArchive) {
            actionsHtml += `<button class="text-button btn-action-tail" data-action="tail" data-path="${path}">Tail</button>`;
        }
        const downloadUrl = getDownloadFileUrl(file);
        actionsHtml += `<a class="text-button btn-action-download" href="${downloadUrl}" download="${escapeHtml(file.name)}" title="Download ${escapeHtml(file.name)}">Download</a>`;

        let typeCell = escapeHtml(file.extension || 'file');
        if (isArchive) {
            const extLabel = getArchiveLabel(file.name, file.extension);
            const archiveClass = extLabel.toLowerCase().replace(/[^a-z0-9]/g, '');
            typeCell = `<span class="folder-type-badge archive ${archiveClass}">${escapeHtml(extLabel)}</span>`;
            if (state.clusterMode && file.nodeBadge) {
                const badgeClass = file.isAllNodes ? 'cluster-file-pill all' : 'cluster-file-pill single';
                typeCell += ` <span class="${badgeClass}">${escapeHtml(file.nodeBadge)}</span>`;
            }
        } else if (state.clusterMode && file.nodeBadge) {
            const badgeClass = file.isAllNodes ? 'cluster-file-pill all' : 'cluster-file-pill single';
            typeCell = `<span class="${badgeClass}">${escapeHtml(file.nodeBadge)}</span>`;
        }

        const fileIcon = isArchive ? '📦' : '📄';
        return '<tr>'
             + `<td><span class="file-cell"><span class="file-icon">${fileIcon}</span> <span class="file-name">${escapeHtml(file.name)}</span></span></td>`
             + `<td class="file-meta" data-label="Size">${formatSize(file.size)}</td>`
             + `<td class="file-meta" data-label="Modified">${formatDate(file.lastModified)}</td>`
             + `<td class="file-meta" data-label="Type">${typeCell}</td>`
             + `<td class="actions"><span class="row-actions">${actionsHtml}</span></td></tr>`;
    }).join('');

    if (visibleFiles.length > maxRender) {
        html += `<tr><td colspan="5" style="text-align: center; padding: 12px; color: var(--muted); font-size: 12px;">`
              + `Showing first ${maxRender} of ${visibleFiles.length.toLocaleString()} files. Use search above to find specific files.`
              + `</td></tr>`;
    }

    $('file-list').innerHTML = html;

    $('empty-state').hidden = (visibleFolders.length > 0 || visibleFiles.length > 0);
    $('empty-state').textContent = (state.files.length || (state.folders && state.folders.length))
        ? 'No files or folders match the current filter.'
        : (state.clusterMode ? 'No log files found in this cluster path.' : (state.remoteMode ? 'No remote files found.' : 'Choose a folder to inspect its files.'));
}

export function updateSortIndicators() {
    document.querySelectorAll('th[data-sort]').forEach(header => {
        const active = header.dataset.sort === state.sort;
        header.setAttribute('aria-sort', active ? (state.ascending ? 'ascending' : 'descending') : 'none');
        const mark = header.querySelector('.sort-mark');
        if (mark) mark.textContent = active ? (state.ascending ? '▲' : '▼') : '';
    });
}

export async function initTree() {
    try {
        const response = await fetch(api('/api/files/roots'));
        if (!response.ok) throw await responseError(response, 'Unable to load roots.');
        const roots = await response.json();
        $('root-count').textContent = roots.length;
        $('folder-tree').textContent = '';
        if (!roots.length) {
            $('folder-tree').innerHTML =
                '<p class="tree-empty">None of the configured folders are reachable from this machine. '
              + 'Check AllowedFolders in appsettings.json.</p>';
            return;
        }
        renderTreeNodes($('folder-tree'), roots, true);

        // Auto-expand first root
        const firstToggle = $('folder-tree').querySelector('.tree-toggle');
        if (firstToggle) {
            firstToggle.click();
        }

        const urlParams = new URLSearchParams(window.location.search);
        if (!state.path && !urlParams.get('path') && !urlParams.get('cluster')) {
            const firstRow = $('folder-tree').querySelector('.tree-row');
            if (firstRow) {
                firstRow.classList.add('active');
                loadFolder(roots[0].path).catch(console.error);
            }
        }
    } catch (error) {
        console.error('Failed to init tree', error);
        $('folder-tree').innerHTML = `<p class="tree-empty">${escapeHtml(error.message)}</p>`;
    }
}

export function renderTreeNodes(container, folders, isRootLevel = false) {
    if (!folders || !folders.length) return;
    const level = document.createElement('div');
    level.className = 'tree-level';
    folders.forEach(folder => {
        const node = document.createElement('div');
        node.className = 'tree-node';
        node.innerHTML =
            `<div class="tree-row ${isRootLevel ? 'root-row' : ''}" data-folder="${escapeHtml(folder.path)}">`
          + `  <button type="button" class="tree-toggle" data-tree-path="${escapeHtml(folder.path)}" `
          + `    data-folder-name="${escapeHtml(folder.name)}" `
          + `    aria-expanded="false" aria-label="Expand ${escapeHtml(folder.name)}">`
          + `    <span class="tree-chevron">›</span>`
          + `  </button>`
          + `  <span class="tree-folder-icon" aria-hidden="true">📁</span>`
          + `  <span class="tree-label" data-folder="${escapeHtml(folder.path)}" title="${escapeHtml(folder.path)}">${escapeHtml(folder.name)}</span>`
          + `</div>`
          + `<div class="tree-children" hidden></div>`;
        level.appendChild(node);
    });
    container.appendChild(level);
}

export function setToggleState(toggle, expanded) {
    const name = toggle.dataset.folderName || 'folder';
    toggle.setAttribute('aria-expanded', String(expanded));
    toggle.setAttribute('aria-label', `${expanded ? 'Collapse' : 'Expand'} ${name}`);
}

export async function toggleTreeNode(toggle, forceExpand = false) {
    if (!toggle || toggle.dataset.busy === 'true') return;
    const node = toggle.closest('.tree-node');
    if (!node) return;
    const children = node.querySelector('.tree-children');
    if (!children) return;

    const isExpanded = toggle.getAttribute('aria-expanded') === 'true';
    if (isExpanded && !forceExpand) {
        setToggleState(toggle, false);
        children.hidden = true;
        return;
    }

    if (isExpanded && forceExpand) return;

    if (toggle.dataset.loaded !== 'true') {
        toggle.dataset.busy = 'true';
        toggle.classList.add('busy');
        try {
            const response = await fetch(`${api('/api/files/list?path=')}${encodeURIComponent(toggle.dataset.treePath)}`);
            if (!response.ok) throw await responseError(response, 'Unable to expand folder.');
            const data = await response.json();
            children.replaceChildren();
            if (data.folders && data.folders.length) renderTreeNodes(children, data.folders);
            else children.innerHTML = '<p class="tree-empty">(No subfolders)</p>';
            toggle.dataset.loaded = 'true';
        } catch (error) {
            children.replaceChildren();
            children.innerHTML = `<p class="tree-empty">${escapeHtml(error.message)}</p>`;
        } finally {
            toggle.dataset.busy = 'false';
            toggle.classList.remove('busy');
        }
    }

    setToggleState(toggle, true);
    children.hidden = false;
}

let folderLoadToken = 0;

export async function loadFolder(path, { force = false } = {}) {
    showPanel('files');
    const key = path || '';
    const token = ++folderLoadToken;

    const fetchAndRender = async () => {
        const response = await fetch(path ? `${api('/api/files/list?path=')}${encodeURIComponent(path)}` : api('/api/files/list'));
        if (token !== folderLoadToken) return;
        if (!response.ok) throw await responseError(response, 'Unable to load folder.');
        const data = await response.json();
        if (token !== folderLoadToken) return;

        // M-9: Cap folderCache at 50 folders to prevent unbounded memory leak
        const cacheKeys = Object.keys(state.folderCache);
        if (cacheKeys.length >= 50) delete state.folderCache[cacheKeys[0]];
        state.folderCache[key] = data;

        state.path = data.currentPath;
        state.localPath = data.currentPath;
        state.files = data.files;
        state.folders = data.folders || [];
        state.parentPath = data.parentPath;
        $('current-path').value = data.currentPath;
        renderFiles();
    };

    if (!force && state.folderCache[key]) {
        const data = state.folderCache[key];
        state.path = data.currentPath;
        state.localPath = data.currentPath;
        state.files = data.files;
        state.folders = data.folders || [];
        state.parentPath = data.parentPath;
        $('current-path').value = data.currentPath;
        renderFiles();
        fetchAndRender().catch(error => console.error('Background refresh failed', error));
        return;
    }

    showLoading('LOADING DIRECTORY...');
    try {
        await fetchAndRender();
    } catch (error) {
        showFilesError(error.message);
        throw error;
    } finally {
        hideLoading();
    }
}
