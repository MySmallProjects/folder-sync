// ============================================================================
// FolderSyncEr Server Web File Viewer & User Admin (Pure API Client)
// ============================================================================

const STORAGE_KEY_SESSION = 'foldersyncer_web_session';
const DEVICE_ID = 'web-viewer-' + (localStorage.getItem('foldersyncer_web_device_id') || (() => {
    const id = Math.random().toString(36).substring(2, 10);
    localStorage.setItem('foldersyncer_web_device_id', id);
    return id;
})());

let session = null;
let allFiles = [];
let allUsers = [];
let selectedNodePath = null;
let selectedNodeIsDir = false;
let activePasswordEditUserId = null;
const collapsedPaths = new Set();

// DOM Elements
const statusBadge = document.getElementById('server-status-badge');
const navTabs = document.getElementById('nav-tabs');
const tabFilesBtn = document.getElementById('tab-files-btn');
const tabAdminBtn = document.getElementById('tab-admin-btn');
const userInfoEl = document.getElementById('user-info');
const logoutBtn = document.getElementById('logout-btn');
const loginView = document.getElementById('login-view');
const browserView = document.getElementById('browser-view');
const adminView = document.getElementById('admin-view');
const loginForm = document.getElementById('login-form');
const loginError = document.getElementById('login-error');
const loginSubmitBtn = document.getElementById('login-submit-btn');

const browserErrorBox = document.getElementById('browser-error-box');
const browserSuccessBox = document.getElementById('browser-success-box');
const searchInput = document.getElementById('search-input');
const includeDeletedCheckbox = document.getElementById('include-deleted-checkbox');
const statsSummary = document.getElementById('stats-summary');
const snapshotSeqLabel = document.getElementById('snapshot-seq-label');
const treeContainer = document.getElementById('tree-container');
const detailsBody = document.getElementById('details-body');
const expandAllBtn = document.getElementById('expand-all-btn');
const collapseAllBtn = document.getElementById('collapse-all-btn');
const refreshBtn = document.getElementById('refresh-btn');

// Upload & New Folder Drawers
const toggleUploadDrawerBtn = document.getElementById('toggle-upload-drawer-btn');
const toggleMkdirDrawerBtn = document.getElementById('toggle-mkdir-drawer-btn');
const uploadDrawer = document.getElementById('upload-drawer');
const uploadTargetFolderInput = document.getElementById('upload-target-folder');
const uploadFileInput = document.getElementById('upload-file-input');
const uploadSubmitBtn = document.getElementById('upload-submit-btn');
const uploadCancelBtn = document.getElementById('upload-cancel-btn');

const mkdirDrawer = document.getElementById('mkdir-drawer');
const mkdirPathInput = document.getElementById('mkdir-path-input');
const mkdirSubmitBtn = document.getElementById('mkdir-submit-btn');
const mkdirCancelBtn = document.getElementById('mkdir-cancel-btn');

// Admin DOM Elements
const adminErrorBox = document.getElementById('admin-error-box');
const adminSuccessBox = document.getElementById('admin-success-box');
const createUserForm = document.getElementById('create-user-form');
const newUsernameInput = document.getElementById('new-username');
const newUserPasswordInput = document.getElementById('new-user-password');
const createUserBtn = document.getElementById('create-user-btn');
const refreshUsersBtn = document.getElementById('refresh-users-btn');
const usersCountLabel = document.getElementById('users-count-label');
const usersTableBody = document.getElementById('users-table-body');

// ----------------------------------------------------------------------------
// Initialization
// ----------------------------------------------------------------------------
document.addEventListener('DOMContentLoaded', async () => {
    await checkServerStatus();
    loadSavedSession();

    if (session && session.accessToken) {
        showAuthenticatedUI();
        await fetchFiles();
    } else {
        showLoginView();
    }
});

async function checkServerStatus() {
    try {
        const res = await fetch('/api/status');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        statusBadge.textContent = `Online • v${data.version}`;
        statusBadge.className = 'badge online';
    } catch {
        statusBadge.textContent = 'Server Offline';
        statusBadge.className = 'badge offline';
    }
}

// ----------------------------------------------------------------------------
// Session & API Request Helper
// ----------------------------------------------------------------------------
function loadSavedSession() {
    try {
        const raw = sessionStorage.getItem(STORAGE_KEY_SESSION);
        if (raw) {
            session = JSON.parse(raw);
        }
    } catch {
        session = null;
    }
}

function saveSession(newSession) {
    session = newSession;
    if (newSession) {
        sessionStorage.setItem(STORAGE_KEY_SESSION, JSON.stringify(newSession));
    } else {
        sessionStorage.removeItem(STORAGE_KEY_SESSION);
    }
}

function isAdminSession() {
    return Boolean(session && session.user && String(session.user.role).toLowerCase() === 'admin');
}

async function apiFetch(url, options = {}) {
    const headers = new Headers(options.headers || {});
    if (session && session.accessToken) {
        headers.set('Authorization', `Bearer ${session.accessToken}`);
    }

    let response = await fetch(url, { ...options, headers });

    // Attempt token refresh on 401 if refreshToken is present
    if (response.status === 401 && session && session.refreshToken) {
        const refreshed = await tryRefreshToken();
        if (refreshed) {
            headers.set('Authorization', `Bearer ${session.accessToken}`);
            response = await fetch(url, { ...options, headers });
        } else {
            saveSession(null);
            showLoginView('Session expired or account was disabled/revoked. Please sign in again.');
            throw new Error('Unauthorized');
        }
    }

    return response;
}

