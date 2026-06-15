/**
 * Tests for WF-338 opt-in LayUI UX bundle JS features:
 * Feature A: wtmTheme (dark-mode toggle + localStorage persistence)
 * Feature C: wtmCounter (char counter wiring)
 */

const fs   = require('fs');
const path = require('path');
const vm   = require('vm');

const SRC = fs.readFileSync(
    path.resolve(__dirname, '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
    'utf8'
);

// ─── VM context factory ────────────────────────────────────────────────────────
function makeEnv({ storage = {}, bodyClasses = [] } = {}) {
    const store = Object.assign({}, storage);
    const localStorageMock = {
        getItem:    (k)    => (store[k] !== undefined ? store[k] : null),
        setItem:    (k, v) => { store[k] = v; },
        removeItem: (k)    => { delete store[k]; },
    };

    const classes = new Set(bodyClasses);
    const bodyMock = {
        classList: {
            add:      (c) => classes.add(c),
            remove:   (c) => classes.delete(c),
            contains: (c) => classes.has(c),
        },
    };

    const elementStore = {};
    const documentMock = {
        body: bodyMock,
        getElementById: (id) => elementStore[id] || null,
        createElement:  (tag) => ({ tagName: tag, setAttribute: () => {}, style: {}, appendChild: () => {} }),
        head: { appendChild: () => {} },
        querySelector: () => null,
        querySelectorAll: () => [],
    };

    const jqMock = function () {
        return {
            find: () => jqMock(),
            addClass: () => jqMock(),
            removeClass: () => jqMock(),
            css: () => jqMock(),
            attr: () => jqMock(),
            on: () => jqMock(),
            toggle: () => jqMock(),
            each: (fn) => { return jqMock(); },
            length: 0,
        };
    };
    jqMock.ajax = () => {};
    jqMock.fn   = {};
    jqMock.extend = function() { return {}; };

    const layuiMock = {
        use: function (mods, cb) { if (cb) cb(); },
        form:    { render: () => {}, on: () => {} },
        table:   { reload: () => {}, resize: () => {}, on: () => {} },
        layer:   { msg: () => {}, alert: () => {}, confirm: () => {} },
        element: { tabChange: () => {}, fold: () => {} },
        each: function (obj, fn) {
            if (Array.isArray(obj)) obj.forEach((item, i) => fn(i, item));
            else Object.keys(obj || {}).forEach(k => fn(k, obj[k]));
        },
    };

    const ctx = vm.createContext({
        window: {},
        document: documentMock,
        $: jqMock,
        console,
        localStorage: localStorageMock,
        layui: layuiMock,
        setTimeout: global.setTimeout.bind(global),
        clearTimeout: global.clearTimeout.bind(global),
        DONOTUSE_TABLAYID: undefined,
        DONOTUSE_COOKIEPRE: '',
        DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(SRC).runInContext(ctx);

    return { theme: ctx.wtmTheme, counter: ctx.wtmCounter, store, classes, elementStore, documentMock };
}

// ─── Feature A: wtmTheme ──────────────────────────────────────────────────────

describe('wtmTheme._storageKey', () => {
    test('returns expected key constant', () => {
        const { theme } = makeEnv();
        expect(theme._storageKey()).toBe('wtm_theme_class');
    });
});

describe('wtmTheme.init', () => {
    test('applies defaultCls when nothing in storage', () => {
        const { theme, classes } = makeEnv();
        theme.init('layui-bg-black');
        expect(classes.has('layui-bg-black')).toBe(true);
    });

    test('prefers stored class over defaultCls', () => {
        const { theme, classes } = makeEnv({ storage: { wtm_theme_class: 'my-theme' } });
        theme.init('layui-bg-black');
        expect(classes.has('my-theme')).toBe(true);
        expect(classes.has('layui-bg-black')).toBe(false);
    });

    test('does nothing when storage empty and defaultCls empty', () => {
        const { theme, classes } = makeEnv();
        theme.init('');
        expect(classes.size).toBe(0);
    });
});

describe('wtmTheme.toggle', () => {
    test('adds class when not active and persists to storage', () => {
        const { theme, classes, store } = makeEnv();
        theme.toggle('layui-bg-black');
        expect(classes.has('layui-bg-black')).toBe(true);
        expect(store['wtm_theme_class']).toBe('layui-bg-black');
    });

    test('removes class when active and clears storage', () => {
        const { theme, classes, store } = makeEnv({ bodyClasses: ['layui-bg-black'], storage: { wtm_theme_class: 'layui-bg-black' } });
        theme.toggle('layui-bg-black');
        expect(classes.has('layui-bg-black')).toBe(false);
        expect(store['wtm_theme_class']).toBeUndefined();
    });

    test('replaces old class when toggling a new one', () => {
        const { theme, classes, store } = makeEnv({ bodyClasses: ['old-theme'], storage: { wtm_theme_class: 'old-theme' } });
        theme.toggle('new-theme');
        expect(classes.has('old-theme')).toBe(false);
        expect(classes.has('new-theme')).toBe(true);
        expect(store['wtm_theme_class']).toBe('new-theme');
    });
});

// ─── Feature C: wtmCounter ────────────────────────────────────────────────────

describe('wtmCounter', () => {
    test('exists as a module', () => {
        const { counter } = makeEnv();
        expect(counter).toBeDefined();
        expect(typeof counter.init).toBe('function');
    });

    test('init does nothing when elements not found', () => {
        const { counter } = makeEnv();
        expect(() => counter.init('nonexistent', 'nonexistent_counter', 100)).not.toThrow();
    });

    test('init wires input event and updates counter', () => {
        const { counter, elementStore } = makeEnv();

        let inputListener = null;
        const fieldEl = {
            value: 'hello',
            addEventListener: (evt, fn) => { if (evt === 'input') inputListener = fn; }
        };
        let counterText = '';
        const counterEl = {
            set textContent(v) { counterText = v; },
            get textContent()  { return counterText; }
        };
        elementStore['myField'] = fieldEl;
        elementStore['myField_counter'] = counterEl;

        counter.init('myField', 'myField_counter', 20);
        expect(counterText).toBe('5/20');
        fieldEl.value = 'hello world';
        if (inputListener) inputListener();
        expect(counterText).toBe('11/20');
    });
});
