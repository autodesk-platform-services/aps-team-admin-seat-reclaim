const $ = (id) => document.getElementById(id);

const login = $('login');
const state = {
    teams: [],
    query: null,        // { teamId, days, source, file }
    productMap: {},     // Team Admin productId -> usage product name (manual overrides)
    result: null,
    statuses: new Map() // "userId|productId" -> { status, message }
};

try {
    const resp = await fetch('/api/auth/profile');
    if (resp.ok) {
        const user = await resp.json();
        login.innerText = `Logout (${user.name})`;
        login.onclick = () => {
            // End the Autodesk SSO session as well, then clear the local session cookies.
            const iframe = document.createElement('iframe');
            iframe.style.visibility = 'hidden';
            iframe.src = 'https://accounts.autodesk.com/Authentication/LogOut';
            document.body.appendChild(iframe);
            iframe.onload = () => {
                window.location.replace('/api/auth/logout');
                document.body.removeChild(iframe);
            };
        };
        await initTeams();
    } else {
        login.innerText = 'Login';
        login.onclick = () => window.location.replace('/api/auth/login');
    }
    login.style.visibility = 'visible';
} catch (err) {
    alert('Could not initialize the application. See console for more details.');
    console.error(err);
}

async function api(url, options = {}) {
    const resp = await fetch(url, options);
    if (resp.status === 401) {
        window.location.replace('/api/auth/login');
        throw new Error('Session expired.');
    }
    if (!resp.ok) {
        const text = await resp.text();
        throw new Error(text || `${resp.status} ${resp.statusText}`);
    }
    return resp.json();
}

function showStatus(text, kind = 'info') {
    const status = $('status');
    status.hidden = !text;
    status.className = `message ${kind}`;
    status.textContent = text || '';
}

async function initTeams() {
    $('teams-panel').hidden = false;
    showStatus('Checking which teams you administer...');
    try {
        state.teams = await api('/api/teams');
    } catch (err) {
        showStatus(`Could not load your teams: ${err.message}`, 'error');
        return;
    }
    showStatus('');

    // Every team you administer can be used with an uploaded report; the Reporting API source also needs the benefit.
    const sorted = [...state.teams.filter(t => t.reportingApiAccess), ...state.teams.filter(t => !t.reportingApiAccess)];
    const select = $('team-select');
    select.innerHTML = '';
    for (const team of sorted) {
        const option = document.createElement('option');
        option.value = team.id;
        option.textContent = team.reportingApiAccess ? team.name : `${team.name} (no Business Success Plan Reporting API access, report upload only)`;
        select.appendChild(option);
    }

    if (sorted.length === 0) {
        const noTeams = $('no-teams');
        noTeams.hidden = false;
        noTeams.textContent = 'You are not an administrator of any team.';
        select.disabled = true;
        return;
    }
    select.value = sorted[0].id;
    select.onchange = () => {
        resetResults();
        loadPools();
    };
    $('query-panel').hidden = false;
    await loadPools();
}

// License pools