async function tryRefreshToken() {
    try {
        const res = await fetch('/api/auth/refresh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                refreshToken: session.refreshToken,
                deviceId: DEVICE_ID
            })
        });
        if (!res.ok) return false;
        const data = await res.json();
        saveSession(data);
        return true;
    } catch {
        return false;
    }
}

async function extractErrorMessage(res, fallback) {
    try {
        const data = await res.json();
        if (data && data.detail) return data.detail;
        if (data && data.title) return data.title;
    } catch {
        // Ignore JSON parse error
    }
    return fallback;
}

function showBrowserError(msg) {
    browserSuccessBox.style.display = 'none';
    browserErrorBox.textContent = msg;
    browserErrorBox.style.display = 'block';
}

function showBrowserSuccess(msg) {
    browserErrorBox.style.display = 'none';
    browserSuccessBox.textContent = msg;
    browserSuccessBox.style.display = 'block';
}

function hideBrowserBanner() {
    browserErrorBox.style.display = 'none';
    browserSuccessBox.style.display = 'none';
}

// ----------------------------------------------------------------------------
// Authentication & Navigation Handlers
// ----------------------------------------------------------------------------
loginForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    loginError.style.display = 'none';
    loginSubmitBtn.disabled = true;
    loginSubmitBtn.textContent = 'Signing in...';

    const username = document.getElementById('username').value.trim();
    const password = document.getElementById('password').value;

    try {
        const res = await fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                username,
                password,
                deviceId: DEVICE_ID,
                deviceName: 'Web Browser TreeViewer'
            })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Login failed (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const data = await res.json();
        saveSession(data);
        showAuthenticatedUI();
        await fetchFiles();
    } catch (err) {
        loginError.textContent = err.message || 'Could not sign in.';
        loginError.style.display = 'block';
    } finally {
        loginSubmitBtn.disabled = false;
        loginSubmitBtn.textContent = 'Sign In';
    }
});

logoutBtn.addEventListener('click', async () => {
    try {
        if (session && session.accessToken) {
            await apiFetch('/api/auth/logout', { method: 'POST' });
        }
    } catch {
        // Ignore network errors on logout
    } finally {
        saveSession(null);
        allFiles = [];
        allUsers = [];
        selectedNodePath = null;
        selectedNodeIsDir = false;
        activePasswordEditUserId = null;
        showLoginView();
    }
});

tabFilesBtn.addEventListener('click', () => {
    switchTab('files');
});

tabAdminBtn.addEventListener('click', () => {
    if (!isAdminSession()) return;
    switchTab('admin');
});

function showLoginView(errorMessage) {
    loginView.style.display = 'flex';
    browserView.style.display = 'none';
    adminView.style.display = 'none';
    navTabs.style.display = 'none';
    tabAdminBtn.style.display = 'none';
    userInfoEl.style.display = 'none';
    logoutBtn.style.display = 'none';
    if (errorMessage) {
        loginError.textContent = errorMessage;
        loginError.style.display = 'block';
    } else {
        loginError.style.display = 'none';
    }
}

function showAuthenticatedUI() {
    loginView.style.display = 'none';
    navTabs.style.display = 'inline-flex';
    tabAdminBtn.style.display = isAdminSession() ? 'inline-flex' : 'none';

    if (session && session.user) {
        userInfoEl.textContent = `👤 ${session.user.username} (${session.user.role})`;
        userInfoEl.style.display = 'inline';
    }
    logoutBtn.style.display = 'inline-flex';
    switchTab('files');
}

function switchTab(tab) {
    hideAdminBanner();
    hideBrowserBanner();
    if (tab === 'admin' && isAdminSession()) {
        tabFilesBtn.classList.remove('active');
        tabAdminBtn.classList.add('active');
        browserView.style.display = 'none';
        adminView.style.display = 'flex';
        fetchUsers();
    } else {
        tabAdminBtn.classList.remove('active');
        tabFilesBtn.classList.add('active');
        adminView.style.display = 'none';
        browserView.style.display = 'flex';
    }
}

// ----------------------------------------------------------------------------
// File Upload & New Folder Drawers
// ----------------------------------------------------------------------------
function getDefaultTargetFolder() {
    if (!selectedNodePath) return '';
    if (selectedNodeIsDir) return selectedNodePath;
    const lastSlash = selectedNodePath.lastIndexOf('/');
    return lastSlash >= 0 ? selectedNodePath.substring(0, lastSlash) : '';
}

toggleUploadDrawerBtn.addEventListener('click', () => {
    mkdirDrawer.style.display = 'none';
    if (uploadDrawer.style.display === 'flex') {
        uploadDrawer.style.display = 'none';
    } else {
        uploadTargetFolderInput.value = getDefaultTargetFolder();
        uploadDrawer.style.display = 'flex';
    }
});

uploadCancelBtn.addEventListener('click', () => {
    uploadDrawer.style.display = 'none';
    uploadFileInput.value = '';
});

