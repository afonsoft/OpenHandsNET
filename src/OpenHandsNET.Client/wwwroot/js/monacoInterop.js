// SPEC-21 RF-004: interop do Monaco vendored (lib/monaco/min/vs) —
// AMD loader + create/setValue/dispose. Zero CDN (airgapped-safe).
(function () {
    let _ready = null;
    const _editors = new Map();

    function load() {
        if (_ready) return _ready;
        _ready = new Promise((resolve, reject) => {
            if (window.monaco) return resolve(window.monaco);
            const s = document.createElement('script');
            s.src = 'lib/monaco/min/vs/loader.js';
            s.onload = () => {
                window.require.config({
                    paths: { vs: 'lib/monaco/min/vs' },
                });
                window.require(['vs/editor/editor.main'],
                    () => resolve(window.monaco), reject);
            };
            s.onerror = () => reject(new Error('monaco loader failed'));
            document.head.appendChild(s);
        });
        return _ready;
    }

    const theme = 'vs-dark';

    window.monacoInterop = {
        createEditor: async function (hostId, value, language, readOnly) {
            const monaco = await load();
            const host = document.getElementById(hostId);
            if (!host) return false;
            dispose(hostId);
            const ed = monaco.editor.create(host, {
                value: value || '',
                language: language || 'plaintext',
                readOnly: readOnly !== false,
                theme,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                automaticLayout: true,
                fontSize: 13,
            });
            _editors.set(hostId, ed);
            return true;
        },

        createDiffEditor: async function (hostId, original, modified, language) {
            const monaco = await load();
            const host = document.getElementById(hostId);
            if (!host) return false;
            dispose(hostId);
            const ed = monaco.editor.createDiffEditor(host, {
                readOnly: true,
                theme,
                renderSideBySide: true,
                minimap: { enabled: false },
                automaticLayout: true,
                fontSize: 13,
            });
            ed.setModel({
                original: monaco.editor.createModel(
                    original || '', language || 'plaintext'),
                modified: monaco.editor.createModel(
                    modified || '', language || 'plaintext'),
            });
            _editors.set(hostId, ed);
            return true;
        },

        setValue: function (hostId, value, language) {
            const ed = _editors.get(hostId);
            if (ed && ed.getModel) {
                const m = ed.getModel();
                if (m) {
                    window.monaco.editor.setModelLanguage(
                        m, language || 'plaintext');
                    m.setValue(value || '');
                }
            }
        },

        setDiff: function (hostId, original, modified, language) {
            const ed = _editors.get(hostId);
            if (ed && ed.setModel) {
                ed.setModel({
                    original: window.monaco.editor.createModel(
                        original || '', language || 'plaintext'),
                    modified: window.monaco.editor.createModel(
                        modified || '', language || 'plaintext'),
                });
            }
        },

        dispose,
    };

    function dispose(hostId) {
        const ed = _editors.get(hostId);
        if (ed) {
            try {
                const model = ed.getModel && ed.getModel();
                if (model && model.original) {
                    model.original.dispose();
                    model.modified.dispose();
                } else if (model) {
                    model.dispose();
                }
            } catch (_) { /* model already disposed */ }
            ed.dispose();
            _editors.delete(hostId);
        }
    }
})();
