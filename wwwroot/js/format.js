export const formatSize = bytes =>
    bytes < 1024 ? `${bytes} B`
    : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB`
    : bytes < 1073741824 ? `${(bytes / 1048576).toFixed(1)} MB`
    : `${(bytes / 1073741824).toFixed(2)} GB`;

export const formatDate = value => {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '-';
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} `
         + `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
};

export const classify = line => {
    if (!line) return '';
    const head = line.length > 2000 ? line.slice(0, 2000) : line;
    const match = head.match(/\b(ERROR|EROR|FATAL|FTL|CRIT|CRITICAL|ERR|WARN|WARNING|WRN|INFO|INF|INFORMATION)\b/i);
    if (match) {
        const level = match[1].toUpperCase();
        if (['ERROR', 'EROR', 'FATAL', 'FTL', 'CRIT', 'CRITICAL', 'ERR'].includes(level)) return 'error';
        if (['WARN', 'WARNING', 'WRN'].includes(level)) return 'warn';
        return 'info';
    }
    if (/\b(EXCEPTION|FAIL|FAILED|FAILURE|TIMEOUT|TIMED OUT)\b/i.test(head)) return 'error';
    return '';
};
