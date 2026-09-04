import { $, escapeHtml } from './utils.js';
import { state } from './state.js';
import { highlightLine } from './viewer.js';

const bookmarks = new Map();

export function initBookmarks() {
    const toggleBtn = $('bookmarks-toggle');
    const panel = $('bookmarks-panel');
    const closeBtn = $('close-bookmarks-btn');
    const copyBtn = $('copy-bookmarks-btn');
    const clearBtn = $('clear-bookmarks-btn');

    if (toggleBtn && panel) {
        toggleBtn.addEventListener('click', () => {
            panel.hidden = !panel.hidden;
            toggleBtn.classList.toggle('active', !panel.hidden);
        });
    }

    if (closeBtn && panel) {
        closeBtn.addEventListener('click', () => {
            panel.hidden = true;
            toggleBtn?.classList.remove('active');
        });
    }

    if (copyBtn) {
        copyBtn.addEventListener('click', copyAllBookmarks);
    }

    if (clearBtn) {
        clearBtn.addEventListener('click', clearBookmarks);
    }

    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.bookmark-btn');
        if (!btn) return;
        const lineEl = btn.closest('.log-line');
        if (!lineEl) return;
        const lineNum = parseInt(btn.dataset.line, 10);
        if (isNaN(lineNum)) return;

        const text = lineEl.textContent.replace(btn.textContent, '').trim();
        const severity = lineEl.querySelector('.error') ? 'error'
                       : lineEl.querySelector('.warn') ? 'warn'
                       : lineEl.querySelector('.info') ? 'info' : '';

        toggleBookmark(lineNum, text, severity);
    });
}

export function isBookmarked(lineNum) {
    return bookmarks.has(lineNum);
}

export function toggleBookmark(lineNum, text, severity) {
    const lineEl = document.getElementById(`L${lineNum}`);
    const btn = lineEl?.querySelector('.bookmark-btn');

    if (bookmarks.has(lineNum)) {
        bookmarks.delete(lineNum);
        if (btn) {
            btn.textContent = '☆';
            btn.title = 'Bookmark this line';
        }
        lineEl?.classList.remove('bookmarked');
    } else {
        bookmarks.set(lineNum, { lineNumber: lineNum, text: text || '', severity: severity || '' });
        if (btn) {
            btn.textContent = '⭐';
            btn.title = 'Remove bookmark';
        }
        lineEl?.classList.add('bookmarked');
    }

    updateUI();
}

export function clearBookmarks() {
    bookmarks.clear();
    document.querySelectorAll('.bookmark-btn').forEach(btn => {
        btn.textContent = '☆';
        btn.title = 'Bookmark this line';
    });
    document.querySelectorAll('.log-line.bookmarked').forEach(el => {
        el.classList.remove('bookmarked');
    });
    updateUI();
}

export function resetBookmarksForNewFile() {
    clearBookmarks();
}

function updateUI() {
    const toggleBtn = $('bookmarks-toggle');
    const countBadge = $('bookmarks-count');
    const count = bookmarks.size;

    if (toggleBtn) {
        toggleBtn.textContent = `⭐ Bookmarks (${count})`;
    }
    if (countBadge) {
        countBadge.textContent = count;
    }
    const mobileBadge = $('mobile-bookmarks-badge');
    if (mobileBadge) {
        if (count > 0) {
            mobileBadge.hidden = false;
            mobileBadge.textContent = count > 99 ? '99+' : count;
        } else {
            mobileBadge.hidden = true;
        }
    }

    const listEl = $('bookmarks-list');
    if (!listEl) return;

    if (count === 0) {
        listEl.innerHTML = '<span class="empty-hint" style="padding: 12px; display: block;">No bookmarks yet. Click ☆ next to any line number to pin it.</span>';
        return;
    }

    const sorted = Array.from(bookmarks.values()).sort((a, b) => a.lineNumber - b.lineNumber);

    listEl.innerHTML = sorted.map(item => `
        <div class="bookmark-item ${item.severity ? 'bm-' + item.severity : ''}" data-line="${item.lineNumber}">
            <div class="bookmark-item-header">
                <span class="bm-line-badge">Line ${item.lineNumber}</span>
                ${item.severity ? `<span class="bm-sev-pill ${item.severity}">${item.severity.toUpperCase()}</span>` : ''}
                <button type="button" class="bm-remove-btn" data-line="${item.lineNumber}" title="Remove bookmark">✕</button>
            </div>
            <div class="bookmark-snippet">${escapeHtml(item.text.length > 140 ? item.text.slice(0, 140) + '...' : item.text)}</div>
        </div>
    `).join('');

    listEl.querySelectorAll('.bookmark-item').forEach(el => {
        el.addEventListener('click', (e) => {
            if (e.target.closest('.bm-remove-btn')) return;
            const lineNum = el.dataset.line;
            highlightLine(lineNum);
            document.getElementById(`L${lineNum}`)?.scrollIntoView({ block: 'center' });
        });
    });

    listEl.querySelectorAll('.bm-remove-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            const lineNum = parseInt(btn.dataset.line, 10);
            toggleBookmark(lineNum);
        });
    });
}

function copyAllBookmarks() {
    if (bookmarks.size === 0) return;

    const sorted = Array.from(bookmarks.values()).sort((a, b) => a.lineNumber - b.lineNumber);
    const summary = `### 🔖 Log Bookmarks Summary\n`
                  + `File: ${state.selectedFile || 'Unknown'}\n`
                  + `Total Bookmarks: ${sorted.length}\n\n`
                  + sorted.map(b => `- **Line ${b.lineNumber}** ${b.severity ? `[${b.severity.toUpperCase()}] ` : ''}: ${b.text}`).join('\n\n');

    navigator.clipboard.writeText(summary).then(() => {
        const btn = $('copy-bookmarks-btn');
        if (btn) {
            const orig = btn.textContent;
            btn.textContent = '✅ Copied!';
            setTimeout(() => { btn.textContent = orig; }, 1800);
        }
    }).catch(err => {
        console.error('Failed to copy bookmarks:', err);
    });
}
