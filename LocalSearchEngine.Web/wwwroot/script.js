document.addEventListener('DOMContentLoaded', () => {
    const searchForm = document.getElementById('searchForm');
    const searchInput = document.getElementById('searchInput');
    const resultsContainer = document.getElementById('resultsContainer');

    searchForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        const query = searchInput.value.trim();
        if (!query) return;

        resultsContainer.innerHTML = '<div class="results-status">Searching local vector database...</div>';

        try {
            const response = await fetch(`/api/search/query?q=${encodeURIComponent(query)}`);
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
            resultsContainer.innerHTML = '';
            resultsContainer.appendChild(buildMessage(error.message, 'error'));
        }
    });

    function displayResults(responseObj, query) {
        const results = (responseObj && responseObj.items) || [];
        resultsContainer.innerHTML = '';

        if (results.length === 0) {
            resultsContainer.appendChild(buildMessage(
                'No results close enough to your query. Try different terms, or index more pages.', 'status'));
            return;
        }

        const header = document.createElement('div');
        header.className = 'results-header';
        const total = responseObj.totalMatches || results.length;
        header.textContent = `Found ${total} relevant ${total === 1 ? 'result' : 'results'}.`;
        resultsContainer.appendChild(header);

        const terms = query.toLowerCase().split(/\s+/).filter(t => t.length > 2);

        results.forEach((result, index) => {
            const card = document.createElement('div');
            card.className = 'result-card';
            card.style.animationDelay = `${Math.min(index, 10) * 0.05}s`;

            const link = document.createElement('a');
            link.className = 'result-url';
            link.href = result.url;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.textContent = result.url;

            const text = document.createElement('p');
            text.className = 'result-text';
            appendHighlighted(text, result.text || '', terms);

            const score = document.createElement('span');
            score.className = 'result-score';
            const similarity = typeof result.similarity === 'number' ? ` · similarity ${result.similarity.toFixed(3)}` : '';
            score.textContent = `Relevance ${Number(result.score).toFixed(3)}${similarity}`;

            card.append(link, text, score);
            resultsContainer.appendChild(card);
        });
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
});
