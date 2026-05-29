(() => {
    'use strict';

    const ITEM_URL = 'https://archive.org/download/kogama-maps-kgmexporter';
    const $ = (id) => document.getElementById(id);
    const statusEl = $('status');
    const tbody = $('results').querySelector('tbody');
    const metaEl = $('meta');
    const searchEl = $('search');
    const regionEl = $('region');
    const sortEl = $('sort');
    const tabMapsEl = $('tabMaps');
    const tabAuthorsEl = $('tabAuthors');
    const authorsEl = $('authors');
    const tableEl = $('results');
    const ownerFilterEl = $('ownerFilter');

    let allMaps = [];
    let view = 'maps';      // 'maps' | 'authors'
    let ownerFilter = null; // selected author key, or null

    fetch('index.json', { cache: 'no-cache' })
        .then((r) => r.ok ? r.json() : Promise.reject(r.status))
        .then((data) => {
            allMaps = data.maps || [];
            metaEl.textContent =
                `${data.count} maps - index regenerated ${data.generatedAt ? new Date(data.generatedAt).toLocaleString() : 'unknown'}`;
            statusEl.textContent = '';
            render();
        })
        .catch((err) => {
            statusEl.textContent = 'Failed to load index.json (' + err + ').';
        });

    [searchEl, regionEl, sortEl].forEach((el) => el.addEventListener('input', render));
    tabMapsEl.addEventListener('click', () => setView('maps'));
    tabAuthorsEl.addEventListener('click', () => setView('authors'));

    function setView(v) {
        view = v;
        tabMapsEl.classList.toggle('active', v === 'maps');
        tabAuthorsEl.classList.toggle('active', v === 'authors');
        tableEl.hidden = v !== 'maps';
        authorsEl.hidden = v !== 'authors';
        regionEl.hidden = v !== 'maps';
        sortEl.hidden = v !== 'maps';
        searchEl.placeholder = v === 'maps'
            ? 'Search by title, owner, or game id...'
            : 'Search authors by name...';
        render();
    }

    function ownerKey(m) {
        return m.OwnerUsername || (m.OwnerProfileId ? '#' + m.OwnerProfileId : '(unknown)');
    }

    function render() {
        updateOwnerBanner();
        if (view === 'authors') { renderAuthors(); return; }
        renderMaps();
    }

    function updateOwnerBanner() {
        ownerFilterEl.replaceChildren();
        if (!ownerFilter || view !== 'maps') {
            ownerFilterEl.hidden = true;
            return;
        }
        ownerFilterEl.hidden = false;
        ownerFilterEl.appendChild(document.createTextNode(`Showing maps by ${ownerFilter} `));
        const clear = document.createElement('a');
        clear.href = '#';
        clear.textContent = '(show all)';
        clear.addEventListener('click', (e) => {
            e.preventDefault();
            ownerFilter = null;
            render();
        });
        ownerFilterEl.appendChild(clear);
    }

    function renderAuthors() {
        const q = searchEl.value.trim().toLowerCase();
        const counts = new Map();
        for (const m of allMaps) {
            const k = ownerKey(m);
            counts.set(k, (counts.get(k) || 0) + 1);
        }
        let authors = [...counts.entries()];
        if (q) authors = authors.filter(([name]) => name.toLowerCase().includes(q));
        authors.sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

        statusEl.textContent = `${authors.length} author${authors.length === 1 ? '' : 's'}`;
        authorsEl.replaceChildren(...authors.slice(0, 1000).map(([name, count]) => {
            const div = document.createElement('div');
            div.className = 'author';
            const n = document.createElement('div');
            n.className = 'name';
            n.textContent = name;
            const c = document.createElement('div');
            c.className = 'count';
            c.textContent = `${count} map${count === 1 ? '' : 's'}`;
            div.appendChild(n);
            div.appendChild(c);
            div.addEventListener('click', () => {
                ownerFilter = name;
                searchEl.value = '';
                setView('maps');
            });
            return div;
        }));
        if (authors.length > 1000) {
            const more = document.createElement('div');
            more.className = 'author';
            more.style.cursor = 'default';
            more.textContent = `... and ${authors.length - 1000} more. Search by name.`;
            authorsEl.appendChild(more);
        }
    }

    function renderMaps() {
        const q = searchEl.value.trim().toLowerCase();
        const region = regionEl.value;
        const sort = sortEl.value;

        let rows = allMaps;
        if (ownerFilter) {
            rows = rows.filter((m) => ownerKey(m) === ownerFilter);
        }
        if (q) {
            rows = rows.filter((m) =>
                (m.GameTitle || '').toLowerCase().includes(q) ||
                (m.OwnerUsername || '').toLowerCase().includes(q) ||
                (m.GameId || '').toLowerCase().includes(q) ||
                (m.Name || '').toLowerCase().includes(q));
        }
        if (region) {
            rows = rows.filter((m) => m.Region === region);
        }

        rows = rows.slice();
        switch (sort) {
            case 'date-asc':
                rows.sort((a, b) => (a.Mtime || '').localeCompare(b.Mtime || ''));
                break;
            case 'title-asc':
                rows.sort((a, b) => (a.GameTitle || a.Name || '').localeCompare(b.GameTitle || b.Name || ''));
                break;
            case 'size-desc':
                rows.sort((a, b) => (b.Size || 0) - (a.Size || 0));
                break;
            case 'date-desc':
            default:
                rows.sort((a, b) => (b.Mtime || '').localeCompare(a.Mtime || ''));
                break;
        }

        statusEl.textContent = `${rows.length} match${rows.length === 1 ? '' : 'es'}`;
        tbody.replaceChildren(...rows.slice(0, 500).map(rowEl));
        if (rows.length > 500) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = 6;
            td.style.textAlign = 'center';
            td.style.color = '#888';
            td.textContent = `... and ${rows.length - 500} more. Narrow your search.`;
            tr.appendChild(td);
            tbody.appendChild(tr);
        }
    }

    function rowEl(m) {
        const tr = document.createElement('tr');
        tr.appendChild(td(m.GameTitle || m.Name || ''));

        const ownerTd = document.createElement('td');
        const owner = ownerKey(m);
        if (owner !== '(unknown)') {
            const a = document.createElement('a');
            a.href = '#';
            a.textContent = owner;
            a.title = `Show maps by ${owner}`;
            a.addEventListener('click', (e) => {
                e.preventDefault();
                ownerFilter = owner;
                searchEl.value = '';
                setView('maps');
            });
            ownerTd.appendChild(a);
        }
        tr.appendChild(ownerTd);

        tr.appendChild(td(m.Region || ''));
        tr.appendChild(td(fmtDate(m.SavedAt || m.Mtime)));
        const sizeTd = td(fmtSize(m.Size));
        sizeTd.className = 'num';
        tr.appendChild(sizeTd);
        const dl = document.createElement('td');
        const a = document.createElement('a');
        a.href = `${ITEM_URL}/${encodeURIComponent(m.Name)}`;
        a.textContent = '.kgmap';
        a.rel = 'noopener';
        dl.appendChild(a);
        tr.appendChild(dl);
        return tr;
    }

    function td(text) {
        const el = document.createElement('td');
        el.textContent = text;
        return el;
    }

    function fmtDate(s) {
        if (!s) return '';
        const d = new Date(s);
        if (isNaN(d.getTime())) return s;
        return d.toISOString().slice(0, 10);
    }

    function fmtSize(n) {
        if (!n) return '';
        if (n < 1024) return n + ' B';
        if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
        return (n / 1024 / 1024).toFixed(1) + ' MB';
    }
})();
