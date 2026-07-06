// Tests for Issue #558 (#470-C): safe named-callback DispatchAction —
// a 'bindSubmit' action type that expresses the FormTagHelper submit-binding
// pattern (layui.form.on('submit(...)', fn) gated by a developer-named
// BeforeSubmit callback) through the eval-free JSON island, WITHOUT eval,
// new Function, or any other string-to-code path.
//
// What this file locks in place:
//   1. DispatchAction gains a 'bindSubmit' case that registers
//      layui.form.on('submit('+action.filter+')', handler) and no-ops
//      (without throwing) when action.filter is missing or layui.form.on is
//      unavailable.
//   2. action.beforeSubmit is resolved to a callable ONLY via a guarded
//      window[name] lookup: identifier-only regex, Object.prototype
//      hasOwnProperty (own property of window, not inherited), and
//      typeof === 'function'. Any failed check silently skips the gate — the
//      submit still proceeds via ff.PostForm — and NEVER throws.
//   3. ff._islandModulesFor maps 'bindSubmit' -> the 'form' module, and
//      dispatch of a bindSubmit island is deferred through
//      ff._dispatchIslandWhenReady / layui.use(['form'], ...) exactly like
//      #556's initForm/laydate deferral, so layui.form.on can never run
//      before the 'form' module has loaded.
//   4. The framework_layui.js active-code eval( count stays exactly 1 (the
//      deprecated _legacyScriptEval path) — no new eval/new Function
//      call sites were introduced.
//
// Following the same convention as framework_layui_556_laydate_island.test.js:
// source-sweep tests assert the real file structure; behavioral-stub tests
// re-implement the same logic (the vm-loaded `ff` module's closures were
// compiled without `document`/`layui` as bare identifiers in scope — see
// setup.js — so the live module functions cannot be exercised directly
// against real DOM/layui mocks here). Any drift between the stub and the
// real file is caught by the source-sweep tests.
//
// Issue #601 (#470-F) update: the four guard checks below (identifier regex,
// denylist, own-property, typeof-function) were EXTRACTED out of this case's
// inline body into the shared ff._resolveGuardedWindowFn(name) helper, so
// TextBoxTagHelper's new 'bindInput' action (ChangeFunc/DoneFunc) can reuse
// the exact same guard instead of a second, possibly-drifting copy. The
// source-sweep assertions below were retargeted at _resolveGuardedWindowFn
// itself (where the literal regex/denylist/hasOwnProperty/typeof checks now
// live) rather than at the bindSubmit case body; a new assertion locks that
// the bindSubmit case delegates to that shared helper. Behavior (all
// downstream describe blocks) is unchanged — bindSubmit's observable
// semantics did not change, only where the guard logic lives.

