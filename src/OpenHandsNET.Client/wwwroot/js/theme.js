// SPEC-26 RF-001/RF-004: helpers de tema + keyboard da command palette.
// O atributo inicial `data-theme` é aplicado pelo ThemeInit inline no
// index.html (antes do CSS) — este módulo assume que ele já rodou.
window.ohnTheme = {
    get() {
        try { return localStorage.getItem('ohn.theme'); }
        catch { return null; }
    },
    set(theme) {
        try { localStorage.setItem('ohn.theme', theme); } catch { }
    },
    system() {
        return window.matchMedia('(prefers-color-scheme: light)').matches
            ? 'light' : 'dark';
    },
    apply(effective) {
        document.documentElement.dataset.theme = effective;
        document
            .querySelector('meta[name="color-scheme"]')
            ?.setAttribute('content', effective);
    },
    _media: null,
    _ref: null,
    watch(ref) {
        this._ref = ref;
        if (!this._media) {
            this._media = window.matchMedia('(prefers-color-scheme: light)');
            this._media.addEventListener('change', (e) => {
                this._ref?.invokeMethodAsync(
                    'OnSystemThemeChanged', e.matches ? 'light' : 'dark');
            });
        }
        return {};
    },
    unwatch() { this._ref = null; }
};

// Command palette: Ctrl+K ou "/" (fora de inputs) → evento global.
window.ohnKeys = {
    _ref: null,
    register(ref) {
        this._ref = ref;
        this._handler = (e) => {
            const tag = document.activeElement?.tagName;
            const typing = tag === 'INPUT' || tag === 'TEXTAREA'
                || tag === 'SELECT'
                || document.activeElement?.isContentEditable;
            if (((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k')
                || (e.key === '/' && !typing)) {
                e.preventDefault();
                this._ref?.invokeMethodAsync('OnPaletteHotkey');
            }
        };
        document.addEventListener('keydown', this._handler);
    },
    unregister() {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler);
            this._handler = null;
        }
        this._ref = null;
    }
};

// Auto-scroll inteligente (SPEC-26 RF-003): só rola se o usuário está no fim.
window.ohnChat = {
    _pinned: new WeakMap(),
    pin(el) {
        if (!el) return;
        this._pinned.set(el, true);
        el.addEventListener('scroll', () => {
            const nearBottom =
                el.scrollHeight - el.scrollTop - el.clientHeight < 48;
            this._pinned.set(el, nearBottom);
        });
    },
    scrollIfPinned(el) {
        if (el && this._pinned.get(el) !== false) {
            el.scrollTo({ top: el.scrollHeight });
        }
    }
};

// SPEC-46: <html lang> acompanha o idioma ativo (a11y/leitores de tela).
window.ohnI18n = {
    setLang(code) { document.documentElement.lang = code || 'pt-BR'; }
};

// SPEC-45: composer — auto-grow até ~10 linhas + helpers de caret.
window.ohnComposer = {
    grow(el) {
        if (!el) return;
        el.style.height = 'auto';
        el.style.height = Math.min(el.scrollHeight, 240) + 'px';
    },
    focus(el) { if (el) el.focus(); },
    caretAtStart(el) { return el ? el.selectionStart === 0 : false; }
};

// SPEC-116 (port OWUIC themes-custom): injeta <style id="user-theme"> com
// as vars da paleta e marca <html> com theme-{id}. 'default' limpa tudo —
// a paleta stock volta sem reload.
window.ohnTheme.setCustom = function (themeId, css) {
    var root = document.documentElement;
    Array.from(root.classList).forEach(function (cls) {
        if (cls.indexOf('theme-') === 0) root.classList.remove(cls);
    });
    var id = themeId && themeId !== 'default' ? themeId : null;
    if (id) root.classList.add('theme-' + id);
    var style = document.getElementById('user-theme');
    if (!style) {
        style = document.createElement('style');
        style.id = 'user-theme';
        document.head.appendChild(style);
    }
    style.textContent = id ? (css || '') : '';
    try {
        localStorage.setItem('ohn.theme.id', id || 'default');
        localStorage.setItem('ohn.theme.css', id ? (css || '') : '');
    } catch (e) { /* storage negado — aplica mesmo assim nesta sessão */ }
};
