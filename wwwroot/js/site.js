(() => {
    'use strict';

    const $ = id => document.getElementById(id);
    const api = path => `${window.appBasePath || ''}${path}`;

    const MAX_VIEWER_LINES = 3000;
    const MAX_LINE_CHARS = 15000;

    const state = {
        path: null,
        selectedFile: null,
        files: [],
        folderCache: {},
        sort: 'name',
        ascending: true,
        connection: null,
        tailing: false,
        paused: false,
        lineCount: 0,
        lastSearchData: null,
        openToken: 0,
        fontSize: 13
    };

    /* ------------------------------------------------------------ storage --- */
    // localStorage throws in some privacy modes; never let a preference read break boot.
    const store = {
        get(key) { try { return localStorage.getItem(key); } catch { return null; } },
        set(key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } }
    };

    /* -------------------------------------------------------------- utils --- */
    const escapeRegExp = string => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const escapeHtml = text => String(text).replace(/[&<>"']/g, m =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[m]));

    const formatSize = bytes =>
        bytes < 1024 ? `${bytes} B`
        : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB`
        : bytes < 1073741824 ? `${(bytes / 1048576).toFixed(1)} MB`
        : `${(bytes / 1073741824).toFixed(2)} GB`;

    const formatDate = value => {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '-';
        const pad = n => String(n).padStart(2, '0');
        return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} `
             + `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
    };

    const classify = line => {
        const match = line.match(/\b(ERROR|FATAL|ERR|FTL|WARN|WARNING|WRN|INFO|INF)\b/);
        if (match) {
            const level = match[1];
            if (['ERROR', 'FATAL', 'ERR', 'FTL'].includes(level)) return 'error';
            if (['WARN', 'WARNING', 'WRN'].includes(level)) return 'warn';
            return 'info';
        }
        if (/\b(EXCEPTION|FAIL)\b/i.test(line)) return 'error';
        return '';
    };

    // Read an error message out of a failed response without assuming it is JSON.
    async function responseError(response, fallback) {
        try {
            const data = await response.clone().json();
            if (data && data.error) return new Error(data.error);
        } catch { /* not JSON */ }
        return new Error(`${fallback} (HTTP ${response.status})`);
    }

    /* --------------------------------------------------------- highlights --- */
    let customRe = null;

    function updateCustomRe() {
        const keywords = $('custom-highlight').value.split(',').map(k => k.trim()).filter(Boolean);
        customRe = keywords.length ? new RegExp(`(${keywords.map(escapeRegExp).join('|')})`, 'gi') : null;
    }

    // Splits raw text into {text, mark} chunks. Everything is escaped exactly once,
    // at render time, so a keyword like "&" can never match inside an "&amp;" entity.
    function splitByRegex(text, regex) {
        if (!regex) return [{ text, mark: false }];
        const chunks = [];
        let lastIndex = 0;
        regex.lastIndex = 0;
        let match;
        while ((match = regex.exec(text)) !== null) {
            if (match.index > lastIndex) chunks.push({ text: text.slice(lastIndex, match.index), mark: false });
            chunks.push({ text: match[0], mark: true });
            lastIndex = match.index + match[0].length;
            if (match[0].length === 0) regex.lastIndex++; // guard against zero-width loops
        }
        if (lastIndex < text.length) chunks.push({ text: text.slice(lastIndex), mark: false });
        return chunks;
    }

    const renderCustom = text =>
        splitByRegex(text, customRe)
            .map(c => c.mark ? `<span class="custom-hl">${escapeHtml(c.text)}</span>` : escapeHtml(c.text))
            .join('');

    function highlightMatches(line, query, regexMode) {
        let searchRe = null;
        if (query) {
            try { searchRe = new RegExp(regexMode ? query : escapeRegExp(query), 'gi'); }
            catch { searchRe = null; }
        }
        if (!searchRe) return renderCustom(line);
        return splitByRegex(line, searchRe)
            .map(c => c.mark ? `<mark>${renderCustom(c.text)}</mark>` : renderCustom(c.text))
            .join('');
    }

    const tryFormatJson = text => {
        if (!$('auto-format-json').checked) return text;
        for (const [open, close] of [['{', '}'], ['[', ']']]) {
            const start = text.indexOf(open);
            const end = text.lastIndexOf(close);
            if (start === -1 || end <= start) continue;
            const chunk = text.slice(start, end + 1);
            if (!chunk.includes('"')) continue;
            try {
                const parsed = JSON.parse(chunk);
                return `${text.slice(0, start)}\n${JSON.stringify(parsed, null, 2)}\n${text.slice(end + 1)}`;
            } catch { /* not valid JSON - leave the line alone */ }
        }
        return text;
    };

    /* ------------------------------------------------------------- render --- */
    function renderLine(lineNumber, text, extraClass = '') {
        const body = text.length > MAX_LINE_CHARS
            ? `${text.slice(0, MAX_LINE_CHARS)} ... [LINE TRUNCATED FOR PERFORMANCE. DOWNLOAD TO VIEW FULL]`
            : text;
        return `<span class="log-line ${extraClass}" id="L${lineNumber}">`
             + `<a href="#L${lineNumber}" class="line-number" data-line="${lineNumber}">${lineNumber}</a>`
             + `<span class="${classify(body)}">${renderCustom(tryFormatJson(body))}</span>`
             + '</span>';
    }

    function renderOutput(content, append = false) {
        const viewer = $('log-viewer');
        if (!append) {
            viewer.textContent = '';
            state.lineCount = 0;
            state.lastSearchData = null;
            $('export-search-button').hidden = true;
        }
        if (!content) return;

        const lines = content.split('\n');
        if (lines.length && lines[lines.length - 1] === '') lines.pop();
        if (!lines.length) return;

        const html = lines.map(line => renderLine(++state.lineCount, line)).join('\n');
        const hasExisting = viewer.childElementCount > 0;
        viewer.insertAdjacentHTML('beforeend', append && hasExisting ? `\n${html}` : html);

        if (append) {
            while (viewer.childElementCount > MAX_VIEWER_LINES) viewer.removeChild(viewer.firstElementChild);
        }
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
    }

    function renderFiles() {
        const query = $('search-input').value.toLowerCase();
        const logsOnly = $('logs-only').checked;
        const visible = state.files.filter(file =>
            (!query || file.name.toLowerCase().includes(query)) &&
            (!logsOnly || (file.extension || '').toLowerCase() === '.log'));

        const key = state.sort;
        visible.sort((a, b) => {
            let result;
            if (key === 'size') result = a.size - b.size;
            else if (key === 'modified') result = new Date(a.lastModified) - new Date(b.lastModified);
            else result = a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
            return result * (state.ascending ? 1 : -1);
        });

        $('file-list').innerHTML = visible.map(file => {
            const path = escapeHtml(file.path);
            return '<tr>'
                 + `<td><span class="file-name">${escapeHtml(file.name)}</span></td>`
                 + `<td class="file-meta" data-label="Size">${formatSize(file.size)}</td>`
                 + `<td class="file-meta" data-label="Modified">${formatDate(file.lastModified)}</td>`
                 + `<td class="file-meta" data-label="Type">${escapeHtml(file.extension || 'file')}</td>`
                 + '<td class="actions"><span class="row-actions">'
                 + `<button class="text-button" data-action="open" data-path="${path}">Open</button>`
                 + `<button class="text-button" data-action="tail" data-path="${path}">Tail</button>`
                 + `<a class="text-button" href="${api('/api/files/download?path=')}${encodeURIComponent(file.path)}" download>Download</a>`
                 + '</span></td></tr>';
        }).join('');

        $('empty-state').hidden = visible.length > 0;
        $('empty-state').textContent = state.files.length
            ? 'No files match the current filter.'
            : 'Choose a folder to inspect its files.';
    }

    function updateSortIndicators() {
        document.querySelectorAll('th[data-sort]').forEach(header => {
            const active = header.dataset.sort === state.sort;
            header.setAttribute('aria-sort', active ? (state.ascending ? 'ascending' : 'descending') : 'none');
            const mark = header.querySelector('.sort-mark');
            if (mark) mark.textContent = active ? (state.ascending ? '▲' : '▼') : '';
        });
    }

    /* ------------------------------------------------------------ loading --- */
    // A single overlay drives every busy state. The previous version wrote skeleton
    // markup into the panels and then blindly replaced any leftover skeleton with
    // "Failed to load.", which clobbered successful renders.
    let pending = 0;

    function showLoading(text) {
        pending++;
        $('loading-text').textContent = text;
        $('loading-overlay').hidden = false;
        $('loading-overlay').classList.remove('hidden');
    }

    function hideLoading() {
        pending = Math.max(0, pending - 1);
        if (pending === 0) $('loading-overlay').classList.add('hidden');
    }

    function showFilesError(message) {
        $('file-list').innerHTML = `<tr><td colspan="5" class="error-row">${escapeHtml(message)}</td></tr>`;
        $('empty-state').hidden = true;
    }

    function showPanel(which) {
        $('file-panel').hidden = which !== 'files';
        $('viewer-panel').hidden = which !== 'viewer';
    }

    /* --------------------------------------------------------------- tree --- */
    async function initTree() {
        try {
            // /roots lists the configured roots themselves. Listing without a path only
            // ever returned the *contents of the first root*, so every other configured
            // root was unreachable from the sidebar.
            const response = await fetch(api('/api/files/roots'));
            if (!response.ok) throw await responseError(response, 'Unable to load roots.');
            const roots = await response.json();
            $('root-count').textContent = roots.length;
            $('folder-tree').textContent = '';
            renderTreeNodes($('folder-tree'), roots);
        } catch (error) {
            console.error('Failed to init tree', error);
            $('folder-tree').innerHTML = `<p class="tree-empty">${escapeHtml(error.message)}</p>`;
        }
    }

    function renderTreeNodes(container, folders) {
        if (!folders || !folders.length) return;
        const level = document.createElement('div');
        level.className = 'tree-level';
        folders.forEach(folder => {
            const node = document.createElement('div');
            node.className = 'tree-node';
            node.innerHTML =
                '<div class="tree-row">'
              + `<button type="button" class="tree-toggle" data-tree-path="${escapeHtml(folder.path)}" `
              + `data-folder-name="${escapeHtml(folder.name)}" `
              + `aria-expanded="false" aria-label="Expand ${escapeHtml(folder.name)}">&#9654;</button>`
              + `<button type="button" class="tree-label" data-folder="${escapeHtml(folder.path)}" `
              + `title="${escapeHtml(folder.path)}">${escapeHtml(folder.name)}</button>`
              + '</div>'
              + '<div class="tree-children" hidden></div>';
            level.appendChild(node);
        });
        container.appendChild(level);
    }

    function setToggleState(toggle, expanded) {
        const name = toggle.dataset.folderName || 'folder';
        toggle.setAttribute('aria-expanded', String(expanded));
        toggle.setAttribute('aria-label', `${expanded ? 'Collapse' : 'Expand'} ${name}`);
        toggle.innerHTML = expanded ? '&#9660;' : '&#9654;';
    }

    document.addEventListener('click', async event => {
        const toggle = event.target.closest('.tree-toggle');
        if (!toggle) return;

        // Re-entrancy guard, set synchronously and held across the await below.
        // Clicking twice while the listing was in flight used to start a second
        // fetch and append a second copy of every child, duplicating the menu.
        if (toggle.dataset.busy === 'true') return;

        const children = toggle.closest('.tree-node').querySelector('.tree-children');

        if (toggle.getAttribute('aria-expanded') === 'true') {
            setToggleState(toggle, false);
            children.hidden = true;
            return;
        }

        if (toggle.dataset.loaded !== 'true') {
            toggle.dataset.busy = 'true';
            toggle.innerHTML = '&#8987;';
            try {
                const response = await fetch(`${api('/api/files/list?path=')}${encodeURIComponent(toggle.dataset.treePath)}`);
                if (!response.ok) throw await responseError(response, 'Unable to expand folder.');
                const data = await response.json();
                // Replace rather than append, so a retry can never stack results.
                children.replaceChildren();
                if (data.folders.length) renderTreeNodes(children, data.folders);
                else children.innerHTML = '<p class="tree-empty">(No subfolders)</p>';
                toggle.dataset.loaded = 'true';
            } catch (error) {
                // Leave it unloaded so the next click retries.
                children.replaceChildren();
                children.innerHTML = `<p class="tree-empty">${escapeHtml(error.message)}</p>`;
            } finally {
                toggle.dataset.busy = 'false';
            }
        }

        setToggleState(toggle, true);
        children.hidden = false;
    });

    /* ------------------------------------------------------------ actions --- */
    async function loadFolder(path, { force = false } = {}) {
        showPanel('files');
        const key = path || '';

        const fetchAndRender = async () => {
            const response = await fetch(path ? `${api('/api/files/list?path=')}${encodeURIComponent(path)}` : api('/api/files/list'));
            if (!response.ok) throw await responseError(response, 'Unable to load folder.');
            const data = await response.json();
            state.folderCache[key] = data;
            state.path = data.currentPath;
            state.files = data.files;
            $('current-path').value = data.currentPath;
            renderFiles();
        };

        // Serve the cache instantly, then revalidate — but "Refresh" must always
        // go to the server, otherwise the button visibly does nothing.
        if (!force && state.folderCache[key]) {
            const data = state.folderCache[key];
            state.path = data.currentPath;
            state.files = data.files;
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

    async function stopTail() {
        state.tailing = false;
        if (!state.connection) return;
        try { await state.connection.stop(); } catch { /* already closed */ }
        state.connection = null;
    }

    async function openFile(path) {
        const token = ++state.openToken;
        showPanel('viewer');
        showLoading('OPENING FILE...');
        try {
            await stopTail();
            state.selectedFile = path;
            const response = await fetch(`${api('/api/files/content?path=')}${encodeURIComponent(path)}`);
            if (!response.ok) throw await responseError(response, 'Unable to open file.');
            const data = await response.json();
            if (token !== state.openToken) return; // a newer open/tail superseded this one
            $('viewer-title').textContent = data.fileName;
            renderOutput(data.content);
            setStatus(data.isTruncated ? 'PARTIAL READ' : 'READY', false);
            $('pause-button').disabled = true;
        } finally {
            hideLoading();
        }
    }

    async function tailFile(path) {
        const token = ++state.openToken;
        showPanel('viewer');
        showLoading('CONNECTING LIVE TAIL...');
        try {
            if (typeof signalR === 'undefined') {
                throw new Error('SignalR client could not load. Check network access and refresh the page.');
            }
            await stopTail();
            state.selectedFile = path;
            $('viewer-title').textContent = path.split(/[\\/]/).pop();

            // Clear BEFORE subscribing. The old code cleared afterwards, which raced the
            // server's initial tail payload and silently wiped the first lines.
            renderOutput('');

            const connection = new signalR.HubConnectionBuilder()
                .withUrl(api('/hubs/log'))
                .withAutomaticReconnect()
                .build();
            connection.on('LogAppended', text => {
                if (!state.paused && state.connection === connection) renderOutput(text, true);
            });
            connection.onreconnecting(() => setStatus('RECONNECTING', false));
            connection.onreconnected(async () => {
                try { await connection.invoke('Subscribe', path); setStatus('LIVE', true); }
                catch { setStatus('OFFLINE', false); }
            });
            connection.onclose(() => { if (state.connection === connection) setStatus('OFFLINE', false); });

            await connection.start();
            if (token !== state.openToken) { try { await connection.stop(); } catch { /* ignore */ } return; }
            state.connection = connection;
            await connection.invoke('Subscribe', path);

            state.tailing = true;
            state.paused = false;
            setStatus('LIVE', true);
            $('pause-button').disabled = false;
            $('pause-button').textContent = 'Pause';
        } finally {
            hideLoading();
        }
    }

    function setStatus(text, live) {
        $('connection-status').textContent = text;
        $('connection-status').classList.toggle('live', !!live);
    }

    async function searchContent() {
        if (!state.selectedFile) throw new Error('Open or tail a file before searching.');
        const query = $('content-search').value;

        if (!query) {
            $('search-summary').textContent = '';
            state.lastSearchData = null;
            if (state.tailing) await tailFile(state.selectedFile);
            else await openFile(state.selectedFile);
            return;
        }

        showLoading('SEARCHING LOGS...');
        try {
            const regex = $('search-mode').value === 'regex';
            const context = $('search-context').value;
            const url = `${api('/api/files/search')}?path=${encodeURIComponent(state.selectedFile)}`
                      + `&query=${encodeURIComponent(query)}&regex=${regex}&context=${encodeURIComponent(context)}`;
            const response = await fetch(url);
            if (!response.ok) throw await responseError(response, 'Unable to search file content.');
            const data = await response.json();

            const matchCount = data.filter(row => row.isMatch).length;
            $('search-summary').textContent = `${matchCount} match${matchCount === 1 ? '' : 'es'}`;

            const viewer = $('log-viewer');
            if (!data.length) {
                viewer.textContent = 'No matching lines.';
            } else {
                viewer.innerHTML = data.map((row, index) => {
                    const gap = index > 0 && row.lineNumber > data[index - 1].lineNumber + 1
                        ? '<span class="log-line divider">--- gap ---</span>\n'
                        : '';
                    const body = row.isMatch
                        ? highlightMatches(row.line, query, regex)
                        : renderCustom(row.line);
                    return `${gap}<span class="log-line ${row.isMatch ? '' : 'context-line'}" id="L${row.lineNumber}">`
                         + `<a href="#L${row.lineNumber}" class="line-number" data-line="${row.lineNumber}">${row.lineNumber}</a>`
                         + `<span class="${classify(row.line)}">${body}</span></span>`;
                }).join('\n');
            }

            state.lastSearchData = data;
            $('export-search-button').hidden = !data.length;
        } finally {
            hideLoading();
        }
    }

    const reportError = error => {
        console.error(error);
        renderOutput(`ERROR: ${error.message}`);
    };

    /* ------------------------------------------------------------- events --- */
    document.addEventListener('click', async event => {
        const lineLink = event.target.closest('.line-number');
        if (lineLink) {
            event.preventDefault();
            highlightLine(lineLink.dataset.line);
            return;
        }

        const target = event.target.closest('[data-folder], [data-action]');
        if (!target) return;
        try {
            if (target.dataset.folder) {
                document.querySelectorAll('.tree-label.active').forEach(el => el.classList.remove('active'));
                target.classList.add('active');
                if (drawerQuery.matches) setSidebar(false);
                await loadFolder(target.dataset.folder);
            } else if (target.dataset.action === 'open') {
                await openFile(target.dataset.path);
            } else if (target.dataset.action === 'tail') {
                await tailFile(target.dataset.path);
            }
        } catch (error) {
            reportError(error);
        }
    });

    function highlightLine(lineNumber) {
        document.querySelectorAll('.highlight-line').forEach(el => el.classList.remove('highlight-line'));
        const el = document.getElementById(`L${lineNumber}`);
        if (!el) return;
        el.classList.add('highlight-line');
        if (state.selectedFile) {
            history.replaceState(null, '', `?path=${encodeURIComponent(state.selectedFile)}#L${lineNumber}`);
        }
    }

    const navigate = () => loadFolder($('current-path').value.trim(), { force: true }).catch(reportError);

    $('go-button').addEventListener('click', navigate);
    $('current-path').addEventListener('keydown', event => { if (event.key === 'Enter') navigate(); });
    $('refresh-button').addEventListener('click', () => loadFolder(state.path, { force: true }).catch(reportError));
    $('reset-root').addEventListener('click', () => { $('current-path').value = ''; navigate(); });
    $('search-input').addEventListener('input', renderFiles);
    $('logs-only').addEventListener('change', renderFiles);
    $('clear-button').addEventListener('click', () => renderOutput(''));
    $('search-button').addEventListener('click', () => {
        searchContent().catch(error => { $('search-summary').textContent = error.message; });
    });
    $('content-search').addEventListener('keydown', event => { if (event.key === 'Enter') $('search-button').click(); });

    $('theme-button').addEventListener('click', () => {
        const dark = document.documentElement.classList.toggle('dark');
        store.set('theme', dark ? 'dark' : 'light');
        $('theme-button').setAttribute('aria-pressed', String(dark));
    });

    // Below this width the sidebar is a fixed overlay drawer rather than a column.
    const drawerQuery = window.matchMedia('(max-width: 900px)');

    function setSidebar(open) {
        document.querySelector('.main-content').classList.toggle('sidebar-collapsed', !open);
        $('toggle-sidebar').setAttribute('aria-expanded', String(open));
        $('sidebar-backdrop').hidden = !(open && drawerQuery.matches);
    }

    const sidebarOpen = () => !document.querySelector('.main-content').classList.contains('sidebar-collapsed');

    $('toggle-sidebar').addEventListener('click', () => setSidebar(!sidebarOpen()));
    $('sidebar-backdrop').addEventListener('click', () => setSidebar(false));

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && drawerQuery.matches && sidebarOpen()) setSidebar(false);
    });

    // Crossing the breakpoint: the drawer should not be left open over the content,
    // and the desktop column should not stay hidden just because the drawer was shut.
    drawerQuery.addEventListener('change', event => setSidebar(!event.matches));

    $('custom-highlight').addEventListener('change', () => {
        updateCustomRe();
        if (!state.selectedFile) return;
        if (state.lastSearchData) searchContent().catch(reportError);
        else if (state.tailing) tailFile(state.selectedFile).catch(reportError);
        else openFile(state.selectedFile).catch(reportError);
    });

    $('auto-format-json').addEventListener('change', event => {
        store.set('autoFormatJson', event.target.checked);
        if (event.target.checked && $('log-viewer').childElementCount) $('format-json-button').click();
    });

    const bindToggle = (id, storageKey, className, target = () => $('log-viewer')) =>
        $(id).addEventListener('change', event => {
            store.set(storageKey, String(event.target.checked));
            target().classList.toggle(className, event.target.checked);
        });

    bindToggle('show-lines', 'showLines', 'show-lines');
    bindToggle('errors-only', 'errorsOnly', 'errors-only-mode');
    $('word-wrap').addEventListener('change', event => {
        store.set('wordWrap', String(event.target.checked));
        $('log-viewer').classList.toggle('nowrap', !event.target.checked);
    });

    $('copy-button').addEventListener('click', async () => {
        const button = $('copy-button');
        try {
            // Skip gap dividers: they have no child element, and the old
            // `lastElementChild.textContent` threw on them, breaking Copy entirely.
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
        // Revoking synchronously can cancel the download in some browsers.
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    });

    $('format-json-button').addEventListener('click', () => {
        document.querySelectorAll('.log-line > span:last-child').forEach(el => {
            const text = el.textContent;
            for (const [open, close] of [['{', '}'], ['[', ']']]) {
                const start = text.indexOf(open);
                const end = text.lastIndexOf(close);
                if (start === -1 || end <= start) continue;
                try {
                    const parsed = JSON.parse(text.slice(start, end + 1));
                    el.textContent = `${text.slice(0, start)}\n${JSON.stringify(parsed, null, 2)}\n${text.slice(end + 1)}`;
                    return;
                } catch { /* not JSON */ }
            }
        });
    });

    $('close-file-button').addEventListener('click', async () => {
        state.openToken++;
        await stopTail();
        state.selectedFile = null;
        state.lastSearchData = null;
        $('content-search').value = '';
        $('search-summary').textContent = '';
        setStatus('OFFLINE', false);
        showPanel('files');
        history.replaceState(null, '', window.location.pathname);
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

    /* ---------------------------------------------------------- bootstrap --- */
    function restorePreferences() {
        const storedSize = parseInt(store.get('fontSize'), 10);
        state.fontSize = Number.isFinite(storedSize) ? Math.min(28, Math.max(8, storedSize)) : 13;
        applyFontSize();

        $('theme-button').setAttribute('aria-pressed', String(document.documentElement.classList.contains('dark')));

        // On a phone the drawer starts closed so the log gets the whole screen.
        setSidebar(!drawerQuery.matches);

        const restore = (id, storageKey, defaultOn) => {
            const stored = store.get(storageKey);
            const on = stored === null ? defaultOn : stored === 'true';
            $(id).checked = on;
            return on;
        };
        restore('auto-format-json', 'autoFormatJson', false);
        $('log-viewer').classList.toggle('show-lines', restore('show-lines', 'showLines', true));
        $('log-viewer').classList.toggle('errors-only-mode', restore('errors-only', 'errorsOnly', false));
        $('log-viewer').classList.toggle('nowrap', !restore('word-wrap', 'wordWrap', true));
    }

    async function boot() {
        restorePreferences();
        updateCustomRe();
        updateSortIndicators();

        const deepLink = new URLSearchParams(window.location.search).get('path');

        // Await the folder load so it cannot race the deep link and flip the panel
        // back to the browser after the file has already opened.
        await loadFolder().catch(reportError);
        initTree();

        if (!deepLink) return;
        try {
            await openFile(deepLink);
        } catch (error) {
            reportError(error);
            return;
        }

        // Jumping to the anchor is a nicety; never let it discard a file that loaded.
        try {
            if (window.location.hash.startsWith('#L')) {
                const lineNumber = window.location.hash.slice(2);
                highlightLine(lineNumber);
                document.getElementById(`L${lineNumber}`)?.scrollIntoView?.({ block: 'center' });
            }
        } catch (error) {
            console.error('Could not scroll to the linked line', error);
        }
    }

    boot();
})();