toggleMkdirDrawerBtn.addEventListener('click', () => {
    uploadDrawer.style.display = 'none';
    if (mkdirDrawer.style.display === 'flex') {
        mkdirDrawer.style.display = 'none';
    } else {
        const base = getDefaultTargetFolder();
        mkdirPathInput.value = base ? `${base}/` : '';
        mkdirDrawer.style.display = 'flex';
        setTimeout(() => mkdirPathInput.focus(), 0);
    }
});

mkdirCancelBtn.addEventListener('click', () => {
    mkdirDrawer.style.display = 'none';
    mkdirPathInput.value = '';
});

uploadDrawer.addEventListener('submit', async (e) => {
    e.preventDefault();
    hideBrowserBanner();

    const files = uploadFileInput.files;
    if (!files || files.length === 0) {
        showBrowserError('Please select at least one file to upload.');
        return;
    }

    const folderPrefix = uploadTargetFolderInput.value.trim().replace(/^\/+|\/+$/g, '');
    uploadSubmitBtn.disabled = true;
    uploadSubmitBtn.textContent = 'Uploading...';

    let uploadedCount = 0;
    let lastUploadedPath = null;

    try {
        for (const file of files) {
            const relPath = folderPrefix ? `${folderPrefix}/${file.name}` : file.name;
            const mtimeIso = file.lastModified ? new Date(file.lastModified).toISOString() : new Date().toISOString();

            const res = await apiFetch(`/api/files?path=${encodeURIComponent(relPath)}`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/octet-stream',
                    'X-Last-Modified-Utc': mtimeIso
                },
                body: file
            });

            if (!res.ok) {
                const msg = await extractErrorMessage(res, `Failed to upload '${relPath}' (HTTP ${res.status})`);
                throw new Error(msg);
            }

            uploadedCount++;
            lastUploadedPath = relPath;
        }

        uploadFileInput.value = '';
        uploadDrawer.style.display = 'none';
        if (lastUploadedPath) {
            selectedNodePath = lastUploadedPath;
            selectedNodeIsDir = false;
        }
        showBrowserSuccess(`Uploaded ${uploadedCount} file(s) successfully.`);
        await fetchFiles();
    } catch (err) {
        showBrowserError(err.message || 'File upload failed.');
    } finally {
        uploadSubmitBtn.disabled = false;
        uploadSubmitBtn.textContent = 'Upload Now';
    }
});

