document.addEventListener('DOMContentLoaded', () => {
    const app = document.getElementById('local-search-app');
    if (!app) return;

    const searchForm = app.querySelector('[data-lse-search-form]');
    const searchInput = app.querySelector('[data-lse-search-input]');
    const resultsContainer = app.querySelector('[data-lse-results]');

    let inFlight = null;

    searchForm.addEventListener('submit', (e) => {
        e.preventDefault();
        const query = searchInput.value.trim();
        if (!query) return;
        // Mirror the search into the URL so it's bookmarkable and Back/Forward replays it; skip the
        // push on a re-submit of the same query to avoid stacking dead history entries.
        if (query !== currentQuery()) {
            history.pushState({ query }, '', `?query=${encodeURIComponent(query)}`);
        }
        performSearch(query);
    });

    // Back/Forward replays whatever ?query= the URL lands on (and clears when there's none). Reading
    // the URL instead of history.state means bookmarked links and the initial entry behave the same.
    window.addEventListener('popstate', () => applyQuery(currentQuery()));

    // Runs a search for an already-trimmed, non-empty query and renders the outcome. Shared by the
    // form submit and by applyQuery (deep-link load and Back/Forward).
    async function performSearch(query) {
        // Cancel any still-running search so a slow earlier query can't land after a newer one.
        if (inFlight) inFlight.abort();
        const controller = new AbortController();
        inFlight = controller;

        resultsContainer.innerHTML = '<div class="results-status">Searching local vector database...</div>';

        try {
            const response = await fetch(`api/search/query?q=${encodeURIComponent(query)}`, { signal: controller.signal });
            if (!response.ok) {
                let message = 'Search failed';
                try {
                    const body = await response.json();
                    if (body && body.error) message = body.error;
                } catch { /* non-JSON error body */ }
                throw new Error(message);
            }

            const results = await response.json();
            displayResults(results, query);
        } catch (error) {
            if (error.name === 'AbortError') return; // superseded by a newer search
            resultsContainer.innerHTML = '';
            resultsContainer.appendChild(buildMessage(error.message, 'error'));
        } finally {
            if (inFlight === controller) inFlight = null;
        }
    }

    // The trimmed ?query= from the address bar, or '' when absent.
    function currentQuery() {
        return (new URLSearchParams(window.location.search).get('query') || '').trim();
    }

    // Drive the UI from a query string: fill the box and search, or clear results when it's empty.
    // encodeURIComponent at the fetch call means spaces/punctuation round-trip cleanly through the URL.
    function applyQuery(query) {
        searchInput.value = query;
        if (query) {
            performSearch(query);
        } else {
            if (inFlight) inFlight.abort();
            resultsContainer.innerHTML = '';
        }
    }

    // Run the ?query= named in the URL on load, so a search can be linked or bookmarked.
    applyQuery(currentQuery());

    function displayResults(responseObj, query) {
        const results = [...((responseObj && responseObj.items) || [])].sort((a, b) =>
            (Number(b.score) - Number(a.score))
            || (Number(b.similarity) - Number(a.similarity)));
        resultsContainer.innerHTML = '';

        if (results.length === 0) {
            resultsContainer.appendChild(buildMessage(
                'No results close enough to your query. Try different terms, or index more pages.', 'status'));
            return;
        }

        const stopWords = new Set(["the", "and", "a", "an", "of", "to", "in", "is", "for", "on", "at", "by", "this", "that", "with", "from", "as", "it", "its"]);
        const terms = query.toLowerCase().split(/\s+/).filter(t => t.length >= 2 && !stopWords.has(t));

        // Split the sorted list into tabs without disturbing the explicit relevance/similarity
        // ordering. "Documents" is everything that isn't an HTML page (PDF/DOCX).
        const groups = [
            { key: 'pages',     label: 'Pages',     items: results.filter(r => r.docKind === 'Html') },
            { key: 'documents', label: 'Documents', items: results.filter(r => r.docKind !== 'Html') },
        ];

        // Default to Pages, but open on whichever tab has results so a documents-only query never
        // lands on an empty tab.
        const activeKey = groups[0].items.length > 0 ? 'pages' : 'documents';

        // Tab switching is CSS-only: a hidden radio per group drives both the active-tab styling and
        // which panel is shown, through :checked sibling selectors in the stylesheet — there are no
        // click handlers. JS just builds the nodes and seeds the initial checked/disabled state from
        // the result counts. The radios must precede the label bar and panels so those selectors can
        // reach them.
        const wrap = document.createElement('div');
        wrap.className = 'result-tabs-wrap';

        const tabBar = document.createElement('div');
        tabBar.className = 'result-tabs';

        const panels = [];
        for (const group of groups) {
            const radio = document.createElement('input');
            radio.type = 'radio';
            radio.name = 'resultTab';
            radio.id = `lse-tab-${group.key}`;
            radio.className = 'result-tab-radio';
            radio.checked = group.key === activeKey;
            radio.disabled = group.items.length === 0; // empty group isn't selectable

            const label = document.createElement('label');
            label.className = 'result-tab';
            label.htmlFor = radio.id;
            label.textContent = `${group.label} (${group.items.length})`;

            const panel = document.createElement('div');
            panel.className = 'results-list';
            panel.id = `lse-panel-${group.key}`;
            group.items.forEach(result => panel.appendChild(buildResultCard(result, terms)));

            wrap.appendChild(radio);
            tabBar.appendChild(label);
            panels.push(panel);
        }

        wrap.appendChild(tabBar);
        panels.forEach(panel => wrap.appendChild(panel));
        resultsContainer.appendChild(wrap);
    }

    // Builds one result card; shared by both tabs. The staggered fade-in delay is applied
    // in CSS (per card position) rather than inline here.
    function buildResultCard(result, terms) {
        const card = document.createElement('div');
        card.className = 'result-card';

        // Web pages use their crawled <title> as the headline. PDFs and Word docs often carry a
        // useless embedded title (e.g. "Microsoft Word - Document1"), so for those we show the
        // file name parsed from the URL instead.
        const headline = result.docKind === 'Html'
            ? (result.title && result.title.trim())
            : fileNameFromUrl(result.url);

        // When a headline is known it becomes the clickable headline and the URL
        // drops to a small line beneath it; otherwise the URL is the headline.
        const link = document.createElement('a');
        link.className = headline ? 'result-title' : 'result-url';
        link.href = result.url;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = headline || result.url;

        const parts = [link];
        if (headline) {
            const urlLine = document.createElement('div');
            urlLine.className = 'result-link-url';
            urlLine.textContent = result.url;
            parts.push(urlLine);
        }

        const text = document.createElement('p');
        text.className = 'result-text';
        appendHighlighted(text, result.text || '', terms);

        const score = document.createElement('span');
        score.className = 'result-score';
        const similarity = typeof result.similarity === 'number' ? ` · similarity ${result.similarity.toFixed(3)}` : '';
        score.textContent = `Relevance ${Number(result.score).toFixed(3)}${similarity}`;

        card.append(...parts, text, score);
        return card;
    }

    // Builds highlighted content using DOM nodes (textContent), so crawled
    // document text is never interpreted as HTML.
    function appendHighlighted(container, text, terms) {
        if (terms.length === 0) {
            container.textContent = text;
            return;
        }

        const pattern = new RegExp(`(${terms.map(escapeRegex).join('|')})`, 'gi');
        let lastIndex = 0;
        let match;
        while ((match = pattern.exec(text)) !== null) {
            if (match.index > lastIndex) {
                container.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
            }
            const mark = document.createElement('mark');
            mark.textContent = match[0];
            container.appendChild(mark);
            lastIndex = pattern.lastIndex;
            if (match.index === pattern.lastIndex) pattern.lastIndex++; // guard against zero-width matches
        }
        if (lastIndex < text.length) {
            container.appendChild(document.createTextNode(text.slice(lastIndex)));
        }
    }

    function buildMessage(text, kind) {
        const div = document.createElement('div');
        div.className = kind === 'error' ? 'message error' : 'results-status';
        div.textContent = text;
        return div;
    }

    function escapeRegex(value) {
        return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    // The file name (with extension) from a URL's path, percent-decoded — e.g.
    // ".../Documents/Annual%20Report.pdf" → "Annual Report.pdf". Returns '' for a path with no
    // final segment or a malformed URL, in which case the card falls back to showing the URL.
    function fileNameFromUrl(url) {
        try {
            const segment = new URL(url).pathname.split('/').filter(Boolean).pop();
            return segment ? decodeURIComponent(segment) : '';
        } catch {
            return '';
        }
    }

    // Quiet one-line index summary in the footer. Stats are decorative: any failure
    // (no index yet, endpoint unavailable) just leaves the footer empty.
    (async function loadStats() {
        try {
            const response = await fetch('api/stats');
            if (!response.ok) return;
            const stats = await response.json();

            const parts = [
                `${Number(stats.indexedPages).toLocaleString()} pages indexed`,
                `${Number(stats.totalChunks).toLocaleString()} chunks`,
                `${(stats.dbSizeBytes / (1024 * 1024)).toFixed(1)} MB`,
            ];
            if (stats.lastCrawledUtc) {
                parts.push(`last crawl ${new Date(stats.lastCrawledUtc).toLocaleString()}`);
            }
            app.querySelector('[data-lse-stats]').textContent = parts.join(' · ');
        } catch { /* leave the footer empty */ }
    })();
});
