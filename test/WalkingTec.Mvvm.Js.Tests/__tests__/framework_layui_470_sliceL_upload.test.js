// Tests for Issue #470 Slice L — opt-in (WtmUIOptions.UseSelectIslandRender,
// default OFF — the SAME flag #470 Slices J/K use) eval-free islandification
// of <wt:upload>/<wt:multiupload>'s render script AND "existing file" init
// script, plus the shared ff.upload namespace (doDelete/doPreview/setValues)
// that replaces the legacy per-widget window['{Id}DoDelete']/['{Id}DoPreview']/
// ['{Id}SetValues'] globals.
//
// This file drives the REAL ff.DispatchAction / ff._renderUploadAction /
// ff._renderMultiUploadAction / ff._renderUploadExistingAction /
// ff._islandModulesFor / ff.upload / the delegated click listener end-to-end
// against a FRESH vm instance of the actual shipped framework_layui.js source
// (real jsdom `document`) — not a source-sweep or a hand-rolled
// reimplementation — following the same convention as
// framework_layui_470_sliceK_transfer.test.js. layui.upload.render/
// layui.layer.load/close/msg/photos are stubbed (layui.upload is a real
// layui.use(...) module in production, never loaded under jsdom).

'use strict';

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

function makeJQueryMock(overrides) {
  const $ = function () { return { 0: null, length: 0 }; };
  $.get = jest.fn();
  $.ajax = jest.fn();
  $.cookie = jest.fn();
  $.ajaxSettings = { xhr: jest.fn(() => ({})) };
  $.fn = {};
  return Object.assign($, overrides || {});
}

function makeUploadLayui(overrides) {
  return Object.assign({
    use: jest.fn((mods, cb) => cb()),
    form: { render: jest.fn() },
    layer: {
      load: jest.fn(() => 1),
      close: jest.fn(),
      msg: jest.fn(),
      photos: jest.fn()
    },
    upload: {
      render: jest.fn(() => ({ __fake: 'uploadInstance' }))
    }
  }, overrides || {});
}

function loadFreshFf(opts) {
  opts = opts || {};
  const $ = opts.$ || makeJQueryMock();
  const layui = opts.layui || makeUploadLayui();
  const ctx = vm.createContext({
    window: {},
    document,
    layui,
    console,
    setTimeout: global.setTimeout.bind(global),
    clearTimeout: global.clearTimeout.bind(global),
    $: $,
    DOMParser: global.DOMParser,
    DONOTUSE_TABLAYID: undefined,
    DONOTUSE_COOKIEPRE: '',
    DONOTUSE_WINDOWGUID: '',
  });
  ctx.window = ctx;
  new vm.Script(src).runInContext(ctx);
  return { ff: ctx.ff, windowObj: ctx, layui, $ };
}

function appendUploadDom(id, opts) {
  opts = opts || {};
  document.body.innerHTML = '';
  const hidden = document.createElement('input');
  hidden.type = 'hidden';
  hidden.id = id;
  if (opts.mode) { hidden.setAttribute('data-wtm-upload-mode', opts.mode); }
  if (opts.cs !== undefined) { hidden.setAttribute('data-wtm-cs', opts.cs); }
  if (opts.fieldName !== undefined) { hidden.setAttribute('data-wtm-field-name', opts.fieldName); }
  if (opts.selected !== undefined) { hidden.setAttribute('data-wtm-selected', JSON.stringify(opts.selected)); }
  const label = document.createElement(opts.labelTag || 'label');
  label.id = id + 'label';
  const form = document.createElement('form');
  form.appendChild(hidden);
  form.appendChild(label);
  document.body.appendChild(form);
  return { hidden, label, form };
}

afterEach(() => { document.body.innerHTML = ''; });

