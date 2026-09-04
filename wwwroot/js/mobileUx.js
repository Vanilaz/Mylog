import { $ } from './utils.js';
import { state, store } from './state.js';

let selectedLineEl = null;
let currentInspectedLine = null;
let callbacks = {};

export function initMobileUx(options = {}) {
    callbacks = options;

    const errorsOnlyBtn = $('mobile-errors-only-btn');
    const ribbonErrBtn = $('mobile-ribbon-err');
    const searchBtn = $('mobile-search-btn');
    const bookmarksBtn = $('mobile-bookmarks-btn');
    const analyticsBtn = $('mobile-analytics-btn');
    const scrollBtn = $('mobile-scroll-btn');
    const filenameChip = $('mobile-filename-chip');

    const wrapBtn = $('mobile-wrap-btn');
    if (wrapBtn) {
        wrapBtn.addEventListener('click', toggleWordWrap);
    }

    // 1. Errors Only Toggle (from bottom pill or stats ribbon)
    if (errorsOnlyBtn) {
        errorsOnlyBtn.addEventListener('click', toggleErrorsOnly);
    }
    if (ribbonErrBtn) {
        ribbonErrBtn.addEventListener('click', toggleErrorsOnly);
    }

    // 2. Quick Search Toggle
    if (searchBtn) {
        searchBtn.addEventListener('click', () => {
            const searchBar = document.querySelector('.content-search');
            const searchInput = $('content-search');
            if (searchBar) {
                const isOpen = searchBar.classList.toggle('mobile-open');
                searchBtn.classList.toggle('active', isOpen);
                if (isOpen && searchInput) {
                    searchInput.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    searchInput.focus();
                }
            }
        });
    }

    // 3. Filename Chip (Tap to return to file browser)
    if (filenameChip) {
        filenameChip.addEventListener('click', () => {
            const closeBtn = $('close-file-button');
            if (closeBtn) closeBtn.click();
        });
    }

    // 4. Bookmarks Drawer Button
    if (bookmarksBtn) {
        bookmarksBtn.addEventListener('click', () => {
            const panel = $('bookmarks-panel');
            if (panel) {
                panel.hidden = !panel.hidden;
                if (!panel.hidden) {
                    panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                }
            }
        });
    }

    // 5. Analytics Modal Button
    if (analyticsBtn) {
        analyticsBtn.addEventListener('click', () => {
            const dashboard = $('analytics-dashboard');
            if (dashboard) {
                dashboard.hidden = !dashboard.hidden;
                if (!dashboard.hidden) {
                    dashboard.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                }
            }
        });
    }

    // 6. Jump to Bottom / Live Auto-scroll
    if (scrollBtn) {
        scrollBtn.addEventListener('click', () => {
            const viewer = $('log-viewer');
            if (viewer) {
                viewer.scrollTo({ top: viewer.scrollHeight, behavior: 'smooth' });
            }
        });
    }

    // 7. Tap-to-Inspect Listener on Log Viewer
    const viewer = $('log-viewer');
    if (viewer) {
        viewer.addEventListener('click', handleViewerClick);
    }

    // 8. Inspector Sheet Close & Action Listeners
    const closeBtn = $('inspector-close-btn');
    const overlay = $('line-inspector-sheet');
    if (closeBtn) closeBtn.addEventListener('click', closeLineInspector);
    if (overlay) {
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeLineInspector();
        });
    }

    const copyLineBtn = $('inspector-copy-line-btn');
    if (copyLineBtn) {
        copyLineBtn.addEventListener('click', copyInspectedLine);
    }

    const bookmarkBtn = $('inspector-bookmark-btn');
    if (bookmarkBtn) {
        bookmarkBtn.addEventListener('click', toggleInspectedBookmark);
    }

    const trackFlowBtn = $('inspector-track-flow-btn');
    if (trackFlowBtn) {
        trackFlowBtn.addEventListener('click', () => {
            if (currentInspectedLine?.guid && callbacks.onTrackFlow) {
                closeLineInspector();
                callbacks.onTrackFlow(currentInspectedLine.guid);
            }
        });
    }

    const copyTokenBtn = $('inspector-copy-token-btn');
    if (copyTokenBtn) {
        copyTokenBtn.addEventListener('click', () => {
            if (currentInspectedLine?.guid) {
                navigator.clipboard.writeText(currentInspectedLine.guid).then(() => {
                    const orig = copyTokenBtn.textContent;
                    copyTokenBtn.textContent = '✅ Copied!';
                    setTimeout(() => { copyTokenBtn.textContent = orig; }, 1500);
                });
            }
        });
    }
}

