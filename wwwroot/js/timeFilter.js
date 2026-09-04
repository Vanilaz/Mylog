import { $ } from './utils.js';

let activeStart = null;
let activeEnd = null;

// Matches HH:mm:ss or HH:mm inside log line, including bracketed [HH:mm:ss] or ISO T14:15
const timeRegex = /(?:[T \[\s]|^)(\d{1,2}):(\d{2})(?::(\d{2}))?/;

function parseToSeconds(timeStr) {
    if (!timeStr) return null;
    const str = timeStr.trim();
    // Support pure hour input (e.g. "14" -> 14:00:00)
    if (/^\d{1,2}$/.test(str)) {
        const hh = parseInt(str, 10);
        if (hh >= 0 && hh <= 24) return hh * 3600;
    }
    const m = str.match(timeRegex);
    if (!m) return null;
    const hh = parseInt(m[1], 10);
    const mm = parseInt(m[2], 10);
    const ss = m[3] ? parseInt(m[3], 10) : 0;
    return hh * 3600 + mm * 60 + ss;
}

export function initTimeFilter() {
    const toggleBtn = $('time-filter-toggle');
    const bar = $('time-filter-bar');
    const applyBtn = $('apply-time-filter');
    const resetBtn = $('reset-time-filter');
    const closeBtn = $('close-time-filter');

    if (toggleBtn && bar) {
        toggleBtn.addEventListener('click', () => {
            bar.hidden = !bar.hidden;
            toggleBtn.classList.toggle('active', !bar.hidden);
            if (!bar.hidden) $('time-start')?.focus();
        });
    }

    if (closeBtn && bar) {
        closeBtn.addEventListener('click', () => {
            bar.hidden = true;
            toggleBtn?.classList.remove('active');
        });
    }

    if (applyBtn) {
        applyBtn.addEventListener('click', applyTimeFilter);
    }

    if (resetBtn) {
        resetBtn.addEventListener('click', resetTimeFilter);
    }

    $('time-start')?.addEventListener('keydown', e => { if (e.key === 'Enter') applyTimeFilter(); });
    $('time-end')?.addEventListener('keydown', e => { if (e.key === 'Enter') applyTimeFilter(); });
}

export function applyTimeFilter() {
    const startStr = $('time-start')?.value.trim();
    const endStr = $('time-end')?.value.trim();

    const startSec = parseToSeconds(startStr);
    const endSec = parseToSeconds(endStr);
    const summaryEl = $('time-filter-summary');

    if (startSec === null && endSec === null) {
        resetTimeFilter();
        return;
    }

    activeStart = startSec;
    activeEnd = endSec;

    const viewer = $('log-viewer');
    const lines = viewer.querySelectorAll('.log-line:not(.divider)');
    let visibleCount = 0;
    let lastSeenTime = null;

    const visibilityMap = [];
    lines.forEach(el => {
        const text = el.textContent;
        const lineTime = parseToSeconds(text);
        if (lineTime !== null) {
            lastSeenTime = lineTime;
        }

        // Use line's own timestamp or inherited from previous line (stack traces)
        const effectiveTime = lineTime !== null ? lineTime : lastSeenTime;

        let inRange = true;
        if (effectiveTime !== null) {
            if (activeStart !== null && activeEnd !== null) {
                if (activeStart <= activeEnd) {
                    // Normal range: e.g. 08:00 to 17:00
                    inRange = effectiveTime >= activeStart && effectiveTime <= activeEnd;
                } else {
                    // Midnight wrap-around: e.g. 23:00 to 01:00
                    inRange = effectiveTime >= activeStart || effectiveTime <= activeEnd;
                }
            } else {
                if (activeStart !== null && effectiveTime < activeStart) inRange = false;
                if (activeEnd !== null && effectiveTime > activeEnd) inRange = false;
            }
        }

        visibilityMap.push(inRange);
        if (inRange) visibleCount++;
    });

    lines.forEach((el, idx) => {
        el.classList.toggle('time-hidden', !visibilityMap[idx]);
    });

    if (summaryEl) {
        summaryEl.textContent = `Showing ${visibleCount.toLocaleString()} of ${lines.length.toLocaleString()} lines`;
    }
}

export function resetTimeFilter() {
    activeStart = null;
    activeEnd = null;
    if ($('time-start')) $('time-start').value = '';
    if ($('time-end')) $('time-end').value = '';
    if ($('time-filter-summary')) $('time-filter-summary').textContent = '';

    const viewer = $('log-viewer');
    viewer.querySelectorAll('.log-line.time-hidden').forEach(el => {
        el.classList.remove('time-hidden');
    });
}