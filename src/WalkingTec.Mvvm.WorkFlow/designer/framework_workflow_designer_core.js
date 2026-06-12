// framework_workflow_designer_core.js — WF-21.5
// Low-code workflow designer: lossless JSON codec, graph model + dirty tracking,
// byte-passthrough, API layer (WriteAsString-tolerant, XSRF header),
// source mode (raw JSON textarea + validate/format), localStorage crash-cache.
//
// Security rules (T-DSN-9):
//   - eval-free: no eval, no new Function, no Function constructor
//   - DOM: all mutations via createElement/createElementNS/textContent/addEventListener
//   - No CDN/network assets (layui/jquery from consumer wwwroot)
//   - No inline event handlers in generated markup
//   - RawNum is a class; instanceof-checked; unforgeable by JSON data
//   - layer.msg/alert never receive server-derived strings
//
// Design: IIFE, exposes window.WtmDesignerCore only.
// Dependencies: none (layui/$ available on window, accessed lazily).
// zh-CN UI strings inline; American-English code/comments.

'use strict';

(function () {

    // ─────────────────────────────────────────────────────────────────────────
    // 1. RawNum — lossless number literal wrapper
    //    Instances wrap the exact text of a JSON number token.
    //    The class ensures it cannot be forged by a JSON data object:
    //    parse() only constructs RawNum for actual JSON number tokens (not via
    //    JSON.parse, which would yield a plain JS number).
    // ─────────────────────────────────────────────────────────────────────────

    function RawNum(raw) {
        // raw is the verbatim lexeme from the JSON source, e.g. "9007199254740993",
        // "0.50", "1E2", "-0".
        if (typeof raw !== 'string') {
            throw new TypeError('RawNum: raw must be a string');
        }
        this.raw = raw;
    }

    RawNum.prototype.toString = function () { return this.raw; };
    RawNum.prototype.valueOf  = function () { return Number(this.raw); };

    // ─────────────────────────────────────────────────────────────────────────
    // 2. WtmJsonRaw — lossless JSON codec
    //    parse(text)    → tree (RawNum for number tokens, plain JS for everything else)
    //    stringify(tree)→ JSON text with RawNum literals emitted verbatim
    //
    //    The tokenizer is hand-rolled (eval-free) and processes JSON strictly per
    //    RFC 8259. It preserves every number as its exact source text.
    // ─────────────────────────────────────────────────────────────────────────

    var WtmJsonRaw = (function () {

        // ── 2a. Tokenizer ────────────────────────────────────────────────────
        // Tokens: '{', '}', '[', ']', ':', ',', string, number, true, false, null

        var T_LBRACE  = 'LBRACE';
        var T_RBRACE  = 'RBRACE';
        var T_LBRACK  = 'LBRACK';
        var T_RBRACK  = 'RBRACK';
        var T_COLON   = 'COLON';
        var T_COMMA   = 'COMMA';
        var T_STRING  = 'STRING';
        var T_NUMBER  = 'NUMBER';
        var T_TRUE    = 'TRUE';
        var T_FALSE   = 'FALSE';
        var T_NULL    = 'NULL';
        var T_EOF     = 'EOF';

        function tokenize(text) {
            var i = 0;
            var len = text.length;
            var tokens = [];

            function skipWhitespace() {
                while (i < len) {
                    var c = text[i];
                    if (c === ' ' || c === '\t' || c === '\n' || c === '\r') {
                        i++;
                    } else {
                        break;
                    }
                }
            }

            function readString() {
                // We are at the opening '"'.
                var start = i;
                i++; // skip opening quote
                while (i < len) {
                    var c = text[i];
                    if (c === '\\') {
                        // skip escape sequence; \uXXXX is 6 chars total but we just need
                        // to avoid stopping on the escaped '"'.
                        i += 2;
                    } else if (c === '"') {
                        i++; // skip closing quote
                        break;
                    } else {
                        i++;
                    }
                }
                return { type: T_STRING, raw: text.slice(start, i) };
            }

            function readNumber() {
                var start = i;
                // optional minus
                if (text[i] === '-') { i++; }
                // integer part
                if (text[i] === '0') {
                    i++;
                } else {
                    while (i < len && text[i] >= '0' && text[i] <= '9') { i++; }
                }
                // optional fraction
                if (i < len && text[i] === '.') {
                    i++;
                    while (i < len && text[i] >= '0' && text[i] <= '9') { i++; }
                }
                // optional exponent
                if (i < len && (text[i] === 'e' || text[i] === 'E')) {
                    i++;
                    if (i < len && (text[i] === '+' || text[i] === '-')) { i++; }
                    while (i < len && text[i] >= '0' && text[i] <= '9') { i++; }
                }
                return { type: T_NUMBER, raw: text.slice(start, i) };
            }

            function readLiteral(expected, tokenType) {
                if (text.slice(i, i + expected.length) === expected) {
                    i += expected.length;
                    return { type: tokenType };
                }
                throw new SyntaxError('WtmJsonRaw: unexpected token at position ' + i);
            }

            while (i < len) {
                skipWhitespace();
                if (i >= len) { break; }
                var ch = text[i];
                if (ch === '{') { i++; tokens.push({ type: T_LBRACE }); }
                else if (ch === '}') { i++; tokens.push({ type: T_RBRACE }); }
                else if (ch === '[') { i++; tokens.push({ type: T_LBRACK }); }
                else if (ch === ']') { i++; tokens.push({ type: T_RBRACK }); }
                else if (ch === ':') { i++; tokens.push({ type: T_COLON }); }
                else if (ch === ',') { i++; tokens.push({ type: T_COMMA }); }
                else if (ch === '"') { tokens.push(readString()); }
                else if (ch === '-' || (ch >= '0' && ch <= '9')) { tokens.push(readNumber()); }
                else if (ch === 't') { tokens.push(readLiteral('true', T_TRUE)); }
                else if (ch === 'f') { tokens.push(readLiteral('false', T_FALSE)); }
                else if (ch === 'n') { tokens.push(readLiteral('null', T_NULL)); }
                else {
                    throw new SyntaxError('WtmJsonRaw: unexpected character "' + ch + '" at position ' + i);
                }
            }
            tokens.push({ type: T_EOF });
            return tokens;
        }

        // ── 2b. Parser ───────────────────────────────────────────────────────

        function parseTokens(tokens) {
            var pos = 0;

            function current() { return tokens[pos]; }
            function consume(expectedType) {
                var tok = tokens[pos];
                if (expectedType && tok.type !== expectedType) {
                    throw new SyntaxError('WtmJsonRaw: expected ' + expectedType + ' but got ' + tok.type);
                }
                pos++;
                return tok;
            }

            function parseValue() {
                var tok = current();
                if (tok.type === T_LBRACE)  { return parseObject(); }
                if (tok.type === T_LBRACK)  { return parseArray(); }
                if (tok.type === T_STRING) {
                    consume();
                    // Use native JSON.parse to decode the string literal properly,
                    // including all escape sequences (\n, \uXXXX, etc.).
                    return JSON.parse(tok.raw);
                }
                if (tok.type === T_NUMBER) {
                    consume();
                    // Preserve exact literal — do NOT convert to a JS number.
                    return new RawNum(tok.raw);
                }
                if (tok.type === T_TRUE)  { consume(); return true; }
                if (tok.type === T_FALSE) { consume(); return false; }
                if (tok.type === T_NULL)  { consume(); return null; }
                throw new SyntaxError('WtmJsonRaw: unexpected token type ' + tok.type + ' at position ' + pos);
            }

            function parseObject() {
                consume(T_LBRACE);
                var obj = {};
                if (current().type === T_RBRACE) {
                    consume();
                    return obj;
                }
                while (true) {
                    var keyTok = consume(T_STRING);
                    var key = JSON.parse(keyTok.raw);
                    consume(T_COLON);
                    var val = parseValue();
                    obj[key] = val;
                    if (current().type === T_COMMA) {
                        consume();
                    } else {
                        break;
                    }
                }
                consume(T_RBRACE);
                return obj;
            }

            function parseArray() {
                consume(T_LBRACK);
                var arr = [];
                if (current().type === T_RBRACK) {
                    consume();
                    return arr;
                }
                while (true) {
                    arr.push(parseValue());
                    if (current().type === T_COMMA) {
                        consume();
                    } else {
                        break;
                    }
                }
                consume(T_RBRACK);
                return arr;
            }

            var result = parseValue();
            if (current().type !== T_EOF) {
                throw new SyntaxError('WtmJsonRaw: trailing content after JSON value');
            }
            return result;
        }

        // ── 2c. Public parse ─────────────────────────────────────────────────

        function parse(text) {
            if (typeof text !== 'string') {
                throw new TypeError('WtmJsonRaw.parse: text must be a string');
            }
            var tokens = tokenize(text);
            return parseTokens(tokens);
        }

        // ── 2d. Stringify ────────────────────────────────────────────────────
        //    RawNum → emit raw literal verbatim (keystone: T-DSN-3)
        //    string → JSON.stringify (normalizes escape forms; safe per design §2.1:
        //             canonicalizer normalizes string escapes identically)
        //    boolean/null → standard literal
        //    object/array → recurse (insertion order kept per §2.3)

        function stringify(tree) {
            if (tree instanceof RawNum) {
                return tree.raw;
            }
            if (tree === null) {
                return 'null';
            }
            if (typeof tree === 'boolean') {
                return tree ? 'true' : 'false';
            }
            if (typeof tree === 'string') {
                return JSON.stringify(tree);
            }
            if (typeof tree === 'number') {
                // Plain JS number fallback (e.g. from writeNum validation path).
                return JSON.stringify(tree);
            }
            if (Array.isArray(tree)) {
                var arrParts = [];
                for (var ai = 0; ai < tree.length; ai++) {
                    arrParts.push(stringify(tree[ai]));
                }
                return '[' + arrParts.join(',') + ']';
            }
            if (typeof tree === 'object') {
                var keys = Object.keys(tree);
                var objParts = [];
                for (var oi = 0; oi < keys.length; oi++) {
                    var k = keys[oi];
                    objParts.push(JSON.stringify(k) + ':' + stringify(tree[k]));
                }
                return '{' + objParts.join(',') + '}';
            }
            // undefined, functions, symbols → omit (JSON.stringify behavior)
            return undefined;
        }

        // ── 2e. Form binding helpers ─────────────────────────────────────────

        // Read a number for display in a form input.
        function readNum(rawNum) {
            if (rawNum instanceof RawNum) { return Number(rawNum.raw); }
            if (typeof rawNum === 'number') { return rawNum; }
            return NaN;
        }

        // JSON number grammar (RFC 8259): optional '-', digits, optional '.' + digits,
        // optional 'e'/'E' + optional '+'/'-' + digits.
        var JSON_NUMBER_RE = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+\-]?\d+)?$/;

        // Write a user-edited number literal into an object property.
        // Returns true if the literal was valid JSON number syntax; false otherwise.
        function writeNum(obj, key, userText) {
            var s = String(userText).trim();
            if (!JSON_NUMBER_RE.test(s)) { return false; }
            obj[key] = new RawNum(s);
            return true;
        }

        return {
            parse:     parse,
            stringify: stringify,
            readNum:   readNum,
            writeNum:  writeNum
        };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 3. GraphModel — dirty tracking + byte-passthrough
    //    Holds a parsed tree from WtmJsonRaw.parse() alongside the original
    //    raw bytes for the not-dirty case.
    //    The "not dirty → send original bytes" rule (T-DSN-1, §2.4 rule 1) is
    //    the ContentHash-stability keystone.
    // ─────────────────────────────────────────────────────────────────────────

    var GraphModel = (function () {
        var _originalBytes = null;  // verbatim server graphJson string
        var _tree = null;           // parsed WtmJsonRaw tree
        var _dirty = false;
        var _schemaVersion = null;  // top-level schemaVersion field
        var _versionId = null;
        var _versionNo = null;
        var _contentHash = null;
        var _draft = null;          // draft metadata { rowVer, lastSavedBy, lastSavedAt, baseContentHash }
        var _baseContentHash = null; // hash the current edit is based on

        function _extractSchemaVersion(tree) {
            if (tree && typeof tree === 'object' && !Array.isArray(tree)) {
                var sv = tree['schemaVersion'];
                if (sv instanceof RawNum) { return Number(sv.raw); }
                if (typeof sv === 'number') { return sv; }
            }
            return null;
        }

        // Load from a graph envelope (from GET /definitions/{code}/graph).
        // envelope: { graphJson, versionId, versionNo, contentHash, schemaVersion,
        //             publishedAt, publishedBy, draft? { graphJson, rowVer, lastSavedBy, lastSavedAt, baseContentHash } }
        // If a draft exists, the draft graphJson becomes the edit buffer (dirty = true).
        function load(envelope) {
            _dirty = false;
            _versionId = envelope ? envelope.versionId : null;
            _versionNo = envelope ? envelope.versionNo : null;
            _contentHash = envelope ? envelope.contentHash : null;
            _draft = (envelope && envelope.draft) || null;

            var graphJson = null;
            if (_draft && _draft.graphJson) {
                graphJson = _draft.graphJson;
                _baseContentHash = _draft.baseContentHash || null;
                _dirty = true; // draft = user had unsaved edits from a previous session
            } else if (envelope && envelope.graphJson) {
                graphJson = envelope.graphJson;
                _baseContentHash = envelope.contentHash || null;
            }

            if (graphJson) {
                _originalBytes = graphJson;
                _tree = WtmJsonRaw.parse(graphJson);
                _schemaVersion = _extractSchemaVersion(_tree);
            } else {
                _originalBytes = null;
                _tree = null;
                _schemaVersion = null;
            }
        }

        // Load raw JSON string directly (source-mode editor or load-old-version flow).
        // Always marks dirty (user authored this text explicitly).
        function loadRaw(rawJson) {
            _originalBytes = rawJson;
            _tree = WtmJsonRaw.parse(rawJson);
            _schemaVersion = _extractSchemaVersion(_tree);
            _dirty = true;
        }

        function getTree()          { return _tree; }
        function getSchemaVersion() { return _schemaVersion; }
        function isDirty()          { return _dirty; }
        function markDirty()        { _dirty = true; }
        function getDraft()         { return _draft; }

        function getVersionInfo() {
            return {
                versionId:       _versionId,
                versionNo:       _versionNo,
                contentHash:     _contentHash,
                baseContentHash: _baseContentHash
            };
        }

        // Produce the payload to send to the server.
        // Rule §2.4-1: not dirty → send original bytes verbatim (ContentHash stability).
        // Rule §2.4-2: dirty → WtmJsonRaw.stringify the tree (RawNum literals preserved).
        function getPayload() {
            if (!_dirty && _originalBytes !== null) {
                return _originalBytes;
            }
            if (_tree === null) { return '{}'; }
            return WtmJsonRaw.stringify(_tree);
        }

        // Patch a property in the tree at a dot-path (array of string keys or numeric indices).
        // Does not traverse into missing sub-objects.
        function setProperty(path, value) {
            if (!_tree || !Array.isArray(path) || path.length === 0) { return; }
            var node = _tree;
            for (var i = 0; i < path.length - 1; i++) {
                if (node === null || typeof node !== 'object') { return; }
                node = node[path[i]];
            }
            if (node === null || typeof node !== 'object') { return; }
            node[path[path.length - 1]] = value;
            _dirty = true;
        }

        // Read a property from the tree at a path (array of keys/indices).
        function getProperty(path) {
            if (!_tree || !Array.isArray(path)) { return undefined; }
            var node = _tree;
            for (var i = 0; i < path.length; i++) {
                if (node === null || typeof node !== 'object') { return undefined; }
                node = node[path[i]];
            }
            return node;
        }

        return {
            load:             load,
            loadRaw:          loadRaw,
            getTree:          getTree,
            getSchemaVersion: getSchemaVersion,
            isDirty:          isDirty,
            markDirty:        markDirty,
            getDraft:         getDraft,
            getVersionInfo:   getVersionInfo,
            getPayload:       getPayload,
            setProperty:      setProperty,
            getProperty:      getProperty
        };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 4. WfHeaders — canonical HTTP header names (FIX-B1 single source of truth)
    //    MUST be byte-identical to DesignerHeaderNames.cs on the server side.
    //    Cross-layer contract tests assert these literal strings match.
    // ─────────────────────────────────────────────────────────────────────────

    var WfHeaders = Object.freeze({
        Xsrf:         'X-WTM-WF-XSRF',
        ExpectedHash: 'X-WTM-WF-Expected-Hash',
        BaseHash:     'X-WTM-WF-Base-Hash'
    });

    // ─────────────────────────────────────────────────────────────────────────
    // 5. DesignerApi — API layer
    //    - WriteAsString-tolerant (server DTOs may encode numbers as strings via
    //      NumberHandling.WriteAsString; native JSON.parse accepts them fine)
    //    - Sends X-WTM-WF-XSRF antiforgery header on every mutating call
    //    - Graph payloads sent as raw text (Content-Type: application/json)
    //    - credentials: same-origin on every call (cookie auth)
    //    - No eval, no new Function, no innerHTML, no inline event handlers
    //    - Header names come from WfHeaders (FIX-B1 single source of truth)
    // ─────────────────────────────────────────────────────────────────────────

    var DesignerApi = (function () {
        var _xsrfToken = null;
        var _baseUrl = 'api/_workflow/designer';

        function setXsrfToken(token) {
            _xsrfToken = token;
        }

        function _mutatingHeaders() {
            var h = { 'Content-Type': 'application/json' };
            if (_xsrfToken) { h[WfHeaders.Xsrf] = _xsrfToken; }
            return h;
        }

        function _readingHeaders() {
            return {};
        }

        // GET bootstrap — returns { requestToken, error } (camelCase per BootstrapResponseDto)
        function getBootstrap() {
            return fetch(_baseUrl + '/bootstrap', {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.json(); });
        }

        // GET definitions — paged definition list
        function listDefinitions(page, pageSize) {
            var params = new URLSearchParams({ page: page || 1, pageSize: pageSize || 20 });
            return fetch(_baseUrl + '/definitions?' + params.toString(), {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.json(); });
        }

        // POST definitions — create definition head
        function createDefinition(dto) {
            return fetch(_baseUrl + '/definitions', {
                method: 'POST',
                credentials: 'same-origin',
                headers: _mutatingHeaders(),
                body: JSON.stringify(dto)
            }).then(function (r) { return r; });
        }

        // PUT definitions/{code} — update head metadata
        function updateDefinitionMeta(code, dto) {
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code), {
                method: 'PUT',
                credentials: 'same-origin',
                headers: _mutatingHeaders(),
                body: JSON.stringify(dto)
            }).then(function (r) { return r; });
        }

        // GET definitions/{code}/graph — load graph envelope
        function getGraph(code) {
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code) + '/graph', {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.json(); });
        }

        // GET definitions/{code}/versions — version history
        function getVersions(code) {
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code) + '/versions', {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.json(); });
        }

        // GET versions/{id}/graph — single immutable version graph
        function getVersionGraph(versionId) {
            return fetch(_baseUrl + '/versions/' + encodeURIComponent(versionId) + '/graph', {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.json(); });
        }

        // GET definitions/{code}/draft — load draft (null if none)
        function getDraft(code) {
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code) + '/draft', {
                method: 'GET',
                credentials: 'same-origin',
                headers: _readingHeaders()
            }).then(function (r) { return r.ok ? r.json() : null; });
        }

        // PUT definitions/{code}/draft — save draft
        // options: { rowVer, ifNoneMatch, baseContentHash }
        //   ifNoneMatch=true → If-None-Match: * (create new draft)
        //   rowVer           → If-Match: "<rowVer>" (update existing draft)
        //   baseContentHash  → X-WTM-WF-Base-Hash header (FIX-B1: was ?base= query param)
        function saveDraft(code, graphJson, options) {
            options = options || {};
            var headers = _mutatingHeaders();
            if (options.ifNoneMatch) {
                headers['If-None-Match'] = '*';
            } else if (options.rowVer !== undefined && options.rowVer !== null) {
                headers['If-Match'] = '"' + options.rowVer + '"';
            }
            // FIX-B1: send baseContentHash as X-WTM-WF-Base-Hash header, NOT as ?base= query param.
            // Server reads Request.Headers[DesignerHeaderNames.BaseHash] (WorkflowDesignerController.cs).
            if (options.baseContentHash) {
                headers[WfHeaders.BaseHash] = options.baseContentHash;
            }
            var url = _baseUrl + '/definitions/' + encodeURIComponent(code) + '/draft';
            return fetch(url, {
                method: 'PUT',
                credentials: 'same-origin',
                headers: headers,
                body: graphJson
            }).then(function (r) { return r; });
        }

        // DELETE definitions/{code}/draft — discard draft (antiforgery required)
        function deleteDraft(code) {
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code) + '/draft', {
                method: 'DELETE',
                credentials: 'same-origin',
                headers: _mutatingHeaders()
            }).then(function (r) { return r; });
        }

        // POST definitions/{code}/publish — publish graph
        // graphPayload: raw JSON string; expectedHash: the base content hash for CAS guard
        function publish(code, graphPayload, expectedHash) {
            var headers = _mutatingHeaders();
            // FIX-B1: use X-WTM-WF-Expected-Hash (was X-WTM-Expected-Hash — missing WF namespace).
            // Server reads Request.Headers[DesignerHeaderNames.ExpectedHash] (WorkflowDesignerController.cs).
            if (expectedHash) {
                headers[WfHeaders.ExpectedHash] = expectedHash;
            }
            return fetch(_baseUrl + '/definitions/' + encodeURIComponent(code) + '/publish', {
                method: 'POST',
                credentials: 'same-origin',
                headers: headers,
                body: graphPayload
            }).then(function (r) { return r; });
        }

        // POST validate — validate graph
        function validate(graphPayload) {
            return fetch(_baseUrl + '/validate', {
                method: 'POST',
                credentials: 'same-origin',
                headers: _mutatingHeaders(),
                body: graphPayload
            }).then(function (r) { return r.json(); });
        }

        return {
            setXsrfToken:         setXsrfToken,
            getBootstrap:         getBootstrap,
            listDefinitions:      listDefinitions,
            createDefinition:     createDefinition,
            updateDefinitionMeta: updateDefinitionMeta,
            getGraph:             getGraph,
            getVersions:          getVersions,
            getVersionGraph:      getVersionGraph,
            getDraft:             getDraft,
            saveDraft:            saveDraft,
            deleteDraft:          deleteDraft,
            publish:              publish,
            validate:             validate
        };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 5. SourceMode — raw JSON textarea editor
    //    format (pretty-print) + syntax validation.
    //    All DOM mutations: createElement, textContent, addEventListener only.
    //    layer.msg/layer.alert are NOT used for server-derived strings (T-DSN-9).
    // ─────────────────────────────────────────────────────────────────────────

    var SourceMode = (function () {
        var _textarea = null;
        var _errorStrip = null;

        // Create the source-mode UI panel and insert into containerEl.
        // Returns { textarea, errorStrip } as DOM elements.
        function init(containerEl) {
            var wrapper = document.createElement('div');
            wrapper.className = 'wtm-source-mode';

            var label = document.createElement('p');
            label.textContent = '源码模式 (JSON)';
            wrapper.appendChild(label);

            _textarea = document.createElement('textarea');
            _textarea.className = 'wtm-source-textarea';
            _textarea.setAttribute('rows', '20');
            _textarea.setAttribute('cols', '80');
            _textarea.setAttribute('spellcheck', 'false');
            wrapper.appendChild(_textarea);

            var btnRow = document.createElement('div');
            btnRow.className = 'wtm-source-btnrow';

            var fmtBtn = document.createElement('button');
            fmtBtn.textContent = '格式化';
            fmtBtn.addEventListener('click', formatJson);
            btnRow.appendChild(fmtBtn);
            wrapper.appendChild(btnRow);

            _errorStrip = document.createElement('div');
            _errorStrip.className = 'wtm-source-error';
            wrapper.appendChild(_errorStrip);

            containerEl.appendChild(wrapper);
            return { textarea: _textarea, errorStrip: _errorStrip };
        }

        // Load raw JSON into the textarea.
        function loadText(jsonText) {
            if (_textarea) { _textarea.value = jsonText || ''; }
        }

        // Read the current textarea content.
        function getText() {
            return _textarea ? _textarea.value : '';
        }

        // Format JSON in the textarea (pretty-print via hand-rolled WtmJsonRaw).
        // Error message set via textContent — never layer.msg with server strings.
        function formatJson() {
            if (!_textarea) { return; }
            var raw = _textarea.value;
            try {
                var parsed = WtmJsonRaw.parse(raw);
                _textarea.value = _prettyStringify(parsed, 0);
                _showError('');
            } catch (e) {
                _showError('JSON 格式错误: ' + e.message);
            }
        }

        // Pretty-print a WtmJsonRaw tree (RawNum literals preserved verbatim).
        function _prettyStringify(tree, indent) {
            var spaces = '  ';
            var pad = '';
            for (var i = 0; i < indent; i++) { pad += spaces; }
            var childPad = pad + spaces;

            if (tree instanceof RawNum) { return tree.raw; }
            if (tree === null)         { return 'null'; }
            if (typeof tree === 'boolean') { return tree ? 'true' : 'false'; }
            if (typeof tree === 'string')  { return JSON.stringify(tree); }
            if (typeof tree === 'number')  { return JSON.stringify(tree); }

            if (Array.isArray(tree)) {
                if (tree.length === 0) { return '[]'; }
                var arrLines = [];
                for (var ai = 0; ai < tree.length; ai++) {
                    arrLines.push(childPad + _prettyStringify(tree[ai], indent + 1));
                }
                return '[\n' + arrLines.join(',\n') + '\n' + pad + ']';
            }

            if (typeof tree === 'object') {
                var keys = Object.keys(tree);
                if (keys.length === 0) { return '{}'; }
                var objLines = [];
                for (var oi = 0; oi < keys.length; oi++) {
                    var k = keys[oi];
                    objLines.push(childPad + JSON.stringify(k) + ': ' + _prettyStringify(tree[k], indent + 1));
                }
                return '{\n' + objLines.join(',\n') + '\n' + pad + '}';
            }
            return 'null';
        }

        function _showError(msg) {
            if (_errorStrip) {
                // textContent only — no innerHTML (T-DSN-9)
                _errorStrip.textContent = msg;
                _errorStrip.style.display = msg ? 'block' : 'none';
            }
        }

        function showError(msg) { _showError(msg); }

        // Validate parse and return { ok, error }
        function validateSyntax() {
            var raw = getText();
            try {
                WtmJsonRaw.parse(raw);
                _showError('');
                return { ok: true, error: null };
            } catch (e) {
                _showError('JSON 格式错误: ' + e.message);
                return { ok: false, error: e.message };
            }
        }

        return {
            init:           init,
            loadText:       loadText,
            getText:        getText,
            formatJson:     formatJson,
            validateSyntax: validateSyntax,
            showError:      showError
        };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 6. LocalStorageCache — crash-recovery cache (best-effort, NOT durability)
    //    Server draft is the durability story. This is labeled 本机备份 in the UI.
    //    Falls back silently if localStorage is unavailable (IT-managed desktops).
    // ─────────────────────────────────────────────────────────────────────────

    var LocalStorageCache = (function () {
        var _keyPrefix = 'wtm_wf_designer_';

        function _key(code) { return _keyPrefix + code; }

        // Save graphJson for a definition code with a timestamp.
        function save(code, graphJson) {
            try {
                var payload = JSON.stringify({ ts: Date.now(), json: graphJson });
                localStorage.setItem(_key(code), payload);
            } catch (e) {
                // Quota exceeded or unavailable — silently ignore.
            }
        }

        // Load { ts, json } for a definition code. Returns null if missing or corrupt.
        function load(code) {
            try {
                var raw = localStorage.getItem(_key(code));
                if (!raw) { return null; }
                var obj = JSON.parse(raw);
                return (obj && typeof obj.json === 'string') ? obj : null;
            } catch (e) {
                return null;
            }
        }

        // Clear the crash-recovery cache for a definition code.
        function clear(code) {
            try {
                localStorage.removeItem(_key(code));
            } catch (e) {
                // silently ignore
            }
        }

        return { save: save, load: load, clear: clear };
    }());

    // ─────────────────────────────────────────────────────────────────────────
    // 7. SchemaVersion guard — T-DSN-12
    //    schemaVersion != 1 → form editing locked; source + SVG view available.
    //    Exported for forms/view modules to call before enabling editing.
    // ─────────────────────────────────────────────────────────────────────────

    var SUPPORTED_SCHEMA_VERSION = 1;

    function isSchemaVersionSupported(sv) {
        return sv === SUPPORTED_SCHEMA_VERSION;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Expose window.WtmDesignerCore
    //    (only if window is available — tests run in jsdom which provides window)
    // ─────────────────────────────────────────────────────────────────────────

    if (typeof window !== 'undefined') {
        window.WtmDesignerCore = {
            RawNum:                   RawNum,
            WtmJsonRaw:               WtmJsonRaw,
            GraphModel:               GraphModel,
            DesignerApi:              DesignerApi,
            SourceMode:               SourceMode,
            LocalStorageCache:        LocalStorageCache,
            SUPPORTED_SCHEMA_VERSION: SUPPORTED_SCHEMA_VERSION,
            isSchemaVersionSupported: isSchemaVersionSupported,
            // FIX-B1: expose WfHeaders so tests can assert cross-layer contract.
            WfHeaders:                WfHeaders,
            version:                  'WF-21.5'
        };
    }

}());
