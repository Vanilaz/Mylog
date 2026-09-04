import { $, escapeHtml, escapeRegExp } from './utils.js';
import { state } from './state.js';
import { classify } from './format.js';

const MAX_VIEWER_LINES = 50000;

let currentRenderToken = 0;

export function cancelPendingRenders() {
    currentRenderToken++;
}

export let customRe = null;

export function updateCustomRe() {
    const keywords = $('custom-highlight').value.split(',').map(k => k.trim()).filter(Boolean);
    customRe = keywords.length ? new RegExp(`(${keywords.map(escapeRegExp).join('|')})`, 'gi') : null;
}

export function splitByRegex(text, regex) {
    if (!regex) return [{ text, mark: false }];
    const chunks = [];
    let lastIndex = 0;
    regex.lastIndex = 0;
    let match;
    while ((match = regex.exec(text)) !== null) {
        if (match.index > lastIndex) chunks.push({ text: text.slice(lastIndex, match.index), mark: false });
        if (match[0].length > 0) chunks.push({ text: match[0], mark: true });
        lastIndex = match.index + match[0].length;
        if (match[0].length === 0) {
            regex.lastIndex++;
            if (regex.lastIndex > text.length) break;
        }
    }
    if (lastIndex < text.length) chunks.push({ text: text.slice(lastIndex), mark: false });
    return chunks;
}

export const renderCustom = text =>
    splitByRegex(text, customRe)
        .map(c => c.mark ? `<span class="custom-hl">${escapeHtml(c.text)}</span>` : escapeHtml(c.text))
        .join('');

