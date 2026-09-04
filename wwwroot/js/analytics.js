import { $, api, responseError, escapeHtml } from './utils.js';
import { state } from './state.js';

let currentData = null;
let currentPath = null;
let callbacks = {};

export function setAnalyticsTarget(path) {
    currentPath = path;
}

export function initAnalytics(options = {}) {
    callbacks = options;

    const btn = $('analytics-button');
    const closeBtn = $('analytics-close');
    const dashboard = $('analytics-dashboard');
    const canvas = $('analytics-canvas');

    if (btn) {
        btn.addEventListener('click', () => {
            const isHidden = dashboard.hidden;
            dashboard.hidden = !isHidden;
            btn.classList.toggle('active', isHidden);
            if (isHidden) {
                const target = currentPath || (state.clusterMode ? state.clusterFile : state.selectedFile);
                if (target) loadAnalytics(target);
            }
        });
    }

    if (closeBtn) {
        closeBtn.addEventListener('click', () => {
            dashboard.hidden = true;
            btn?.classList.remove('active');
        });
    }

    if (canvas) {
        canvas.addEventListener('click', handleCanvasClick);
    }

    // KPI Click Handlers
    const kpiError = document.querySelector('.kpi-card.kpi-error');
    if (kpiError) {
        kpiError.addEventListener('click', () => {
            if (callbacks.onFilterKeyword) {
                callbacks.onFilterKeyword('(?:\\|\\s*(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\s*\\||\\[(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\]|\\b(?:ERROR|EROR|FATAL|FTL|CRITICAL):|Exception:|\\b[A-Za-z0-9_.]+Exception\\b)', true, true);
            }
        });
    }

    const kpiWarn = document.querySelector('.kpi-card.kpi-warn');
    if (kpiWarn) {
        kpiWarn.addEventListener('click', () => {
            if (callbacks.onFilterKeyword) {
                callbacks.onFilterKeyword('(?:\\|\\s*(?:WARN|WARNING|WRN)\\s*\\||\\[(?:WARN|WARNING|WRN)\\]|\\b(?:WARN|WARNING|WRN):)', true, true);
            }
        });
    }

    const kpiInfo = document.querySelector('.kpi-card.kpi-info');
    if (kpiInfo) {
        kpiInfo.addEventListener('click', () => {
            if (callbacks.onFilterKeyword) {
                callbacks.onFilterKeyword('(?:\\|\\s*(?:INFO|INF|INFORMATION)\\s*\\||\\[(?:INFO|INF|INFORMATION)\\]|\\b(?:INFO|INF|INFORMATION):)', true, false);
            }
        });
    }

    const kpiTotal = document.querySelector('.kpi-card.kpi-total');
    if (kpiTotal) {
        kpiTotal.addEventListener('click', () => {
            if (callbacks.onFilterKeyword) {
                callbacks.onFilterKeyword('', false, false);
            }
        });
    }
}

let analyticsLoadToken = 0;

export async function loadAnalytics(path) {
    currentPath = path;
    const dashboard = $('analytics-dashboard');
    if ((!path && !state.clusterFile && !state.remoteFile) || dashboard.hidden) return;

    const token = ++analyticsLoadToken;
    $('analytics-timestamp-range').textContent = 'Analyzing log file...';

    try {
        let url;
        if (state.clusterMode && state.clusterId) {
            const fileName = state.clusterFile || path;
            url = `${api(`/api/cluster/${encodeURIComponent(state.clusterId)}/analytics`)}?path=${encodeURIComponent(state.clusterPath || '')}&fileName=${encodeURIComponent(fileName)}&nodeId=${encodeURIComponent(state.clusterNodeId || 'all')}`;
        } else if (state.remoteMode && state.remoteProject) {
            const fileName = state.remoteFile || path;
            url = `${api('/api/remote/analytics')}?projectName=${encodeURIComponent(state.remoteProject)}&fileName=${encodeURIComponent(fileName)}`;
        } else {
            url = `${api('/api/files/analytics')}?path=${encodeURIComponent(path || state.selectedFile)}`;
        }

        const res = await fetch(url);
        if (token !== analyticsLoadToken) return;
        if (!res.ok) throw await responseError(res, 'Failed to fetch log analytics');

        const data = await res.json();
        if (token !== analyticsLoadToken) return;
        currentData = data;
        renderAnalytics(currentData);
    } catch (err) {
        if (token !== analyticsLoadToken) return;
        console.error('Analytics load error:', err);
        $('analytics-timestamp-range').textContent = err.message || 'Analysis failed';
    }
}

