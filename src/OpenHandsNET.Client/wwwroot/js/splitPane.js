// Divisor arrastável entre chat (esq) e workbench (dir); persiste em localStorage.
window.ohnSplit = {
    init(containerId, key) {
        const box = document.getElementById(containerId);
        if (!box) return;
        const divider = box.querySelector('.pane-divider');
        const left = box.querySelector('.pane-left');
        if (!divider || !left) return;
        const saved = localStorage.getItem('ohn-split-' + key);
        if (saved) left.style.flexBasis = saved;
        let dragging = false;
        divider.addEventListener('pointerdown', e => {
            dragging = true;
            divider.setPointerCapture(e.pointerId);
            e.preventDefault();
        });
        divider.addEventListener('pointermove', e => {
            if (!dragging) return;
            const rect = box.getBoundingClientRect();
            const pct = Math.min(75, Math.max(20, (e.clientX - rect.left) / rect.width * 100));
            left.style.flexBasis = pct + '%';
        });
        const end = () => {
            if (dragging) localStorage.setItem('ohn-split-' + key, left.style.flexBasis);
            dragging = false;
        };
        divider.addEventListener('pointerup', end);
        divider.addEventListener('pointercancel', end);
    },
};