export function highlightMatches(line, query, regexMode) {
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

export function formatJsonString(text) {
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
}

let _autoFormatJson = false;
export const setAutoFormatJson = (v) => { _autoFormatJson = v; };
export const getAutoFormatJson = () => _autoFormatJson;

export const tryFormatJson = text => {
    if (!_autoFormatJson) return text;
    return formatJsonString(text);
};

export function renderChips(html) {
    if (!html || !html.includes('-')) return html;
    return html.replace(/\b([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b/g, (match) => {
        return `<span class="trace-chip" data-trace="${match}" title="Click to isolate transaction flow">${match}</span>`;
    });
}

let lastRenderedLevel = '';

function formatNodeChip(nodeName) {
    if (!nodeName) return '';
    let label = nodeName;
    let colorClass = 'node-default';

    if (/Node\s*1\b|node-0?1\b|LB-0?1\b|Server\s*A\b/i.test(nodeName)) {
        label = 'Node 1';
        colorClass = 'node-1';
    } else if (/Node\s*2\b|node-0?2\b|LB-0?2\b|Server\s*B\b/i.test(nodeName)) {
        label = 'Node 2';
        colorClass = 'node-2';
    } else if (/Node\s*3\b|node-0?3\b|LB-0?3\b|Server\s*C\b/i.test(nodeName)) {
        label = 'Node 3';
        colorClass = 'node-3';
    } else if (nodeName.length > 12) {
        label = nodeName.slice(0, 10) + '..';
    }

    return `<span class="node-badge ${colorClass}" data-node="${escapeHtml(nodeName)}" title="Server Instance: ${escapeHtml(nodeName)}">${escapeHtml(label)}</span>`;
}

export function renderLine(lineNumber, text, extraClass = '', nodeName = null, preRenderedContent = null) {
    const body = text;
    let levelClass = classify(body);

    if (!levelClass && (/^\s+at\s+/.test(body) || /^\s+---\s+/.test(body) || /^\s*Caused by:/.test(body))) {
        if (lastRenderedLevel === 'error') {
            levelClass = 'error stack-trace-line';
        }
    } else if (levelClass) {
        lastRenderedLevel = levelClass;
    } else if (/^\d{4}-\d{2}-\d{2}/.test(body) || /^\d{1,2}:\d{2}:\d{2}/.test(body)) {
        lastRenderedLevel = '';
    }

    let jsonAttrs = '';
    let bodyToRender = body;
    if ((body.includes('{') && body.includes('}')) || (body.includes('[') && body.includes(']'))) {
        const formatted = formatJsonString(body);
        if (formatted !== body) {
            const isExpanded = _autoFormatJson;
            jsonAttrs = ` data-raw="${escapeHtml(body)}" data-json-expanded="${isExpanded}"`;
            if (isExpanded) {
                bodyToRender = formatted;
            }
        }
    }

    const nodeChip = formatNodeChip(nodeName);
    const content = preRenderedContent !== null
        ? renderChips(preRenderedContent)
        : renderChips(renderCustom(bodyToRender));

    return `<span class="log-line ${levelClass} ${extraClass}" id="L${lineNumber}"${jsonAttrs}>`
         + `<span class="log-gutter">`
         + `<button type="button" class="bookmark-btn" data-line="${lineNumber}" title="Bookmark this line">☆</button>`
         + `<a href="#L${lineNumber}" class="line-number" data-line="${lineNumber}">${lineNumber}</a>`
         + `</span>`
         + `<span class="log-content ${levelClass}">${nodeChip}${content}</span>`
         + `</span>`;
}

export function toggleJsonFormatting(forceExpand = null, searchQuery = '', isRegex = false) {
    let linesWithJson = Array.from(document.querySelectorAll('.log-line[data-raw]'));

    if (!linesWithJson.length) {
        document.querySelectorAll('.log-line:not(.divider)').forEach(lineEl => {
            const contentEl = lineEl.querySelector('.log-content');
            const text = contentEl ? contentEl.textContent : '';
            if ((text.includes('{') && text.includes('}')) || (text.includes('[') && text.includes(']'))) {
                const formatted = formatJsonString(text);
                if (formatted !== text) {
                    lineEl.dataset.raw = text;
                    lineEl.dataset.jsonExpanded = text.includes('\n') ? 'true' : 'false';
                }
            }
        });
        linesWithJson = Array.from(document.querySelectorAll('.log-line[data-raw]'));
    }

    if (!linesWithJson.length) return false;

    const isAnyExpanded = linesWithJson.some(el => el.dataset.jsonExpanded === 'true');
    const shouldExpand = forceExpand !== null
        ? forceExpand
        : !isAnyExpanded;

    for (const lineEl of linesWithJson) {
        const raw = lineEl.dataset.raw;
        if (!raw) continue;

        const contentEl = lineEl.querySelector('.log-content');
        if (!contentEl) continue;

        lineEl.dataset.jsonExpanded = String(shouldExpand);

        const textToDisplay = shouldExpand ? formatJsonString(raw) : raw;

        const nodeBadge = contentEl.querySelector('.node-badge');
        const nodeChip = nodeBadge ? nodeBadge.outerHTML : '';

        const innerContent = searchQuery
            ? highlightMatches(textToDisplay, searchQuery, isRegex)
            : renderCustom(textToDisplay);

        contentEl.innerHTML = nodeChip + renderChips(innerContent);
    }

    updateFormatJsonButton(shouldExpand);
    return shouldExpand;
}

export function updateFormatJsonButton(isExpanded) {
    const btn = $('format-json-button');
    if (!btn) return;
    btn.textContent = isExpanded ? 'Collapse JSON' : 'Format JSON';
    btn.title = isExpanded ? 'Collapse JSON back to single line' : 'Format and expand inline JSON';
    btn.classList.toggle('active', !!isExpanded);
    btn.setAttribute('aria-pressed', String(!!isExpanded));
}

export function renderSearchResults(data, query, regexMode, options = {}) {
    const viewer = $('log-viewer');
    cancelPendingRenders();
    currentRenderToken++;
    const myToken = currentRenderToken;

    state.lastSearchData = data || [];
    lastRenderedLevel = '';
    updateFormatJsonButton(_autoFormatJson);

    if (!data || !data.length) {
        viewer.innerHTML = '<span class="log-line divider">No matching lines found.</span>';
        $('search-summary').textContent = '0 matches';
        $('export-search-button').hidden = true;
        return;
    }

    const matchCount = data.filter(r => r.isMatch !== false).length;
    const sourceLabel = options.source === 'cluster' ? ' across cluster' : '';
    $('search-summary').textContent = `${matchCount.toLocaleString()} match${matchCount === 1 ? '' : 'es'}${sourceLabel}`;
    $('export-search-button').hidden = false;

    function renderRow(row, index) {
        let gap = '';
        if (index > 0) {
            const prevRow = data[index - 1];
            if (row.nodeName && prevRow.nodeName && row.nodeName !== prevRow.nodeName) {
                gap = `<span class="log-line divider">--- Node: ${escapeHtml(row.nodeName)} ---</span>\n`;
            } else if (row.lineNumber > prevRow.lineNumber + 1) {
                gap = '<span class="log-line divider">--- gap ---</span>\n';
            }
        }
        const isMatch = row.isMatch !== false;
        const extraClass = isMatch ? '' : 'context-line';
        const lineText = _autoFormatJson ? formatJsonString(row.line) : row.line;
        const preRendered = isMatch
            ? highlightMatches(lineText, query, regexMode)
            : renderCustom(lineText);
        return gap + renderLine(row.lineNumber, row.line, extraClass, row.nodeName || null, preRendered);
    }

    const CHUNK_SIZE = 250;
    const initialLines = data.slice(0, CHUNK_SIZE);
    viewer.innerHTML = initialLines.map((row, idx) => renderRow(row, idx)).join('\n');
    viewer.scrollTop = 0;

    if (data.length <= CHUNK_SIZE) return;

    let currentIdx = CHUNK_SIZE;
    function renderNextSearchChunk() {
        if (myToken !== currentRenderToken) return;
        const chunk = data.slice(currentIdx, currentIdx + CHUNK_SIZE);
        if (!chunk.length) return;

        const html = chunk.map((row, idx) => renderRow(row, currentIdx + idx)).join('\n');
        viewer.insertAdjacentHTML('beforeend', '\n' + html);
        currentIdx += CHUNK_SIZE;
        if (currentIdx < data.length) {
            requestAnimationFrame(renderNextSearchChunk);
        }
    }

    requestAnimationFrame(renderNextSearchChunk);
}

export function renderClusterOutput(data) {
    const viewer = $('log-viewer');
    currentRenderToken++;
    viewer.textContent = '';
    state.lineCount = 0;
    state.errCount = 0;
    state.wrnCount = 0;
    state.lastSearchData = null;
    lastRenderedLevel = '';
    $('export-search-button').hidden = true;
    updateFormatJsonButton(_autoFormatJson);

    if (!data || !data.lines || !data.lines.length) {
        viewer.textContent = 'No log lines found in cluster.';
        return;
    }

    let truncationHeader = '';
    if (data.isTruncated) {
        truncationHeader = `<span class="log-line divider">--- Showing last ${data.lines.length.toLocaleString()} of ${data.totalLines.toLocaleString()} lines across cluster. Download cluster ZIP to view full logs. ---</span>\n`;
    }

    for (const item of data.lines) {
        const levelClass = classify(item.line);
        if (levelClass === 'error') state.errCount++;
        else if (levelClass === 'warn') state.wrnCount++;
    }

    function renderClusterItem(item, idx) {
        let gap = '';
        if (data.viewMode === 'concat' && data.isMergedView) {
            if (idx === 0) {
                gap = `<span class="log-line divider">=== Node: ${escapeHtml(item.nodeName)} ===</span>\n`;
            } else {
                const prev = data.lines[idx - 1];
                if (prev && prev.nodeName && item.nodeName !== prev.nodeName) {
                    gap = `<span class="log-line divider">=== Node: ${escapeHtml(item.nodeName)} ===</span>\n`;
                }
            }
        }
        const showNode = data.isMergedView ? item.nodeName : null;
        return gap + renderLine(idx + 1, item.line, '', showNode);
    }

    const myToken = currentRenderToken;
    const CHUNK_SIZE = 250;
    const initialLines = data.lines.slice(0, CHUNK_SIZE);
    const initialHtml = truncationHeader + initialLines.map((item, idx) => renderClusterItem(item, idx)).join('\n');

    viewer.insertAdjacentHTML('beforeend', initialHtml);
    state.lineCount = initialLines.length;

    if (data.lines.length <= CHUNK_SIZE) {
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
        return;
    }

    let currentIdx = CHUNK_SIZE;
    function renderNextClusterChunk() {
        if (myToken !== currentRenderToken) return;
        const chunk = data.lines.slice(currentIdx, currentIdx + CHUNK_SIZE);
        if (!chunk.length) {
            if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
            return;
        }
        const html = chunk.map((item, idx) => renderClusterItem(item, currentIdx + idx)).join('\n');
        viewer.insertAdjacentHTML('beforeend', `\n${html}`);
        state.lineCount += chunk.length;
        currentIdx += CHUNK_SIZE;
        if (currentIdx < data.lines.length) {
            requestAnimationFrame(renderNextClusterChunk);
        } else {
            if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
        }
    }

    requestAnimationFrame(renderNextClusterChunk);
}

export function renderOutput(content, append = false) {
    const viewer = $('log-viewer');
    if (!append) {
        currentRenderToken++;
        viewer.textContent = '';
        state.lineCount = 0;
        state.errCount = 0;
        state.wrnCount = 0;
        state.lastSearchData = null;
        lastRenderedLevel = '';
        $('export-search-button').hidden = true;
        updateFormatJsonButton(_autoFormatJson);
    }
    if (!content) return;

    const lines = content.split('\n');
    if (lines.length && lines[lines.length - 1] === '') lines.pop();
    if (!lines.length) return;

    if (append) {
        for (const line of lines) {
            const levelClass = classify(line);
            if (levelClass === 'error') state.errCount++;
            else if (levelClass === 'warn') state.wrnCount++;
        }
        const html = lines.map(line => renderLine(++state.lineCount, line)).join('\n');
        const hasExisting = viewer.childElementCount > 0;
        viewer.insertAdjacentHTML('beforeend', hasExisting ? `\n${html}` : html);

        while (viewer.childElementCount > MAX_VIEWER_LINES) {
            const evicted = viewer.firstElementChild;
            if (evicted && evicted.classList) {
                if (evicted.classList.contains('error')) state.errCount = Math.max(0, state.errCount - 1);
                else if (evicted.classList.contains('warn')) state.wrnCount = Math.max(0, state.wrnCount - 1);
            }
            viewer.removeChild(evicted);
        }
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
        return;
    }

    const myToken = currentRenderToken;
    let renderLines = lines;
    let truncationHeader = '';

    if (lines.length > MAX_VIEWER_LINES) {
        const startLineOffset = lines.length - MAX_VIEWER_LINES;
        renderLines = lines.slice(startLineOffset);
        state.lineCount = startLineOffset;
        truncationHeader = `<span class="log-line divider">--- Showing last ${MAX_VIEWER_LINES.toLocaleString()} of ${lines.length.toLocaleString()} lines. Download file to view all. ---</span>\n`;
    }

    // Count errors/warnings incrementally as we render
    for (const line of renderLines) {
        const levelClass = classify(line);
        if (levelClass === 'error') state.errCount++;
        else if (levelClass === 'warn') state.wrnCount++;
    }

    const CHUNK_SIZE = 250;
    const initialLines = renderLines.slice(0, CHUNK_SIZE);
    const initialHtml = truncationHeader + initialLines.map(line => renderLine(++state.lineCount, line)).join('\n');
    viewer.insertAdjacentHTML('beforeend', initialHtml);

    if (renderLines.length <= CHUNK_SIZE) {
        if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
        return;
    }

    let currentIdx = CHUNK_SIZE;
    function renderNextChunk() {
        if (myToken !== currentRenderToken) return;
        const chunk = renderLines.slice(currentIdx, currentIdx + CHUNK_SIZE);
        if (!chunk.length) {
            if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
            return;
        }
        const html = chunk.map(line => renderLine(++state.lineCount, line)).join('\n');
        viewer.insertAdjacentHTML('beforeend', `\n${html}`);
        currentIdx += CHUNK_SIZE;
        if (currentIdx < renderLines.length) {
            requestAnimationFrame(renderNextChunk);
        } else {
            if ($('auto-scroll').checked) viewer.scrollTop = viewer.scrollHeight;
        }
    }

    requestAnimationFrame(renderNextChunk);
}

export function highlightLine(lineNumber) {
    document.querySelectorAll('.highlight-line').forEach(el => el.classList.remove('highlight-line'));
    const el = document.getElementById(`L${lineNumber}`);
    if (!el) return;
    el.classList.add('highlight-line');
    if (state.selectedFile) {
        history.replaceState(null, '', `?path=${encodeURIComponent(state.selectedFile)}#L${lineNumber}`);
    }
}

export const reportError = error => {
    console.error(error);
    renderOutput(`ERROR: ${error.message}`);
};
