// SPEC-120: helpers da Notification API — permissão só é pedida no
// gesto do usuário (toggle em Settings), nunca no boot.
window.scNotify = {
    supported() { return 'Notification' in window; },
    permission() {
        return 'Notification' in window ? Notification.permission : 'denied';
    },
    async request() {
        if (!('Notification' in window)) return 'denied';
        try { return await Notification.requestPermission(); }
        catch { return Notification.permission; }
    },
    hidden() { return document.hidden; },
    show(title, options) {
        if (!('Notification' in window) || Notification.permission !== 'granted') {
            return false;
        }
        try {
            const n = new Notification(title, options || {});
            if (options?.url) {
                n.onclick = function () {
                    window.focus();
                    window.location.href = options.url;
                };
            }
            return true;
        } catch { return false; }
    }
};