// ---------------------------------------------------------------------------
// Source sweep
// ---------------------------------------------------------------------------
describe('#470 Slice L — source sweep', () => {
  test('active-code eval( count is still exactly 1 after #470 Slice L changes', () => {
    const matches = active.match(/\beval\(/g) || [];
    expect(matches).toHaveLength(1);
    expect(active).not.toMatch(/new\s+Function\s*\(/);
  });

  test("'upload' case delegates to ff._renderUploadAction, no inline body", () => {
    const block = active.match(/case\s+['"]upload['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderUploadAction\(\s*action\s*\)/);
  });

  test("'multiUpload' case delegates to ff._renderMultiUploadAction, no inline body", () => {
    const block = active.match(/case\s+['"]multiUpload['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderMultiUploadAction\(\s*action\s*\)/);
  });

  test("'uploadExisting' case delegates to ff._renderUploadExistingAction, no inline body", () => {
    const block = active.match(/case\s+['"]uploadExisting['"]:[\s\S]{0,200}?break;/);
    expect(block).not.toBeNull();
    expect(block[0]).toMatch(/ff\._renderUploadExistingAction\(\s*action\s*\)/);
  });

  test("'upload'/'multiUpload' HAVE an _islandModulesFor entry — layui.upload IS a layui.use(...) module", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).toMatch(/needed\.upload\s*=\s*true/);
    expect(fn).toMatch(/mods\.push\('upload'\)/);
  });

  test("'uploadExisting' has NO _islandModulesFor entry — no layui module dependency", () => {
    const fn = active.match(/_islandModulesFor:\s*function[\s\S]*?\n\s{4}\},/)[0];
    expect(fn).not.toMatch(/uploadExisting/);
  });
});

// ---------------------------------------------------------------------------
// (1) dispatch 'upload' -> layui.upload.render called with expected config
// ---------------------------------------------------------------------------
describe('#470 Slice L — upload basic render', () => {
  test('dispatch calls layui.upload.render with the expected config', () => {
    appendUploadDom('U1', { mode: 'single', cs: 'cs1' });
    const { ff, layui } = loadFreshFf();

    ff.DispatchAction({
      actions: [{
        type: 'upload', id: 'U1', el: '#U1button', url: '/_Framework/Upload',
        size: 100, showPreview: true, previewWidth: 64, previewHeight: 64
      }]
    });

    expect(layui.upload.render).toHaveBeenCalledTimes(1);
    const cfg = layui.upload.render.mock.calls[0][0];
    expect(cfg.elem).toBe('#U1button');
    expect(cfg.url).toBe('/_Framework/Upload');
    expect(cfg.size).toBe(100);
    expect(cfg.accept).toBe('file');
    expect(cfg.exts).toBeUndefined();
  });

  test('exts present when action.exts is set', () => {
    appendUploadDom('U1b');
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'upload', id: 'U1b', el: '#U1bbutton', url: '/u', size: 0, exts: 'jpg|png' }]
    });
    expect(layui.upload.render.mock.calls[0][0].exts).toBe('jpg|png');
  });

  test('module deferral: ff._dispatchIslandWhenReady defers upload through layui.use(["upload"], cb)', () => {
    appendUploadDom('U2');
    const { ff, layui } = loadFreshFf();
    const payload = { actions: [{ type: 'upload', id: 'U2', el: '#U2button', url: '/u', size: 0 }] };

    ff._dispatchIslandWhenReady(payload);

    expect(layui.use).toHaveBeenCalledTimes(1);
    expect(layui.use.mock.calls[0][0]).toEqual(['upload']);
    expect(layui.upload.render).toHaveBeenCalledTimes(1);
  });

  test('layui.upload not loaded: skips with a console.warn, never throws', () => {
    appendUploadDom('U3');
    const { ff } = loadFreshFf({ layui: { use: jest.fn((m, cb) => cb()) } });
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});

    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'upload', id: 'U3', el: '#U3button' }] });
    }).not.toThrow();
    expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('layui.upload is not loaded'));
    warnSpy.mockRestore();
  });

  test('missing id/el: safe no-op, never throws', () => {
    const { ff, layui } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'upload' }] });
    }).not.toThrow();
    expect(layui.upload.render).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// (2) upload done callback — showPreview true/false branches