async function loadPools() {
    const teamId = $('team-select').value;
    const button = $('refresh-pools');
    button.disabled = true;
    $('pools-panel').hidden = false;
    $('pools-updated').textContent = 'Loading...';
    try {
        const pools = await api(`/api/teams/${encodeURIComponent(teamId)}/product-pools`);
        if (teamId !== $('team-select').value) {
            return; // another team was selected meanwhile
        }
        const tbody = document.querySelector('#pools-table tbody');
        tbody.innerHTML = '';
        if (pools.length === 0) {
            const td = tbody.insertRow().insertCell();
            td.colSpan = 5;
            td.className = 'empty';
            td.textContent = 'This team has no product pools.';
        }
        for (const pool of pools) {
            const tr = tbody.insertRow();
            tr.insertCell().textContent = pool.productName ?? pool.productId;
            tr.insertCell().textContent = pool.unlimited ? 'Unlimited' : (pool.total ?? '—');
            tr.insertCell().textContent = pool.assigned ?? '—';
            tr.insertCell().textContent = pool.unlimited ? '—' : (pool.available ?? '—');
            tr.insertCell().textContent = pool.endDate ? pool.endDate.slice(0, 10) : '—';
        }
        $('pools-updated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
    } catch (err) {
        $('pools-updated').textContent = `Could not load pools: ${err.message}`;
    } finally {
        button.disabled = false;
    }
}

$('refresh-pools').onclick = () => loadPools();

// Re-run the analysis against current Team Admin assignments (e.g. after licenses were assigned in Autodesk Account).
$('refresh-results').onclick = async () => {
    await Promise.all([runQuery(), loadPools()]);
};

// Query form

for (const radio of document.querySelectorAll('input[name="source"]')) {
    radio.onchange = () => {
        const isReport = document.querySelector('input[name="source"]:checked').value === 'report';
        $('report-file').disabled = !isReport;
        $('report-file').required = isReport;    };
}

$('query-form').onsubmit = async (ev) => {
    ev.preventDefault();
    const source = document.querySelector('input[name="source"]:checked').value;
    const file = $('report-file').files[0];
    if (source === 'report' && !file) {
        showStatus('Choose a report file to upload.', 'error');
        return;
    }
    const selectedTeam = state.teams.find(t => t.id === $('team-select').value);
    if (source === 'api' && !selectedTeam?.reportingApiAccess) {
        showStatus('This team has no Business Success Plan Reporting API access. Upload a report instead.', 'error');
        return;
    }
    state.query = { teamId: $('team-select').value, days: parseInt($('days').value, 10), source, file };
    state.productMap = {};
    await runQuery();
};

$('apply-mapping').onclick = async () => {
    for (const select of document.querySelectorAll('#mapping-table select')) {
        if (select.value) {
            state.productMap[select.dataset.productId] = select.value;
        } else {
            delete state.productMap[select.dataset.productId];
        }
    }
    await runQuery();
};

async function runQuery() {
    const { teamId, days, source, file } = state.query;
    $('find').disabled = true;
    showStatus(source === 'api'
        ? 'Querying the Business Success Plan Reporting API. This can take a minute...'
        : 'Reading the report...');
    try {
        let result;
        if (source === 'api') {
            result = await api(`/api/teams/${encodeURIComponent(teamId)}/inactive-users`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ days, productMap: state.productMap })
            });
        } else {
            const form = new FormData();
            form.append('file', file);
            form.append('days', days);
            form.append('productMap', JSON.stringify(state.productMap));
            result = await api(`/api/teams/${encodeURIComponent(teamId)}/inactive-users/report`, { method: 'POST', body: form });
        }
        state.result = result;
        state.statuses.clear();
        showStatus('');
        renderResults();
    } catch (err) {
        showStatus(err.message, 'error');
    } finally {
        $('find').disabled = false;
    }
}

function resetResults() {
    state.result = null;
    state.statuses.clear();
    $('results-panel').hidden = true;
}

// Results

function rowKey(row) {
    return `${row.userId}|${row.productId}`;
}

function renderResults() {
    const result = state.result;
    $('results-panel').hidden = false;
    const removable = result.rows.filter(r => r.removable).length;
    $('summary').textContent =
        `${result.rows.length} inactive license(s) found with no usage between ${result.cutoff} and ${result.asOf} ` +
        `(${result.days} days, source: ${result.source}). ${removable} can be removed; ` +
        `team-level assignments must be changed in Autodesk Account.`;

    const warnings = $('warnings');
    warnings.innerHTML = '';
    for (const text of result.warnings) {
        const div = document.createElement('div');
        div.className = 'message warning';
        div.textContent = text;
        warnings.appendChild(div);
    }

    renderMapping(result);

    const tbody = document.querySelector('#results-table tbody');
    tbody.innerHTML = '';
    if (result.rows.length === 0) {
        const tr = tbody.insertRow();
        const td = tr.insertCell();
        td.colSpan = 8;
        td.className = 'empty';
        td.textContent = 'No inactive users found.';
    }
    for (const row of result.rows) {
        const tr = tbody.insertRow();
        tr.dataset.key = rowKey(row);
        const check = document.createElement('input');
        check.type = 'checkbox';
        check.className = 'row-check';
        check.disabled = !row.removable;
        check.title = row.removable ? '' : 'Inherited from the team; it cannot be removed with the Team Admin API.';
        check.onchange = updateSelection;
        tr.insertCell().appendChild(check);
        tr.insertCell().textContent = row.name ?? '';
        tr.insertCell().textContent = row.email ?? '';
        tr.insertCell().textContent = row.productName ?? row.productId;
        tr.insertCell().textContent = row.assignmentType;
        tr.insertCell().textContent = row.lastUsed ?? 'No usage in data';
        tr.insertCell().textContent = row.daysInactive ?? '—';
        tr.insertCell().className = 'status';
        if (!row.removable) {
            tr.classList.add('disabled');
        }
    }
    $('select-all').checked = false;
    updateSelection();
}

