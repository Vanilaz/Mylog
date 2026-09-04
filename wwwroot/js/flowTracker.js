import { $, escapeHtml } from './utils.js';
import { state } from './state.js';

let activeTraceId = null;
let callbacks = {};

export function initFlowTracker(options = {}) {
    callbacks = options;

    const copyBtn = $('copy-flow-btn');
    const exitBtn = $('exit-flow-btn');

    if (copyBtn) {
        copyBtn.addEventListener('click', copyFlowToClipboard);
    }

    if (exitBtn) {
        exitBtn.addEventListener('click', exitFlow);
    }

    // Delegated click for trace chips in log viewer
    document.addEventListener('click', (event) => {
        const chip = event.target.closest('.trace-chip');
        if (!chip) return;
        const traceId = chip.dataset.trace;
        if (traceId) {
            isolateFlow(traceId);
        }
    });
}

export async function isolateFlow(traceId) {
    if (!traceId) return;
    activeTraceId = traceId;

    const banner = $('flow-banner');
    const traceEl = $('flow-trace-id');
    const countBadge = $('flow-count-badge');

    if (banner) banner.hidden = false;
    if (traceEl) traceEl.textContent = traceId;
    if (countBadge) countBadge.textContent = 'Tracing...';

    // Populate search and execute to get all matching steps across the file
    if (callbacks.onSearchFlow) {
        await callbacks.onSearchFlow(traceId);
    }

    // Update count badge after search completes
    setTimeout(() => {
        const viewer = $('log-viewer');
        const matchCount = viewer?.querySelectorAll('.log-line:not(.divider)').length || 0;
        if (countBadge) countBadge.textContent = `${matchCount} event${matchCount === 1 ? '' : 's'}`;
    }, 150);
}

export function resetFlowTracker() {
    activeTraceId = null;
    const banner = $('flow-banner');
    if (banner) banner.hidden = true;
    const traceEl = $('flow-trace-id');
    if (traceEl) traceEl.textContent = '';
    const countBadge = $('flow-count-badge');
    if (countBadge) countBadge.textContent = '';
}

export async function exitFlow(silent = false) {
    resetFlowTracker();

    if (!silent && callbacks.onExitFlow) {
        await callbacks.onExitFlow();
    }
}

export function isFlowActive() {
    return !!activeTraceId;
}

export function getActiveTraceId() {
    return activeTraceId;
}

function copyFlowToClipboard() {
    const viewer = $('log-viewer');
    if (!viewer) return;

    const lines = Array.from(viewer.querySelectorAll('.log-line:not(.divider)'))
        .map(el => {
            const lineNum = el.querySelector('.line-number')?.textContent || '';
            const text = el.textContent.replace(lineNum, '').trim();
            return `[L${lineNum}] ${text}`;
        })
        .join('\n');

    const summary = `=== Transaction Flow: ${activeTraceId} ===\n`
                  + `File: ${state.selectedFile || 'Unknown'}\n`
                  + `Total Events: ${viewer.querySelectorAll('.log-line:not(.divider)').length}\n\n`
                  + lines;

    navigator.clipboard.writeText(summary).then(() => {
        const btn = $('copy-flow-btn');
        if (btn) {
            const orig = btn.textContent;
            btn.textContent = '✅ Copied!';
            setTimeout(() => { btn.textContent = orig; }, 1800);
        }
    }).catch(err => {
        console.error('Failed to copy flow:', err);
    });
}