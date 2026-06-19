// Tests for pure functions in framework_layui.js
// ff is loaded into global scope via setup.js

describe('removeByID', () => {
  test('removes item matching by ID', () => {
    const arr = [{ ID: 1 }, { ID: 2 }, { ID: 3 }];
    removeByID(arr, { ID: 2 });
    expect(arr).toEqual([{ ID: 1 }, { ID: 3 }]);
  });

  test('no-op when ID not found', () => {
    const arr = [{ ID: 1 }, { ID: 2 }];
    removeByID(arr, { ID: 99 });
    expect(arr).toEqual([{ ID: 1 }, { ID: 2 }]);
  });

  test('removes only first matching item', () => {
    const arr = [{ ID: 1 }, { ID: 1 }, { ID: 2 }];
    removeByID(arr, { ID: 1 });
    expect(arr).toHaveLength(2);
  });
});

describe('ff.guid', () => {
  test('returns string in GUID format', () => {
    const result = ff.guid();
    expect(result).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  });

  test('each call returns a different value', () => {
    const guids = new Set(Array.from({ length: 20 }, () => ff.guid()));
    expect(guids.size).toBe(20);
  });
});

describe('ff.concatWhereStr', () => {
  test('appends where conditions to URL', () => {
    const result = ff.concatWhereStr('/api/list', ['name', 'age'], { name: 'alice', age: 30 });
    expect(result).toBe('/api/list&name=alice&age=30');
  });

  test('returns original URL when data is null', () => {
    expect(ff.concatWhereStr('/api/list', ['name'], null)).toBe('/api/list');
  });

  test('returns original URL when whereStr is empty', () => {
    expect(ff.concatWhereStr('/api/list', [], { name: 'alice' })).toBe('/api/list');
  });

  test('returns empty string when tempUrl is null and data is null', () => {
    expect(ff.concatWhereStr(null, null, null)).toBe('');
  });
});

describe('ff.getTreeChecked', () => {
  test('collects leaf node IDs', () => {
    const items = [
      { id: 'a', children: [{ id: 'a1', children: [] }, { id: 'a2', children: null }] },
      { id: 'b', children: [] },
    ];
    expect(ff.getTreeChecked(items)).toEqual(['a1', 'a2', 'b']);
  });

  test('returns empty array for empty input', () => {
    expect(ff.getTreeChecked([])).toEqual([]);
  });

  test('handles deeply nested structure', () => {
    const items = [{ id: 'root', children: [{ id: 'leaf', children: [] }] }];
    expect(ff.getTreeChecked(items)).toEqual(['leaf']);
  });
});

describe('ff.getTreeItems', () => {
  const data = [
    { Value: '1', Text: 'Option A', Disabled: false, Selected: false, Icon: null, Children: null },
    { Value: '2', Text: 'Option B', Disabled: true, Selected: true, Icon: 'icon-b', Children: null },
  ];

  test('maps data to tree item format', () => {
    const result = ff.getTreeItems(data, []);
    expect(result[0]).toMatchObject({ value: '1', name: 'Option A', disabled: false, selected: false });
    expect(result[1]).toMatchObject({ value: '2', name: 'Option B', disabled: true, selected: true });
  });

  test('marks item selected when value in svals', () => {
    const result = ff.getTreeItems(data, ['1']);
    expect(result[0].selected).toBe(true);
  });

  test('handles null svals', () => {
    const result = ff.getTreeItems(data, null);
    expect(result).toHaveLength(2);
  });

  test('processes children recursively', () => {
    const nested = [{
      Value: 'p', Text: 'Parent', Disabled: false, Selected: false, Icon: null,
      Children: [{ Value: 'c', Text: 'Child', Disabled: false, Selected: false, Icon: null, Children: null }]
    }];
    const result = ff.getTreeItems(nested, []);
    expect(result[0].children).toHaveLength(1);
    expect(result[0].children[0].value).toBe('c');
  });
});

describe('ff.getComboItems', () => {
  const data = [
    { Value: '1', Text: 'Apple', Disabled: false, Selected: false, Icon: null, Children: null },
    { Value: '2', Text: 'Banana', Disabled: false, Selected: true, Icon: null, Children: null },
    { Value: '3', Text: 'Cherry', Disabled: false, Selected: false, Icon: null, Children: null },
  ];

  test('maps data to combo item format', () => {
    const result = ff.getComboItems(data, [], false);
    expect(result[0]).toMatchObject({ value: '1', name: 'Apple', disabled: false });
  });

  // BUG-6: when useDefaultvalue=false, item.Selected must be respected
  test('[BUG-6] respects item.Selected when useDefaultvalue=false and svals empty', () => {
    const result = ff.getComboItems(data, [], false);
    expect(result[0].selected).toBe(false);  // item.Selected=false
    expect(result[1].selected).toBe(true);   // item.Selected=true — must NOT be reset to false
    expect(result[2].selected).toBe(false);  // item.Selected=false
  });

  test('[BUG-6] svals override when useDefaultvalue=true', () => {
    const result = ff.getComboItems(data, ['1'], true);
    expect(result[0].selected).toBe(true);   // in svals
    expect(result[1].selected).toBe(false);  // not in svals, useDefaultvalue ignores item.Selected
    expect(result[2].selected).toBe(false);
  });

  test('svals select items when useDefaultvalue=false', () => {
    const result = ff.getComboItems(data, ['3'], false);
    expect(result[2].selected).toBe(true);   // in svals
  });

  test('returns empty array when data is null', () => {
    expect(ff.getComboItems(null, [], false)).toEqual([]);
  });

  test('disabled parameter overrides item.Disabled', () => {
    const result = ff.getComboItems(data, [], false, true);
    expect(result.every(i => i.disabled === true)).toBe(true);
  });
});

describe('ff.getTransferItems', () => {
  const data = [
    { Value: 'x', Text: 'X Item', Disabled: false, Selected: true },
    { Value: 'y', Text: 'Y Item', Disabled: true, Selected: false },
  ];

  test('maps data to transfer item format', () => {
    const result = ff.getTransferItems(data, []);
    expect(result[0]).toMatchObject({ value: 'x', title: 'X Item', disabled: false });
    expect(result[1]).toMatchObject({ value: 'y', title: 'Y Item', disabled: true });
  });

  test('returns empty array for empty data', () => {
    expect(ff.getTransferItems([], [])).toEqual([]);
  });

  test('handles null svals', () => {
    const result = ff.getTransferItems(data, null);
    expect(result).toHaveLength(2);
  });
});
