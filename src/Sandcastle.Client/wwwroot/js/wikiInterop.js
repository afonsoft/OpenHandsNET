// SPEC-145 — wikiInterop: renderiza blocos `mermaid` do markdown da
// wiki. O bundle vendored (lib/mermaid/mermaid.min.js, ~2.5MB) é
// carregado sob demanda — só quando a página tem diagrama.
window.wikiInterop = (() => {
    let mermaidPromise = null;

    function loadMermaid() {
        if (window.mermaid) {
            return Promise.resolve(window.mermaid);
        }
        if (!mermaidPromise) {
            mermaidPromise = new Promise((resolve, reject) => {
                const s = document.createElement("script");
                s.src = "lib/mermaid/mermaid.min.js";
                s.onload = () => resolve(window.mermaid);
                s.onerror = () => reject(new Error("mermaid load failed"));
                document.head.appendChild(s);
            }).then(m => {
                m.initialize({
                    startOnLoad: false,
                    securityLevel: "strict",
                    theme: document.documentElement.dataset.theme === "dark"
                        || document.documentElement.classList.contains("dark")
                        ? "dark" : "default",
                });
                return m;
            });
        }
        return mermaidPromise;
    }

    // Converte <pre><code class="language-mermaid|lang-mermaid">… em
    // <pre class="mermaid"> e roda mermaid.run sobre os nós.
    async function render(containerId) {
        const host = document.getElementById(containerId);
        if (!host) return false;
        const blocks = host.querySelectorAll(
            "pre code.language-mermaid, pre code.lang-mermaid");
        if (blocks.length === 0) return false;
        const mermaid = await loadMermaid();
        const targets = [];
        blocks.forEach(code => {
            const pre = code.parentElement;
            if (!pre || pre.tagName !== "PRE") return;
            const holder = document.createElement("pre");
            holder.className = "mermaid";
            holder.textContent = code.textContent;
            pre.replaceWith(holder);
            targets.push(holder);
        });
        try {
            await mermaid.run({ nodes: targets });
        } catch (e) {
            // diagrama inválido — mostra o source (fallback code block).
            console.warn("mermaid render failed", e);
        }
        return true;
    }

    return { render };
})();
