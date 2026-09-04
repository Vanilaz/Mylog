export const $ = id => document.getElementById(id);
export const api = path => `${window.appBasePath || ''}${path}`;

export const escapeRegExp = string => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
export const escapeHtml = text => String(text).replace(/[&<>"']/g, m =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[m]));

export function getFileExtension(filename) {
    if (!filename) return '';
    const lastDot = filename.lastIndexOf('.');
    return lastDot >= 0 ? filename.slice(lastDot).toLowerCase() : '';
}

export function isArchiveFile(filename, extension) {
    const ext = (extension || getFileExtension(filename)).toLowerCase();
    return ['.zip', '.tar', '.gz', '.7z', '.rar', '.bz2'].includes(ext) ||
           /\.(zip|tar|gz|7z|rar|bz2)$/i.test(filename || '');
}

export function getArchiveLabel(filename, extension) {
    const lowerName = (filename || '').toLowerCase();
    if (lowerName.endsWith('.tar.gz')) return 'TAR.GZ';
    const ext = (getFileExtension(filename) || extension || '.zip').toLowerCase();
    return (ext.replace('.', '') || 'ZIP').toUpperCase();
}

// Read an error message out of a failed response without assuming it is JSON.
export async function responseError(response, fallback) {
    try {
        const data = await response.clone().json();
        if (data && data.error) return new Error(data.error);
    } catch { /* not JSON */ }
    return new Error(`${fallback} (HTTP ${response.status})`);
}

export let pending = 0;

export function showLoading(text) {
    pending++;
    $('loading-text').textContent = text;
    $('loading-overlay').hidden = false;
    $('loading-overlay').classList.remove('hidden');
}

export function hideLoading() {
    pending = Math.max(0, pending - 1);
    if (pending === 0) $('loading-overlay').classList.add('hidden');
}

export function showFilesError(message) {
    $('file-list').innerHTML = `<tr><td colspan="5" class="error-row">${escapeHtml(message)}</td></tr>`;
    $('empty-state').hidden = true;
}

export function showPanel(which) {
    $('file-panel').hidden = which !== 'files';
    $('viewer-panel').hidden = which !== 'viewer';
}

export function setStatus(text, live) {
    $('connection-status').textContent = text;
    $('connection-status').classList.toggle('live', !!live);
}
