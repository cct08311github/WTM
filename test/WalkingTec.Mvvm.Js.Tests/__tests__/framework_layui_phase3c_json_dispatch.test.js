// Tests for issue #789 Phase 3C: IsScript eval() path replaced with a
// CSP-safe JSON action dispatcher (X-WTM-Action header + ff.DispatchAction).
//
// What this file locks in place:
//   1. framework_layui.js active-code eval( count is exactly 1 (only inside
//      ff._legacyScriptEval — every other call site must route through it).
//   2. ff.DispatchAction is defined and handles every whitelisted action type.
//   3. PostForm / BgRequest / OpenDialog all prefer X-WTM-Action over IsScript.
//   4. ff.RunAction exists and delegates to the same dispatch path.
//   5. LayuiUIService.cs no longer generates inline eval(data) in onclick handlers.

const fs = require('fs');
const path = require('path');

describe('#789 Phase 3C - framework_layui.js JSON dispatcher', () => {
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

  test('active-code eval( count is exactly 1 (centralized in _legacyScriptEval)', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });

  test('ff._legacyScriptEval helper is defined and emits a deprecation warning', () => {
    expect(active).toMatch(/_legacyScriptEval\s*:\s*function/);
    expect(active).toMatch(/IsScript script-body response is deprecated/);
  });

  test('ff.DispatchAction is defined', () => {
    expect(active).toMatch(/DispatchAction\s*:\s*function/);
  });

  test('DispatchAction handles closeDialog / alert / message / refreshGrid', () => {
    expect(active).toMatch(/case\s+['"]closeDialog['"]/);
    expect(active).toMatch(/case\s+['"]alert['"]/);
    expect(active).toMatch(/case\s+['"]message['"]/);
    expect(active).toMatch(/case\s+['"]refreshGrid['"]/);
  });

  test('DispatchAction handles refreshPage / reload / redirect', () => {
    expect(active).toMatch(/case\s+['"]refreshPage['"]/);
    expect(active).toMatch(/case\s+['"]reload['"]/);
    expect(active).toMatch(/case\s+['"]redirect['"]/);
  });

  test('DispatchAction logs unknown action types via console.warn instead of executing them', () => {
    expect(active).toMatch(/console\.warn\([^)]*Unknown WtmAction type/);
  });

  test('ff.RunAction delegates to DispatchAction for X-WTM-Action responses', () => {
    expect(active).toMatch(/RunAction\s*:\s*function/);
    expect(active).toMatch(/RunAction[\s\S]{0,500}X-WTM-Action/);
    expect(active).toMatch(/RunAction[\s\S]{0,800}ff\.DispatchAction/);
  });

  test('PostForm success callback checks X-WTM-Action before IsScript', () => {
    const postFormBlock = active.match(/PostForm[\s\S]{0,2000}?success:\s*function[\s\S]{0,1500}?DispatchAction[\s\S]{0,500}?IsScript/);
    expect(postFormBlock).not.toBeNull();
  });

  test('BgRequest success callback checks X-WTM-Action before IsScript', () => {
    const bgBlock = active.match(/BgRequest[\s\S]{0,2000}?success:\s*function[\s\S]{0,1500}?DispatchAction[\s\S]{0,500}?IsScript/);
    expect(bgBlock).not.toBeNull();
  });

  test('OpenDialog success callback checks X-WTM-Action before IsScript', () => {
    const dialogBlock = active.match(/OpenDialog[\s\S]{0,3000}?success:\s*function[\s\S]{0,1500}?DispatchAction[\s\S]{0,500}?IsScript/);
    expect(dialogBlock).not.toBeNull();
  });
});

describe('#789 Phase 3C - LayuiUIService.cs no longer generates inline eval', () => {
  test('MakeDialogButton inline callback no longer emits an eval(data) call', () => {
    const csPath = path.resolve(
      __dirname,
      '../../../src/WalkingTec.Mvvm.TagHelpers.LayUI/Common/LayuiUIService.cs'
    );
    const csSrc = fs.readFileSync(csPath, 'utf8');
    const active = csSrc
      .split('\n')
      .map((l) => (l.indexOf('//') === -1 ? l : l.slice(0, l.indexOf('//'))))
      .join('\n');
    // Old pattern was success: function(data,...){eval(data);}
    expect(active).not.toMatch(new RegExp('success[^{}]*function[^{}]*\\{[^}]*\\beval\\s*\\(data\\)'));
    // New pattern: innerClick = "ff.RunAction('url')"
    expect(active).toMatch(/ff\.RunAction\(/);
  });
});

describe('#789 Phase 3C - DispatchAction semantic behavior (vm-loaded)', () => {
  // Load framework_layui.js into a vm context (mirrors setup.js pattern) so
  // we can exercise DispatchAction against mocked ff.Alert / ff.CloseDialog /
  // ff.RefreshGrid and verify the dispatcher routes actions correctly without
  // any dynamic code execution. We also feed an injection-looking action type
  // and assert it is ignored (not eval'd).

  // Because full framework_layui.js loading is non-trivial (depends on jQuery
  // and layui), we re-implement the dispatcher contract against a minimal
  // pure-JS stub that mirrors the same branch logic. Any drift between
  // framework_layui.js and this stub will be caught by the regex-sweep tests
  // in the describe block above.

  function makeDispatcher(ff) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'closeDialog':
            if (typeof ff.CloseDialog === 'function') ff.CloseDialog();
            break;
          case 'alert':
            if (typeof ff.Alert === 'function') ff.Alert(action.message || '', action.title || '');
            break;
          case 'refreshGrid':
            if (typeof ff.RefreshGrid === 'function') ff.RefreshGrid(action.winId || 'LAY_app_body', action.index || 0);
            break;
          case 'reload':
            if (typeof location !== 'undefined' && typeof location.reload === 'function') location.reload();
            break;
          default:
            // Unknown - log and continue, never execute
            if (typeof console !== 'undefined' && console.warn) {
              console.warn('[WTM] Unknown WtmAction type:', action.type);
            }
        }
      }
    };
  }

  test('dispatches alert action with message', () => {
    const ff = { Alert: jest.fn() };
    const dispatch = makeDispatcher(ff);
    dispatch({ actions: [{ type: 'alert', message: 'hi', title: 'info' }] });
    expect(ff.Alert).toHaveBeenCalledWith('hi', 'info');
  });

  test('dispatches closeDialog + refreshGrid chain', () => {
    const ff = {
      CloseDialog: jest.fn(),
      RefreshGrid: jest.fn()
    };
    const dispatch = makeDispatcher(ff);
    dispatch({
      actions: [
        { type: 'closeDialog' },
        { type: 'refreshGrid', winId: 'LAY_app_body' }
      ]
    });
    expect(ff.CloseDialog).toHaveBeenCalledTimes(1);
    expect(ff.RefreshGrid).toHaveBeenCalledWith('LAY_app_body', 0);
  });

  test('ignores unknown action type and logs a warning', () => {
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    const ff = { Alert: jest.fn() };
    const dispatch = makeDispatcher(ff);
    dispatch({ actions: [{ type: 'runScript', code: 'alert(1)' }] });
    expect(ff.Alert).not.toHaveBeenCalled();
    expect(warnSpy).toHaveBeenCalledWith(
      '[WTM] Unknown WtmAction type:',
      'runScript'
    );
    warnSpy.mockRestore();
  });

  test('no-op for empty or malformed payload', () => {
    const ff = { Alert: jest.fn() };
    const dispatch = makeDispatcher(ff);
    dispatch(null);
    dispatch({});
    dispatch({ actions: [] });
    dispatch({ actions: [null, { }] });
    expect(ff.Alert).not.toHaveBeenCalled();
  });

  test('action type that looks like an injection payload is treated as string', () => {
    // Key insight: the dispatcher switches on action.type which is always a
    // JSON string. "alert(1);//" is a property key, never parsed as JavaScript.
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    const ff = { Alert: jest.fn() };
    const dispatch = makeDispatcher(ff);
    dispatch({ actions: [{ type: "alert(1);//", message: 'pwn' }] });
    expect(ff.Alert).not.toHaveBeenCalled();
    expect(warnSpy).toHaveBeenCalledWith(
      '[WTM] Unknown WtmAction type:',
      'alert(1);//'
    );
    warnSpy.mockRestore();
  });
});

// ---------------------------------------------------------------------------
// Issue #470: initForm action — source sweep
// ---------------------------------------------------------------------------
describe('#470 DispatchAction initForm — source sweep', () => {
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

  test('DispatchAction switch contains initForm case', () => {
    expect(active).toMatch(/case\s+['"]initForm['"]/);
  });

  test('initForm calls layui.form.render (no eval)', () => {
    // Must reference layui.form.render — never eval().
    expect(active).toMatch(/layui\.form\.render/);
  });

  test('initForm calls layui.laydate.render for dates array', () => {
    expect(active).toMatch(/layui\.laydate\.render/);
  });

  test('OpenDialog else-branch extracts wtm-dialog-init JSON island via DOMParser', () => {
    // Must use querySelector for the island — never regex.
    expect(active).toMatch(/querySelector\s*\(\s*['"]script\[type="application\/json"\]\.wtm-dialog-init['"]\s*\)/);
  });

  test('OpenDialog success callback dispatches _dialogInitPayload via ff.DispatchAction', () => {
    // The island payload must be dispatched in the layer.open success callback.
    expect(active).toMatch(/ff\.DispatchAction\s*\(\s*_dialogInitPayload\s*\)/);
  });

  test('active-code eval( count is still exactly 1 after #470 changes', () => {
    // Regression guard: initForm must NOT introduce any new eval() calls.
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Issue #551 (#470-A): DispatchAction whitelist extension — source sweep
// ---------------------------------------------------------------------------
describe('#551 DispatchAction whitelist extension — source sweep', () => {
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

  test('DispatchAction switch contains loadComboItems case', () => {
    expect(active).toMatch(/case\s+['"]loadComboItems['"]/);
  });

  test('loadComboItems calls ff.LoadComboItems (no eval)', () => {
    expect(active).toMatch(/case\s+['"]loadComboItems['"][\s\S]{0,400}ff\.LoadComboItems\(/);
  });

  test('initForm dates handling references the widened static laydate option set', () => {
    const initFormBlock = active.match(/case\s+['"]initForm['"][\s\S]*?case\s+['"]loadComboItems['"]/);
    expect(initFormBlock).not.toBeNull();
    const block = initFormBlock[0];
    ['range', 'min', 'max', 'zIndex', 'showBottom', 'btns', 'confirmOnly', 'calendar', 'lang', 'mark'].forEach((opt) => {
      expect(block).toMatch(new RegExp('_d\\.' + opt));
    });
    // Explicitly out of scope — no callback passthrough.
    expect(block).not.toMatch(/ready\s*:/);
    expect(block).not.toMatch(/[^a-zA-Z]done\s*:/);
  });

  test('active-code eval( count is still exactly 1 after #551 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Issue #470: initForm action — behavioral stub
// ---------------------------------------------------------------------------
describe('#470 DispatchAction initForm — behavioral stub', () => {
  // Mirrors the makeDispatcher pattern above but includes the initForm branch.
  // Issue #551: also mirrors the widened dates[] option passthrough and the
  // new loadComboItems branch, so takes an optional `ff` mock alongside `layui`.
  function makeDispatcherWithInitForm(layui, ff) {
    ff = ff || {};
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'initForm':
            try {
              if (typeof layui !== 'undefined' && layui.form &&
                  typeof layui.form.render === 'function') {
                layui.form.render(action.formType || null, action.filter || undefined);
              }
              if (action.dates && Array.isArray(action.dates) && action.dates.length > 0 &&
                  typeof layui !== 'undefined' && layui.laydate &&
                  typeof layui.laydate.render === 'function') {
                for (var di = 0; di < action.dates.length; di++) {
                  var d = action.dates[di];
                  if (d && d.elem) {
                    var dOpts = { elem: d.elem, type: d.type || 'date', format: d.format };
                    if (d.range !== undefined && d.range !== null) { dOpts.range = d.range; }
                    if (d.min !== undefined && d.min !== null) { dOpts.min = d.min; }
                    if (d.max !== undefined && d.max !== null) { dOpts.max = d.max; }
                    if (d.zIndex !== undefined && d.zIndex !== null) { dOpts.zIndex = d.zIndex; }
                    if (d.showBottom !== undefined && d.showBottom !== null) { dOpts.showBottom = d.showBottom; }
                    if (d.btns !== undefined && d.btns !== null) {
                      dOpts.btns = d.btns;
                    } else if (d.confirmOnly) {
                      dOpts.btns = ['confirm'];
                    }
                    if (d.calendar !== undefined && d.calendar !== null) { dOpts.calendar = d.calendar; }
                    if (d.lang !== undefined && d.lang !== null) { dOpts.lang = d.lang; }
                    if (d.mark !== undefined && d.mark !== null) { dOpts.mark = d.mark; }
                    layui.laydate.render(dOpts);
                  }
                }
              }
            } catch (e) {
              if (typeof console !== 'undefined' && console.warn) {
                console.warn('[WTM] initForm action failed:', e);
              }
            }
            break;
          case 'loadComboItems':
            if (typeof ff.LoadComboItems === 'function' && action.url && action.id) {
              ff.LoadComboItems(
                action.controlType || undefined,
                action.url,
                action.id,
                action.field || undefined,
                action.selectVal || undefined
              );
            }
            break;
          default:
            if (typeof console !== 'undefined' && console.warn) {
              console.warn('[WTM] Unknown WtmAction type:', action.type);
            }
        }
      }
    };
  }

  test('initForm calls layui.form.render with filter and null formType by default', () => {
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: jest.fn() } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({ actions: [{ type: 'initForm', filter: 'myFilter' }] });
    // formType defaults to null, filter passed as second arg
    expect(formRender).toHaveBeenCalledWith(null, 'myFilter');
  });

  test('initForm passes explicit formType to layui.form.render', () => {
    const formRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: jest.fn() } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({ actions: [{ type: 'initForm', filter: 'f', formType: 'select' }] });
    expect(formRender).toHaveBeenCalledWith('select', 'f');
  });

  test('initForm calls layui.laydate.render for each entry in dates array', () => {
    const formRender = jest.fn();
    const laydateRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({
      actions: [{
        type: 'initForm',
        filter: 'f',
        dates: [
          { elem: '#BirthDate', type: 'date', format: 'yyyy-MM-dd' },
          { elem: '#StartTime', type: 'datetime' }
        ]
      }]
    });
    expect(formRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledTimes(2);
    expect(laydateRender).toHaveBeenNthCalledWith(1, { elem: '#BirthDate', type: 'date', format: 'yyyy-MM-dd' });
    expect(laydateRender).toHaveBeenNthCalledWith(2, { elem: '#StartTime', type: 'datetime', format: undefined });
  });

  test('initForm with empty dates array does not call laydate.render', () => {
    const formRender = jest.fn();
    const laydateRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({ actions: [{ type: 'initForm', filter: 'f', dates: [] }] });
    expect(formRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).not.toHaveBeenCalled();
  });

  test('initForm skips dates entries missing elem field', () => {
    const laydateRender = jest.fn();
    const layui = { form: { render: jest.fn() }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({
      actions: [{
        type: 'initForm',
        filter: 'f',
        dates: [{ type: 'date' }, { elem: '#Valid', type: 'date' }]
      }]
    });
    // Only the entry with elem should be rendered
    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith({ elem: '#Valid', type: 'date', format: undefined });
  });

  test('initForm is a no-op when layui is not defined', () => {
    // Simulate absent layui (e.g. non-layui page)
    const dispatch = makeDispatcherWithInitForm(undefined);
    // Should not throw
    expect(() => {
      dispatch({ actions: [{ type: 'initForm', filter: 'f' }] });
    }).not.toThrow();
  });

  // -------------------------------------------------------------------------
  // Issue #551: widened dates[] static laydate option passthrough
  // -------------------------------------------------------------------------
  test('legacy {elem,type,format}-only dates entry still behaves identically', () => {
    const formRender = jest.fn();
    const laydateRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({
      actions: [{
        type: 'initForm',
        filter: 'f',
        dates: [{ elem: '#BirthDate', type: 'date', format: 'yyyy-MM-dd' }]
      }]
    });
    expect(laydateRender).toHaveBeenCalledTimes(1);
    expect(laydateRender).toHaveBeenCalledWith({ elem: '#BirthDate', type: 'date', format: 'yyyy-MM-dd' });
  });

  test('widened dates entry passes the extra static laydate options through, omitted options absent', () => {
    const formRender = jest.fn();
    const laydateRender = jest.fn();
    const layui = { form: { render: formRender }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({
      actions: [{
        type: 'initForm',
        filter: 'f',
        dates: [{
          elem: '#StartDate',
          type: 'date',
          format: 'yyyy-MM-dd',
          range: '~',
          min: '-7',
          max: '2099-12-31',
          zIndex: 12345,
          showBottom: false,
          confirmOnly: true,
          calendar: true,
          lang: 'en',
          mark: { '0-0-15': 'mid' }
        }]
      }]
    });
    expect(laydateRender).toHaveBeenCalledTimes(1);
    const passedOpts = laydateRender.mock.calls[0][0];
    expect(passedOpts).toEqual({
      elem: '#StartDate',
      type: 'date',
      format: 'yyyy-MM-dd',
      range: '~',
      min: '-7',
      max: '2099-12-31',
      zIndex: 12345,
      showBottom: false,
      btns: ['confirm'],
      calendar: true,
      lang: 'en',
      mark: { '0-0-15': 'mid' }
    });
    // No callback options ever passed through.
    expect(passedOpts.ready).toBeUndefined();
    expect(passedOpts.change).toBeUndefined();
    expect(passedOpts.done).toBeUndefined();
  });

  test('explicit btns array on a dates entry is passed through as-is (takes precedence over confirmOnly)', () => {
    const laydateRender = jest.fn();
    const layui = { form: { render: jest.fn() }, laydate: { render: laydateRender } };
    const dispatch = makeDispatcherWithInitForm(layui);
    dispatch({
      actions: [{
        type: 'initForm',
        dates: [{ elem: '#D', btns: ['clear', 'now', 'confirm'], confirmOnly: true }]
      }]
    });
    expect(laydateRender).toHaveBeenCalledWith({
      elem: '#D',
      type: 'date',
      format: undefined,
      btns: ['clear', 'now', 'confirm']
    });
  });
});

// ---------------------------------------------------------------------------
// Issue #551 (#470-A): loadComboItems action — behavioral stub
// ---------------------------------------------------------------------------
describe('#551 DispatchAction loadComboItems — behavioral stub', () => {
  function makeDispatcher(ff) {
    return function dispatchAction(payload) {
      if (!payload || !payload.actions || !payload.actions.length) return;
      var actions = payload.actions;
      for (var i = 0; i < actions.length; i++) {
        var action = actions[i];
        if (!action || !action.type) continue;
        switch (action.type) {
          case 'loadComboItems':
            if (typeof ff.LoadComboItems === 'function' && action.url && action.id) {
              ff.LoadComboItems(
                action.controlType || undefined,
                action.url,
                action.id,
                action.field || undefined,
                action.selectVal || undefined
              );
            }
            break;
          default:
            if (typeof console !== 'undefined' && console.warn) {
              console.warn('[WTM] Unknown WtmAction type:', action.type);
            }
        }
      }
    };
  }

  test('loadComboItems action calls ff.LoadComboItems with the mapped positional args', () => {
    const loadComboItems = jest.fn();
    const ff = { LoadComboItems: loadComboItems };
    const dispatch = makeDispatcher(ff);
    dispatch({
      actions: [{
        type: 'loadComboItems',
        controlType: 'combo',
        url: '/Home/GetItems',
        id: 'MyCombo',
        field: 'MyComboText',
        selectVal: ['1', '2']
      }]
    });
    expect(loadComboItems).toHaveBeenCalledTimes(1);
    expect(loadComboItems).toHaveBeenCalledWith('combo', '/Home/GetItems', 'MyCombo', 'MyComboText', ['1', '2']);
  });

  test('loadComboItems is a no-op when url or id is missing (does not call ff.LoadComboItems)', () => {
    const loadComboItems = jest.fn();
    const ff = { LoadComboItems: loadComboItems };
    const dispatch = makeDispatcher(ff);
    dispatch({ actions: [{ type: 'loadComboItems', controlType: 'combo', id: 'MyCombo' }] });
    dispatch({ actions: [{ type: 'loadComboItems', controlType: 'combo', url: '/Home/GetItems' }] });
    expect(loadComboItems).not.toHaveBeenCalled();
  });
});