mkdirDrawer.addEventListener('submit', async (e) => {
    e.preventDefault();
    hideBrowserBanner();

    const path = mkdirPathInput.value.trim().replace(/^\/+|\/+$/g, '');
    if (!path) {
        showBrowserError('Please enter a valid folder path.');
        return;
    }

    mkdirSubmitBtn.disabled = true;
    try {
        const res = await apiFetch('/api/directories', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Failed to create folder (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const dirDto = await res.json();
        mkdirPathInput.value = '';
        mkdirDrawer.style.display = 'none';
        selectedNodePath = dirDto.path;
        selectedNodeIsDir = true;
        showBrowserSuccess(`Created folder '${dirDto.path}'.`);
        await fetchFiles();
    } catch (err) {
        showBrowserError(err.message || 'Could not create folder.');
    } finally {
        mkdirSubmitBtn.disabled = false;
    }
});

// ----------------------------------------------------------------------------
// User Administration (Admin Only)
// ----------------------------------------------------------------------------
function showAdminError(msg) {
    adminSuccessBox.style.display = 'none';
    adminErrorBox.textContent = msg;
    adminErrorBox.style.display = 'block';
}

function showAdminSuccess(msg) {
    adminErrorBox.style.display = 'none';
    adminSuccessBox.textContent = msg;
    adminSuccessBox.style.display = 'block';
}

function hideAdminBanner() {
    adminErrorBox.style.display = 'none';
    adminSuccessBox.style.display = 'none';
}

async function fetchUsers() {
    if (!isAdminSession()) return;
    usersTableBody.innerHTML = '<tr><td colspan="8" class="empty-state">Loading users...</td></tr>';

    try {
        const res = await apiFetch('/api/users');
        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Failed to load users (HTTP ${res.status})`);
            throw new Error(msg);
        }

        allUsers = await res.json();
        usersCountLabel.textContent = String(allUsers.length);
        renderUsersTable();
    } catch (err) {
        if (err.message !== 'Unauthorized') {
            usersTableBody.innerHTML = `<tr><td colspan="8" class="empty-state" style="color:#f87171;">${escapeHtml(err.message)}</td></tr>`;
        }
    }
}

function renderUsersTable() {
    if (!allUsers || allUsers.length === 0) {
        usersTableBody.innerHTML = '<tr><td colspan="8" class="empty-state">No users found.</td></tr>';
        return;
    }

    usersTableBody.innerHTML = '';

    for (const u of allUsers) {
        const tr = document.createElement('tr');
        const isAdmin = String(u.role).toLowerCase() === 'admin';

        let statusBadgeHtml = '<span class="badge online">Active</span>';
        if (u.isDeleted) {
            statusBadgeHtml = '<span class="badge offline">Deleted</span>';
        } else if (u.isDisabled) {
            statusBadgeHtml = '<span class="badge warning">Disabled</span>';
        }

        const roleBadgeHtml = isAdmin
            ? '<span class="badge admin-role">Admin</span>'
            : '<span class="badge">User</span>';

        const nameStyle = u.isDeleted ? 'text-decoration: line-through; color: var(--text-muted);' : 'font-weight: 600;';

        tr.innerHTML = `
            <td>#${u.id}</td>
            <td><span style="${nameStyle}">${escapeHtml(u.username)}</span></td>
            <td>${roleBadgeHtml}</td>
            <td>${statusBadgeHtml}</td>
            <td><code>storage/${escapeHtml(u.username)}/</code></td>
            <td>${formatUtc(u.createdUtc)}</td>
            <td>${formatUtc(u.lastLoginUtc)}</td>
            <td id="user-actions-cell-${u.id}"></td>
        `;

        usersTableBody.appendChild(tr);

        const actionsCell = tr.querySelector(`#user-actions-cell-${u.id}`);
        if (isAdmin) {
            actionsCell.innerHTML = '<span style="color: var(--text-muted); font-size: 12px;">Primary Admin (Protected)</span>';
        } else {
            const actionsWrap = document.createElement('div');
            actionsWrap.className = 'user-actions';

            const toggleDisableBtn = document.createElement('button');
            toggleDisableBtn.type = 'button';
            toggleDisableBtn.className = u.isDisabled ? 'btn sm success' : 'btn sm warning';
            toggleDisableBtn.textContent = u.isDisabled ? 'Enable' : 'Disable';
            toggleDisableBtn.addEventListener('click', () => updateUserStatus(u.id, u.username, { isDisabled: !u.isDisabled }));
            actionsWrap.appendChild(toggleDisableBtn);

            const toggleDeleteBtn = document.createElement('button');
            toggleDeleteBtn.type = 'button';
            toggleDeleteBtn.className = u.isDeleted ? 'btn sm secondary' : 'btn sm danger';
            toggleDeleteBtn.textContent = u.isDeleted ? 'Restore User' : 'Mark Deleted';
            toggleDeleteBtn.addEventListener('click', () => updateUserStatus(u.id, u.username, {
                isDeleted: !u.isDeleted,
                ...(u.isDeleted ? { isDisabled: false } : {})
            }));
            actionsWrap.appendChild(toggleDeleteBtn);

            const pwBtn = document.createElement('button');
            pwBtn.type = 'button';
            pwBtn.className = 'btn sm secondary';
            pwBtn.textContent = '🔑 Change Password';
            pwBtn.addEventListener('click', () => {
                activePasswordEditUserId = (activePasswordEditUserId === u.id) ? null : u.id;
                renderUsersTable();
            });
            actionsWrap.appendChild(pwBtn);

            actionsCell.appendChild(actionsWrap);

            if (activePasswordEditUserId === u.id) {
                const pwBox = document.createElement('div');
                pwBox.className = 'inline-pw-box';
                pwBox.innerHTML = `
                    <input type="password" id="inline-pw-input-${u.id}" placeholder="New password (min 4 chars)" autocomplete="new-password">
                    <button type="button" id="inline-pw-save-${u.id}" class="btn sm">Save</button>
                    <button type="button" id="inline-pw-cancel-${u.id}" class="btn sm secondary">Cancel</button>
                `;
                actionsCell.appendChild(pwBox);

                const inputEl = pwBox.querySelector(`#inline-pw-input-${u.id}`);
                const saveEl = pwBox.querySelector(`#inline-pw-save-${u.id}`);
                const cancelEl = pwBox.querySelector(`#inline-pw-cancel-${u.id}`);

                saveEl.addEventListener('click', () => changeUserPassword(u.id, u.username, inputEl.value));
                cancelEl.addEventListener('click', () => {
                    activePasswordEditUserId = null;
                    renderUsersTable();
                });
                setTimeout(() => inputEl.focus(), 0);
            }
        }
    }
}

createUserForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!isAdminSession()) return;

    hideAdminBanner();
    const username = newUsernameInput.value.trim();
    const password = newUserPasswordInput.value;

    if (!username || !password) {
        showAdminError('Both username and initial password are required.');
        return;
    }

    createUserBtn.disabled = true;
    try {
        const res = await apiFetch('/api/users', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, password })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Could not create user (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const created = await res.json();
        newUsernameInput.value = '';
        newUserPasswordInput.value = '';
        showAdminSuccess(`Created user '${created.username}' (#${created.id}). Their files will be stored under storage/${created.username}/`);
        await fetchUsers();
    } catch (err) {
        showAdminError(err.message || 'Failed to create user.');
    } finally {
        createUserBtn.disabled = false;
    }
});

