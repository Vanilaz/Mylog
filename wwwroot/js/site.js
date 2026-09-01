(() => {
    const escapeRegExp = string => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const state = { path: null, selectedFile: null, files: [], sort: 'name', ascending: true, connection: null, paused: false, lineCount: 0, lastSearchData: null, fontSize: parseInt(localStorage.getItem('fontSize')) || 12 };
    if (localStorage.getItem('theme') === 'dark') document.body.classList.add('dark');
    if (localStorage.getItem('autoFormatJson') === 'true') { document.addEventListener('DOMContentLoaded', () => { const afj = document.getElementById('auto-format-json'); if (afj) afj.checked = true; }); }
    if (localStorage.getItem('wordWrap') === 'false') { document.addEventListener('DOMContentLoaded', () => { const ww = document.getElementById('word-wrap'); if (ww) ww.checked = false; document.getElementById('log-viewer').classList.add('nowrap'); }); }
    if (localStorage.getItem('showLines') === 'false') { document.addEventListener('DOMContentLoaded', () => { const sl = document.getElementById('show-lines'); if (sl) sl.checked = false; document.getElementById('log-viewer').classList.remove('show-lines'); }); }
    if (localStorage.getItem('errorsOnly') === 'true') { document.addEventListener('DOMContentLoaded', () => { const eo = document.getElementById('errors-only'); if (eo) eo.checked = true; document.getElementById('log-viewer').classList.add('errors-only-mode'); }); }
    document.addEventListener('DOMContentLoaded', () => { document.body.style.setProperty('--log-font-size', state.fontSize + 'px'); });
    const $ = id => document.getElementById(id);
    const escapeHtml = text => text.replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[m]));
    const formatSize = bytes => bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1048576).toFixed(1)} MB`;
    const formatDate = value => new Date(value).toLocaleString('sv-SE').replace('T', ' ');
    const classify = line => {
        const lvlMatch = line.match(/\b(ERROR|FATAL|ERR|FTL|WARN|WARNING|WRN|INFO|INF)\b/);
        if (lvlMatch) {
            const lvl = lvlMatch[1];
            if (['ERROR', 'FATAL', 'ERR', 'FTL'].includes(lvl)) return 'error';
            if (['WARN', 'WARNING', 'WRN'].includes(lvl)) return 'warn';
            if (['INFO', 'INF'].includes(lvl)) return 'info';
        }
        if (/\b(EXCEPTION|FAIL)\b/i.test(line)) return 'error';
        return '';
    };
    let customRe = null;
    function updateCustomRe() {
        const kws = $('custom-highlight').value.split(',').map(k => k.trim()).filter(k => k);
        customRe = kws.length > 0 ? new RegExp(`(${kws.map(escapeRegExp).join('|')})`, 'gi') : null;
    }
    function applyCustomHighlights(text) {
        if (!customRe) return text;
        let result = '';
        let lastIdx = 0;
        text.replace(customRe, (m, ...a) => {
            const off = a[a.length - 2];
            result += text.slice(lastIdx, off) + `<span class="custom-hl">${m}</span>`;
            lastIdx = off + m.length;
            return m;
        });
        return result + text.slice(lastIdx);
    }
    function highlightMatches(line, query, regexMode) {
        let searchRe = null;
        if (query) {
            try { searchRe = new RegExp(regexMode ? query : escapeRegExp(query), 'gi'); } 
            catch { searchRe = null; }
        }
        if (!searchRe && !customRe) return escapeHtml(line);
        let result = '';
        let lastIdx = 0;
        if (searchRe) {
            let chunks = [];
            line.replace(searchRe, (match, ...args) => {
                const offset = args[args.length - 2];
                chunks.push({ text: line.slice(lastIdx, offset), isMark: false });
                chunks.push({ text: match, isMark: true });
                lastIdx = offset + match.length;
                return match;
            });
            chunks.push({ text: line.slice(lastIdx), isMark: false });
            for (const chunk of chunks) {
                if (chunk.isMark) {
                    result += `<mark>${applyCustomHighlights(escapeHtml(chunk.text))}</mark>`;
                } else {
                    result += applyCustomHighlights(escapeHtml(chunk.text));
                }
            }
        } else if (customRe) {
            result = applyCustomHighlights(escapeHtml(line));
        }
        return result;
    }
    const tryFormatJson = (text) => {
        if (!$('auto-format-json') || !$('auto-format-json').checked) return text;
        let start = text.indexOf('{'); let end = text.lastIndexOf('}');
        if (start !== -1 && end > start) {
            let chunk = text.substring(start, end + 1);
            if (chunk.indexOf('"') !== -1 && chunk.indexOf(':') !== -1) {
                try { let parsed = JSON.parse(chunk); return text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); } catch(e) {}
            }
        }
        start = text.indexOf('['); end = text.lastIndexOf(']');
        if (start !== -1 && end > start) {
            let chunk = text.substring(start, end + 1);
            if (chunk.indexOf('"') !== -1 && chunk.indexOf(':') !== -1) {
                try { let parsed = JSON.parse(chunk); return text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); } catch(e) {}
            }
        }
        return text;
    };
    function renderOutput(content, append = false) {
        const viewer = $('log-viewer');
        if (!append) { viewer.innerHTML = ''; state.lineCount = 0; $('export-search-button').style.display = 'none'; state.lastSearchData = null; }
        if (!content && !append) return;
        let newLines = content.split('\n');
        if (newLines.length > 0 && newLines[newLines.length - 1] === '') newLines.pop();
        if (!append && newLines.length > 2000) {
            newLines = newLines.slice(-2000);
            viewer.insertAdjacentHTML('beforeend', '<div style="color: var(--warn); padding: 8px; text-align: center; border-bottom: 1px solid #334155; margin-bottom: 10px;">[ Showing last 2000 lines for performance. Download file to see full content. ]</div>\n');
        }
        const html = newLines.map(line => {
            state.lineCount++;
            if (line.length > 15000) {
                line = line.substring(0, 15000) + ' ... [LINE TRUNCATED FOR PERFORMANCE. DOWNLOAD TO VIEW FULL]';
            }
            return `<span class="log-line" id="L${state.lineCount}"><a href="#L${state.lineCount}" class="line-number" onclick="highlightLine(${state.lineCount})">${state.lineCount}</a><span class="${classify(line)}">${applyCustomHighlights(escapeHtml(tryFormatJson(line)))}</span></span>`;
        }).join('\n');
        if (append && viewer.innerHTML !== '') {
            viewer.insertAdjacentHTML('beforeend', '\n' + html);
            while (viewer.childElementCount > 3000) viewer.removeChild(viewer.firstElementChild);
        } else {
            viewer.insertAdjacentHTML('beforeend', html);
        }
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight; 
    }
    function renderFiles() { const query = $('search-input').value.toLowerCase(); const visible = state.files.filter(file => (!query || file.name.toLowerCase().includes(query)) && (!$('logs-only').checked || file.extension.toLowerCase() === '.log')); visible.sort((a, b) => { const left = state.sort === 'size' ? a.size : state.sort === 'modified' ? new Date(a.lastModified) : a.name; const right = state.sort === 'size' ? b.size : state.sort === 'modified' ? new Date(b.lastModified) : b.name; return (left > right ? 1 : left < right ? -1 : 0) * (state.ascending ? 1 : -1); }); $('file-list').innerHTML = visible.map(file => `<tr><td><span class="file-name">${escapeHtml(file.name)}</span></td><td class="file-meta">${formatSize(file.size)}</td><td class="file-meta">${formatDate(file.lastModified)}</td><td class="file-meta">${escapeHtml(file.extension || 'file')}</td><td class="actions" style="text-align: right; display: flex; gap: 4px; justify-content: flex-end;"><button class="text-button" data-action="open" data-path="${escapeHtml(file.path)}">Open</button><button class="text-button" data-action="tail" data-path="${escapeHtml(file.path)}">Tail</button><a href="${window.appBasePath}/api/files/download?path=${encodeURIComponent(file.path)}" download style="text-decoration: none;"><button class="text-button">Download</button></a></td></tr>`).join(''); $('empty-state').style.display = visible.length ? 'none' : 'block'; }
    function showLoading(text) { if (text === "LOADING DIRECTORY...") { $("file-list").innerHTML = Array(5).fill("<tr><td colspan=\"5\"><div class=\"skeleton-line\"></div></td></tr>").join(""); } else { $("log-viewer").innerHTML = Array(8).fill(0).map((_, i) => `<div class="skeleton-line" style="width: ${40 + Math.random() * 40}%"></div>`).join(""); } }
    function hideLoading() { if ($("file-list").innerHTML.includes("skeleton-line")) $("file-list").innerHTML = "<tr><td colspan=\"5\" style=\"text-align:center; color:var(--error);\">Failed to load.</td></tr>"; if ($("log-viewer").innerHTML.includes("skeleton-line")) $("log-viewer").innerHTML = "Failed to load."; }
    async function initTree() {
        try {
            const response = await fetch(`${window.appBasePath}/api/files/list`);
            const data = await response.json();
            $('root-count').textContent = data.folders.length;
            $('folder-tree').innerHTML = '';
            renderTreeNodes($('folder-tree'), data.folders);
        } catch (e) { console.error('Failed to init tree', e); }
    }
    function renderTreeNodes(container, folders) {
        if (!folders || folders.length === 0) return;
        const ul = document.createElement('div');
        ul.className = 'tree-level';
        ul.style.paddingLeft = container.id === 'folder-tree' ? '0' : '15px';
        folders.forEach(folder => {
            const node = document.createElement('div');
            node.className = 'tree-node';
            node.innerHTML = `
                <div style="display: flex; align-items: center; padding: 4px 0; cursor: pointer;">
                    <span class="tree-toggle" data-tree-path="${escapeHtml(folder.path)}" style="width: 20px; text-align: center; color: var(--muted); font-family: monospace;">&#9654;</span>
                    <span class="tree-label" data-folder="${escapeHtml(folder.path)}" style="color: var(--ink); flex: 1; user-select: none;">${escapeHtml(folder.name)}</span>
                </div>
                <div class="tree-children" style="display: none;"></div>
            `;
            ul.appendChild(node);
        });
        container.appendChild(ul);
    }
    document.addEventListener('click', async event => {
        if (event.target.classList.contains('tree-toggle')) {
            const btn = event.target;
            const path = btn.dataset.treePath;
            const childrenDiv = btn.parentElement.nextElementSibling;
            if (btn.classList.contains('expanded')) {
                btn.classList.remove('expanded');
                btn.innerHTML = '&#9654;';
                childrenDiv.style.display = 'none';
            } else {
                if (childrenDiv.innerHTML === '') {
                    try {
                        btn.innerHTML = '&#8987;';
                        const response = await fetch(`${window.appBasePath}/api/files/list?path=` + encodeURIComponent(path));
                        const data = await response.json();
                        if (data.folders.length > 0) renderTreeNodes(childrenDiv, data.folders);
                        else childrenDiv.innerHTML = '<div style="padding-left:15px; color:var(--muted); font-size:11px; font-style:italic;">(No subfolders)</div>';
                    } catch(e) { childrenDiv.innerHTML = '<div style="color:red;">Error</div>'; }
                }
                btn.classList.add('expanded');
                btn.innerHTML = '&#9660;';
                childrenDiv.style.display = 'block';
            }
        }
    });

    state.folderCache = state.folderCache || {};
    async function loadFolder(path) { 
        document.getElementById('file-panel').style.display = 'flex'; 
        document.getElementById('viewer-panel').style.display = 'none'; 
        const fetchAndRender = async () => {
            const response = await fetch(`${window.appBasePath}/api/files/list${path ? `?path=${encodeURIComponent(path)}` : ''}`); 
            const data = await response.json(); 
            if (!response.ok) throw new Error(data.error || 'Unable to load folder.'); 
            state.folderCache[path || ''] = data;
            state.path = data.currentPath; state.files = data.files; $('current-path').value = data.currentPath; 
            renderFiles();
        };
        if (state.folderCache[path || '']) {
            const data = state.folderCache[path || ''];
            state.path = data.currentPath; state.files = data.files; $('current-path').value = data.currentPath; 
            renderFiles();
            fetchAndRender().catch(e => console.error(e));
        } else {
            showLoading('LOADING DIRECTORY...'); 
            try { await fetchAndRender(); } finally { hideLoading(); } 
        }
    }
    async function openFile(path) { document.getElementById('file-panel').style.display = 'none'; document.getElementById('viewer-panel').style.display = 'flex'; showLoading('OPENING FILE...'); try { state.selectedFile = path; const response = await fetch(`${window.appBasePath}/api/files/content?path=${encodeURIComponent(path)}`); const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Unable to open file.'); $('viewer-title').textContent = data.fileName; renderOutput(data.content); $('connection-status').textContent = data.isTruncated ? 'PARTIAL READ' : 'READY'; $('pause-button').disabled = true; } finally { hideLoading(); } }
    async function tailFile(path) { document.getElementById('file-panel').style.display = 'none'; document.getElementById('viewer-panel').style.display = 'flex'; showLoading('CONNECTING LIVE TAIL...'); try { state.selectedFile = path; if (typeof signalR === 'undefined') throw new Error('SignalR client could not load. Check network access and refresh the page.'); if (!state.connection) { state.connection = new signalR.HubConnectionBuilder().withUrl(`${window.appBasePath}/hubs/log`).withAutomaticReconnect().build(); state.connection.on('LogAppended', text => { if (!state.paused) renderOutput(text, true); }); } if (state.connection.state === 'Disconnected') { await state.connection.start(); } await state.connection.invoke('Subscribe', path); state.paused = false; $('viewer-title').textContent = path.split(/[\\/]/).pop(); $('connection-status').textContent = 'LIVE'; $('connection-status').classList.add('live'); $('pause-button').disabled = false; $('pause-button').textContent = 'Pause'; renderOutput(''); } finally { hideLoading(); } }
    async function searchContent() { 
        showLoading('SEARCHING LOGS...'); 
        try {
            if (!state.selectedFile) throw new Error('Open or tail a file before searching.'); 
            const query = $('content-search').value; 
            if (!query) throw new Error('Please enter a search query.');
            const regex = $('search-mode').value === 'regex'; 
            const ctx = $('search-context').value; 
            const response = await fetch(`${window.appBasePath}/api/files/search?path=${encodeURIComponent(state.selectedFile)}&query=${encodeURIComponent(query)}&regex=${regex}&context=${ctx}`); 
            const data = await response.json(); 
            if (!response.ok) throw new Error(data.error || 'Unable to search file content.'); 
            const matchCount = data.filter(d => d.isMatch).length; 
            $('search-summary').textContent = `${matchCount} match${matchCount === 1 ? '' : 'es'}`; 
            $('log-viewer').innerHTML = data.length ? data.map((result, i) => {
                let html = '';
                if (i > 0 && result.lineNumber > data[i - 1].lineNumber + 1) {
                    html += `<span class="log-line divider" style="opacity: 0.5; padding: 4px 10px; font-style: italic; color: #888;">--- gap ---</span>\n`;
                }
                html += `<span class="log-line ${result.isMatch ? '' : 'context-line'}" id="L${result.lineNumber}"><a href="#L${result.lineNumber}" class="line-number" onclick="highlightLine(${result.lineNumber})">${result.lineNumber}</a><span class="${classify(result.line)}">${result.isMatch ? highlightMatches(result.line, query, regex) : highlightMatches(result.line, '', false)}</span></span>`;
                return html;
            }).join('\n') : 'No matching lines.'; 
            state.lastSearchData = data; 
            $('export-search-button').style.display = data.length ? 'inline-block' : 'none'; 
        } finally {
            hideLoading();
        }
    }
    document.addEventListener('click', async event => {
        try {
            const target = event.target.closest('[data-folder], [data-action]');
            if (!target) return;
            if (target.dataset.folder) await loadFolder(target.dataset.folder);
            if (target.dataset.action === 'open') await openFile(target.dataset.path);
            if (target.dataset.action === 'tail') await tailFile(target.dataset.path);
        } catch (error) { renderOutput(`ERROR: ${error.message}`); }
    });
    const navigate = () => loadFolder($('current-path').value.trim()).catch(error => renderOutput(`ERROR: ${error.message}`));
    updateCustomRe();
    $('custom-highlight').addEventListener('change', () => {
        updateCustomRe();
        if (!state.selectedFile) return;
        if (state.connection) { state.connection.stop().then(() => tailFile(state.selectedFile)); }
        else if (state.lastSearchData) { searchContent(); }
        else { openFile(state.selectedFile); }
    });
    $('go-button').addEventListener('click', navigate); $('current-path').addEventListener('keydown', event => { if (event.key === 'Enter') navigate(); }); $('refresh-button').addEventListener('click', () => loadFolder(state.path)); $('reset-root').addEventListener('click', () => { $('current-path').value = ''; navigate(); }); $('search-input').addEventListener('input', renderFiles); $('logs-only').addEventListener('change', renderFiles); $('clear-button').addEventListener('click', () => renderOutput('')); $('search-button').addEventListener('click', () => searchContent().catch(error => { $('search-summary').textContent = error.message; })); $('content-search').addEventListener('keydown', event => { if (event.key === 'Enter') $('search-button').click(); }); $('theme-button').addEventListener('click', () => { const isDark = document.body.classList.toggle('dark'); localStorage.setItem('theme', isDark ? 'dark' : 'light'); }); $('auto-format-json').addEventListener('change', (e) => { localStorage.setItem('autoFormatJson', e.target.checked); if(e.target.checked && $('log-viewer').innerHTML) { $('format-json-button').click(); } }); $('word-wrap').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.remove('nowrap'); localStorage.setItem('wordWrap', 'true'); } else { $('log-viewer').classList.add('nowrap'); localStorage.setItem('wordWrap', 'false'); } }); $('show-lines').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.add('show-lines'); localStorage.setItem('showLines', 'true'); } else { $('log-viewer').classList.remove('show-lines'); localStorage.setItem('showLines', 'false'); } }); $('errors-only').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.add('errors-only-mode'); localStorage.setItem('errorsOnly', 'true'); } else { $('log-viewer').classList.remove('errors-only-mode'); localStorage.setItem('errorsOnly', 'false'); } }); $('copy-button').addEventListener('click', async () => { try { const text = Array.from($('log-viewer').querySelectorAll('.log-line')).map(el => el.lastElementChild.textContent).join('\n'); await navigator.clipboard.writeText(text); const btn = $('copy-button'); const old = btn.textContent; btn.textContent = 'Copied!'; setTimeout(() => btn.textContent = old, 2000); } catch(err) { alert('Failed to copy'); } }); $('export-search-button').addEventListener('click', () => { if (!state.lastSearchData) return; const text = state.lastSearchData.map(r => r.line).join('\n'); const blob = new Blob([text], { type: 'text/plain' }); const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `search_${Date.now()}.txt`; a.click(); URL.revokeObjectURL(url); }); $('format-json-button').addEventListener('click', () => { document.querySelectorAll('.log-line > span:last-child').forEach(el => { let text = el.textContent; let start = text.indexOf('{'); let end = text.lastIndexOf('}'); if (start !== -1 && end > start) { try { let parsed = JSON.parse(text.substring(start, end + 1)); el.textContent = text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); return; } catch(e) {} } start = text.indexOf('['); end = text.lastIndexOf(']'); if (start !== -1 && end > start) { try { let parsed = JSON.parse(text.substring(start, end + 1)); el.textContent = text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); } catch(e) {} } }); }); $('close-file-button').addEventListener('click', async () => { if (state.connection) { await state.connection.stop(); state.connection = null; } state.selectedFile = null; $('viewer-panel').style.display = 'none'; $('file-panel').style.display = 'flex'; history.replaceState(null, null, window.location.pathname); }); $('pause-button').addEventListener('click', () => { state.paused = !state.paused; $('pause-button').textContent = state.paused ? 'Resume' : 'Pause'; });
    $('font-inc').addEventListener('click', () => { state.fontSize += 1; localStorage.setItem('fontSize', state.fontSize); document.body.style.setProperty('--log-font-size', state.fontSize + 'px'); });
    $('font-dec').addEventListener('click', () => { state.fontSize = Math.max(8, state.fontSize - 1); localStorage.setItem('fontSize', state.fontSize); document.body.style.setProperty('--log-font-size', state.fontSize + 'px'); });
    window.highlightLine = (lineNum) => {
        document.querySelectorAll('.highlight-line').forEach(el => el.classList.remove('highlight-line'));
        const el = document.getElementById('L' + lineNum);
        if (el) {
            el.classList.add('highlight-line');
            history.replaceState(null, null, `?path=${encodeURIComponent(state.selectedFile)}#L${lineNum}`);
        }
    };
    function checkUrlParams() {
        const params = new URLSearchParams(window.location.search);
        const path = params.get('path');
        if (path) { openFile(path).then(() => { if (window.location.hash.startsWith('#L')) { const num = window.location.hash.substring(2); window.highlightLine(num); const el = document.getElementById('L' + num); if (el) el.scrollIntoView({ block: 'center' }); } }); }
    }
    document.querySelectorAll('th[data-sort]').forEach(header => header.addEventListener('click', () => { state.ascending = state.sort === header.dataset.sort ? !state.ascending : true; state.sort = header.dataset.sort; renderFiles(); })); initTree(); loadFolder().catch(error => renderOutput(`ERROR: ${error.message}`));
    checkUrlParams();
})();

document.getElementById('toggle-sidebar').addEventListener('click', () => { document.querySelector('.main-content').classList.toggle('sidebar-collapsed'); });