export function toggleErrorsOnly() {
    const viewer = $('log-viewer');
    const btn = $('mobile-errors-only-btn');
    const ribbonBtn = $('mobile-ribbon-err');
    const optCheckbox = $('errors-only');

    if (!viewer) return;

    const isActive = viewer.classList.toggle('errors-only-active');
    viewer.classList.toggle('errors-only-mode', isActive);

    if (btn) btn.classList.toggle('active', isActive);
    if (ribbonBtn) ribbonBtn.classList.toggle('active', isActive);
    if (optCheckbox) {
        optCheckbox.checked = isActive;
        store.set('errorsOnly', isActive);
    }
    state.errorsOnly = isActive;

    if (isActive) {
        const firstError = viewer.querySelector('.log-line.error');
        if (firstError) {
            firstError.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }
}

export function syncErrorsOnly(isActive) {
    const viewer = $('log-viewer');
    const btn = $('mobile-errors-only-btn');
    const ribbonBtn = $('mobile-ribbon-err');
    if (viewer) {
        viewer.classList.toggle('errors-only-active', isActive);
        viewer.classList.toggle('errors-only-mode', isActive);
    }
    if (btn) btn.classList.toggle('active', isActive);
    if (ribbonBtn) ribbonBtn.classList.toggle('active', isActive);
}

export function toggleWordWrap() {
    const viewer = $('log-viewer');
    const btn = $('mobile-wrap-btn');
    const optCheckbox = $('word-wrap');

    if (!viewer) return;

    const isNowrap = viewer.classList.toggle('nowrap');
    const isWrapped = !isNowrap;

    if (btn) btn.classList.toggle('active', isWrapped);
    if (optCheckbox) optCheckbox.checked = isWrapped;
    store.set('wordWrap', String(isWrapped));
}

export function syncWordWrap(isWrapped) {
    const viewer = $('log-viewer');
    const btn = $('mobile-wrap-btn');
    if (viewer) {
        viewer.classList.toggle('nowrap', !isWrapped);
    }
    if (btn) btn.classList.toggle('active', isWrapped);
}

export function updateMobileStats(totalLines, errors, warns) {
    const errEl = $('mobile-err-count');
    const wrnEl = $('mobile-wrn-count');
    const totalEl = $('mobile-line-count');

    if (errEl) errEl.textContent = errors || 0;
    if (wrnEl) wrnEl.textContent = warns || 0;
    if (totalEl) totalEl.textContent = Number(totalLines || 0).toLocaleString();
}

export function updateMobileFileInfo(fileName, isLive = false) {
    const chip = $('mobile-filename-chip');
    const tag = $('mobile-status-tag');

    if (chip) chip.textContent = fileName || 'No file open';
    if (tag) {
        tag.textContent = isLive ? '🟢 LIVE TAIL' : '🟢 READY';
    }
}

export function updateMobileStatsFromViewer() {
    updateMobileStats(state.lineCount, state.errCount, state.wrnCount);
}

export function updateMobileBookmarkBadge(count) {
    const badge = $('mobile-bookmarks-badge');
    if (!badge) return;
    if (count > 0) {
        badge.hidden = false;
        badge.textContent = count > 99 ? '99+' : count;
    } else {
        badge.hidden = true;
    }
}

function handleViewerClick(event) {
    if (event.target.closest('.bookmark-btn') || event.target.closest('.trace-chip') || event.target.closest('.line-number')) {
        return;
    }

    const lineEl = event.target.closest('.log-line:not(.divider)');
    if (!lineEl) return;

    const isMobile = window.innerWidth <= 768 || window.matchMedia('(pointer: coarse)').matches;
    if (!isMobile) return;

    inspectLine(lineEl);
}

function inspectLine(lineEl) {
    if (selectedLineEl) {
        selectedLineEl.classList.remove('inspector-selected');
    }

    selectedLineEl = lineEl;
    lineEl.classList.add('inspector-selected');

    const lineNum = lineEl.querySelector('.line-number')?.dataset.line || lineEl.id.replace('L', '');
    const rawText = lineEl.textContent.replace(lineNum, '').trim();

    let level = 'INFO';
    const head = rawText.length > 2000 ? rawText.slice(0, 2000) : rawText;
    if (lineEl.classList.contains('error') || /\b(ERROR|FATAL|EXCEPTION|FAIL)\b/i.test(head)) {
        level = 'ERROR';
    } else if (lineEl.classList.contains('warn') || /\b(WARN|WARNING)\b/i.test(head)) {
        level = 'WARN';
    }

    const timeMatch = rawText.slice(0, 200).match(/\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[+-]\d{2}:?\d{2}|Z)?\b|\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\b/);
    const timestamp = timeMatch ? timeMatch[0] : '';

    const guidMatch = rawText.includes('-') ? rawText.match(/\b([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b/) : null;
    const guid = guidMatch ? guidMatch[1] : null;

    currentInspectedLine = {
        lineNum: parseInt(lineNum, 10),
        rawText,
        level,
        timestamp,
        guid,
        el: lineEl
    };

    const sheet = $('line-inspector-sheet');
    const badgeEl = $('inspector-badge');
    const lineNumEl = $('inspector-line-num');
    const timeEl = $('inspector-time');
    const tokenCard = $('inspector-token-card');
    const tokenVal = $('inspector-token-value');
    const bmarkBtn = $('inspector-bookmark-btn');

    if (badgeEl) {
        const icon = level === 'ERROR' ? '🚨' : level === 'WARN' ? '⚠️' : 'ℹ️';
        badgeEl.textContent = `${icon} ${level} [Line ${lineNum}]`;
        badgeEl.className = `inspector-badge ${level.toLowerCase()}`;
    }
    if (lineNumEl) lineNumEl.textContent = '';
    if (timeEl) timeEl.textContent = timestamp;

    if (guid && tokenCard && tokenVal) {
        tokenCard.hidden = false;
        tokenVal.textContent = guid;
    } else if (tokenCard) {
        tokenCard.hidden = true;
    }

    if (bmarkBtn) {
        const isBookmarked = lineEl.classList.contains('bookmarked');
        bmarkBtn.textContent = isBookmarked ? '⭐ Remove Bookmark' : '⭐ Add Bookmark';
    }

    if (sheet) sheet.hidden = false;
}

export function closeLineInspector() {
    const sheet = $('line-inspector-sheet');
    if (sheet) sheet.hidden = true;
    if (selectedLineEl) {
        selectedLineEl.classList.remove('inspector-selected');
        selectedLineEl = null;
    }
    currentInspectedLine = null;
}

function copyInspectedLine() {
    if (!currentInspectedLine) return;
    navigator.clipboard.writeText(currentInspectedLine.rawText).then(() => {
        const btn = $('inspector-copy-line-btn');
        if (btn) {
            const orig = btn.textContent;
            btn.textContent = '✅ Copied!';
            setTimeout(() => { btn.textContent = orig; }, 1500);
        }
    });
}

function toggleInspectedBookmark() {
    if (!currentInspectedLine) return;
    const btn = currentInspectedLine.el?.querySelector('.bookmark-btn');
    if (btn) {
        btn.click();
        const bmarkBtn = $('inspector-bookmark-btn');
        if (bmarkBtn) {
            const isBookmarked = currentInspectedLine.el.classList.contains('bookmarked');
            bmarkBtn.textContent = isBookmarked ? '⭐ Remove Bookmark' : '⭐ Add Bookmark';
        }
    }
}