const fs = require('fs');
const path = require('path');

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
describe('#558 (#470-C) — source sweep', () => {
  test('DispatchAction switch contains a bindSubmit case', () => {
    expect(active).toMatch(/case\s+['"]bindSubmit['"]/);
  });

  function bindSubmitBlock() {
    // bindSubmit's guard clauses each contain their own early `break;`, so a
    // non-greedy match up to the FIRST `break;` would only capture the guard
    // line. Capture the whole case body instead, up to the switch's next
    // case label. Issue #564 added 'bindValidate' and 'highlightErrors'
    // cases immediately after bindSubmit (previously the last case before
    // default), so this now stops at 'bindValidate' rather than 'default:'.
    const block = active.match(/case\s+['"]bindSubmit['"]:[\s\S]{0,2500}?\n\s*case\s+['"]bindValidate['"]/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('bindSubmit case guards on action.filter being a non-empty string', () => {
    const block = bindSubmitBlock();
    expect(block).toMatch(/!action\.filter\s*\|\|\s*typeof\s+action\.filter\s*!==\s*['"]string['"]/);
  });

  test('bindSubmit case validates action.filter against the identifier regex (no-op on failure)', () => {
    const block = bindSubmitBlock();
    // eslint-disable-next-line no-useless-escape
    expect(block).toMatch(/!\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\/\.test\(\s*action\.filter\s*\)/);
  });

  // Issue #601: the four checks below (identifier regex, denylist,
  // own-property, typeof-function) were extracted out of the bindSubmit case
  // body into the shared ff._resolveGuardedWindowFn(name) helper — see the
  // '_resolveGuardedWindowFn' describe block further down, which now owns
  // these assertions (retargeted at `name` instead of `action.beforeSubmit`,
  // since the helper is name-agnostic and shared with 'bindInput', #601).
  // This test locks in that the bindSubmit case actually DELEGATES to that
  // shared helper rather than re-inlining an equivalent (and possibly
  // drifting) copy of the same checks.
  test('bindSubmit case resolves beforeSubmit via the shared ff._resolveGuardedWindowFn helper', () => {
    const block = bindSubmitBlock();
    expect(block).toMatch(/ff\._resolveGuardedWindowFn\(\s*action\.beforeSubmit\s*\)/);
  });

  test('bindSubmit case guards on layui.form.on being available', () => {
    const block = bindSubmitBlock();
    expect(block).toMatch(/typeof\s+layui\s*===\s*['"]undefined['"]/);
    expect(block).toMatch(/typeof\s+layui\.form\.on\s*!==\s*['"]function['"]/);
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

  test('bindSubmit case registers layui.form.on with the action.filter-derived submit event', () => {
    const block = bindSubmitBlock();
    expect(block).toMatch(/layui\.form\.on\(\s*['"]submit\(['"]\s*\+\s*action\.filter\s*\+\s*['"]\)['"]/);
  });

  test('bindSubmit case delegates the actual handler to ff._makeBindSubmitHandler (no inline eval/new Function)', () => {
    const block = bindSubmitBlock();
    expect(block).toMatch(/ff\._makeBindSubmitHandler\(/);
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });

  test('ff._makeBindSubmitHandler is defined and calls ff.PostForm, gated by beforeFn', () => {
    const block = active.match(/_makeBindSubmitHandler\s*:\s*function[\s\S]{0,500}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/beforeFn\s*&&\s*beforeFn\(data\)\s*===\s*false/);
    expect(block[0]).toMatch(/ff\.PostForm\(\s*url\s*,\s*formId\s*,\s*divId\s*\)/);
  });

  test('_islandModulesFor maps bindSubmit -> the form module', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,1600}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]bindSubmit['"][\s\S]{0,80}needed\.form\s*=\s*true/);
  });

  test('WTM_BEFORESUBMIT_DENYLIST is a module-level Set constant', () => {
    expect(active).toMatch(/var\s+WTM_BEFORESUBMIT_DENYLIST\s*=/);
    expect(active).toMatch(/new\s+Set\s*\(\s*\[/);
  });

  test('denylist Set contains the dangerous globals (eval/Function/setTimeout/fetch/open/postMessage/…)', () => {
    const block = active.match(/WTM_BEFORESUBMIT_DENYLIST\s*=[\s\S]{0,700}?\]\s*\)/);
    expect(block).not.toBeNull();
    const required = [
      'eval', 'Function', 'setTimeout', 'setInterval', 'fetch',
      'XMLHttpRequest', 'WebSocket', 'open', 'postMessage', 'alert',
      'confirm', 'prompt', 'queueMicrotask', 'requestAnimationFrame',
      'Worker', 'SharedWorker', 'importScripts', 'structuredClone'
    ];
    for (const name of required) {
      expect(block[0]).toMatch(new RegExp("['\"]" + name + "['\"]"));
    }
  });

  test('no new Function( call sites anywhere in the active source', () => {
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test('active-code eval( count is still exactly 1 after #558 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'bindSubmit' DispatchAction case + the window[name] guard
// ---------------------------------------------------------------------------
// Mirrors the real DispatchAction 'bindSubmit' case and _makeBindSubmitHandler
// exactly (locked in by the source-sweep tests above), exercised against an
// injectable `windowObj` and `layui` so adversarial beforeSubmit names can be
// probed without touching the real jsdom `window`.
describe('#558 DispatchAction bindSubmit — behavioral stub', () => {
  function makeBindSubmitHandler(beforeFn, formId, url, divId, postFormSpy) {
    return function (data) {
      if (beforeFn && beforeFn(data) === false) { return false; }
      postFormSpy(url, formId, divId);
      return false;
    };
  }

  // Mirrors the module-level WTM_BEFORESUBMIT_DENYLIST in framework_layui.js.
  // Kept in sync by the source-sweep test 'denylist Set contains the dangerous
  // globals' below.
  const DENYLIST = new Set([
    'eval', 'Function', 'setTimeout', 'setInterval', 'setImmediate',
    'fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource',
    'open', 'postMessage', 'alert', 'confirm', 'prompt',
    'queueMicrotask', 'requestAnimationFrame', 'requestIdleCallback',
    'Worker', 'SharedWorker', 'importScripts', 'structuredClone',
    'Image', 'navigator', 'location', 'document', 'window', 'globalThis',
    'Reflect', 'Proxy'
  ]);

  function resolveBeforeFn(windowObj, name) {
    if (name &&
        typeof name === 'string' &&
        /^[A-Za-z_$][\w$]*$/.test(name) &&
        !DENYLIST.has(name) &&
        Object.prototype.hasOwnProperty.call(windowObj, name) &&
        typeof windowObj[name] === 'function') {
      return windowObj[name];
    }
    return null;
  }

  function makeDispatcher(layui, windowObj, postFormSpy) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'bindSubmit': {
            if (!action.filter || typeof action.filter !== 'string' ||
                !/^[A-Za-z_$][\w$]*$/.test(action.filter)) { break; }
            if (typeof layui === 'undefined' || !layui || !layui.form ||
                typeof layui.form.on !== 'function') { break; }
            var beforeFn = resolveBeforeFn(windowObj, action.beforeSubmit);
            layui.form.on(
              'submit(' + action.filter + ')',
              makeBindSubmitHandler(
                beforeFn,
                action.formId || '',
                action.url || '',
                action.divId || '',
                postFormSpy
              )
            );
            break;
          }
          default:
            // ignore
        }
      }
    };
  }

  function makeLayui() {
    var handlers = {};
    return {
      form: {
        on: jest.fn(function (evt, fn) { handlers[evt] = fn; })
      },
      _fire: function (evt, data) {
        if (!handlers[evt]) { throw new Error('no handler registered for ' + evt); }
        return handlers[evt](data);
      },
      _hasHandler: function (evt) { return typeof handlers[evt] === 'function'; }
    };
  }

  // -------------------------------------------------------------------------
  // Happy path
  // -------------------------------------------------------------------------
  test('registers a layui.form.on("submit(filter)") handler', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);
    dispatch({ actions: [{ type: 'bindSubmit', filter: 'myFormfilter', formId: 'myForm', url: '', divId: 'body' }] });
    expect(layui.form.on).toHaveBeenCalledTimes(1);
    expect(layui.form.on.mock.calls[0][0]).toBe('submit(myFormfilter)');
    expect(layui._hasHandler('submit(myFormfilter)')).toBe(true);
  });

  test('submitting with no beforeSubmit calls ff.PostForm with url/formId/divId and returns false', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);
    dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', formId: 'myForm', url: '/submit', divId: 'body' }] });
    const ret = layui._fire('submit(f)', { field: { a: '1' } });
    expect(postFormSpy).toHaveBeenCalledTimes(1);
    expect(postFormSpy).toHaveBeenCalledWith('/submit', 'myForm', 'body');
    expect(ret).toBe(false);
  });

  test('a valid beforeSubmit function is resolved and invoked with the submit data', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const before = jest.fn().mockReturnValue(true);
    const windowObj = { myBeforeSubmit: before };
    const dispatch = makeDispatcher(layui, windowObj, postFormSpy);
    dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', beforeSubmit: 'myBeforeSubmit', formId: 'myForm', url: '', divId: 'body' }] });
    const data = { field: { x: '1' } };
    layui._fire('submit(f)', data);
    expect(before).toHaveBeenCalledTimes(1);
    expect(before).toHaveBeenCalledWith(data);
    expect(postFormSpy).toHaveBeenCalledTimes(1);
  });

  test('beforeSubmit returning false BLOCKS PostForm (submit cancelled)', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const before = jest.fn().mockReturnValue(false);
    const windowObj = { myBeforeSubmit: before };
    const dispatch = makeDispatcher(layui, windowObj, postFormSpy);
    dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', beforeSubmit: 'myBeforeSubmit', formId: 'myForm', url: '', divId: 'body' }] });
    const ret = layui._fire('submit(f)', {});
    expect(before).toHaveBeenCalledTimes(1);
    expect(postFormSpy).not.toHaveBeenCalled();
    expect(ret).toBe(false);
  });

  test('beforeSubmit returning a non-false truthy value still lets PostForm proceed', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const before = jest.fn().mockReturnValue('anything-not-false');
    const windowObj = { myBeforeSubmit: before };
    const dispatch = makeDispatcher(layui, windowObj, postFormSpy);
    dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', beforeSubmit: 'myBeforeSubmit', formId: 'myForm', url: '', divId: 'body' }] });
    layui._fire('submit(f)', {});
    expect(postFormSpy).toHaveBeenCalledTimes(1);
  });

  // -------------------------------------------------------------------------
  // Guards
  // -------------------------------------------------------------------------
  test('no-ops without throwing when action.filter is missing', () => {
    const layui = makeLayui();
    const dispatch = makeDispatcher(layui, {}, jest.fn());
    expect(() => {
      dispatch({ actions: [{ type: 'bindSubmit', beforeSubmit: 'x' }] });
    }).not.toThrow();
    expect(layui.form.on).not.toHaveBeenCalled();
  });

  test('no-ops without throwing when layui.form.on is unavailable', () => {
    const dispatch = makeDispatcher(undefined, {}, jest.fn());
    expect(() => {
      dispatch({ actions: [{ type: 'bindSubmit', filter: 'f' }] });
    }).not.toThrow();
  });

  test('no-ops without throwing when layui.form exists but has no "on" method', () => {
    const dispatch = makeDispatcher({ form: {} }, {}, jest.fn());
    expect(() => {
      dispatch({ actions: [{ type: 'bindSubmit', filter: 'f' }] });
    }).not.toThrow();
  });

  // -------------------------------------------------------------------------
  // ADVERSARIAL: the window[name] guard
  // -------------------------------------------------------------------------
  describe('adversarial beforeSubmit names — gate is skipped, never throws, submit still proceeds', () => {
    function runAdversarial(windowObj, beforeSubmitName) {
      const layui = makeLayui();
      const postFormSpy = jest.fn();
      const dispatch = makeDispatcher(layui, windowObj, postFormSpy);
      expect(() => {
        dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', beforeSubmit: beforeSubmitName, formId: 'myForm', url: '', divId: 'body' }] });
      }).not.toThrow();
      let ret;
      expect(() => {
        ret = layui._fire('submit(f)', {});
      }).not.toThrow();
      expect(postFormSpy).toHaveBeenCalledTimes(1);
      expect(postFormSpy).toHaveBeenCalledWith('', 'myForm', 'body');
      expect(ret).toBe(false);
    }

    test('dotted name "a.b" — rejected by the identifier regex', () => {
      const spy = jest.fn();
      const windowObj = { a: { b: spy } };
      runAdversarial(windowObj, 'a.b');
      expect(spy).not.toHaveBeenCalled();
    });

    test('"window" — self-reference (rejected by the denylist; also a non-function)', () => {
      const windowObj = {};
      windowObj.window = windowObj; // mimic window.window === window
      runAdversarial(windowObj, 'window');
    });

    test('bracketed name "x[0]" — rejected by the identifier regex', () => {
      const spy = jest.fn();
      const windowObj = { x: [spy] };
      runAdversarial(windowObj, 'x[0]');
      expect(spy).not.toHaveBeenCalled();
    });

    test('"constructor" — inherited from Object.prototype, not an OWN property, rejected by hasOwnProperty', () => {
      const windowObj = {}; // windowObj.constructor exists via the prototype chain only
      expect(Object.prototype.hasOwnProperty.call(windowObj, 'constructor')).toBe(false);
      runAdversarial(windowObj, 'constructor');
    });

    test('"toString" — inherited from Object.prototype, rejected by hasOwnProperty', () => {
      const windowObj = {};
      expect(Object.prototype.hasOwnProperty.call(windowObj, 'toString')).toBe(false);
      runAdversarial(windowObj, 'toString');
    });

    test('"__proto__" — prototype-chain trick, rejected (not an own data property with a function value)', () => {
      const windowObj = {};
      runAdversarial(windowObj, '__proto__');
    });

    test('name resolves to a non-function value — rejected by the typeof function check', () => {
      const windowObj = { notAFunction: 'just a string', alsoNot: 42, orThis: { nested: true } };
      runAdversarial(windowObj, 'notAFunction');
    });

    test('empty string beforeSubmit — gate skipped, submit proceeds', () => {
      const windowObj = {};
      runAdversarial(windowObj, '');
    });

    test('undefined beforeSubmit — gate skipped, submit proceeds', () => {
      const windowObj = {};
      runAdversarial(windowObj, undefined);
    });

    test('null beforeSubmit — gate skipped, submit proceeds', () => {
      const windowObj = {};
      runAdversarial(windowObj, null);
    });

    test('non-string beforeSubmit (a function object itself) — rejected by the typeof string check', () => {
      const maliciousFn = jest.fn();
      const windowObj = {};
      runAdversarial(windowObj, maliciousFn);
      expect(maliciousFn).not.toHaveBeenCalled();
    });

    test('a maliciously-named global function is NEVER invoked through a rejected name', () => {
      const malicious = jest.fn();
      const windowObj = { 'evil()//<script>': malicious };
      runAdversarial(windowObj, 'evil()//<script>');
      expect(malicious).not.toHaveBeenCalled();
    });
  });

  // -------------------------------------------------------------------------
  // ADVERSARIAL: denylisted dangerous globals (defense-in-depth)
  // -------------------------------------------------------------------------
  // Each of these is an own, callable property of the real window whose name
  // passes the identifier regex — so WITHOUT the denylist, the base guard
  // would resolve it and (if beforeSubmit were ever attacker-influenced)
  // hand an execution/exfiltration primitive to the submit path. With the
  // denylist, the name is rejected: the spy standing in for window[name] is
  // NEVER called, and ff.PostForm still fires (submit proceeds without a
  // before-hook). We simulate the dangerous global by placing a spy under
  // that own name on the injected windowObj — the denylist must reject it by
  // NAME regardless of what it points to.
  describe('adversarial denylisted globals — resolved as own functions but rejected by the denylist', () => {
    function runDenied(name) {
      const layui = makeLayui();
      const postFormSpy = jest.fn();
      const dangerousSpy = jest.fn();
      const windowObj = {};
      windowObj[name] = dangerousSpy; // own, callable, identifier-named
      // Sanity: without a denylist this WOULD resolve (own + fn + identifier).
      expect(Object.prototype.hasOwnProperty.call(windowObj, name)).toBe(true);
      expect(typeof windowObj[name]).toBe('function');
      expect(/^[A-Za-z_$][\w$]*$/.test(name)).toBe(true);

      const dispatch = makeDispatcher(layui, windowObj, postFormSpy);
      expect(() => {
        dispatch({ actions: [{ type: 'bindSubmit', filter: 'f', beforeSubmit: name, formId: 'myForm', url: '', divId: 'body' }] });
      }).not.toThrow();
      const ret = layui._fire('submit(f)', {});
      // The dangerous global is NEVER invoked...
      expect(dangerousSpy).not.toHaveBeenCalled();
      // ...and the submit still proceeds via ff.PostForm.
      expect(postFormSpy).toHaveBeenCalledTimes(1);
      expect(postFormSpy).toHaveBeenCalledWith('', 'myForm', 'body');
      expect(ret).toBe(false);
    }

    const DENIED = [
      'eval', 'Function', 'setTimeout', 'setInterval', 'setImmediate',
      'fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource',
      'open', 'postMessage', 'alert', 'confirm', 'prompt',
      'queueMicrotask', 'requestAnimationFrame', 'requestIdleCallback',
      'Worker', 'SharedWorker', 'importScripts', 'structuredClone',
      'Image', 'navigator', 'location', 'document', 'window', 'globalThis',
      'Reflect', 'Proxy'
    ];

    test.each(DENIED)('"%s" as beforeSubmit — gate skipped, name never invoked, PostForm still fires', (name) => {
      runDenied(name);
    });
  });

  // -------------------------------------------------------------------------
  // ADVERSARIAL: non-identifier filter — handler NOT registered (no-op)
  // -------------------------------------------------------------------------
  describe('adversarial filter values — non-identifier filters register no handler', () => {
    function expectNoHandler(filterValue) {
      const layui = makeLayui();
      const postFormSpy = jest.fn();
      const dispatch = makeDispatcher(layui, {}, postFormSpy);
      expect(() => {
        dispatch({ actions: [{ type: 'bindSubmit', filter: filterValue, formId: 'myForm', url: '', divId: 'body' }] });
      }).not.toThrow();
      expect(layui.form.on).not.toHaveBeenCalled();
    }

    test('filter "a)b" — parenthesis breakout attempt, no handler registered', () => {
      expectNoHandler('a)b');
    });

    test('filter "x y" — whitespace, no handler registered', () => {
      expectNoHandler('x y');
    });

    test('filter "" — empty string, no handler registered', () => {
      expectNoHandler('');
    });

    test('filter "a.b" — dotted, no handler registered', () => {
      expectNoHandler('a.b');
    });

    test('filter "f);evil(" — injection into the submit selector, no handler registered', () => {
      expectNoHandler('f);evil(');
    });

    test('a valid identifier filter still registers exactly one handler', () => {
      const layui = makeLayui();
      const dispatch = makeDispatcher(layui, {}, jest.fn());
      dispatch({ actions: [{ type: 'bindSubmit', filter: 'myForm1filter', formId: 'myForm', url: '', divId: 'body' }] });
      expect(layui.form.on).toHaveBeenCalledTimes(1);
      expect(layui.form.on.mock.calls[0][0]).toBe('submit(myForm1filter)');
    });
  });
});

// ---------------------------------------------------------------------------
// _islandModulesFor + deferred dispatch through layui.use(['form'], ...)
// ---------------------------------------------------------------------------
// Reuses the #556 "late-loading module" harness pattern: layui.form is
// initially ABSENT and only appears once load('form') is called, mirroring
// layui's real async module loading. This proves bindSubmit can never
// register its submit handler before the 'form' module is ready.
describe('#558 hardening — bindSubmit defers through layui.use(["form"]) until the form module loads', () => {
  function islandModulesFor(payload) {
    var needed = { form: false, laydate: false };
    if (payload && payload.actions) {
      for (var i = 0; i < payload.actions.length; i++) {
        var a = payload.actions[i];
        if (!a || !a.type) { continue; }
        if (a.type === 'laydate') {
          needed.laydate = true;
        } else if (a.type === 'initForm') {
          needed.form = true;
          if (a.dates && a.dates.length) { needed.laydate = true; }
        } else if (a.type === 'bindSubmit') {
          needed.form = true;
        }
      }
    }
    var mods = [];
    if (needed.form) { mods.push('form'); }
    if (needed.laydate) { mods.push('laydate'); }
    return mods;
  }

  function makeDispatchWhenReady(getLayui, dispatchAction) {
    return function (payload) {
      var mods = islandModulesFor(payload);
      var layui = getLayui();
      if (mods.length > 0 && layui && typeof layui.use === 'function') {
        layui.use(mods, function () { dispatchAction(payload); });
      } else {
        dispatchAction(payload);
      }
    };
  }

  function makeDispatchAction(getLayui) {
    return function (payload) {
      if (!payload || !payload.actions) { return; }
      payload.actions.forEach(function (a) {
        if (!a || !a.type) { return; }
        var layui = getLayui();
        if (a.type === 'bindSubmit') {
          if (!a.filter || typeof a.filter !== 'string') { return; }
          if (!layui || !layui.form || typeof layui.form.on !== 'function') { return; }
          layui.form.on('submit(' + a.filter + ')', function () { return false; });
        }
      });
    };
  }

  function makeLateLayui() {
    var pending = [];
    var loaded = { form: false, laydate: false };
    var formOn = jest.fn();
    var layui = {
      use: function (mods, cb) {
        pending.push({ mods: mods, cb: cb });
        flush();
      }
    };
    function flush() {
      pending = pending.filter(function (p) {
        var ready = p.mods.every(function (m) { return loaded[m]; });
        if (ready) { p.cb(); return false; }
        return true;
      });
    }
    return {
      layui: layui,
      formOn: formOn,
      load: function (mod) {
        loaded[mod] = true;
        if (mod === 'form') { layui.form = { on: formOn }; }
        flush();
      }
    };
  }

  test('_islandModulesFor returns ["form"] for a bindSubmit-only payload', () => {
    expect(islandModulesFor({ actions: [{ type: 'bindSubmit', filter: 'f' }] })).toEqual(['form']);
  });

  test('bindSubmit registration is DEFERRED (not dropped) when the form module is not yet loaded, then fires on load', () => {
    const harness = makeLateLayui();
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);

    dispatchWhenReady({ actions: [{ type: 'bindSubmit', filter: 'f' }] });
    // form module not yet loaded -> layui.form.on must NOT have fired yet.
    expect(harness.formOn).not.toHaveBeenCalled();

    harness.load('form');
    expect(harness.formOn).toHaveBeenCalledTimes(1);
    expect(harness.formOn.mock.calls[0][0]).toBe('submit(f)');
  });

  test('when the form module is ALREADY loaded, registration fires synchronously (no regression for the fast path)', () => {
    const harness = makeLateLayui();
    harness.load('form'); // module ready BEFORE dispatch
    const dispatchAction = makeDispatchAction(function () { return harness.layui; });
    const dispatchWhenReady = makeDispatchWhenReady(function () { return harness.layui; }, dispatchAction);

    dispatchWhenReady({ actions: [{ type: 'bindSubmit', filter: 'f' }] });
    expect(harness.formOn).toHaveBeenCalledTimes(1);
  });
});
