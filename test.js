const escapeHtml = string => string.replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[m]);
const escapeRegExp = string => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

let customRe = /(123)/gi;
function highlightMatches(line, query, regexMode) {
    let searchRe = null;
    if (query) {
        try { searchRe = new RegExp(regexMode ? query : escapeRegExp(query), regexMode ? 'gi' : 'i'); } 
        catch { searchRe = null; }
    }
    if (!searchRe && !customRe) return escapeHtml(line);
    let result = '';
    let lastIdx = 0;
    if (searchRe) {
        let chunks = [];
        line.replace(searchRe, (match, ...args) => {
            const offset = args[args.length - 2];
            chunks.push({ text: line.slice(lastIdx, offset), isMark: false });
            chunks.push({ text: match, isMark: true });
            lastIdx = offset + match.length;
            return match;
        });
        chunks.push({ text: line.slice(lastIdx), isMark: false });
        for (const chunk of chunks) {
            if (chunk.isMark) {
                result += `<mark>${escapeHtml(chunk.text)}</mark>`;
            } else if (customRe && chunk.text) {
                let cLast = 0;
                chunk.text.replace(customRe, (m, ...a) => {
                    const off = a[a.length - 2];
                    result += escapeHtml(chunk.text.slice(cLast, off));
                    result += `<span class="custom-hl">${escapeHtml(m)}</span>`;
                    cLast = off + m.length;
                    return m;
                });
                result += escapeHtml(chunk.text.slice(cLast));
            } else {
                result += escapeHtml(chunk.text);
            }
        }
    } else if (customRe) {
        line.replace(customRe, (m, ...a) => {
            const off = a[a.length - 2];
            result += escapeHtml(line.slice(lastIdx, off));
            result += `<span class="custom-hl">${escapeHtml(m)}</span>`;
            lastIdx = off + m.length;
            return m;
        });
        result += escapeHtml(line.slice(lastIdx));
    }
    return result;
}

console.log(highlightMatches("error 123", "", false));
