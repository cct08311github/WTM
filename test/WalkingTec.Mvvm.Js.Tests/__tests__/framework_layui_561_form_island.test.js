// Tests for Issue #561 (#470-B slice 2): FormTagHelper now emits a SINGLE
// wtm-dialog-init island carrying BOTH an 'initForm' action (replacing
// ff.RenderForm(Id)) and a 'bindSubmit' action (replacing the inline
// layui.form.on('submit(...)') + BeforeSubmit-gate script), instead of the
// legacy inline <script>.
//
// #556 and #558 already lock in 'initForm' and 'bindSubmit' individually as
// isolated single-action payloads. This file is the "payoff slice" check:
// a single {actions:[initForm, bindSubmit]} island — the exact shape
// FormTagHelper now emits — must dispatch BOTH actions from one
// ff.DispatchAction call, in order, without either interfering with the
// other.
//
// Following the same convention as framework_layui_556_laydate_island.test.js
// and framework_layui_558_bindsubmit.test.js: source-sweep tests assert the
// real file structure; the behavioral-stub re-implements the same
// initForm/bindSubmit logic already locked in by those files (the vm-loaded
// `ff` module's closures were compiled without `document`/`layui` as bare
// identifiers in scope — see setup.js — so the live module functions cannot
// be exercised directly against real DOM/layui mocks here). Any drift
// between the stub and the real file is caught by the source-sweep tests in
// #556/#558 (unchanged by this issue) plus the sweep below.

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
// Source sweep — the two action types this payoff slice depends on must
// still exist unchanged (FormTagHelper does not add a new DispatchAction
// case; it only combines two pre-existing ones — see #561 constraints).
// ---------------------------------------------------------------------------
describe('#561 (#470-B slice 2) — source sweep', () => {
  test('DispatchAction still has both an initForm case and a bindSubmit case', () => {
    expect(active).toMatch(/case\s+['"]initForm['"]/);
    expect(active).toMatch(/case\s+['"]bindSubmit['"]/);
  });

  test('_islandModulesFor maps both initForm and bindSubmit to the form module', () => {
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,1600}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]initForm['"][\s\S]{0,80}needed\.form\s*=\s*true/);
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]bindSubmit['"][\s\S]{0,80}needed\.form\s*=\s*true/);
  });

  test('no new eval/new Function call sites introduced (still exactly 1 legacy eval)', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: a combined {actions:[initForm, bindSubmit]} island —
// exactly the shape FormTagHelper (#561) now emits — dispatches both.
// ---------------------------------------------------------------------------
describe('#561 combined island — {actions:[initForm, bindSubmit]} dispatches both', () => {
  // Mirrors the real 'initForm' case (framework_layui.js): calls
  // layui.form.render(formType||null, filter||undefined). No laydate here
  // since FormTagHelper never emits a 'dates' array.
  function dispatchInitForm(layui, action) {
    if (layui && layui.form && typeof layui.form.render === 'function') {
      layui.form.render(action.formType || null, action.filter || undefined);
    }
  }

  // Mirrors the real 'bindSubmit' case + _makeBindSubmitHandler exactly
  // (locked in by #558's source-sweep tests).
  function makeBindSubmitHandler(beforeFn, formId, url, divId, postFormSpy) {
    return function (data) {
      if (beforeFn && beforeFn(data) === false) { return false; }
      postFormSpy(url, formId, divId);
      return false;
    };
  }

  function resolveBeforeFn(windowObj, name) {
    if (name &&
        typeof name === 'string' &&
        /^[A-Za-z_$][\w$]*$/.test(name) &&
        Object.prototype.hasOwnProperty.call(windowObj, name) &&
        typeof windowObj[name] === 'function') {
      return windowObj[name];
    }
    return null;
  }

  function dispatchBindSubmit(layui, windowObj, postFormSpy, action) {
    if (!action.filter || typeof action.filter !== 'string' ||
        !/^[A-Za-z_$][\w$]*$/.test(action.filter)) { return; }
    if (!layui || !layui.form || typeof layui.form.on !== 'function') { return; }
    var beforeFn = resolveBeforeFn(windowObj, action.beforeSubmit);
    layui.form.on(
      'submit(' + action.filter + ')',
      makeBindSubmitHandler(beforeFn, action.formId || '', action.url || '', action.divId || '', postFormSpy)
    );
  }

  function makeDispatcher(layui, windowObj, postFormSpy) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      payload.actions.forEach(function (action) {
        if (!action || !action.type) return;
        switch (action.type) {
          case 'initForm':
            dispatchInitForm(layui, action);
            break;
          case 'bindSubmit':
            dispatchBindSubmit(layui, windowObj, postFormSpy, action);
            break;
          default:
          // ignore — not exercised by this payoff-slice test
        }
      });
    };
  }

  function makeLayui() {
    var handlers = {};
    return {
      form: {
        render: jest.fn(),
        on: jest.fn(function (evt, fn) { handlers[evt] = fn; })
      },
      _fire: function (evt, data) {
        if (!handlers[evt]) { throw new Error('no handler registered for ' + evt); }
        return handlers[evt](data);
      },
      _hasHandler: function (evt) { return typeof handlers[evt] === 'function'; }
    };
  }

  // FormTagHelper's exact emitted shape: {actions:[{type:'initForm',filter:Id},
  // {type:'bindSubmit',filter:Id+'filter',formId:Id,divId:...}]}
  function formIslandPayload(overrides) {
    return {
      actions: [
        { type: 'initForm', filter: 'wtForm_1' },
        Object.assign(
          { type: 'bindSubmit', filter: 'wtForm_1filter', formId: 'wtForm_1', url: null, divId: 'ViewDivwtForm_1' },
          overrides || {}
        )
      ]
    };
  }

  test('dispatching a combined island calls layui.form.render AND registers the submit handler', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);

    dispatch(formIslandPayload());

    expect(layui.form.render).toHaveBeenCalledTimes(1);
    expect(layui.form.render).toHaveBeenCalledWith(null, 'wtForm_1');
    expect(layui.form.on).toHaveBeenCalledTimes(1);
    expect(layui.form.on.mock.calls[0][0]).toBe('submit(wtForm_1filter)');
    expect(layui._hasHandler('submit(wtForm_1filter)')).toBe(true);
  });

  test('firing the bound submit handler calls ff.PostForm with formId/url/divId from the island', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);

    dispatch(formIslandPayload());
    const ret = layui._fire('submit(wtForm_1filter)', { field: {} });

    expect(postFormSpy).toHaveBeenCalledTimes(1);
    // action.url is null (omitted from the JSON island) -> 'action.url || ""'
    // resolves it to '', matching the legacy ff.PostForm('', Id, ViewDivId)
    // call this replaces (PostForm then falls back to the form's own
    // action attribute when its url argument is empty).
    expect(postFormSpy).toHaveBeenCalledWith('', 'wtForm_1', 'ViewDivwtForm_1');
    expect(ret).toBe(false);
  });

  test('initForm runs even when bindSubmit is guarded off (non-identifier filter) — the two actions do not interfere', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);

    dispatch(formIslandPayload({ filter: 'bad filter' }));

    expect(layui.form.render).toHaveBeenCalledTimes(1);
    expect(layui.form.on).not.toHaveBeenCalled();
  });

  test('a beforeSubmit gate on the combined island still blocks PostForm when it returns false', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const before = jest.fn().mockReturnValue(false);
    const windowObj = { myGate: before };
    const dispatch = makeDispatcher(layui, windowObj, postFormSpy);

    dispatch(formIslandPayload({ beforeSubmit: 'myGate' }));
    layui._fire('submit(wtForm_1filter)', {});

    expect(before).toHaveBeenCalledTimes(1);
    expect(postFormSpy).not.toHaveBeenCalled();
  });

  test('a beforeSubmit gate on the combined island lets PostForm through when it returns true', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const before = jest.fn().mockReturnValue(true);
    const windowObj = { myGate: before };
    const dispatch = makeDispatcher(layui, windowObj, postFormSpy);

    dispatch(formIslandPayload({ beforeSubmit: 'myGate' }));
    layui._fire('submit(wtForm_1filter)', {});

    expect(before).toHaveBeenCalledTimes(1);
    expect(postFormSpy).toHaveBeenCalledTimes(1);
  });

  test('no beforeSubmit on the combined island — submit proceeds straight through', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);

    dispatch(formIslandPayload()); // no beforeSubmit override
    layui._fire('submit(wtForm_1filter)', {});

    expect(postFormSpy).toHaveBeenCalledTimes(1);
  });

  test('an OldPost-style island with initForm only (no bindSubmit) still renders the form and registers nothing', () => {
    const layui = makeLayui();
    const postFormSpy = jest.fn();
    const dispatch = makeDispatcher(layui, {}, postFormSpy);

    dispatch({ actions: [{ type: 'initForm', filter: 'wtForm_2' }] });

    expect(layui.form.render).toHaveBeenCalledWith(null, 'wtForm_2');
    expect(layui.form.on).not.toHaveBeenCalled();
  });
});
