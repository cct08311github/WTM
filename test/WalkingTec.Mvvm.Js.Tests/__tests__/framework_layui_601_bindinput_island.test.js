// Tests for Issue #601 (#470-F): 'bindInput' DispatchAction case —
// TextBoxTagHelper's ChangeFunc/DoneFunc island migration off the inline
// oninput/onchange attributes ff.SafeHtml (DOMPurify) strips from every
// dialog partial and PostForm/BgRequest redraw.
//
// What this file locks in place:
//   1. DispatchAction gains a 'bindInput' case: addEventListener('input', ...)
//      for changeFunc, addEventListener('change', ...) for doneFunc, calling
//      fn(el.value) — reproducing the legacy onchange="fn(this.value)"
//      calling convention (DOM0 handler `this` === element; addEventListener
//      listener `this` === element too, so fn(el.value) is the same
//      (fn, argument) pair either way).
//   2. changeFunc/doneFunc are resolved through the SAME #558 guarded
//      window[name] resolver bindSubmit's beforeSubmit uses — now extracted
//      as the shared ff._resolveGuardedWindowFn(name) helper (identifier
//      regex + denylist + own-property + typeof function). Any failed check
//      silently skips JUST that one callback binding, never throws.
//   3. #578/#585-style containment: when action.formId is present, the
//      target element must resolve INSIDE that form (formEl.contains(el)) or
//      the whole bind is skipped.
//   4. bindSubmit's 'beforeSubmit' resolution now delegates to the SAME
//      shared ff._resolveGuardedWindowFn — no second, drifting resolver.
//   5. framework_layui.js's active-code eval( count is unchanged (still
//      exactly 1).
//
// Following the same convention as framework_layui_578_writeback_containment
// .test.js / framework_layui_585_taginput_hardening.test.js: loads a FRESH
// instance of the real framework_layui.js into its own vm context (real
// jsdom `document`) and calls the REAL ff.DispatchAction directly, so these
// tests exercise the actual shipped code, not a hand-rolled reimplementation.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const srcPath = path.resolve(
  __dirname,
  '../../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'
);
const src = fs.readFileSync(srcPath, 'utf8');

const stripLineComments = (text) =>
  text
    .split('\n')
    .map((line) => {
      const idx = line.indexOf('//');
      return idx === -1 ? line : line.slice(0, idx);
    })
    .join('\n');

