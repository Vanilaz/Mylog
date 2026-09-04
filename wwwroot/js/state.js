export const state = {
    path: null,
    localPath: null,
    selectedFile: null,
    files: [],
    folders: [],
    parentPath: null,
    folderCache: {},
    sort: 'name',
    ascending: true,
    connection: null,
    tailing: false,
    paused: false,
    lineCount: 0,
    errCount: 0,
    wrnCount: 0,
    lastSearchData: null,
    openToken: 0,
    fontSize: 13,
    remoteMode: false,
    remoteProject: null,
    remoteFile: null,
    clusterMode: false,
    clusterId: null,
    clusterFile: null,
    clusterPath: '',
    clusterNodeId: 'all'
};

// localStorage throws in some privacy modes; never let a preference read break boot.
export const store = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } }
};