function renderAnalytics(data) {
    if (!data) return;

    $('kpi-errors').textContent = Number(data.errorCount).toLocaleString();
    $('kpi-warns').textContent = Number(data.warningCount).toLocaleString();
    $('kpi-infos').textContent = Number(data.infoCount).toLocaleString();
    $('kpi-total').textContent = Number(data.totalLines).toLocaleString();

    $('analytics-timestamp-range').textContent = `Scanned ${Number(data.totalLines).toLocaleString()} lines (${data.hourlyDistribution?.length || 0} time buckets)`;

    // Render Canvas Chart
    drawHistogram(data.hourlyDistribution || []);

    // Render Top Errors
    const topErrorsContainer = $('analytics-top-errors');
    if (!data.topErrors || data.topErrors.length === 0) {
        topErrorsContainer.innerHTML = '<span class="empty-hint">No error patterns detected.</span>';
    } else {
        topErrorsContainer.innerHTML = data.topErrors.map(item => `
            <div class="top-error-item" data-search="${escapeHtml(item.searchQuery)}" data-pattern="${escapeHtml(item.pattern)}" title="Click to filter: ${escapeHtml(item.searchQuery)}">
                <span class="error-count-badge">${Number(item.count).toLocaleString()}x</span>
                <span class="error-pattern-text">${escapeHtml(item.pattern)}</span>
            </div>
        `).join('');

        topErrorsContainer.querySelectorAll('.top-error-item').forEach(el => {
            el.addEventListener('click', () => {
                const search = el.dataset.search || el.dataset.pattern;
                if (callbacks.onFilterKeyword) {
                    callbacks.onFilterKeyword(search, false, true);
                }
            });
        });
    }
}

function drawHistogram(buckets) {
    const canvas = $('analytics-canvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');

    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = (rect.width || 600) * dpr;
    canvas.height = (rect.height || 150) * dpr;
    ctx.scale(dpr, dpr);

    const w = rect.width || 600;
    const h = rect.height || 150;
    ctx.clearRect(0, 0, w, h);

    if (buckets.length === 0) {
        ctx.fillStyle = '#94a3b8';
        ctx.font = '12px "Source Sans 3", sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('No hourly data found in scanned lines.', w / 2, h / 2);
        return;
    }

    const maxVal = Math.max(1, ...buckets.map(b => Math.max(b.errors, b.warnings)));
    const paddingLeft = 35;
    const paddingBottom = 25;
    const paddingTop = 15;
    const chartW = w - paddingLeft - 10;
    const chartH = h - paddingTop - paddingBottom;

    // Grid lines
    ctx.strokeStyle = 'rgba(200, 220, 215, 0.4)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(paddingLeft, paddingTop);
    ctx.lineTo(paddingLeft + chartW, paddingTop);
    ctx.moveTo(paddingLeft, paddingTop + chartH / 2);
    ctx.lineTo(paddingLeft + chartW, paddingTop + chartH / 2);
    ctx.moveTo(paddingLeft, paddingTop + chartH);
    ctx.lineTo(paddingLeft + chartW, paddingTop + chartH);
    ctx.stroke();

    // Y labels
    ctx.fillStyle = '#64748b';
    ctx.font = '10px "Fira Code", monospace';
    ctx.textAlign = 'right';
    ctx.fillText(maxVal.toString(), paddingLeft - 5, paddingTop + 4);
    ctx.fillText(Math.round(maxVal / 2).toString(), paddingLeft - 5, paddingTop + chartH / 2 + 4);
    ctx.fillText('0', paddingLeft - 5, paddingTop + chartH + 4);

    const barGroupWidth = chartW / buckets.length;
    const barW = Math.max(3, (barGroupWidth - 6) / 2);

    buckets.forEach((b, i) => {
        const x = paddingLeft + i * barGroupWidth;
        const errH = (b.errors / maxVal) * chartH;
        const warnH = (b.warnings / maxVal) * chartH;

        // Draw Warning bar (Amber)
        ctx.fillStyle = '#f59e0b';
        ctx.fillRect(x + 2, paddingTop + chartH - warnH, barW, warnH);

        // Draw Error bar (Red)
        ctx.fillStyle = '#ef4444';
        ctx.fillRect(x + 2 + barW + 1, paddingTop + chartH - errH, barW, errH);

        // Label on X-axis (every few buckets)
        if (buckets.length <= 12 || i % Math.ceil(buckets.length / 8) === 0 || i === buckets.length - 1) {
            ctx.fillStyle = '#64748b';
            ctx.font = '9px "Source Sans 3", sans-serif';
            ctx.textAlign = 'center';
            const label = b.hourLabel.length > 10 ? b.hourLabel.slice(-5) : b.hourLabel;
            ctx.fillText(label, x + barGroupWidth / 2, h - 6);
        }
    });
}

function handleCanvasClick(e) {
    if (!currentData || !currentData.hourlyDistribution?.length) return;
    const canvas = $('analytics-canvas');
    const rect = canvas.getBoundingClientRect();
    const clickX = e.clientX - rect.left;

    const paddingLeft = 35;
    const chartW = (rect.width || 600) - paddingLeft - 10;
    const buckets = currentData.hourlyDistribution;
    const barGroupWidth = chartW / buckets.length;

    const idx = Math.floor((clickX - paddingLeft) / barGroupWidth);
    if (idx >= 0 && idx < buckets.length) {
        const bucket = buckets[idx];
        const query = bucket.rawTimestamp || bucket.hourLabel;
        if (query && query !== 'Recent' && callbacks.onFilterHour) {
            callbacks.onFilterHour(query, false);
        } else if (bucket.errors > 0 && callbacks.onFilterKeyword) {
            callbacks.onFilterKeyword('(?:\\|\\s*(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\s*\\||\\[(?:ERROR|EROR|FATAL|FTL|CRITICAL)\\]|\\b(?:ERROR|EROR|FATAL|FTL|CRITICAL):|Exception:|\\b[A-Za-z0-9_.]+Exception\\b)', true, true);
        }
    }
}