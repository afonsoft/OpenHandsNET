// SPEC-94: proxies corporativos podem bloquear assets de boot pela
// extensão na URL (.wasm/.dat/.dll) ou por sniffing de payload binário.
// Todo asset de _framework que não seja .js passa por uma cadeia de três
// camadas (adaptado do open-webui boot.js):
//   1. /framework-assets/{stem}/{ext}         — sem sufixo bloqueado na URL;
//      bytes idênticos, então o hash de integridade do boot valida nativo.
//   2. /framework-assets/{stem}/{ext}?enc=b64 — base64 text/plain, derrota
//      content-sniffing; SHA-256 revalidado no cliente via crypto.subtle.
//   3. defaultUri                             — dev / ambientes sem proxy.
// Se todas falharem, Blazor.start rejeita e #app mostra mensagem legível.
(function () {
    "use strict";

    var STRINGS = {
        "pt-BR": {
            bootTitle: "Não foi possível iniciar o aplicativo",
            bootBody1: "A rede ou o proxy corporativo bloqueou arquivos necessários ao carregamento (filtro de downloads por tipo de mídia).",
            bootBody2: "Recarregue a página. Se o problema persistir, contate o suporte de TI ou acesse por outra rede.",
            reload: "Recarregar"
        },
        "en-US": {
            bootTitle: "The app could not be started",
            bootBody1: "The network or corporate proxy blocked files required for startup (media-type download filtering).",
            bootBody2: "Reload the page. If the problem persists, contact IT support or try another network.",
            reload: "Reload"
        }
    };
    var locale = (typeof localStorage !== "undefined" &&
        localStorage.getItem("locale")) || navigator.language || "pt-BR";
    var strings = STRINGS[locale] || STRINGS[locale && locale.split("-")[0]]
        || STRINGS["pt-BR"];

    var frameworkSegment = "/_framework/";
    var mimeByExt = {
        wasm: "application/wasm",
        json: "application/json",
        map: "application/json",
        js: "text/javascript"
    };

    function mirrorUrl(stem, ext, enc) {
        var url = new URL("framework-assets/" + stem + "/" + ext,
            document.baseURI);
        if (enc) {
            url.searchParams.set("enc", enc);
        }
        return url;
    }

    function decodeBase64(text) {
        var clean = text.replace(/\s+/g, "");
        if (typeof Uint8Array.fromBase64 === "function") {
            return Uint8Array.fromBase64(clean);
        }
        var bin = atob(clean);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) {
            bytes[i] = bin.codePointAt(i);
        }
        return bytes;
    }

    // A integridade do manifesto de boot é "sha256-<base64>"; o payload b64
    // transforma os bytes no fio — revalidamos antes de usar.
    function verifyIntegrity(bytes, integrity) {
        if (!integrity) {
            return Promise.resolve(true);
        }
        var hash = null;
        var tokens = integrity.split(/\s+/);
        for (var i = 0; i < tokens.length; i++) {
            if (tokens[i].indexOf("sha256-") === 0) {
                hash = tokens[i].substring(7);
                break;
            }
        }
        if (hash === null || !window.crypto || !crypto.subtle) {
            return Promise.resolve(false);
        }
        return crypto.subtle.digest("SHA-256", bytes).then(function (buf) {
            var digest = new Uint8Array(buf);
            var bin = "";
            for (var j = 0; j < digest.length; j++) {
                bin += String.fromCodePoint(digest[j]);
            }
            return btoa(bin) === hash;
        });
    }

    function fetchEncoded(url, ext, integrity) {
        return fetch(url).then(function (response) {
            if (!response.ok) {
                throw new Error("b64 mirror " + response.status);
            }
            return response.text();
        }).then(function (text) {
            var bytes = decodeBase64(text);
            return verifyIntegrity(bytes, integrity).then(function (ok) {
                if (!ok) {
                    throw new Error("integrity mismatch for " + url.pathname);
                }
                var headers = new Headers();
                headers.set("Content-Type",
                    mimeByExt[ext] || "application/octet-stream");
                return new Response(bytes, { headers: headers });
            });
        });
    }

    function loadAsset(defaultUri, integrity, stem, ext) {
        var init = integrity ? { integrity: integrity } : {};
        var encoded = function () {
            return fetchEncoded(mirrorUrl(stem, ext, "b64"), ext, integrity);
        };
        return fetch(mirrorUrl(stem, ext, null), init).then(
            function (response) {
                return response.ok ? response : encoded();
            },
            encoded
        ).catch(function () {
            return fetch(defaultUri, init);
        });
    }

    function showBootError() {
        var app = document.getElementById("app");
        if (!app) {
            return;
        }
        app.innerHTML =
            '<div style="max-width:36rem;margin:4rem auto;padding:0 1rem;' +
            'font-family:sans-serif">' +
            '<h1 style="font-size:1.25rem">' + strings.bootTitle + "</h1>" +
            "<p>" + strings.bootBody1 + "</p>" +
            "<p>" + strings.bootBody2 + "</p>" +
            '<p><a href=".">' + strings.reload + "</a></p></div>";
    }

    try {
        Blazor.start({
            loadBootResource: function (type, name, defaultUri, integrity) {
                try {
                    var path = new URL(defaultUri, document.baseURI).pathname;
                    if (!path.includes(frameworkSegment) ||
                        path.endsWith(".js")) {
                        return null; // .js carrega normal — proxies liberam
                    }
                    var fileName = path.substring(path.lastIndexOf("/") + 1);
                    var dot = fileName.lastIndexOf(".");
                    if (dot <= 0) {
                        return null;
                    }
                    return loadAsset(
                        defaultUri,
                        integrity,
                        fileName.substring(0, dot),
                        fileName.substring(dot + 1));
                } catch {
                    return null;
                }
            }
        }).catch(function (err) {
            console.error("boot failed", err);
            showBootError();
        });
    } catch (err) {
        console.error("boot failed", err);
        showBootError();
    }
})();
