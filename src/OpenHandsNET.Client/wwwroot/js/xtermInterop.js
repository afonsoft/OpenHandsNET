// xterm.js interop — instância por tab de conversa.
window.ohnTerm = {
    terms: {},
    init(id) {
        const el = document.getElementById(id);
        if (!el || this.terms[id]) return;
        const term = new Terminal({
            convertEol: true,
            fontSize: 13,
            fontFamily: "'Cascadia Code', 'JetBrains Mono', monospace",
            theme: {
                background: '#0d1117', foreground: '#c9d1d9',
                cursor: '#58a6ff', selectionBackground: '#264f78',
            },
            cursorBlink: true,
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(el);
        fit.fit();
        this.terms[id] = { term, fit };
        const ro = new ResizeObserver(() => { try { fit.fit(); } catch { } });
        ro.observe(el);
    },
    write(id, data) {
        this.terms[id]?.term.write(data);
    },
    writeln(id, data) {
        this.terms[id]?.term.writeln(data);
    },
    clear(id) {
        this.terms[id]?.term.clear();
    },
    onInput(id, dotNetRef) {
        const t = this.terms[id];
        if (t) t.term.onData(d => dotNetRef.invokeMethodAsync('OnTerminalInput', d));
    },
    dispose(id) {
        this.terms[id]?.term.dispose();
        delete this.terms[id];
    },
};
