(() => {
    'use strict';

    const ITEM_URL = 'https://archive.org/download/kogama-maps-kgmexporter';
    const $ = (id) => document.getElementById(id);
    const statusEl = $('status');
    const tbody = $('results').querySelector('tbody');
    const metaEl = $('meta');
    const searchEl = $('search');
    const regionEl = $('region');
    const typeEl = $('type');
    const sortEl = $('sort');
    const tabMapsEl = $('tabMaps');
    const tabAuthorsEl = $('tabAuthors');
    const authorsEl = $('authors');
    const tableEl = $('results');
    const ownerFilterEl = $('ownerFilter');

    let allMaps = [];
    let view = 'maps';      // 'maps' | 'authors'
    let ownerFilter = null; // selected author key, or null

    // All maps come from index.json (.kgmap + .kgm preserved on archive.org,
    // permanent download links).
    fetch('index.json', { cache: 'no-cache' })
        .then((r) => r.ok ? r.json() : Promise.reject(r.status))
        .catch((err) => { console.warn('index.json failed:', err); return null; })
        .then((index) => {
            if (!index) {
                statusEl.textContent = 'Failed to load index data.';
                return;
            }
            allMaps = (index.maps || []).map((m) => ({ ...m, Type: m.Type || 'kgmap' }));

            const nKgmap = allMaps.filter((m) => m.Type === 'kgmap').length;
            const nKgm = allMaps.length - nKgmap;
            const parts = [];
            if (nKgmap) parts.push(`${nKgmap} .kgmap`);
            if (nKgm) parts.push(`${nKgm} .kgm`);
            const when = index.generatedAt
                ? new Date(index.generatedAt).toLocaleString()
                : 'unknown';
            metaEl.textContent = `${allMaps.length} files (${parts.join(' + ')}) - index regenerated ${when}`;
            statusEl.textContent = '';
            render();
        });

    [searchEl, regionEl, typeEl, sortEl].forEach((el) => el.addEventListener('input', render));
    tabMapsEl.addEventListener('click', () => setView('maps'));
    tabAuthorsEl.addEventListener('click', () => setView('authors'));

    function setView(v) {
        view = v;
        tabMapsEl.classList.toggle('active', v === 'maps');
        tabAuthorsEl.classList.toggle('active', v === 'authors');
        tableEl.hidden = v !== 'maps';
        authorsEl.hidden = v !== 'authors';
        regionEl.hidden = v !== 'maps';
        typeEl.hidden = v !== 'maps';
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
        const type = typeEl.value;
        const sort = sortEl.value;

        let rows = allMaps;
        if (ownerFilter) {
            rows = rows.filter((m) => ownerKey(m) === ownerFilter);
        }
        if (type) {
            rows = rows.filter((m) => m.Type === type);
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
                rows.sort((a, b) => dateMs(a) - dateMs(b));
                break;
            case 'title-asc':
                rows.sort((a, b) => (a.GameTitle || a.Name || '').localeCompare(b.GameTitle || b.Name || ''));
                break;
            case 'size-desc':
                rows.sort((a, b) => (b.Size || 0) - (a.Size || 0));
                break;
            case 'date-desc':
            default:
                rows.sort((a, b) => dateMs(b) - dateMs(a));
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
        const titleTd = td(m.GameTitle || m.Name || '', 'Title');
        titleTd.className = 'title';
        tr.appendChild(titleTd);

        const ownerTd = td('', 'Owner');
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

        tr.appendChild(td(m.Region || '', 'Region'));
        tr.appendChild(td(fmtDate(m.SavedAt || m.Mtime), 'Saved'));
        const sizeTd = td(fmtSize(m.Size), 'Size');
        sizeTd.className = 'num';
        tr.appendChild(sizeTd);
        const dl = td('', 'Download');
        const a = document.createElement('a');
        // Every entry carries its own Url now: a direct file, or the .zip/.rar
        // it lives in. Fall back to the legacy kgmexporter item for old data.
        a.href = m.Url || `${ITEM_URL}/${encodeURIComponent(m.Name)}`;
        const kind = m.Type === 'kgm' ? '.kgm' : '.kgmap';
        if (m.Container) {
            const ext = /\.rar$/i.test(m.Container) ? '.rar' : '.zip';
            a.textContent = `${kind} (in ${ext})`;
            a.title = `Inside ${m.Container}`;
        } else {
            a.textContent = kind;
        }
        a.rel = 'noopener';
        dl.appendChild(a);
        tr.appendChild(dl);
        return tr;
    }

    function td(text, label) {
        const el = document.createElement('td');
        el.textContent = text;
        if (label) el.dataset.label = label;
        return el;
    }

    function dateMs(m) {
        // kgm entries carry only SavedAt (ISO); kgmap entries carry Mtime
        // (unix seconds) and usually SavedAt too. Normalise to a number so
        // both kinds sort together.
        if (m.SavedAt) {
            const t = Date.parse(m.SavedAt);
            if (!isNaN(t)) return t;
        }
        if (m.Mtime) {
            const n = parseInt(m.Mtime, 10);
            if (!isNaN(n)) return n * 1000;
        }
        return 0;
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
