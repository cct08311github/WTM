// Mock globals required by framework_layui.js before script is loaded
global.DONOTUSE_TABLAYID = undefined;
global.DONOTUSE_COOKIEPRE = '';
global.DONOTUSE_WINDOWGUID = '';

// Minimal jQuery mock (only what pure functions need)
global.$ = Object.assign(
  function () { return { cookie: jest.fn() }; },
  { cookie: jest.fn() }
);

// Minimal layui mock
global.layui = {
  use: jest.fn(),
  layer: { msg: jest.fn(), alert: jest.fn() },
  form: { render: jest.fn() },
  table: { reload: jest.fn() },
  element: { tabChange: jest.fn() },
};

// Load framework_layui.js, injecting global as 'window' so pure functions are accessible
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const src = fs.readFileSync(
  path.resolve(__dirname, '../../src/WalkingTec.Mvvm.Mvc/framework_layui.js'),
  'utf8'
);

// Minimal jQuery mock for vm context ($.ajax called at script load for i18n)
const jqueryMock = Object.assign(
  function () { return jqueryMock; },
  {
    ajax: jest.fn(),
    cookie: jest.fn(),
    fn: {},
  }
);

// createContext with window=global: script's `window.ff = {...}` writes to global.ff
const context = vm.createContext({
  window: global,
  global: global,
  $: jqueryMock,
  Array, String, Math, Object, console,
  setTimeout, clearTimeout, setInterval, clearInterval,
});
new vm.Script(src).runInContext(context);