const active = stripLineComments(src);

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#601 (#470-F) — source sweep', () => {
  test('DispatchAction switch contains a bindInput case', () => {
    expect(active).toMatch(/case\s+['"]bindInput['"]/);
  });

  function bindInputBlock() {
    const block = active.match(/case\s+['"]bindInput['"]:[\s\S]{0,2000}?\n\s*break;/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('bindInput case guards on action.elemId being a non-empty string', () => {
    const block = bindInputBlock();
    expect(block).toMatch(/!action\.elemId\s*\|\|\s*typeof\s+action\.elemId\s*!==\s*['"]string['"]/);
  });

  test('bindInput case resolves the target element via document.getElementById', () => {
    const block = bindInputBlock();
    expect(block).toMatch(/document\.getElementById\(\s*action\.elemId\s*\)/);
  });

  test('bindInput case resolves changeFunc/doneFunc through ff._resolveGuardedWindowFn (no second resolver)', () => {
    const block = bindInputBlock();
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.changeFunc\s*\)/);
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.doneFunc\s*\)/);
  });

  test('bindInput case binds changeFunc on the "input" event and doneFunc on the "change" event', () => {
    const block = bindInputBlock();
    expect(block).toMatch(/addEventListener\(\s*['"]input['"]/);
    expect(block).toMatch(/addEventListener\(\s*['"]change['"]/);
  });

  test('bindInput case gates on form containment when action.formId is present', () => {
    const block = bindInputBlock();
    expect(block).toMatch(/_biFormEl\.contains\(/);
  });

  test('bindSubmit case now delegates to the shared ff._resolveGuardedWindowFn resolver', () => {
    const block = active.match(/case\s+['"]bindSubmit['"]:[\s\S]{0,1200}?\n\s*case\s+['"]bindValidate['"]/)[0];
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.beforeSubmit\s*\)/);
  });

  test('ff._resolveGuardedWindowFn applies the identifier regex, denylist, own-property, and typeof-function guards', () => {
    const block = active.match(/_resolveGuardedWindowFn\s*:\s*function[\s\S]{0,700}?\n\s*\},/);
    expect(block).not.toBeNull();
    // eslint-disable-next-line no-useless-escape
    expect(block[0]).toMatch(/\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\/\.test\(\s*name\s*\)/);
    expect(block[0]).toMatch(/WTM_BEFORESUBMIT_DENYLIST\s*&&\s*WTM_BEFORESUBMIT_DENYLIST\.has\(\s*name\s*\)/);
    expect(block[0]).toMatch(/Object\.prototype\.hasOwnProperty\.call\(\s*window\s*,\s*name\s*\)/);
    expect(block[0]).toMatch(/typeof\s+window\[name\]\s*===\s*['"]function['"]/);
  });

  test('_islandModulesFor does NOT require any layui module for bindInput (pure DOM, like tagInput)', () => {
    // Issue #470 Slice K: bumped from 1800 — 'renderTransfer' added a new
    // _islandModulesFor branch (layui.transfer IS a layui.use(...) module,
    // unlike xm-select/tagInput/bindInput), growing the function body.
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,2200}?\n\s*\},/)[0];
    expect(block).not.toMatch(/a\.type\s*===\s*['"]bindInput['"]/);
  });

  test('no new Function( call sites anywhere in the active source', () => {
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('active-code eval( count is still exactly 1 after #601 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Real ff.DispatchAction harness (mirrors #578/#585's loadFreshFf)
// ---------------------------------------------------------------------------
function loadFreshFf() {
  const jqueryMock = Object.assign(
    function () { return { cookie: jest.fn() }; },
    { ajax: jest.fn(), cookie: jest.fn(), fn: {} }
  );
  const ctx = vm.createContext({
    window: {},
    document,
    layui: {},
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: jqueryMock,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx };
}

function appendForm(formId) {
  const form = document.createElement('form');
  form.id = formId;
  document.body.appendChild(form);
  return form;
}

function appendTextInput(parent, id) {
  const el = document.createElement('input');
  el.type = 'text';
  el.id = id;
  parent.appendChild(el);
  return el;
}

afterEach(() => { document.body.innerHTML = ''; });

describe('#601 bindInput — real ff.DispatchAction behavior', () => {
  test('happy path: changeFunc fires on the "input" event with el.value', () => {
    const { ff, windowObj } = loadFreshFf();
    const form = appendForm('wtForm_bi1');
    const el = appendTextInput(form, 'tb1');
    const spy = jest.fn();
    windowObj.myChange = spy;

    ff.DispatchAction({
      actions: [{ type: 'bindInput', elemId: 'tb1', changeFunc: 'myChange', formId: 'wtForm_bi1' }],
    });

    el.value = 'hello';
    el.dispatchEvent(new window.Event('input'));
    expect(spy).toHaveBeenCalledWith('hello');
  });

  test('happy path: doneFunc fires on the "change" event with el.value', () => {
    const { ff, windowObj } = loadFreshFf();
    const form = appendForm('wtForm_bi2');
    const el = appendTextInput(form, 'tb2');
    const spy = jest.fn();
    windowObj.myDone = spy;

    ff.DispatchAction({
      actions: [{ type: 'bindInput', elemId: 'tb2', doneFunc: 'myDone', formId: 'wtForm_bi2' }],
    });

    el.value = 'world';
    el.dispatchEvent(new window.Event('change'));
    expect(spy).toHaveBeenCalledWith('world');
  });

  test('both changeFunc and doneFunc can be bound on the same element independently', () => {
    const { ff, windowObj } = loadFreshFf();
    const el = appendTextInput(document.body, 'tb3');
    const changeSpy = jest.fn();
    const doneSpy = jest.fn();
    windowObj.myChange3 = changeSpy;
    windowObj.myDone3 = doneSpy;

    ff.DispatchAction({
      actions: [{ type: 'bindInput', elemId: 'tb3', changeFunc: 'myChange3', doneFunc: 'myDone3' }],
    });

    el.value = 'a';
    el.dispatchEvent(new window.Event('input'));
    expect(changeSpy).toHaveBeenCalledWith('a');
    expect(doneSpy).not.toHaveBeenCalled();

    el.value = 'b';
    el.dispatchEvent(new window.Event('change'));
    expect(doneSpy).toHaveBeenCalledWith('b');
  });

  test('no-ops without throwing when action.elemId is missing', () => {
    const { ff } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'bindInput', changeFunc: 'x' }] });
    }).not.toThrow();
  });

  test('no-ops without throwing when the target element does not exist in the DOM', () => {
    const { ff } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'does_not_exist', changeFunc: 'x' }] });
    }).not.toThrow();
  });

  // -------------------------------------------------------------------------
  // Guarded resolver — adversarial changeFunc/doneFunc names
  // -------------------------------------------------------------------------
  describe('adversarial changeFunc/doneFunc names — gate is skipped, never throws', () => {
    test('dotted name "a.b" is rejected — no listener fires', () => {
      const { ff, windowObj } = loadFreshFf();
      const el = appendTextInput(document.body, 'tb4');
      const spy = jest.fn();
      windowObj.a = { b: spy };

      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'tb4', changeFunc: 'a.b' }] });
      }).not.toThrow();

      el.value = 'x';
      expect(() => el.dispatchEvent(new window.Event('input'))).not.toThrow();
      expect(spy).not.toHaveBeenCalled();
    });

    test('denylisted global name "eval" is rejected even though own+callable+identifier', () => {
      const { ff, windowObj } = loadFreshFf();
      const el = appendTextInput(document.body, 'tb5');
      const spy = jest.fn();
      windowObj.eval = spy;

      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'tb5', changeFunc: 'eval' }] });
      }).not.toThrow();

      el.value = 'x';
      el.dispatchEvent(new window.Event('input'));
      expect(spy).not.toHaveBeenCalled();
    });

    test('non-function value is rejected', () => {
      const { ff, windowObj } = loadFreshFf();
      const el = appendTextInput(document.body, 'tb6');
      windowObj.notAFunction = 'just a string';

      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'tb6', changeFunc: 'notAFunction' }] });
      }).not.toThrow();

      el.value = 'x';
      expect(() => el.dispatchEvent(new window.Event('input'))).not.toThrow();
    });

    test('inherited-only own-property name ("constructor") is rejected', () => {
      const { ff } = loadFreshFf();
      const el = appendTextInput(document.body, 'tb7');

      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'tb7', changeFunc: 'constructor' }] });
      }).not.toThrow();

      el.value = 'x';
      expect(() => el.dispatchEvent(new window.Event('input'))).not.toThrow();
    });

    test('absent changeFunc/doneFunc simply binds nothing (no listeners, no throw)', () => {
      const { ff } = loadFreshFf();
      const el = appendTextInput(document.body, 'tb8');

      expect(() => {
        ff.DispatchAction({ actions: [{ type: 'bindInput', elemId: 'tb8' }] });
      }).not.toThrow();

      expect(() => {
        el.dispatchEvent(new window.Event('input'));
        el.dispatchEvent(new window.Event('change'));
      }).not.toThrow();
    });
  });

  // -------------------------------------------------------------------------
  // #578/#585-style form containment
  // -------------------------------------------------------------------------
  describe('#601 bindInput form-containment (mirrors #578/#585)', () => {
    test('regression: formId present, element inside the form -> binding proceeds', () => {
      const { ff, windowObj } = loadFreshFf();
      const form = appendForm('wtForm_c1');
      const el = appendTextInput(form, 'tbc1');
      const spy = jest.fn();
      windowObj.changeC1 = spy;

      ff.DispatchAction({
        actions: [{ type: 'bindInput', elemId: 'tbc1', changeFunc: 'changeC1', formId: 'wtForm_c1' }],
      });
      el.value = 'yes';
      el.dispatchEvent(new window.Event('input'));
      expect(spy).toHaveBeenCalledWith('yes');
    });

    test('SECURITY: formId present, element resolves OUTSIDE the form -> binding is skipped', () => {
      const { ff, windowObj } = loadFreshFf();
      appendForm('wtForm_c2');
      const outsider = appendTextInput(document.body, 'attackerTb');
      const spy = jest.fn();
      windowObj.changeC2 = spy;

      expect(() => {
        ff.DispatchAction({
          actions: [{ type: 'bindInput', elemId: 'attackerTb', changeFunc: 'changeC2', formId: 'wtForm_c2' }],
        });
      }).not.toThrow();

      outsider.value = 'no';
      outsider.dispatchEvent(new window.Event('input'));
      expect(spy).not.toHaveBeenCalled();
    });

    test('fail-closed: formId present but the form element does not exist in the DOM -> binding is skipped', () => {
      const { ff, windowObj } = loadFreshFf();
      const el = appendTextInput(document.body, 'tbc3');
      const spy = jest.fn();
      windowObj.changeC3 = spy;

      ff.DispatchAction({
        actions: [{ type: 'bindInput', elemId: 'tbc3', changeFunc: 'changeC3', formId: 'wtForm_does_not_exist' }],
      });
      el.value = 'no';
      el.dispatchEvent(new window.Event('input'));
      expect(spy).not.toHaveBeenCalled();
    });

    test('back-compat: formId absent (old island) -> binding proceeds unguarded exactly as before', () => {
      const { ff, windowObj } = loadFreshFf();
      const el = appendTextInput(document.body, 'tbc4');
      const spy = jest.fn();
      windowObj.changeC4 = spy;

      ff.DispatchAction({
        actions: [{ type: 'bindInput', elemId: 'tbc4', changeFunc: 'changeC4' }],
      });
      el.value = 'yes';
      el.dispatchEvent(new window.Event('input'));
      expect(spy).toHaveBeenCalledWith('yes');
    });
  });
});