async function updateUserStatus(userId, username, payload) {
    hideAdminBanner();
    try {
        const res = await apiFetch(`/api/users/${userId}/status`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Failed to update user status (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const updated = await res.json();
        const stateDesc = updated.isDeleted
            ? 'marked as deleted'
            : (updated.isDisabled ? 'disabled' : 'enabled');
        showAdminSuccess(`User '${username}' is now ${stateDesc}.`);
        await fetchUsers();
    } catch (err) {
        showAdminError(err.message || 'Failed to update user status.');
    }
}

async function changeUserPassword(userId, username, newPassword) {
    hideAdminBanner();
    if (!newPassword || newPassword.length < 4) {
        showAdminError('New password must be at least 4 characters long.');
        return;
    }

    try {
        const res = await apiFetch(`/api/users/${userId}/password`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ newPassword })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Failed to change password (HTTP ${res.status})`);
            throw new Error(msg);
        }

        activePasswordEditUserId = null;
        showAdminSuccess(`Password updated for user '${username}' (active device sessions were signed out).`);
        renderUsersTable();
    } catch (err) {
        showAdminError(err.message || 'Failed to change password.');
    }
}

refreshUsersBtn.addEventListener('click', () => {
    hideAdminBanner();
    fetchUsers();
});

// ----------------------------------------------------------------------------
// Fetching Files & Building the Tree Structure
// ----------------------------------------------------------------------------
async function fetchFiles() {
    treeContainer.innerHTML = '<div class="empty-state">Loading files from API...</div>';
    const includeDeleted = includeDeletedCheckbox.checked;

    try {
        let items = [];
        let cursor = null;
        let snapshotSeq = 0;

        do {
            const params = new URLSearchParams({
                includeDeleted: includeDeleted ? 'true' : 'false',
                limit: '1000'
            });
            if (cursor) params.set('cursor', cursor);

            const res = await apiFetch(`/api/files?${params.toString()}`);
            if (!res.ok) throw new Error(`API returned HTTP ${res.status}`);
            const page = await res.json();

            items = items.concat(page.items || []);
            cursor = page.nextCursor;
            snapshotSeq = page.snapshotSeq;
        } while (cursor);

        allFiles = items;
        snapshotSeqLabel.textContent = `Snapshot #${snapshotSeq}`;
        renderTree();
    } catch (err) {
        if (err.message !== 'Unauthorized') {
            treeContainer.innerHTML = `<div class="empty-state" style="color: #f87171;">Failed to load files: ${escapeHtml(err.message)}</div>`;
        }
    }
}

function buildTree(files, filterQuery) {
    const root = {
        name: '/',
        path: '',
        isDirectory: true,
        children: new Map(),
        fileDto: null,
        totalSize: 0,
        fileCount: 0,
        isDeleted: false
    };

    const normalizedQuery = (filterQuery || '').trim().toLowerCase();

    const matchingFiles = normalizedQuery
        ? files.filter(f => f.path.toLowerCase().includes(normalizedQuery))
        : files;

    for (const item of matchingFiles) {
        const parts = item.path.split('/').filter(Boolean);
        let current = root;
        let currentPath = '';

        for (let i = 0; i < parts.length; i++) {
            const part = parts[i];
            currentPath = currentPath ? `${currentPath}/${part}` : part;
            const isLeaf = i === parts.length - 1;

            if (!current.children.has(part)) {
                current.children.set(part, {
                    name: part,
                    path: currentPath,
                    isDirectory: isLeaf ? Boolean(item.isDirectory) : true,
                    children: new Map(),
                    fileDto: isLeaf ? item : null,
                    totalSize: 0,
                    fileCount: 0,
                    isDeleted: isLeaf ? Boolean(item.isDeleted) : false
                });
            } else if (isLeaf) {
                const existing = current.children.get(part);
                existing.fileDto = item;
                existing.isDirectory = Boolean(item.isDirectory) || existing.children.size > 0;
                existing.isDeleted = Boolean(item.isDeleted);
            }

            current = current.children.get(part);
        }
    }

    function computeAggregates(node) {
        if (!node.isDirectory) {
            const sz = node.fileDto ? (node.fileDto.size || 0) : 0;
            node.totalSize = sz;
            node.fileCount = 1;
            return { size: sz, count: 1 };
        }

        let sizeSum = 0;
        let countSum = 0;
        for (const child of node.children.values()) {
            const childAgg = computeAggregates(child);
            sizeSum += childAgg.size;
            countSum += childAgg.count;
        }
        node.totalSize = sizeSum;
        node.fileCount = countSum;
        return { size: sizeSum, count: countSum };
    }

    computeAggregates(root);
    return root;
}

function findNodeByPath(root, targetPath) {
    if (!targetPath) return null;
    const parts = targetPath.split('/').filter(Boolean);
    let current = root;
    for (const part of parts) {
        if (!current.children.has(part)) return null;
        current = current.children.get(part);
    }
    return current;
}

function renderTree() {
    const filterQuery = searchInput.value;
    const root = buildTree(allFiles, filterQuery);

    const fileItems = allFiles.filter(f => !f.isDirectory);
    const dirItems = allFiles.filter(f => f.isDirectory);
    const totalBytes = fileItems.reduce((acc, f) => acc + (f.size || 0), 0);
    statsSummary.textContent = `${fileItems.length} files, ${dirItems.length} folders (${formatBytes(totalBytes)})`;

    if (root.children.size === 0) {
        treeContainer.innerHTML = '<div class="empty-state">No files found on the server. Use "⬆ Upload File(s)" or "📁+ New Folder" above.</div>';
        detailsBody.innerHTML = '<div class="empty-state">Select a file or folder in the tree to inspect, rename, download, or delete it.</div>';
        return;
    }

    const fragment = document.createDocumentFragment();
    const sortedTopNodes = sortNodes(Array.from(root.children.values()));

    for (const node of sortedTopNodes) {
        fragment.appendChild(createNodeDom(node, 0));
    }

    treeContainer.innerHTML = '';
    treeContainer.appendChild(fragment);

    if (selectedNodePath) {
        const selectedNode = findNodeByPath(root, selectedNodePath);
        if (selectedNode) {
            renderDetails(selectedNode);
        } else {
            selectedNodePath = null;
            selectedNodeIsDir = false;
            detailsBody.innerHTML = '<div class="empty-state">Select a file or folder in the tree to inspect, rename, download, or delete it.</div>';
        }
    }
}

function sortNodes(nodes) {
    return nodes.sort((a, b) => {
        if (a.isDirectory !== b.isDirectory) {
            return a.isDirectory ? -1 : 1; // Folders first
        }
        return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
    });
}

function createNodeDom(node, depth) {
    const wrapper = document.createElement('div');
    wrapper.className = 'tree-node';

    const row = document.createElement('div');
    row.className = 'tree-row';
    if (node.path === selectedNodePath) {
        row.classList.add('selected');
    }
    if (node.isDeleted) {
        row.classList.add('deleted');
    }
    row.style.paddingLeft = `${12 + depth * 20}px`;

    const isCollapsed = collapsedPaths.has(node.path);

    const toggle = document.createElement('span');
    toggle.className = 'tree-toggle';
    if (node.isDirectory && node.children.size > 0) {
        toggle.textContent = isCollapsed ? '▸' : '▾';
    } else {
        toggle.textContent = '';
    }

    const icon = document.createElement('span');
    icon.className = 'node-icon';
    if (node.isDirectory) {
        icon.textContent = isCollapsed ? '📁' : '📂';
    } else {
        icon.textContent = getFileIcon(node.name);
    }

    const nameSpan = document.createElement('span');
    nameSpan.className = 'node-name';
    nameSpan.textContent = node.name;

    row.appendChild(toggle);
    row.appendChild(icon);
    row.appendChild(nameSpan);

    if (node.isDeleted) {
        const delBadge = document.createElement('span');
        delBadge.className = 'tag-deleted';
        delBadge.textContent = 'DELETED';
        row.appendChild(delBadge);
    }

    const meta = document.createElement('span');
    meta.className = 'node-meta';
    if (node.isDirectory) {
        meta.textContent = `${node.fileCount} item${node.fileCount === 1 ? '' : 's'} • ${formatBytes(node.totalSize)}`;
    } else if (node.fileDto) {
        meta.textContent = `v${node.fileDto.version} • ${formatBytes(node.fileDto.size)}`;
    }
    row.appendChild(meta);

    wrapper.appendChild(row);

    let childrenContainer = null;
    if (node.isDirectory && node.children.size > 0) {
        childrenContainer = document.createElement('div');
        childrenContainer.className = 'tree-children' + (isCollapsed ? ' collapsed' : '');
        const sortedChildren = sortNodes(Array.from(node.children.values()));
        for (const child of sortedChildren) {
            childrenContainer.appendChild(createNodeDom(child, depth + 1));
        }
        wrapper.appendChild(childrenContainer);
    }

    row.addEventListener('click', (e) => {
        e.stopPropagation();
        selectedNodePath = node.path;
        selectedNodeIsDir = Boolean(node.isDirectory);

        if (node.isDirectory && node.children.size > 0) {
            if (collapsedPaths.has(node.path)) {
                collapsedPaths.delete(node.path);
            } else {
                collapsedPaths.add(node.path);
            }
        }

        renderTree();
    });

    return wrapper;
}

// ----------------------------------------------------------------------------
// Item Metadata & Actions (Download, Preview, Rename, Delete, Restore)
// ----------------------------------------------------------------------------
function renderDetails(node) {
    const dto = node.fileDto;
    const typeLabel = node.isDirectory ? 'Folder' : 'File';

    let html = `
        <div class="detail-item">
            <span class="detail-label">Relative Path</span>
            <span class="detail-value">${escapeHtml(node.path)}</span>
        </div>
        <div class="detail-item">
            <span class="detail-label">Type</span>
            <span class="detail-value">${typeLabel}${node.isDeleted ? ' (Soft-Deleted / Trash)' : ''}</span>
        </div>
    `;

    // Action buttons for active or soft-deleted items
    if (!node.isDeleted) {
        html += `
            <div style="display: flex; flex-wrap: wrap; gap: 8px; margin-top: 2px;">
                ${(!node.isDirectory && dto) ? '<button id="download-file-btn" class="btn sm" type="button">⬇ Download</button>' : ''}
                ${(!node.isDirectory && dto && isPreviewableText(node.name, dto.size)) ? '<button id="preview-file-btn" class="btn sm secondary" type="button">👁 Preview Text</button>' : ''}
                ${node.isDirectory ? '<button id="upload-into-folder-btn" class="btn sm success" type="button">⬆ Upload Here</button>' : ''}
                <button id="rename-item-btn" class="btn sm secondary" type="button">✏️ Rename / Move</button>
                <button id="delete-item-btn" class="btn sm danger" type="button">🗑️ Delete</button>
            </div>
            <div id="inline-action-container"></div>
            <div id="preview-container"></div>
        `;
    } else if (dto && dto.isDeleted) {
        html += `
            <div style="display: flex; flex-wrap: wrap; gap: 8px; margin-top: 2px;">
                <button id="restore-item-btn" class="btn sm success" type="button">♻️ Restore from Trash</button>
            </div>
        `;
    }

    if (node.isDirectory) {
        html += `
            <div class="detail-item">
                <span class="detail-label">Contained Files</span>
                <span class="detail-value">${node.fileCount} (${formatBytes(node.totalSize)})</span>
            </div>
        `;
    }

    if (dto) {
        html += `
            <div class="detail-item">
                <span class="detail-label">Server ID</span>
                <span class="detail-value">${escapeHtml(dto.id)}</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Version &amp; Sequence</span>
                <span class="detail-value">v${dto.version} (ChangeSeq #${dto.changeSeq})</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Size</span>
                <span class="detail-value">${formatBytes(dto.size)} (${dto.size.toLocaleString()} bytes)</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">SHA-256</span>
                <span class="detail-value">${escapeHtml(dto.sha256 || '—')}</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Last Modified (UTC)</span>
                <span class="detail-value">${formatUtc(dto.lastModifiedUtc)}</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Uploaded At (UTC)</span>
                <span class="detail-value">${formatUtc(dto.uploadedAtUtc)}</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Uploaded By Device</span>
                <span class="detail-value">${escapeHtml(dto.uploadedByDeviceId || '—')}</span>
            </div>
        `;
    }

    detailsBody.innerHTML = html;

    const downloadBtn = document.getElementById('download-file-btn');
    if (downloadBtn && dto) {
        downloadBtn.addEventListener('click', () => downloadFile(dto));
    }

    const previewBtn = document.getElementById('preview-file-btn');
    if (previewBtn && dto) {
        previewBtn.addEventListener('click', () => previewTextFile(dto));
    }

    const uploadHereBtn = document.getElementById('upload-into-folder-btn');
    if (uploadHereBtn) {
        uploadHereBtn.addEventListener('click', () => {
            mkdirDrawer.style.display = 'none';
            uploadTargetFolderInput.value = node.path;
            uploadDrawer.style.display = 'flex';
            uploadFileInput.focus();
        });
    }

    const renameBtn = document.getElementById('rename-item-btn');
    if (renameBtn) {
        renameBtn.addEventListener('click', () => showInlineRenameBox(node));
    }

    const deleteBtn = document.getElementById('delete-item-btn');
    if (deleteBtn) {
        deleteBtn.addEventListener('click', () => showInlineDeleteConfirm(node));
    }

    const restoreBtn = document.getElementById('restore-item-btn');
    if (restoreBtn && dto) {
        restoreBtn.addEventListener('click', () => restoreItemFromTrash(dto));
    }
}

function showInlineRenameBox(node) {
    const container = document.getElementById('inline-action-container');
    if (!container) return;

    container.innerHTML = `
        <div class="detail-box">
            <span class="detail-label">Rename / Move ${node.isDirectory ? 'Folder' : 'File'}</span>
            <input type="text" id="rename-path-input" value="${escapeHtml(node.path)}">
            <div style="display: flex; gap: 8px;">
                <button type="button" id="confirm-rename-btn" class="btn sm">Save</button>
                <button type="button" id="cancel-rename-btn" class="btn sm secondary">Cancel</button>
            </div>
        </div>
    `;

    const inputEl = document.getElementById('rename-path-input');
    const saveBtn = document.getElementById('confirm-rename-btn');
    const cancelBtn = document.getElementById('cancel-rename-btn');

    saveBtn.addEventListener('click', () => renameNode(node, inputEl.value));
    cancelBtn.addEventListener('click', () => { container.innerHTML = ''; });
    inputEl.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            renameNode(node, inputEl.value);
        } else if (e.key === 'Escape') {
            container.innerHTML = '';
        }
    });
    setTimeout(() => {
        inputEl.focus();
        inputEl.select();
    }, 0);
}

