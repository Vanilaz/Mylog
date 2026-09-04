import { $, api, responseError, showLoading, hideLoading, setStatus, escapeHtml, getFileExtension } from './utils.js';
import { state } from './state.js';
import { renderFiles } from './browser.js';

let _projects = [];
let _currentRemotePath = '';
let _openFileCallback = null;

export function initRemote(options = {}) {
    _openFileCallback = options.onOpenFile;

    const refreshBtn = $('refresh-projects');
    if (refreshBtn) {
        refreshBtn.addEventListener('click', () => loadRemoteProjects(true));
    }

    const resetRootBtn = $('reset-remote-root');
    if (resetRootBtn) {
        resetRootBtn.addEventListener('click', () => browseRemotePath(''));
    }
}

export async function loadRemoteProjects(forceRefresh = false) {
    const tree = $('remote-file-tree');
    if (!tree) return;

    if (_projects.length && !forceRefresh) {
        if (!_currentRemotePath) renderProjectsList(_projects);
        return;
    }

    try {
        const statusEl = $('remote-status');
        if (statusEl) statusEl.textContent = 'Loading projects...';
        tree.innerHTML = '<div class="cluster-loading">Loading projects...</div>';
        const res = await fetch(api('/api/remote/projects'));
        if (!res.ok) throw await responseError(res, 'Unable to load projects');
        const rawProjects = await res.json();
        const seen = new Set();
        _projects = rawProjects.filter(p => {
            const key = p.name ? p.name.trim().toLowerCase() : '';
            if (!key || seen.has(key)) return false;
            seen.add(key);
            return true;
        });

        if (statusEl) statusEl.textContent = '';
        if (!_currentRemotePath) {
            renderProjectsList(_projects);
        } else {
            browseRemotePath(_currentRemotePath);
        }
    } catch (err) {
        const statusEl = $('remote-status');
        if (statusEl) statusEl.textContent = err.message || 'Error loading projects';
        tree.innerHTML = `<div class="cluster-error">⚠️ ${escapeHtml(err.message || 'Error loading projects')}</div>`;
    }
}

export function syncRemoteMainTable(path, data) {
    if (!state.remoteMode) return;
    state.path = path || '';
    state.remoteProject = path || '';
    state.parentPath = data.parentPath || '';
    state.folders = (data.folders || []).map(f => ({ name: f.name, path: f.path }));
    state.files = (data.files || []).map(f => {
        const fileName = f.name || '';
        const ext = f.extension || getFileExtension(fileName);
        return {
            name: fileName,
            path: fileName,
            size: f.size || 0,
            lastModified: f.lastModified || new Date().toISOString(),
            extension: ext || (fileName.toLowerCase().endsWith('.log') ? '.log' : '')
        };
    });
    const locationInput = $('current-path');
    if (locationInput) {
        locationInput.value = path ? `Remote: ${path}` : 'Remote: All Projects';
    }
    renderFiles();
}

function renderProjectsList(projects) {
    const tree = $('remote-file-tree');
    if (!tree) return;
    _currentRemotePath = '';

    if (!projects || !projects.length) {
        tree.innerHTML = '<div class="cluster-empty">No remote projects found.</div>';
        syncRemoteMainTable('', { folders: [], files: [], parentPath: '' });
        return;
    }

    let html = '';
    projects.forEach(p => {
        html += `<div class="cluster-file-item folder remote-folder-row" data-remote-path="${escapeHtml(p.name)}">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📁</span>
                <span class="cluster-file-name" title="${escapeHtml(p.name)}">${escapeHtml(p.name)}</span>
            </div>
        </div>`;
    });

    tree.innerHTML = html;
    attachTreeEvents(tree);

    syncRemoteMainTable('', {
        folders: projects.map(p => ({ name: p.name, path: p.name })),
        files: [],
        parentPath: ''
    });
}

export async function browseRemotePath(path = '') {
    const tree = $('remote-file-tree');
    if (!tree) return;

    if (!_projects.length) {
        await loadRemoteProjects();
    }

    if (!path) {
        renderProjectsList(_projects);
        return;
    }

    _currentRemotePath = path;
    tree.innerHTML = '<div class="cluster-loading">Scanning files & folders...</div>';

    try {
        const res = await fetch(api('/api/remote/files?projectName=' + encodeURIComponent(path)));
        if (!res.ok) throw await responseError(res, 'Unable to load remote files');
        const data = await res.json();

        renderRemoteFileList(path, data);
    } catch (err) {
        tree.innerHTML = `<div class="cluster-error">⚠️ ${escapeHtml(err.message)}</div>`;
    }
}

function renderRemoteFileList(currentPath, data) {
    const tree = $('remote-file-tree');
    if (!tree) return;

    let html = '';

    // Parent navigation
    if (data.parentPath) {
        html += `<div class="cluster-file-item folder remote-folder-row" data-remote-path="${escapeHtml(data.parentPath)}">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📁</span>
                <span class="cluster-file-name">.. (${escapeHtml(data.parentPath.split('/').pop())})</span>
            </div>
        </div>`;
    } else {
        html += `<div class="cluster-file-item folder remote-folder-row" data-remote-path="">
            <div class="cluster-file-info">
                <span class="cluster-file-icon">📁</span>
                <span class="cluster-file-name">.. (All Projects)</span>
            </div>
        </div>`;
    }

    // Subfolders
    if (data.folders && data.folders.length > 0) {
        data.folders.forEach(f => {
            html += `<div class="cluster-file-item folder remote-folder-row" data-remote-path="${escapeHtml(f.path)}">
                <div class="cluster-file-info">
                    <span class="cluster-file-icon">📁</span>
                    <span class="cluster-file-name">${escapeHtml(f.name)}</span>
                </div>
            </div>`;
        });
    }

    // Files
    if (data.files && data.files.length > 0) {
        data.files.forEach(f => {
            const isActive = state.remoteFile === f.name && state.remoteProject === currentPath;
            html += `<div class="cluster-file-item file remote-file-row ${isActive ? 'active' : ''}" data-remote-project="${escapeHtml(currentPath)}" data-remote-file="${escapeHtml(f.name)}">
                <div class="cluster-file-info">
                    <span class="cluster-file-icon">📄</span>
                    <span class="cluster-file-name" title="${escapeHtml(f.name)}">${escapeHtml(f.name)}</span>
                </div>
            </div>`;
        });
    }

    if ((!data.folders || !data.folders.length) && (!data.files || !data.files.length)) {
        html += '<div class="cluster-empty">No files or subfolders found in this path.</div>';
    }

    tree.innerHTML = html;
    attachTreeEvents(tree);

    syncRemoteMainTable(currentPath, data);
}

function attachTreeEvents(tree) {
    tree.querySelectorAll('.remote-folder-row').forEach(el => {
        el.addEventListener('click', () => {
            const path = el.getAttribute('data-remote-path');
            browseRemotePath(path);
        });
    });

    tree.querySelectorAll('.remote-file-row').forEach(el => {
        el.addEventListener('click', async () => {
            tree.querySelectorAll('.remote-file-row').forEach(r => r.classList.remove('active'));
            el.classList.add('active');

            const project = el.getAttribute('data-remote-project');
            const file = el.getAttribute('data-remote-file');

            state.remoteMode = true;
            state.clusterMode = false;
            state.remoteProject = project;
            state.remoteFile = file;

            if (_openFileCallback) {
                _openFileCallback(project, file);
            }
        });
    });
}