// ---------------------------------------------------------------------------
describe('#470 Slice L — upload done callback builds delegated markup', () => {
  test('showPreview true: builds img + delete icon with data-wtm-upload-* attributes', () => {
    const { label, hidden } = appendUploadDom('U4', { mode: 'single', cs: 'csX' });
    const previewObj = { preview: jest.fn((cb) => cb(0, { name: 'a.png' }, 'data:image/png;base64,x')) };
    const layui = makeUploadLayui({
      upload: { render: jest.fn((opts) => { opts.before(previewObj); return { __fake: 'x' }; }) }
    });
    const { ff } = loadFreshFf({ layui });

    ff.DispatchAction({
      actions: [{ type: 'upload', id: 'U4', el: '#U4button', url: '/u', size: 0, showPreview: true, previewWidth: 64, previewHeight: 64 }]
    });
    const cfg = layui.upload.render.mock.calls[0][0];
    cfg.done({ Data: { Id: 'file1', Name: 'a.png' } });

    const img = label.querySelector('img');
    expect(img).not.toBeNull();
    expect(img.getAttribute('data-wtm-upload-action')).toBe('preview');
    expect(img.getAttribute('data-wtm-upload-id')).toBe('U4');
    expect(img.getAttribute('data-wtm-file-id')).toBe('file1');
    const del = label.querySelector('i.layui-icon-close');
    expect(del).not.toBeNull();
    expect(del.getAttribute('data-wtm-upload-action')).toBe('delete');
    expect(hidden.value).toBe('file1');
  });

  test('showPreview false: builds a delete button with data-wtm-upload-* attributes', () => {
    const { label } = appendUploadDom('U5');
    const layui = makeUploadLayui({
      upload: { render: jest.fn((opts) => { opts.before({}); return {}; }) }
    });
    const { ff } = loadFreshFf({ layui });

    ff.DispatchAction({
      actions: [{ type: 'upload', id: 'U5', el: '#U5button', url: '/u', size: 0, showPreview: false, deleteText: 'Del' }]
    });
    const cfg = layui.upload.render.mock.calls[0][0];
    cfg.done({ Data: { Id: 'file2', Name: 'b.txt' } });

    const btn = label.querySelector('button');
    expect(btn).not.toBeNull();
    expect(btn.getAttribute('data-wtm-upload-action')).toBe('delete');
    expect(btn.getAttribute('data-wtm-file-id')).toBe('file2');
    expect(btn.textContent).toBe('b.txt  Del');
  });

  test('empty Data.Id: label cleared, layer.msg(uploadFailedText) called', () => {
    appendUploadDom('U6');
    const layui = makeUploadLayui({
      upload: { render: jest.fn((opts) => { opts.before({}); return {}; }) }
    });
    const { ff } = loadFreshFf({ layui });
    ff.DispatchAction({
      actions: [{ type: 'upload', id: 'U6', el: '#U6button', url: '/u', size: 0, uploadFailedText: 'Failed!' }]
    });
    const cfg = layui.upload.render.mock.calls[0][0];
    cfg.done({ Data: { Id: '' } });
    expect(layui.layer.msg).toHaveBeenCalledWith('Failed!');
  });

  test('error callback closes the loading layer, never throws', () => {
    appendUploadDom('U7');
    const layui = makeUploadLayui({
      upload: { render: jest.fn((opts) => { opts.before({}); return {}; }) }
    });
    const { ff } = loadFreshFf({ layui });
    ff.DispatchAction({ actions: [{ type: 'upload', id: 'U7', el: '#U7button', url: '/u', size: 0 }] });
    const cfg = layui.upload.render.mock.calls[0][0];
    expect(() => cfg.error()).not.toThrow();
    expect(layui.layer.close).toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// (3) multiUpload dispatch + done callback
// ---------------------------------------------------------------------------
describe('#470 Slice L — multiUpload render + done callback', () => {
  test('dispatch calls layui.upload.render with multiple:true and number', () => {
    appendUploadDom('MU1', { mode: 'multi', fieldName: 'Files', selected: [] });
    const { ff, layui } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'multiUpload', id: 'MU1', el: '#MU1button', url: '/u', size: 0, number: 5 }]
    });
    const cfg = layui.upload.render.mock.calls[0][0];
    expect(cfg.multiple).toBe(true);
    expect(cfg.number).toBe(5);
  });

  test('initial setValues call seeds hidden inputs from data-wtm-selected before any upload', () => {
    const { form } = appendUploadDom('MU2', { mode: 'multi', fieldName: 'Files', selected: ['a', 'b'] });
    const { ff } = loadFreshFf();
    ff.DispatchAction({
      actions: [{ type: 'multiUpload', id: 'MU2', el: '#MU2button', url: '/u', size: 0 }]
    });
    const inputs = form.querySelectorAll('input[name^="Files["]');
    expect(inputs.length).toBe(2);
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['a', 'b']);
  });

  test('done callback: pushes new file id into state.selected and rebuilds hidden inputs, appends preview markup', () => {
    const { label, form } = appendUploadDom('MU3', { mode: 'multi', fieldName: 'Files', selected: [], cs: 'csM' });
    const layui = makeUploadLayui({
      upload: { render: jest.fn((opts) => { opts.before(); return {}; }) }
    });
    const { ff } = loadFreshFf({ layui });
    ff.DispatchAction({
      actions: [{ type: 'multiUpload', id: 'MU3', el: '#MU3button', url: '/u', size: 0, showPreview: true, previewWidth: 64, previewHeight: 64 }]
    });
    const cfg = layui.upload.render.mock.calls[0][0];
    cfg.done({ Data: { Id: 'newfile', Name: 'c.png' } });

    const inputs = form.querySelectorAll('input[name^="Files["]');
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['newfile']);
    const wrapper = label.querySelector('#labelnewfile');
    expect(wrapper).not.toBeNull();
    const img = wrapper.querySelector('img');
    expect(img.getAttribute('data-wtm-file-id')).toBe('newfile');
    expect(img.getAttribute('layer-src')).toContain('csM');
  });
});

