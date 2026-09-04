import { $, api, setStatus, showPanel, showLoading, hideLoading } from './utils.js';
import { state } from './state.js';
import { renderOutput } from './viewer.js';
import { updateMobileFileInfo, updateMobileStatsFromViewer } from './mobileUx.js';

let _pollTimer = null;
let _pollAbort = null;
let _tailPosition = 0;

export async function stopTail() {
    state.tailing = false;
    if (_pollTimer) {
        clearInterval(_pollTimer);
        _pollTimer = null;
    }
    if (_pollAbort) {
        try { _pollAbort.abort(); } catch { /* ignore */ }
        _pollAbort = null;
    }
    if (state.connection) {
        try { await state.connection.stop(); } catch { /* already closed */ }
        state.connection = null;
    }
}

export async function tailFile(path, options = {}) {
    const token = ++state.openToken;
    showPanel('viewer');
    showLoading('CONNECTING LIVE TAIL...');

    try {
        await stopTail();
        state.selectedFile = path;
        const fileName = path.split(/[\\/]/).pop();
        $('viewer-title').textContent = fileName;
        updateMobileFileInfo(fileName, true);

        renderOutput('');

        // 1. Try SignalR with direct WebSockets (only for local non-cluster files)
        let signalRSuccess = false;
        if (!options.clusterId && typeof signalR !== 'undefined') {
            try {
                signalRSuccess = await tryStartSignalR(path, token);
            } catch (err) {
                console.info('[Tail] SignalR unavailable or VIP mismatch, falling back to HTTP tail:', err.message);
            }
        }

        if (token !== state.openToken) return;

        // 2. If SignalR fails or unavailable, start robust HTTP Live Tail
        if (!signalRSuccess) {
            await startHttpTail(path, options, token);
        }

        state.tailing = true;
        state.paused = false;
        setStatus('LIVE', true);
        const pauseBtn = $('pause-button');
        if (pauseBtn) {
            pauseBtn.disabled = false;
            pauseBtn.textContent = 'Pause';
        }
    } finally {
        hideLoading();
    }
}

async function tryStartSignalR(path, token) {
    const startConn = async () => {
        let conn = null;
        try {
            // First attempt: direct WebSockets with skipNegotiation to bypass LB negotiate ID mismatch
            conn = new signalR.HubConnectionBuilder()
                .withUrl(api('/hubs/log'), {
                    skipNegotiation: true,
                    transport: signalR.HttpTransportType.WebSockets
                })
                .withAutomaticReconnect()
                .build();

            wireSignalREvents(conn, path);
            await conn.start();
            return conn;
        } catch (wsErr) {
            if (conn) try { await conn.stop(); } catch { /* ignore */ }

            // Second attempt: standard negotiation
            conn = new signalR.HubConnectionBuilder()
                .withUrl(api('/hubs/log'))
                .withAutomaticReconnect()
                .build();

            wireSignalREvents(conn, path);
            await conn.start();
            return conn;
        }
    };

    // 2.5 second timeout to avoid keeping user waiting
    const timeoutPromise = new Promise((_, reject) =>
        setTimeout(() => reject(new Error('SignalR timeout')), 2500)
    );

    const connection = await Promise.race([startConn(), timeoutPromise]);
    if (token !== state.openToken) {
        try { await connection.stop(); } catch { /* ignore */ }
        return false;
    }

    state.connection = connection;
    await connection.invoke('Subscribe', path);
    return true;
}

function wireSignalREvents(connection, path) {
    connection.on('LogAppended', text => {
        if (!state.paused && state.connection === connection) {
            renderOutput(text, true);
            updateMobileStatsFromViewer();
        }
    });

    connection.onreconnecting(() => setStatus('RECONNECTING', false));
    connection.onreconnected(async () => {
        try {
            await connection.invoke('Subscribe', path);
            setStatus('LIVE', true);
        } catch {
            setStatus('OFFLINE', false);
        }
    });

    connection.onclose(() => {
        if (state.connection === connection && state.tailing) {
            console.warn('[Tail] SignalR disconnected; switching silently to HTTP tail.');
            state.connection = null;
            startHttpTail(path, {}, state.openToken);
        }
    });
}

async function startHttpTail(path, options, token) {
    _tailPosition = 0;

    const buildUrl = (pos) => {
        if (options.clusterId) {
            return `${api(`/api/cluster/${encodeURIComponent(options.clusterId)}/tail`)}?path=${encodeURIComponent(options.relativePath || '')}&fileName=${encodeURIComponent(options.fileName || path)}&nodeId=${encodeURIComponent(options.nodeId || 'all')}&position=${pos}&maxLines=500`;
        }
        return `${api('/api/files/tail')}?path=${encodeURIComponent(path)}&position=${pos}&maxLines=500`;
    };

    // Initial tail fetch (position 0 = end slice)
    try {
        const res = await fetch(buildUrl(0));
        if (res.ok) {
            const data = await res.json();
            if (token === state.openToken) {
                _tailPosition = data.newPosition ?? data.NewPosition ?? 0;
                const initialText = data.content || data.Content || (data.lines || data.Lines || []).join('\n');
                renderOutput(initialText);
                updateMobileStatsFromViewer();
            }
        }
    } catch (err) {
        console.warn('[Tail] Initial HTTP tail load failed:', err.message);
    }

    // Polling interval every 1.5 seconds
    _pollTimer = setInterval(async () => {
        if (token !== state.openToken || state.paused || !state.tailing) return;

        try {
            _pollAbort = new AbortController();
            const res = await fetch(buildUrl(_tailPosition), { signal: _pollAbort.signal });
            if (!res.ok) return;

            const data = await res.json();
            if (token !== state.openToken || state.paused || !state.tailing) return;

            const isReset = data.isReset || data.IsReset;
            const newPos = data.newPosition ?? data.NewPosition ?? _tailPosition;
            _tailPosition = newPos;

            const newContent = data.content || data.Content || (data.lines || data.Lines || []).join('\n');
            if (newContent) {
                if (isReset) {
                    renderOutput(newContent);
                } else {
                    renderOutput(newContent, true);
                }
                updateMobileStatsFromViewer();
            }
        } catch (err) {
            if (err.name !== 'AbortError') {
                console.warn('[Tail] Poll error:', err.message);
            }
        }
    }, 1500);
}