async function renameNode(node, rawTargetPath) {
    hideBrowserBanner();
    const toPath = (rawTargetPath || '').trim().replace(/^\/+|\/+$/g, '');
    if (!toPath) {
        showBrowserError('Target path cannot be empty.');
        return;
    }
    if (toPath === node.path) {
        const container = document.getElementById('inline-action-container');
        if (container) container.innerHTML = '';
        return;
    }

    try {
        const res = await apiFetch('/api/files/move', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                fromPath: node.path,
                toPath: toPath,
                ifMatchSha256: null
            })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Rename failed (HTTP ${res.status})`);
            throw new Error(msg);
        }

        selectedNodePath = toPath;
        showBrowserSuccess(`Renamed '${node.path}' to '${toPath}'.`);
        await fetchFiles();
    } catch (err) {
        showBrowserError(err.message || 'Could not rename item.');
    }
}

function showInlineDeleteConfirm(node) {
    const container = document.getElementById('inline-action-container');
    if (!container) return;

    const desc = node.isDirectory
        ? `Delete folder '${node.path}' and all ${node.fileCount} file(s) inside it?`
        : `Delete file '${node.path}'?`;

    container.innerHTML = `
        <div class="detail-box" style="border-color: rgba(239, 68, 68, 0.5);">
            <span style="font-size: 13px; color: #fca5a5;">${escapeHtml(desc)}</span>
            <span style="font-size: 12px; color: var(--text-secondary);">The item will be soft-deleted (moved to Trash).</span>
            <div style="display: flex; gap: 8px;">
                <button type="button" id="confirm-delete-btn" class="btn sm danger">Confirm Delete</button>
                <button type="button" id="cancel-delete-btn" class="btn sm secondary">Cancel</button>
            </div>
        </div>
    `;

    document.getElementById('confirm-delete-btn').addEventListener('click', () => deleteNode(node));
    document.getElementById('cancel-delete-btn').addEventListener('click', () => { container.innerHTML = ''; });
}

async function deleteNode(node) {
    hideBrowserBanner();
    try {
        let targetId = node.fileDto ? node.fileDto.id : null;

        // If this is an implicit folder in the tree without a directory record yet, materialize it first
        if (!targetId && node.isDirectory) {
            const mkdirRes = await apiFetch('/api/directories', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path: node.path })
            });
            if (!mkdirRes.ok) {
                const msg = await extractErrorMessage(mkdirRes, `Could not prepare folder deletion (HTTP ${mkdirRes.status})`);
                throw new Error(msg);
            }
            const dirDto = await mkdirRes.json();
            targetId = dirDto.id;
        }

        if (!targetId) {
            throw new Error('Item ID not found.');
        }

        const res = await apiFetch(`/api/files/${encodeURIComponent(targetId)}`, {
            method: 'DELETE'
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Delete failed (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const deletedPath = node.path;
        if (!includeDeletedCheckbox.checked) {
            selectedNodePath = null;
            selectedNodeIsDir = false;
        }
        showBrowserSuccess(`Deleted '${deletedPath}' (moved to Trash).`);
        await fetchFiles();
    } catch (err) {
        showBrowserError(err.message || 'Could not delete item.');
    }
}

async function restoreItemFromTrash(dto) {
    hideBrowserBanner();
    try {
        const res = await apiFetch(`/api/trash/${encodeURIComponent(dto.id)}/restore`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ targetPath: null })
        });

        if (!res.ok) {
            const msg = await extractErrorMessage(res, `Restore failed (HTTP ${res.status})`);
            throw new Error(msg);
        }

        const data = await res.json();
        showBrowserSuccess(`Restored '${data.path}' (${data.restoredCount} item(s)).`);
        await fetchFiles();
    } catch (err) {
        showBrowserError(err.message || 'Could not restore item.');
    }
}

async function downloadFile(dto) {
    try {
        const res = await apiFetch(`/api/files/${encodeURIComponent(dto.id)}/content`);
        if (!res.ok) throw new Error(`Download failed (HTTP ${res.status})`);
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = dto.path.split('/').pop() || 'download';
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    } catch (err) {
        showBrowserError(`Error downloading file: ${err.message}`);
    }
}

async function previewTextFile(dto) {
    const container = document.getElementById('preview-container');
    if (!container) return;
    container.innerHTML = '<div class="detail-label" style="margin-bottom:4px;">Loading preview...</div>';
    try {
        const res = await apiFetch(`/api/files/${encodeURIComponent(dto.id)}/content`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const text = await res.text();
        container.innerHTML = `
            <div class="detail-label" style="margin-bottom: 4px;">Content Preview</div>
            <pre class="preview-box">${escapeHtml(text)}</pre>
        `;
    } catch (err) {
        container.innerHTML = `<div style="color:#f87171; font-size:12px;">Preview failed: ${escapeHtml(err.message)}</div>`;
    }
}

// ----------------------------------------------------------------------------
// Toolbar Controls & Helpers
// ----------------------------------------------------------------------------
searchInput.addEventListener('input', () => renderTree());
includeDeletedCheckbox.addEventListener('change', () => fetchFiles());
refreshBtn.addEventListener('click', () => {
    hideBrowserBanner();
    fetchFiles();
});

expandAllBtn.addEventListener('click', () => {
    collapsedPaths.clear();
    renderTree();
});

collapseAllBtn.addEventListener('click', () => {
    for (const f of allFiles) {
        const parts = f.path.split('/').filter(Boolean);
        let p = '';
        for (let i = 0; i < parts.length; i++) {
            p = p ? `${p}/${parts[i]}` : parts[i];
            if (i < parts.length - 1 || f.isDirectory) {
                collapsedPaths.add(p);
            }
        }
    }
    renderTree();
});

function isPreviewableText(filename, size) {
    if (size > 256 * 1024) return false; // Cap preview at 256 KB
    const ext = (filename.split('.').pop() || '').toLowerCase();
    return ['txt', 'md', 'json', 'xml', 'csv', 'log', 'yml', 'yaml', 'ini', 'cfg', 'cs', 'js', 'ts', 'py', 'sh', 'html', 'css'].includes(ext);
}

function getFileIcon(name) {
    const ext = (name.split('.').pop() || '').toLowerCase();
    if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'ico'].includes(ext)) return '🖼️';
    if (['zip', 'tar', 'gz', '7z', 'rar'].includes(ext)) return '🗜️';
    if (['pdf', 'doc', 'docx', 'txt', 'md'].includes(ext)) return '📄';
    if (['json', 'xml', 'yml', 'yaml', 'cs', 'js', 'py', 'sh'].includes(ext)) return '📝';
    return '📄';
}

function formatBytes(bytes) {
    if (!bytes || bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
}

function formatUtc(isoStr) {
    if (!isoStr) return '—';
    try {
        return new Date(isoStr).toISOString().replace('T', ' ').substring(0, 19) + ' UTC';
    } catch {
        return isoStr;
    }
}

function escapeHtml(str) {
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}