// ---------------------------------------------------------------------------
// (4) ff.upload.doDelete / doPreview / setValues — single mode
// ---------------------------------------------------------------------------
describe('#470 Slice L — ff.upload single-mode doDelete/doPreview', () => {
  test('doDelete (single): appends DeletedFileIds hidden input, clears label + value, resets progress bar', () => {
    const { hidden, label, form } = appendUploadDom('S1', { mode: 'single' });
    hidden.value = 'file1';
    label.innerHTML = '<img/>';
    const bar = document.createElement('div');
    bar.className = 'layui-progress-bar';
    const wrap = document.createElement('div');
    wrap.className = 'layui-progress';
    wrap.appendChild(bar);
    document.body.appendChild(wrap);

    const { ff } = loadFreshFf();
    ff.upload.doDelete('S1', 'file1');

    const delInput = form.querySelector('#DeletedFileIds');
    expect(delInput).not.toBeNull();
    expect(delInput.value).toBe('file1');
    expect(label.innerHTML).toBe('');
    expect(hidden.value).toBe('');
    expect(bar.style.width).toBe('0%');
  });

  test('doPreview (single): opens layer.photos with a single-item data array built from state.cs', () => {
    appendUploadDom('S2', { mode: 'single', cs: 'myConnStr' });
    const { ff, layui } = loadFreshFf();
    ff.upload.doPreview('S2', 'fileXYZ');
    expect(layui.layer.photos).toHaveBeenCalledWith({
      photos: { data: [{ src: '/_Framework/GetFile/fileXYZ?_DONOT_USE_CS=myConnStr' }] },
      anim: 5
    });
  });

  test('doPreview: no-op (never throws) when layui.layer.photos is unavailable', () => {
    appendUploadDom('S3', { mode: 'single' });
    const { ff } = loadFreshFf({ layui: { use: jest.fn((m, cb) => cb()) } });
    expect(() => ff.upload.doPreview('S3', 'f')).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// (5) ff.upload.doDelete / doPreview / setValues — multi mode
// ---------------------------------------------------------------------------
describe('#470 Slice L — ff.upload multi-mode doDelete/doPreview/setValues', () => {
  test('doDelete (multi): ajax round trip removes the #label{fileid} element, filters selected, calls setValues', () => {
    const { label, form } = appendUploadDom('M1', { mode: 'multi', fieldName: 'Files', selected: ['a', 'b'] });
    const wrapper = document.createElement('label');
    wrapper.id = 'labelb';
    label.appendChild(wrapper);

    const $ = makeJQueryMock({
      ajax: jest.fn((opts) => { opts.success(); })
    });
    const { ff } = loadFreshFf({ $ });

    ff.upload.doDelete('M1', 'b');

    expect(label.querySelector('#labelb')).toBeNull();
    const inputs = form.querySelectorAll('input[name^="Files["]');
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['a']);
  });

  test('doDelete (multi): ajax error path logs and never throws', () => {
    appendUploadDom('M2', { mode: 'multi', fieldName: 'Files', selected: ['a'] });
    // NOTE: framework_layui.js fires its own top-level $.ajax({url:'/_framework/
    // GetScriptLanguage', ...}) (success-only, no error handler) as soon as the
    // file loads — this mock must tolerate that call too, not just the
    // DeletedFile call this test targets.
    const $ = makeJQueryMock({
      ajax: jest.fn((opts) => { if (typeof opts.error === 'function') { opts.error(); } })
    });
    const { ff } = loadFreshFf({ $ });
    expect(() => ff.upload.doDelete('M2', 'a')).not.toThrow();
  });

  test('doPreview (multi): opens the container-wide layer.photos gallery, fileid arg ignored', () => {
    appendUploadDom('M3', { mode: 'multi' });
    const { ff, layui } = loadFreshFf();
    ff.upload.doPreview('M3', 'irrelevant');
    expect(layui.layer.photos).toHaveBeenCalledWith({ photos: '#M3label', anim: 5 });
  });

  test('setValues: removes stale marked hidden inputs before appending fresh ones', () => {
    const { form, hidden } = appendUploadDom('M4', { mode: 'multi', fieldName: 'Files', selected: ['x'] });
    const stale = document.createElement('input');
    stale.type = 'hidden';
    stale.setAttribute('M4hidden', 'M4');
    stale.value = 'stale';
    form.appendChild(stale);

    const { ff } = loadFreshFf();
    ff.upload.setValues('M4');

    expect(form.contains(stale)).toBe(false);
    const inputs = form.querySelectorAll('input[name^="Files["]');
    expect(Array.from(inputs).map((i) => i.value)).toEqual(['x']);
    expect(hidden.value).toBe('1');
  });

  test('setValues: hidden field cleared to "" when nothing selected', () => {
    const { hidden } = appendUploadDom('M5', { mode: 'multi', fieldName: 'Files', selected: [] });
    const { ff } = loadFreshFf();
    ff.upload.setValues('M5');
    expect(hidden.value).toBe('');
  });
});

// ---------------------------------------------------------------------------
// (6) ff.upload._getState — lazy DOM-attribute seeding + shared caching
// ---------------------------------------------------------------------------
describe('#470 Slice L — ff.upload._getState lazy seeding', () => {
  test('first access reads data-wtm-* attributes off the hidden input; subsequent calls reuse the cached object', () => {
    appendUploadDom('G1', { mode: 'multi', cs: 'c1', fieldName: 'F1', selected: ['1', '2'] });
    const { ff } = loadFreshFf();
    const s1 = ff.upload._getState('G1');
    expect(s1.mode).toBe('multi');
    expect(s1.cs).toBe('c1');
    expect(s1.fieldName).toBe('F1');
    expect(s1.selected).toEqual(['1', '2']);
    const s2 = ff.upload._getState('G1');
    expect(s2).toBe(s1);
  });

  test('missing element / malformed data-wtm-selected: safe defaults, never throws', () => {
    const { ff } = loadFreshFf();
    expect(() => ff.upload._getState('doesnotexist')).not.toThrow();
    const s = ff.upload._getState('doesnotexist');
    expect(s.mode).toBe('single');
    expect(s.selected).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// (7) delegated click listener — resolves both freshly-uploaded and
// existing-file markup via data-wtm-upload-* attributes
// ---------------------------------------------------------------------------
describe('#470 Slice L — delegated document click listener', () => {
  test('click on an element with data-wtm-upload-action="delete" calls ff.upload.doDelete(id, fileid)', () => {
    appendUploadDom('D1', { mode: 'single' });
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff.upload, 'doDelete').mockImplementation(() => {});
    const btn = document.createElement('button');
    btn.setAttribute('data-wtm-upload-action', 'delete');
    btn.setAttribute('data-wtm-upload-id', 'D1');
    btn.setAttribute('data-wtm-file-id', 'f1');
    document.body.appendChild(btn);

    btn.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

    expect(spy).toHaveBeenCalledWith('D1', 'f1');
    spy.mockRestore();
  });

  test('click on an element with data-wtm-upload-action="preview" calls ff.upload.doPreview(id, fileid)', () => {
    appendUploadDom('D2', { mode: 'single' });
    const { ff } = loadFreshFf();
    const spy = jest.spyOn(ff.upload, 'doPreview').mockImplementation(() => {});
    const img = document.createElement('img');
    img.setAttribute('data-wtm-upload-action', 'preview');
    img.setAttribute('data-wtm-upload-id', 'D2');
    img.setAttribute('data-wtm-file-id', 'f2');
    document.body.appendChild(img);

    img.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

    expect(spy).toHaveBeenCalledWith('D2', 'f2');
    spy.mockRestore();
  });

  test('click on a plain element without the attribute: no-op, never throws', () => {
    const { ff } = loadFreshFf();
    const div = document.createElement('div');
    document.body.appendChild(div);
    expect(() => div.dispatchEvent(new window.MouseEvent('click', { bubbles: true }))).not.toThrow();
  });
});

// ---------------------------------------------------------------------------
// (8) uploadExisting island — ajax fetch + safe DOM markup build
// ---------------------------------------------------------------------------
describe('#470 Slice L — uploadExisting island', () => {
  test('single mode, showPreview true, not disabled: builds img + delete icon with correct attributes, no HTML injection from the file name', () => {
    const { label } = appendUploadDom('E1', { mode: 'single' });
    const $ = makeJQueryMock({
      ajax: jest.fn((opts) => { opts.success('<img src=x onerror=alert(1)>'); })
    });
    const { ff } = loadFreshFf({ $ });

    ff.DispatchAction({
      actions: [{
        type: 'uploadExisting', id: 'E1', mode: 'single', showPreview: true,
        previewWidth: 64, previewHeight: 64, disabled: false,
        files: [{ fileId: 'ex1', getUrl: '/get/ex1', downloadUrl: '/dl/ex1', pictureUrl: '/pic/ex1' }]
      }]
    });

    const img = label.querySelector('img');
    expect(img).not.toBeNull();
    expect(img.alt).toBe('<img src=x onerror=alert(1)>');
    // The malicious "name" must render as inert text (alt attribute), never
    // as markup — confirm no injected <img> descendant exists.
    expect(label.querySelectorAll('img').length).toBe(1);
    expect(img.getAttribute('data-wtm-upload-action')).toBe('preview');
    expect(img.getAttribute('data-wtm-file-id')).toBe('ex1');
    const del = label.querySelector('i.layui-icon-close');
    expect(del).not.toBeNull();
    expect(del.getAttribute('data-wtm-upload-action')).toBe('delete');
  });

  test('single mode, showPreview true, disabled: preview img present, NO delete icon', () => {
    const { label } = appendUploadDom('E2', { mode: 'single' });
    const $ = makeJQueryMock({ ajax: jest.fn((opts) => { opts.success('name.png'); }) });
    const { ff } = loadFreshFf({ $ });
    ff.DispatchAction({
      actions: [{
        type: 'uploadExisting', id: 'E2', mode: 'single', showPreview: true,
        previewWidth: 64, previewHeight: 64, disabled: true,
        files: [{ fileId: 'ex2', getUrl: '/get/ex2', downloadUrl: '/dl/ex2', pictureUrl: '/pic/ex2' }]
      }]
    });
    expect(label.querySelector('img')).not.toBeNull();
    expect(label.querySelector('i.layui-icon-close')).toBeNull();
  });

  test('single mode, showPreview false, disabled: renders a download link, not a delete button', () => {
    const { label } = appendUploadDom('E3', { mode: 'single' });
    const $ = makeJQueryMock({ ajax: jest.fn((opts) => { opts.success('doc.pdf'); }) });
    const { ff } = loadFreshFf({ $ });
    ff.DispatchAction({
      actions: [{
        type: 'uploadExisting', id: 'E3', mode: 'single', showPreview: false, disabled: true,
        files: [{ fileId: 'ex3', getUrl: '/get/ex3', downloadUrl: '/dl/ex3', pictureUrl: '' }]
      }]
    });
    const link = label.querySelector('a');
    expect(link).not.toBeNull();
    expect(link.href).toContain('/dl/ex3');
    expect(link.textContent).toBe('doc.pdf');
    expect(label.querySelector('button')).toBeNull();
  });

  test('single mode, showPreview false, not disabled: renders a delete button with data-wtm-upload-* attributes', () => {
    const { label } = appendUploadDom('E4', { mode: 'single' });
    const $ = makeJQueryMock({ ajax: jest.fn((opts) => { opts.success('doc.pdf'); }) });
    const { ff } = loadFreshFf({ $ });
    ff.DispatchAction({
      actions: [{
        type: 'uploadExisting', id: 'E4', mode: 'single', showPreview: false, disabled: false, deleteText: 'Del',
        files: [{ fileId: 'ex4', getUrl: '/get/ex4', downloadUrl: '/dl/ex4', pictureUrl: '' }]
      }]
    });
    const btn = label.querySelector('button');
    expect(btn).not.toBeNull();
    expect(btn.getAttribute('data-wtm-upload-action')).toBe('delete');
    expect(btn.getAttribute('data-wtm-file-id')).toBe('ex4');
    expect(btn.textContent).toBe('doc.pdf  Del');
  });

  test('multi mode: each file wraps in its own <label id="label{fileId}">, with layer-src on the preview img', () => {
    const { label } = appendUploadDom('E5', { mode: 'multi' });
    const $ = makeJQueryMock({ ajax: jest.fn((opts) => { opts.success('m.png'); }) });
    const { ff } = loadFreshFf({ $ });
    ff.DispatchAction({
      actions: [{
        type: 'uploadExisting', id: 'E5', mode: 'multi', showPreview: true, previewWidth: 64, previewHeight: 64, disabled: false,
        files: [
          { fileId: 'mf1', getUrl: '/get/mf1', downloadUrl: '/dl/mf1', pictureUrl: '/pic/mf1' },
          { fileId: 'mf2', getUrl: '/get/mf2', downloadUrl: '/dl/mf2', pictureUrl: '/pic/mf2' }
        ]
      }]
    });
    const w1 = label.querySelector('#labelmf1');
    const w2 = label.querySelector('#labelmf2');
    expect(w1).not.toBeNull();
    expect(w2).not.toBeNull();
    expect(w1.querySelector('img').getAttribute('layer-src')).toBe('/dl/mf1');
  });

  test('missing #{id}label container: no-op, never throws', () => {
    document.body.innerHTML = '';
    const { ff } = loadFreshFf();
    expect(() => {
      ff.DispatchAction({
        actions: [{ type: 'uploadExisting', id: 'nope', mode: 'single', files: [{ fileId: 'x', getUrl: '/g' }] }]
      });
    }).not.toThrow();
  });

  test('empty files array: no ajax call, never throws', () => {
    appendUploadDom('E6', { mode: 'single' });
    const $ = makeJQueryMock();
    const { ff } = loadFreshFf({ $ });
    // Clear the load-time GetScriptLanguage call (framework_layui.js's own
    // top-level $.ajax(...), unrelated to this action) so the assertion below
    // targets only calls made by the action under test.
    $.ajax.mockClear();
    expect(() => {
      ff.DispatchAction({ actions: [{ type: 'uploadExisting', id: 'E6', mode: 'single', files: [] }] });
    }).not.toThrow();
    expect($.ajax).not.toHaveBeenCalled();
  });

  test('does not need a layui module — dispatches immediately even when layui is undefined', () => {
    appendUploadDom('E7', { mode: 'single' });
    const $ = makeJQueryMock({ ajax: jest.fn((opts) => { opts.success('n'); }) });
    const ctx = vm.createContext({
      window: {}, document, layui: undefined, console,
      setTimeout: global.setTimeout.bind(global), clearTimeout: global.clearTimeout.bind(global),
      $: $, DOMParser: global.DOMParser,
      DONOTUSE_TABLAYID: undefined, DONOTUSE_COOKIEPRE: '', DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    // Clear the load-time GetScriptLanguage call (see the comment in the
    // "empty files array" test above) before exercising the action under test.
    $.ajax.mockClear();
    expect(() => {
      ctx.ff.DispatchAction({
        actions: [{ type: 'uploadExisting', id: 'E7', mode: 'single', files: [{ fileId: 'x', getUrl: '/g', downloadUrl: '/d', pictureUrl: '/p' }] }]
      });
    }).not.toThrow();
    expect($.ajax).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------------------
// (9) dispatch reaches ff._renderUploadAction via the OpenDialog2 (selector
// search-panel) path too — mirrors framework_layui_470_sliceK_transfer
// .test.js's harness.
// ---------------------------------------------------------------------------
describe('#470 Slice L — dispatch reaches layui.upload.render via ff.OpenDialog2 (selector path)', () => {
  function makeJQueryMockForOpenDialog2() {
    function wrap(el) { return { 0: el, length: el ? 1 : 0 }; }
    const $ = function (selector) {
      if (typeof selector === 'string' && selector.charAt(0) === '#') {
        return wrap(document.getElementById(selector.slice(1)));
      }
      return wrap(null);
    };
    $.ajax = jest.fn((opts) => {
      const request = { getResponseHeader: () => null };
      opts.success(OPEN_DIALOG2_RESPONSE_HTML, 'success', request);
    });
    $.ajaxSettings = { xhr: jest.fn(() => ({})) };
    $.cookie = jest.fn();
    $.fn = {};
    return $;
  }

  function loadFreshFfForOpenDialog2(layui, $) {
    const ctx = vm.createContext({
      window: {},
      document,
      layui: layui,
      console,
      setTimeout: global.setTimeout.bind(global),
      clearTimeout: global.clearTimeout.bind(global),
      $: $,
      DOMParser: global.DOMParser,
      DOMPurify: { sanitize: (html) => (html === undefined || html === null ? '' : html) },
      DONOTUSE_TABLAYID: undefined,
      DONOTUSE_COOKIEPRE: '',
      DONOTUSE_WINDOWGUID: '',
    });
    ctx.window = ctx;
    new vm.Script(src).runInContext(ctx);
    return { ff: ctx.ff, windowObj: ctx };
  }

  function makeOpenDialog2Layui(uploadRender) {
    return {
      use: jest.fn((mods, cb) => cb()),
      form: { render: jest.fn() },
      upload: { render: uploadRender },
      layer: {
        load: jest.fn(() => 1),
        close: jest.fn(),
        msg: jest.fn(),
        alert: jest.fn(),
        full: jest.fn(),
        open: jest.fn((opts) => {
          const container = document.createElement('div');
          container.innerHTML = opts.content;
          document.body.appendChild(container);
          if (typeof opts.success === 'function') { opts.success(container); }
          return 'mockWinId';
        })
      }
    };
  }

  function makeOpenDialog2TempEl(id, innerHtml) {
    const el = document.createElement('div');
    el.id = id;
    el.innerHTML = innerHtml;
    document.body.appendChild(el);
    return el;
  }

  const OPEN_DIALOG2_RESPONSE_HTML =
    '<div>wtVar_x = table.render(xoption);</div>' +
    '<table id="g1" lay-filter="f1"></table>' +
    '$$SearchPanel$$';

  afterEach(() => {
    jest.restoreAllMocks();
    document.body.innerHTML = '';
  });

  test('an upload island tokenized into the search panel reaches layui.upload.render end-to-end', () => {
    const uploadRender = jest.fn(() => ({ __fake: 'ins' }));
    const layui = makeOpenDialog2Layui(uploadRender);
    const $ = makeJQueryMockForOpenDialog2();
    const payload = {
      type: 'upload', id: 'SelectorUpload470l', el: '#SelectorUpload470lbutton', url: '/u', size: 0
    };
    makeOpenDialog2TempEl(
      'Temp470l',
      '$$dialoginit$$' + JSON.stringify(payload) + '$$#dialoginit$$' +
      '<input type="hidden" id="SelectorUpload470l" data-wtm-upload-mode="single"/>' +
      '<label id="SelectorUpload470llabel"></label>'
    );
    const { ff } = loadFreshFfForOpenDialog2(layui, $);

    ff.OpenDialog2('/some/search/url', 'w470l', 'Title', 500, 400, '#Temp470l');

    expect(uploadRender).toHaveBeenCalledTimes(1);
    // Module deferral proven via layui.use(['upload'], ...).
    expect(layui.use).toHaveBeenCalledWith(['upload'], expect.any(Function));
  });
});
