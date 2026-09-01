(() => {
    const escapeRegExp = string => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const state = { path: null, selectedFile: null, files: [], sort: 'name', ascending: true, connection: null, paused: false, lineCount: 0, lastSearchData: null, fontSize: parseInt(localStorage.getItem('fontSize')) || 12 };
    if (localStorage.getItem('theme') === 'dark') document.body.classList.add('dark');
    if (localStorage.getItem('wordWrap') === 'false') { document.addEventListener('DOMContentLoaded', () => { const ww = document.getElementById('word-wrap'); if (ww) ww.checked = false; document.getElementById('log-viewer').classList.add('nowrap'); }); }
    if (localStorage.getItem('showLines') === 'false') { document.addEventListener('DOMContentLoaded', () => { const sl = document.getElementById('show-lines'); if (sl) sl.checked = false; document.getElementById('log-viewer').classList.remove('show-lines'); }); }
    if (localStorage.getItem('errorsOnly') === 'true') { document.addEventListener('DOMContentLoaded', () => { const eo = document.getElementById('errors-only'); if (eo) eo.checked = true; document.getElementById('log-viewer').classList.add('errors-only-mode'); }); }
    document.addEventListener('DOMContentLoaded', () => { document.body.style.setProperty('--log-font-size', state.fontSize + 'px'); });
    const $ = id => document.getElementById(id);
    const escapeHtml = value => value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
    const formatSize = bytes => bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1048576).toFixed(1)} MB`;
    const formatDate = value => new Date(value).toLocaleString('sv-SE').replace('T', ' ');
    const classify = line => /error/i.test(line) ? 'error' : /warn/i.test(line) ? 'warn' : /info/i.test(line) ? 'info' : '';
    function applyCustomHighlights(html) {
        const kws = $('custom-highlight').value.split(',').map(k => k.trim()).filter(k => k);
        if (kws.length === 0) return html;
        let t = html;
        kws.forEach(kw => {
            const re = new RegExp(`(?![^<]*>)(${escapeRegExp(kw)})`, 'gi');
            t = t.replace(re, '<span class="custom-hl">$1</span>');
        });
        return t;
    }
    function highlightMatches(line, query, regex) {
        let output = '';
        if (!query) { output = escapeHtml(line); }
        else {
            let matchIndex = 0; let lastIndex = 0;
            try {
                const re = new RegExp(regex ? query : escapeRegExp(query), regex ? 'gi' : 'i');
                let match;
                while ((match = re.exec(line)) !== null && matchIndex < 100) {
                    if (match[0].length === 0) break;
                    output += escapeHtml(line.slice(lastIndex, match.index));
                    output += `<mark>${escapeHtml(match[0])}</mark>`;
                    lastIndex = re.lastIndex; matchIndex++;
                }
                output += escapeHtml(line.slice(lastIndex));
            } catch { output = escapeHtml(line); }
        }
        return applyCustomHighlights(output);
    }
    function renderOutput(text, append = false) { 
        const viewer = $('log-viewer'); 
        if (!append) { viewer.innerHTML = ''; state.lineCount = 0; $('export-search-button').style.display = 'none'; state.lastSearchData = null; }
        if (!text && !append) return;
        const newLines = text.split('\n');
        if (newLines.length > 0 && newLines[newLines.length - 1] === '') newLines.pop();
        const html = newLines.map(line => {
            state.lineCount++;
            return `<span class="log-line" id="L${state.lineCount}"><a href="#L${state.lineCount}" class="line-number" onclick="highlightLine(${state.lineCount})">${state.lineCount}</a><span class="${classify(line)}">${highlightMatches(line, '', false)}</span></span>`;
        }).join('\n');
        if (append && viewer.innerHTML !== '') viewer.insertAdjacentHTML('beforeend', '\n' + html);
        else viewer.innerHTML = html;
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight; 
    }
    function renderFiles() { const query = $('search-input').value.toLowerCase(); const visible = state.files.filter(file => (!query || file.name.toLowerCase().includes(query)) && (!$('logs-only').checked || file.extension.toLowerCase() === '.log')); visible.sort((a, b) => { const left = state.sort === 'size' ? a.size : state.sort === 'modified' ? new Date(a.lastModified) : a.name; const right = state.sort === 'size' ? b.size : state.sort === 'modified' ? new Date(b.lastModified) : b.name; return (left > right ? 1 : left < right ? -1 : 0) * (state.ascending ? 1 : -1); }); $('file-list').innerHTML = visible.map(file => `<tr><td><span class="file-name">${escapeHtml(file.name)}</span></td><td class="file-meta">${formatSize(file.size)}</td><td class="file-meta">${formatDate(file.lastModified)}</td><td class="file-meta">${escapeHtml(file.extension || 'file')}</td><td class="actions"><button data-action="open" data-path="${escapeHtml(file.path)}">Open</button><button data-action="tail" data-path="${escapeHtml(file.path)}">Tail</button><a href="${window.appBasePath}/api/files/download?path=${encodeURIComponent(file.path)}" download><button>Download</button></a></td></tr>`).join(''); $('empty-state').style.display = visible.length ? 'none' : 'block'; }
    async function loadFolder(path) { const response = await fetch(`${window.appBasePath}/api/files/list${path ? `?path=${encodeURIComponent(path)}` : ''}`); const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Unable to load folder.'); state.path = data.currentPath; state.files = data.files; $('current-path').value = data.currentPath; $('root-count').textContent = data.folders.length; let html = data.parentPath ? `<button class="folder" style="font-weight: 600; font-style: italic; color: var(--muted);" data-folder="${escapeHtml(data.parentPath)}">&#11014; [ Go Up ]</button>` : ''; html += data.folders.map(folder => `<button class="folder ${folder.path === state.path ? 'active' : ''}" data-folder="${escapeHtml(folder.path)}">${escapeHtml(folder.name)}</button>`).join(''); $('folder-tree').innerHTML = html || '<span class="file-meta">No subfolders</span>'; renderFiles(); }
    async function openFile(path) { state.selectedFile = path; const response = await fetch(`${window.appBasePath}/api/files/content?path=${encodeURIComponent(path)}`); const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Unable to open file.'); $('viewer-title').textContent = data.fileName; renderOutput(data.content); $('connection-status').textContent = data.isTruncated ? 'PARTIAL READ' : 'READY'; $('pause-button').disabled = true; }
    async function tailFile(path) { state.selectedFile = path; if (typeof signalR === 'undefined') throw new Error('SignalR client could not load. Check network access and refresh the page.'); if (!state.connection) { state.connection = new signalR.HubConnectionBuilder().withUrl(`${window.appBasePath}/hubs/log`).withAutomaticReconnect().build(); state.connection.on('LogAppended', text => { if (!state.paused) renderOutput(text, true); }); await state.connection.start(); } await state.connection.invoke('Subscribe', path); state.paused = false; $('viewer-title').textContent = path.split(/[\\/]/).pop(); $('connection-status').textContent = 'LIVE'; $('connection-status').classList.add('live'); $('pause-button').disabled = false; $('pause-button').textContent = 'Pause'; renderOutput(''); }
    async function searchContent() { if (!state.selectedFile) throw new Error('Open or tail a file before searching.'); const query = $('content-search').value; const regex = $('search-mode').value === 'regex'; const ctx = $('search-context').value; const response = await fetch(`${window.appBasePath}/api/files/search?path=${encodeURIComponent(state.selectedFile)}&query=${encodeURIComponent(query)}&regex=${regex}&context=${ctx}`); const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Unable to search file content.'); const matchCount = data.filter(d => d.isMatch).length; $('search-summary').textContent = `${matchCount} match${matchCount === 1 ? '' : 'es'}`; $('log-viewer').innerHTML = data.length ? data.map(result => `<span class="log-line ${result.isMatch ? '' : 'context-line'}" id="L${result.lineNumber}"><a href="#L${result.lineNumber}" class="line-number" onclick="highlightLine(${result.lineNumber})">${result.lineNumber}</a><span class="${classify(result.line)}">${result.isMatch ? highlightMatches(result.line, query, regex) : applyCustomHighlights(escapeHtml(result.line))}</span></span>`).join('\n') : 'No matching lines.'; state.lastSearchData = data; $('export-search-button').style.display = data.length ? 'inline-block' : 'none'; }
    document.addEventListener('click', async event => { const button = event.target.closest('button'); if (!button) return; try { if (button.dataset.folder) await loadFolder(button.dataset.folder); if (button.dataset.action === 'open') await openFile(button.dataset.path); if (button.dataset.action === 'tail') await tailFile(button.dataset.path); } catch (error) { renderOutput(`ERROR: ${error.message}`); } });
    const navigate = () => loadFolder($('current-path').value.trim()).catch(error => renderOutput(`ERROR: ${error.message}`));
    $('custom-highlight').addEventListener('change', () => {
        if (!state.selectedFile) return;
        if (state.connection) { state.connection.stop().then(() => tailFile(state.selectedFile)); }
        else if (state.lastSearchData) { searchContent(); }
        else { openFile(state.selectedFile); }
    });
    $('go-button').addEventListener('click', navigate); $('current-path').addEventListener('keydown', event => { if (event.key === 'Enter') navigate(); }); $('refresh-button').addEventListener('click', () => loadFolder(state.path)); $('reset-root').addEventListener('click', () => { $('current-path').value = ''; navigate(); }); $('search-input').addEventListener('input', renderFiles); $('logs-only').addEventListener('change', renderFiles); $('clear-button').addEventListener('click', () => renderOutput('')); $('search-button').addEventListener('click', () => searchContent().catch(error => { $('search-summary').textContent = error.message; })); $('content-search').addEventListener('keydown', event => { if (event.key === 'Enter') $('search-button').click(); }); $('theme-button').addEventListener('click', () => { const isDark = document.body.classList.toggle('dark'); localStorage.setItem('theme', isDark ? 'dark' : 'light'); }); $('word-wrap').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.remove('nowrap'); localStorage.setItem('wordWrap', 'true'); } else { $('log-viewer').classList.add('nowrap'); localStorage.setItem('wordWrap', 'false'); } }); $('show-lines').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.add('show-lines'); localStorage.setItem('showLines', 'true'); } else { $('log-viewer').classList.remove('show-lines'); localStorage.setItem('showLines', 'false'); } }); $('errors-only').addEventListener('change', (e) => { if (e.target.checked) { $('log-viewer').classList.add('errors-only-mode'); localStorage.setItem('errorsOnly', 'true'); } else { $('log-viewer').classList.remove('errors-only-mode'); localStorage.setItem('errorsOnly', 'false'); } }); $('copy-button').addEventListener('click', async () => { try { const text = Array.from($('log-viewer').querySelectorAll('.log-line')).map(el => el.lastElementChild.textContent).join('\n'); await navigator.clipboard.writeText(text); const btn = $('copy-button'); const old = btn.textContent; btn.textContent = 'Copied!'; setTimeout(() => btn.textContent = old, 2000); } catch(err) { alert('Failed to copy'); } }); $('export-search-button').addEventListener('click', () => { if (!state.lastSearchData) return; const text = state.lastSearchData.map(r => r.line).join('\n'); const blob = new Blob([text], { type: 'text/plain' }); const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `search_${Date.now()}.txt`; a.click(); URL.revokeObjectURL(url); }); $('format-json-button').addEventListener('click', () => { document.querySelectorAll('.log-line > span:last-child').forEach(el => { let text = el.textContent; let start = text.indexOf('{'); let end = text.lastIndexOf('}'); if (start !== -1 && end > start) { try { let parsed = JSON.parse(text.substring(start, end + 1)); el.textContent = text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); return; } catch(e) {} } start = text.indexOf('['); end = text.lastIndexOf(']'); if (start !== -1 && end > start) { try { let parsed = JSON.parse(text.substring(start, end + 1)); el.textContent = text.substring(0, start) + '\n' + JSON.stringify(parsed, null, 2) + '\n' + text.substring(end + 1); } catch(e) {} } }); }); $('pause-button').addEventListener('click', () => { state.paused = !state.paused; $('pause-button').textContent = state.paused ? 'Resume' : 'Pause'; });
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
    document.querySelectorAll('th[data-sort]').forEach(header => header.addEventListener('click', () => { state.ascending = state.sort === header.dataset.sort ? !state.ascending : true; state.sort = header.dataset.sort; renderFiles(); })); loadFolder().catch(error => renderOutput(`ERROR: ${error.message}`));
    checkUrlParams();
})();