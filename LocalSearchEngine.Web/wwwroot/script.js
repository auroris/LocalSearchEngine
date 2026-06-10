document.addEventListener('DOMContentLoaded', () => {
    const searchForm = document.getElementById('searchForm');
    const searchInput = document.getElementById('searchInput');
    const resultsContainer = document.getElementById('resultsContainer');
    
    // Search Functionality
    searchForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        const query = searchInput.value.trim();
        if (!query) return;

        resultsContainer.innerHTML = '<div style="text-align:center; color: var(--text-secondary);">Searching local vector database...</div>';

        try {
            const response = await fetch(`/api/search/query?q=${encodeURIComponent(query)}`);
            if (!response.ok) throw new Error('Search failed');
            
            const results = await response.json();
            displayResults(results, query);
        } catch (error) {
            resultsContainer.innerHTML = `<div class="message error">Error: ${error.message}</div>`;
        }
    });

    function displayResults(responseObj, query) {
        const results = responseObj.items || [];
        if (!results || results.length === 0) {
            resultsContainer.innerHTML = '<div style="text-align:center; color: var(--text-secondary);">No relevant results found. Try indexing some URLs first!</div>';
            return;
        }

        const terms = query.toLowerCase().split(' ').filter(t => t.length > 2);
        
        let headerHtml = `<div class="results-header" style="margin-bottom: 1rem; color: var(--text-secondary);">Found ${responseObj.totalMatches} total matches.</div>`;

        resultsContainer.innerHTML = headerHtml + results.map((result, index) => {
            // Very basic highlighting
            let highlightedText = result.text;
            terms.forEach(term => {
                const regex = new RegExp(`(${term})`, 'gi');
                highlightedText = highlightedText.replace(regex, '<mark>$1</mark>');
            });

            return `
                <div class="result-card" style="animation-delay: ${index * 0.1}s">
                    <a href="${result.url}" target="_blank" class="result-url">${result.url}</a>
                    <p class="result-text">${highlightedText}</p>
                    <span class="result-score">Similarity Score: ${result.score.toFixed(4)}</span>
                </div>
            `;
        }).join('');
    }
});