function renderMapping(result) {
    const tbody = document.querySelector('#mapping-table tbody');
    tbody.innerHTML = '';
    for (const match of result.productMatches) {
        const tr = tbody.insertRow();
        tr.insertCell().textContent = match.productName ?? match.productId;
        const select = document.createElement('select');
        select.dataset.productId = match.productId;
        select.add(new Option('(no match)', ''));
        const names = new Set(result.usageProducts);
        if (match.usageProduct) {
            names.add(match.usageProduct);
        }
        for (const name of [...names].sort()) {
            select.add(new Option(name, name));
        }
        select.value = match.usageProduct ?? '';
        tr.insertCell().appendChild(select);
        if (!match.usageProduct) {
            tr.classList.add('unmatched');
        }
    }
    // Open the panel when something needs attention.
    $('mapping-panel').open = result.productMatches.some(m => !m.usageProduct);
}

$('select-all').onchange = (ev) => {
    for (const check of document.querySelectorAll('.row-check:not(:disabled)')) {
        check.checked = ev.target.checked;
    }
    updateSelection();
};

function selectedItems() {
    return [...document.querySelectorAll('.row-check:checked')].map(check => {
        const [userId, productId] = check.closest('tr').dataset.key.split('|');
        return { userId, productId };
    });
}

function updateSelection() {
    const count = selectedItems().length;
    $('selection-count').textContent = `${count} selected`;
    $('remove').disabled = count === 0;
}

$('remove').onclick = async () => {
    const items = selectedItems();
    $('confirm-text').textContent =
        `This removes ${items.length} product license(s) from the selected users in this team. ` +
        'The seats return to the product pools. This cannot be undone from this app.';
    const dialog = $('confirm-dialog');
    dialog.returnValue = '';
    dialog.showModal();
    await new Promise(resolve => dialog.addEventListener('close', resolve, { once: true }));
    if (dialog.returnValue !== 'confirm') {
        return;
    }

    $('remove').disabled = true;
    showStatus(`Removing ${items.length} license(s)...`);
    try {
        const results = await api(`/api/teams/${encodeURIComponent(state.query.teamId)}/assignments/remove`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ items })
        });
        for (const r of results) {
            const tr = document.querySelector(`#results-table tr[data-key="${CSS.escape(`${r.userId}|${r.productId}`)}"]`);
            if (!tr) {
                continue;
            }
            const cell = tr.querySelector('.status');
            cell.textContent = { Removed: 'Removed', Inherited: 'Inherited grant remains', Failed: 'Failed' }[r.status] ?? r.status;
            cell.title = r.message ?? '';
            cell.className = `status ${r.status.toLowerCase()}`;
            const check = tr.querySelector('.row-check');
            check.checked = false;
            if (r.status === 'Removed') {
                check.disabled = true;
                tr.classList.add('removed');
            }
        }
        const removed = results.filter(r => r.status === 'Removed').length;
        loadPools(); // seats returned to the pool
        showStatus(`${removed} of ${results.length} license(s) removed.` +
            (removed < results.length ? ' Hover over a status for details.' : ''), removed === results.length ? 'success' : 'warning');
    } catch (err) {
        showStatus(err.message, 'error');
    } finally {
        updateSelection();
    }
};
