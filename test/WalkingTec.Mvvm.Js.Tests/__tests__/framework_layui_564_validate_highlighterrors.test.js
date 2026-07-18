// Tests for Issue #564 (#470-D): FormTagHelper's last two residual inline
// <script> fragments — the auto-validate handler and the ModelState
// error-highlight/focus block — are replaced by two new DispatchAction
// action types:
//   1. 'bindValidate' — reproduces
//        var {Id}validate = false;
//        layui.form.on('submit({Id}filterAuto)', function(data){
//          {Id}validate = true; return false;
//        });
//      via window[formId + 'validate'] instead of a top-level `var`
//      (equivalent global-scope observable state).
//   2. 'highlightErrors' — reproduces the legacy inline script that, for
//      every ModelState error message, prepended a plain-text label into
//      the form's first submit button's parent container, added
//      'layui-form-danger' to every field that had an error, and focused
//      the first errored field. ALL error-message insertion is done via
//      .textContent (never innerHTML/insertAdjacentHTML/eval) since
//      ModelState messages can carry user/validation-attribute-influenced
//      content. action.errors[].field is resolved via
//      document.getElementById (never a hand-built CSS-selector string) and
//      the resolved element must live inside the form root, closing off
//      selector-injection / cross-form-field concerns.
//
// Following the same convention as framework_layui_558_bindsubmit.test.js /
// framework_layui_561_form_island.test.js: source-sweep tests assert the
// real file structure; behavioral-stub tests re-implement the same logic
// against real jsdom `document` + mocked `layui` (the vm-loaded `ff` module's
// closures were compiled without `document`/`layui` as bare identifiers in
// scope — see setup.js — so the live module functions cannot be exercised
// directly here). Any drift between the stub and the real file is caught by
// the source-sweep tests below.

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
// Source sweep — 'bindValidate'
// ---------------------------------------------------------------------------
describe('#564 (#470-D) — bindValidate source sweep', () => {
  test('DispatchAction switch contains a bindValidate case', () => {
    expect(active).toMatch(/case\s+['"]bindValidate['"]/);
  });

  function bindValidateBlock() {
    const block = active.match(/case\s+['"]bindValidate['"]:[\s\S]{0,2000}?\n\s*case\s+['"]highlightErrors['"]/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('bindValidate case guards on action.filter being a non-empty identifier string', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/!action\.filter\s*\|\|\s*typeof\s+action\.filter\s*!==\s*['"]string['"]/);
    // eslint-disable-next-line no-useless-escape
    expect(block).toMatch(/!\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\/\.test\(\s*action\.filter\s*\)/);
  });

  test('bindValidate case guards on action.formId being a non-empty identifier string', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/!action\.formId\s*\|\|\s*typeof\s+action\.formId\s*!==\s*['"]string['"]/);
    // eslint-disable-next-line no-useless-escape
    expect(block).toMatch(/!\/\^\[A-Za-z_\$\]\[\\w\$\]\*\$\/\.test\(\s*action\.formId\s*\)/);
  });

  test('bindValidate case guards on layui.form.on being available', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/typeof\s+layui\s*===\s*['"]undefined['"]/);
    expect(block).toMatch(/typeof\s+layui\.form\.on\s*!==\s*['"]function['"]/);
  });

  test('bindValidate case sets window[formId + "validate"] = false before binding', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/window\[\s*_validateVarName\s*\]\s*=\s*false/);
    expect(block).toMatch(/_validateVarName\s*=\s*action\.formId\s*\+\s*['"]validate['"]/);
  });

  test('bindValidate case registers layui.form.on with the action.filter-derived submit event', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/layui\.form\.on\(\s*['"]submit\(['"]\s*\+\s*action\.filter\s*\+\s*['"]\)['"]/);
  });

  test('bindValidate case delegates the handler to ff._makeBindValidateHandler (no inline eval/new Function)', () => {
    const block = bindValidateBlock();
    expect(block).toMatch(/ff\._makeBindValidateHandler\(/);
    expect(block).not.toMatch(/\beval\(/);
    expect(block).not.toMatch(/new\s+Function\s*\(/);
  });

  test('ff._makeBindValidateHandler is defined and sets window[varName] = true', () => {
    const block = active.match(/_makeBindValidateHandler\s*:\s*function[\s\S]{0,300}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/window\[\s*varName\s*\]\s*=\s*true/);
  });

  test('_islandModulesFor maps bindValidate -> the form module', () => {
    // Issue #470 Slice G: bound bumped 1400 -> 2200 — the function grew with
    // the new 'ueditor'/'layedit' module-deferral branches.
    const block = active.match(/_islandModulesFor\s*:\s*function[\s\S]{0,2200}?\n\s*\},/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/a\.type\s*===\s*['"]bindValidate['"][\s\S]{0,80}needed\.form\s*=\s*true/);
  });
});

// ---------------------------------------------------------------------------
// Source sweep — 'highlightErrors'
// ---------------------------------------------------------------------------
describe('#564 (#470-D) — highlightErrors source sweep', () => {
  test('DispatchAction switch contains a highlightErrors case', () => {
    expect(active).toMatch(/case\s+['"]highlightErrors['"]/);
  });

  function highlightErrorsBlock() {
    // Issue #601: retargeted from a `default:` terminator to the next case
    // label (`tagInput`, the case immediately following highlightErrors) —
    // #601 inserted a new, sizeable 'bindInput' case between 'tagInput' and
    // 'default:', which pushed the highlightErrors-to-`default:` span past
    // this regex's {0,3000} bound and made the match fail. Stopping at the
    // next case label (mirroring #558's bindSubmitBlock() convention) is
    // immune to further insertions later in the switch.
    const block = active.match(/case\s+['"]highlightErrors['"]:[\s\S]{0,3000}?\n\s*case\s+['"]tagInput['"]/);
    expect(block).not.toBeNull();
    return block[0];
  }

  test('highlightErrors case guards on action.errors being a non-empty array', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/!action\.errors\s*\|\|\s*!Array\.isArray\(\s*action\.errors\s*\)/);
  });

  test('highlightErrors case resolves the form root via document.getElementById(action.formId)', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/document\.getElementById\(\s*action\.formId\s*\)/);
  });

  test('highlightErrors case resolves each field via document.getElementById — never a hand-built CSS selector', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/document\.getElementById\(\s*_heErr\.field\s*\)/);
    // No selector-string construction with the field value (e.g. '#' + field / querySelector('#'+...)).
    expect(block).not.toMatch(/querySelector\(\s*['"]#['"]\s*\+/);
  });

  test('highlightErrors case scopes field lookups to its own form root via .contains(...)', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/_heFormEl\.contains\(\s*_heFieldEl\s*\)/);
    expect(block).toMatch(/_heFormEl\.contains\(\s*_heFocusEl\s*\)/);
  });

  test('highlightErrors case inserts the message via .textContent, never innerHTML/insertAdjacentHTML/eval', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/_heLabel\.textContent\s*=\s*_heMsg/);
    expect(block).not.toMatch(/\.innerHTML\s*=/);
    expect(block).not.toMatch(/insertAdjacentHTML/);
    expect(block).not.toMatch(/\beval\(/);
  });

  test('highlightErrors case builds the message element via document.createElement (no HTML string building)', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/document\.createElement\(\s*['"]div['"]\s*\)/);
    expect(block).toMatch(/document\.createElement\(\s*['"]label['"]\s*\)/);
  });

  test('highlightErrors case marks errored fields with the layui-form-danger class', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/classList\.add\(\s*['"]layui-form-danger['"]\s*\)/);
  });

  test('highlightErrors case focuses the first errored field only when action.focusFirst is set', () => {
    const block = highlightErrorsBlock();
    expect(block).toMatch(/action\.focusFirst\s*&&\s*_heFirstFieldId/);
    expect(block).toMatch(/_heFocusEl\.focus\(\)/);
  });

  test('active-code eval( count is still exactly 1 after #564 changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'bindValidate' DispatchAction case
// ---------------------------------------------------------------------------
describe('#564 DispatchAction bindValidate — behavioral stub', () => {
  function makeBindValidateHandler(windowObj, varName) {
    return function (data) {
      windowObj[varName] = true;
      return false;
    };
  }

  function dispatchBindValidate(action, layui, windowObj) {
    if (!action.filter || typeof action.filter !== 'string' ||
        !/^[A-Za-z_$][\w$]*$/.test(action.filter)) { return; }
    if (!action.formId || typeof action.formId !== 'string' ||
        !/^[A-Za-z_$][\w$]*$/.test(action.formId)) { return; }
    if (typeof layui === 'undefined' || !layui || !layui.form ||
        typeof layui.form.on !== 'function') { return; }
    var varName = action.formId + 'validate';
    windowObj[varName] = false;
    layui.form.on('submit(' + action.filter + ')', makeBindValidateHandler(windowObj, varName));
  }

  function makeLayui() {
    var handlers = {};
    return {
      form: { on: jest.fn(function (evt, fn) { handlers[evt] = fn; }) },
      _fire: function (evt, data) {
        if (!handlers[evt]) { throw new Error('no handler registered for ' + evt); }
        return handlers[evt](data);
      }
    };
  }

  test('registers layui.form.on("submit(<Id>filterAuto)", ...) with the exact filter string', () => {
    const layui = makeLayui();
    const windowObj = {};
    dispatchBindValidate({ type: 'bindValidate', filter: 'wtForm_test1filterAuto', formId: 'wtForm_test1' }, layui, windowObj);
    expect(layui.form.on).toHaveBeenCalledTimes(1);
    expect(layui.form.on.mock.calls[0][0]).toBe('submit(wtForm_test1filterAuto)');
  });

  test('sets window[formId + "validate"] = false immediately (before any submit fires)', () => {
    const layui = makeLayui();
    const windowObj = {};
    dispatchBindValidate({ type: 'bindValidate', filter: 'wtForm_afilterAuto', formId: 'wtForm_a' }, layui, windowObj);
    expect(windowObj.wtForm_avalidate).toBe(false);
  });

  test('flips window[formId + "validate"] to true when the bound submit handler fires (validation passed)', () => {
    const layui = makeLayui();
    const windowObj = {};
    dispatchBindValidate({ type: 'bindValidate', filter: 'wtForm_bfilterAuto', formId: 'wtForm_b' }, layui, windowObj);
    expect(windowObj.wtForm_bvalidate).toBe(false);
    const ret = layui._fire('submit(wtForm_bfilterAuto)', {});
    expect(windowObj.wtForm_bvalidate).toBe(true);
    expect(ret).toBe(false);
  });

  test('validation flag stays false if the submit handler never fires (validation failed / never triggered)', () => {
    const layui = makeLayui();
    const windowObj = {};
    dispatchBindValidate({ type: 'bindValidate', filter: 'wtForm_cfilterAuto', formId: 'wtForm_c' }, layui, windowObj);
    expect(windowObj.wtForm_cvalidate).toBe(false);
    // No layui._fire(...) call — layui's own client-side validation blocked the submit event.
    expect(windowObj.wtForm_cvalidate).toBe(false);
  });

  test('no-ops without throwing when action.filter is a non-identifier string', () => {
    const layui = makeLayui();
    const windowObj = {};
    expect(() => {
      dispatchBindValidate({ type: 'bindValidate', filter: 'a)b', formId: 'wtForm_d' }, layui, windowObj);
    }).not.toThrow();
    expect(layui.form.on).not.toHaveBeenCalled();
  });

  test('no-ops without throwing when layui.form.on is unavailable', () => {
    const windowObj = {};
    expect(() => {
      dispatchBindValidate({ type: 'bindValidate', filter: 'wtForm_efilterAuto', formId: 'wtForm_e' }, undefined, windowObj);
    }).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// Behavioral stub: 'highlightErrors' DispatchAction case (real jsdom document)
// ---------------------------------------------------------------------------
describe('#564 DispatchAction highlightErrors — behavioral stub (jsdom)', () => {
  function dispatchHighlightErrors(action) {
    if (!action.errors || !Array.isArray(action.errors) || action.errors.length === 0) { return; }
    if (!action.formId || typeof action.formId !== 'string') { return; }
    var formEl = document.getElementById(action.formId);
    if (!formEl) { return; }
    var submitBtn = formEl.querySelector('button[type="submit"]');
    var msgContainer = submitBtn ? submitBtn.parentNode : null;
    var firstFieldId = null;
    for (var i = 0; i < action.errors.length; i++) {
      var err = action.errors[i];
      if (!err) { continue; }
      if (firstFieldId === null && err.field) { firstFieldId = err.field; }
      if (msgContainer) {
        var msg = (err.message === undefined || err.message === null) ? '' : String(err.message);
        var msgDiv = document.createElement('div');
        msgDiv.className = 'layui-input-block';
        msgDiv.style.textAlign = 'left';
        var label = document.createElement('label');
        label.style.color = 'red';
        // XSS-safe: textContent never parses the string as HTML.
        label.textContent = msg;
        msgDiv.appendChild(label);
        msgContainer.insertBefore(msgDiv, msgContainer.firstChild);
      }
      if (err.field && typeof err.field === 'string') {
        var fieldEl = document.getElementById(err.field);
        if (fieldEl && formEl.contains(fieldEl)) {
          fieldEl.classList.add('layui-form-danger');
        }
      }
    }
    if (action.focusFirst && firstFieldId) {
      var focusEl = document.getElementById(firstFieldId);
      if (focusEl && formEl.contains(focusEl) && typeof focusEl.focus === 'function') {
        focusEl.focus();
      }
    }
  }

  function makeForm(formId, fieldIds) {
    document.body.innerHTML = '';
    const form = document.createElement('form');
    form.id = formId;
    fieldIds.forEach((fid) => {
      const input = document.createElement('input');
      input.id = fid;
      form.appendChild(input);
    });
    const btn = document.createElement('button');
    btn.type = 'submit';
    form.appendChild(btn);
    document.body.appendChild(form);
    return form;
  }

  afterEach(() => {
    document.body.innerHTML = '';
  });

  // -------------------------------------------------------------------------
  // 1. Correct field marked, message set via textContent, no injected elements
  // -------------------------------------------------------------------------
  test('marks the correct field with layui-form-danger and sets the message via textContent (plain text node)', () => {
    makeForm('wtForm_1', ['TestVM_Name']);
    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_1',
      errors: [{ field: 'TestVM_Name', message: 'Name is required' }],
      focusFirst: true
    });

    const field = document.getElementById('TestVM_Name');
    expect(field.classList.contains('layui-form-danger')).toBe(true);

    const label = document.querySelector('#wtForm_1 label');
    expect(label).not.toBeNull();
    expect(label.textContent).toBe('Name is required');
    // Proof the message is a plain text node, not parsed markup: the label
    // element has zero child ELEMENTS (only its text node).
    expect(label.children.length).toBe(0);
  });

  // -------------------------------------------------------------------------
  // 2. First errored field is focused when focusFirst is true
  // -------------------------------------------------------------------------
  test('focuses the first errored field when focusFirst is true', () => {
    makeForm('wtForm_2', ['TestVM_Name', 'TestVM_Email']);
    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_2',
      errors: [
        { field: 'TestVM_Name', message: 'Name is required' },
        { field: 'TestVM_Email', message: 'Email is invalid' }
      ],
      focusFirst: true
    });

    expect(document.activeElement).toBe(document.getElementById('TestVM_Name'));
  });

  test('does NOT focus any field when focusFirst is false/omitted', () => {
    makeForm('wtForm_2b', ['TestVM_Name']);
    const before = document.activeElement;
    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_2b',
      errors: [{ field: 'TestVM_Name', message: 'Name is required' }]
      // focusFirst intentionally omitted
    });
    expect(document.activeElement).toBe(before);
  });

  // -------------------------------------------------------------------------
  // 3. ADVERSARIAL XSS: HTML/script-bearing error message
  // -------------------------------------------------------------------------
  test('ADVERSARIAL: an <img onerror=...> message never creates an <img> element or fires onerror', () => {
    makeForm('wtForm_3', ['TestVM_Name']);
    const payload = '<img src=x onerror=alert(1)>';

    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_3',
      errors: [{ field: 'TestVM_Name', message: payload }],
      focusFirst: true
    });

    // No <img> anywhere in the document — the string was never parsed as HTML.
    expect(document.querySelector('img')).toBeNull();

    const label = document.querySelector('#wtForm_3 label');
    expect(label).not.toBeNull();
    expect(label.textContent).toBe(payload);
    expect(label.innerHTML).not.toContain('<img');
    expect(label.children.length).toBe(0);
  });

  test('ADVERSARIAL: a "</script><script>" message never creates a script element', () => {
    makeForm('wtForm_4', ['TestVM_Name']);
    const payload = '</script><script>alert(1)</script>';

    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_4',
      errors: [{ field: 'TestVM_Name', message: payload }]
    });

    expect(document.querySelector('script')).toBeNull();
    const label = document.querySelector('#wtForm_4 label');
    expect(label.textContent).toBe(payload);
  });

  // -------------------------------------------------------------------------
  // Multiple errors, un-deduplicated message list (matches legacy behaviour)
  // -------------------------------------------------------------------------
  test('every error message gets its own label, even multiple errors on the same field', () => {
    makeForm('wtForm_5', ['TestVM_Name']);
    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_5',
      errors: [
        { field: 'TestVM_Name', message: 'Name is required' },
        { field: 'TestVM_Name', message: 'Name must be at least 3 characters' }
      ],
      focusFirst: true
    });

    const labels = document.querySelectorAll('#wtForm_5 label');
    expect(labels.length).toBe(2);
    const texts = Array.from(labels).map((l) => l.textContent).sort();
    expect(texts).toEqual(['Name is required', 'Name must be at least 3 characters'].sort());
  });

  // -------------------------------------------------------------------------
  // Scoping: a field id that resolves OUTSIDE the form root is ignored
  // -------------------------------------------------------------------------
  test('SECURITY: a field id resolving to an element outside the form root is never marked or focused', () => {
    makeForm('wtForm_6', ['TestVM_Name']);
    // An element with a colliding id that lives OUTSIDE the form.
    const outsider = document.createElement('input');
    outsider.id = 'outsiderField';
    document.body.appendChild(outsider);

    dispatchHighlightErrors({
      type: 'highlightErrors',
      formId: 'wtForm_6',
      errors: [{ field: 'outsiderField', message: 'should not apply' }],
      focusFirst: true
    });

    expect(outsider.classList.contains('layui-form-danger')).toBe(false);
    expect(document.activeElement).not.toBe(outsider);
  });

  test('no-ops without throwing when action.errors is empty', () => {
    makeForm('wtForm_7', ['TestVM_Name']);
    expect(() => {
      dispatchHighlightErrors({ type: 'highlightErrors', formId: 'wtForm_7', errors: [] });
    }).not.toThrow();
    expect(document.querySelector('#wtForm_7 label')).toBeNull();
  });

  test('no-ops without throwing when the form root does not exist in the DOM', () => {
    expect(() => {
      dispatchHighlightErrors({
        type: 'highlightErrors',
        formId: 'wtForm_does_not_exist',
        errors: [{ field: 'x', message: 'y' }]
      });
    }).not.toThrow();
  });
});
