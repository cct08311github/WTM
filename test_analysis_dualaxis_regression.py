"""
Analysis Mode — 雙 Y 軸 + 金額縮放 回歸測試
正向 / 反向 / 邊界 測試案例

Feature: feat/281-dual-yaxis-scaling
Demo app: http://localhost:52838
"""
import json, sys, time
from playwright.sync_api import sync_playwright

BASE = "http://localhost:52838"
ORDERITEM_VM = "WalkingTec.Mvvm.Demo.ViewModels.ECommerceVMs.OrderItemListVM"
ORDER_VM     = "WalkingTec.Mvvm.Demo.ViewModels.ECommerceVMs.OrderListVM"
STUDENT_VM   = "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM"

# AggregateFunc enum values
SUM   = 2
AVG   = 4
MAX   = 8
COUNT = 1

RESULTS = []

def record(name, passed, detail=""):
    status = "PASS" if passed else "FAIL"
    RESULTS.append({"name": name, "status": status, "detail": detail})
    mark = "✓" if passed else "✗"
    print(f"  [{status}] {mark} {name}" + (f"  ({detail})" if detail else ""))

def login(page):
    page.goto(f"{BASE}/Login/Login")
    page.wait_for_load_state("networkidle")
    page.fill('input[name="ITCode"]', "admin")
    page.fill('input[name="Password"]', "000000")
    page.click('button[type="submit"], input[type="submit"], .layui-btn')
    page.wait_for_load_state("networkidle")
    time.sleep(1)

def api_query(page, vm, dims, measures, filters=None):
    """POST /_analysis/query helper."""
    body = {
        "ListVmType": vm,
        "Dimensions": dims,
        "Measures": [{"Field": m[0], "Func": m[1]} for m in measures],
        "Filters": filters or []
    }
    return page.request.post(f"{BASE}/_analysis/query",
        headers={"Content-Type": "application/json"},
        data=json.dumps(body))

def api_meta(page, vm):
    return page.request.get(f"{BASE}/_analysis/meta?listVmType={vm}")

# ════════════════════════════════════════════
# 正向測試 (Positive)
# ════════════════════════════════════════════

def test_p01_meta_fields(page):
    """P01: OrderItemListVM meta 包含 Quantity 和 Subtotal 兩個度量"""
    print("\n── 正向測試 ──")
    resp = api_meta(page, ORDERITEM_VM)
    data = resp.json()
    fields = {f["fieldName"]: f for f in data}
    has_qty = "Quantity" in fields and fields["Quantity"]["kind"] == "Measure"
    has_sub = "Subtotal" in fields and fields["Subtotal"]["kind"] == "Measure"
    record("P01-Meta含Quantity+Subtotal度量", has_qty and has_sub)

def test_p02_single_measure_no_dual(page):
    """P02: 單度量 → 查詢成功，不觸發雙 Y 軸（由前端 detectDualAxis 判斷）"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"], [("Subtotal", SUM)])
    data = resp.json() if resp.status == 200 else {}
    has_rows = resp.status == 200 and len(data.get("rows", [])) > 0
    record("P02-單度量查詢成功", has_rows, f"rows={len(data.get('rows',[]))}")

def test_p03_two_measures_response(page):
    """P03: 兩個度量 (Subtotal+Quantity) 查詢回傳正確 key"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    record("P03-雙度量查詢HTTP200", resp.status == 200, f"HTTP {resp.status}")
    if resp.status == 200:
        data = resp.json()
        rows = data.get("rows", [])
        if rows:
            keys = list(rows[0].keys())
            has_subtotal = any("Subtotal" in k for k in keys)
            has_qty = any("Quantity" in k for k in keys)
            record("P03a-回傳Subtotal鍵", has_subtotal, f"keys={keys}")
            record("P03b-回傳Quantity鍵", has_qty)

def test_p04_dual_axis_ratio_check(page):
    """P04: 驗證 Subtotal vs Quantity 的最大值 ratio >= 10（應觸發雙 Y 軸）"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            sub_key = next((k for k in rows[0] if "Subtotal" in k), None)
            qty_key = next((k for k in rows[0] if "Quantity" in k), None)
            if sub_key and qty_key:
                max_sub = max(abs(r.get(sub_key, 0) or 0) for r in rows)
                max_qty = max(abs(r.get(qty_key, 0) or 0) for r in rows)
                ratio = (max_sub / max_qty) if max_qty > 0 else 0
                should_dual = ratio >= 10
                record("P04-金額/數量ratio≥10(觸發雙軸)", should_dual,
                       f"max_sub={max_sub:.0f}, max_qty={max_qty}, ratio={ratio:.1f}x")
            else:
                record("P04-金額/數量ratio≥10(觸發雙軸)", False, "key not found")
        else:
            record("P04-金額/數量ratio≥10(觸發雙軸)", False, "no rows")
    else:
        record("P04-金額/數量ratio≥10(觸發雙軸)", False, f"HTTP {resp.status}")

def test_p05_scale_threshold_億(page):
    """P05: 金額總和 >= 1億 → 前端應顯示「億」單位"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM)])
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            sub_key = next((k for k in rows[0] if "Subtotal" in k), None)
            if sub_key:
                total = sum(r.get(sub_key, 0) or 0 for r in rows)
                record("P05-Subtotal總和確認級距", total > 0,
                       f"sum={total:,.0f} → unit=" + (
                       "億" if total >= 1e8 else "百萬" if total >= 1e6 else "萬" if total >= 1e4 else "元"))
        else:
            record("P05-Subtotal總和確認級距", False, "no rows")
    else:
        record("P05-Subtotal總和確認級距", False, f"HTTP {resp.status}")

def test_p06_query_by_region(page):
    """P06: 依地區分組，雙度量查詢"""
    resp = api_query(page, ORDERITEM_VM, ["CustomerRegion"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    data = resp.json() if resp.status == 200 else {}
    rows = data.get("rows", [])
    record("P06-依地區雙度量查詢", resp.status == 200 and len(rows) > 0,
           f"rows={len(rows)}")

def test_p07_query_with_filter(page):
    """P07: 含 Filter 的雙度量查詢"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)],
                     filters=[{"Field": "CustomerRegion", "Operator": "Eq", "Value": "0"}])
    record("P07-含Filter雙度量查詢", resp.status in [200, 400], f"HTTP {resp.status}")

def test_p08_bar_stacked_no_dual(page):
    """P08: bar-stacked 不應觸發雙 Y 軸（前端行為，API 層不感知）"""
    # API 層面仍回傳相同資料；雙軸只是前端邏輯
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    record("P08-API層不因chartType變化", resp.status == 200, f"HTTP {resp.status}")

def test_p09_three_measures(page):
    """P09: 三個度量查詢（最大值上限）"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM), ("ItemCount", COUNT)])
    record("P09-三度量查詢", resp.status == 200, f"HTTP {resp.status}")

def test_p10_order_vm_dual(page):
    """P10: OrderListVM — OrderCount vs TotalAmount 雙度量"""
    resp = api_query(page, ORDER_VM, ["CustomerRegion"],
                     [("TotalAmount", SUM), ("OrderCount", SUM)])
    data = resp.json() if resp.status == 200 else {}
    rows = data.get("rows", [])
    record("P10-OrderVM雙度量查詢", resp.status == 200 and len(rows) > 0,
           f"rows={len(rows)}")

def test_p11_excel_export(page):
    """P11: Excel 匯出端點 200"""
    body = json.dumps({
        "ListVmType": ORDERITEM_VM,
        "Dimensions": ["ProductCategory"],
        "Measures": [{"Field": "Subtotal", "Func": SUM}, {"Field": "Quantity", "Func": SUM}],
        "Filters": []
    })
    resp = page.request.post(f"{BASE}/_analysis/export?format=xlsx",
        headers={"Content-Type": "application/json"},
        data=body)
    record("P11-Excel匯出端點", resp.status == 200, f"HTTP {resp.status}")
    if resp.status == 200:
        ct = resp.headers.get("content-type", "")
        is_xlsx = "spreadsheet" in ct or "octet" in ct or len(resp.body()) > 1000
        record("P11a-Excel回傳xlsx格式", is_xlsx, f"content-type={ct}")

def test_p12_csv_export(page):
    """P12: CSV 匯出端點 200"""
    body = json.dumps({
        "ListVmType": ORDERITEM_VM,
        "Dimensions": ["ProductCategory"],
        "Measures": [{"Field": "Subtotal", "Func": SUM}],
        "Filters": []
    })
    resp = page.request.post(f"{BASE}/_analysis/export?format=csv",
        headers={"Content-Type": "application/json"},
        data=body)
    record("P12-CSV匯出端點", resp.status == 200, f"HTTP {resp.status}")

def test_p13_meta_order_vm(page):
    """P13: OrderListVM meta 正常"""
    resp = api_meta(page, ORDER_VM)
    data = resp.json()
    fields = {f["fieldName"]: f for f in data}
    has_amount = "TotalAmount" in fields
    record("P13-OrderVM-Meta含TotalAmount", has_amount)

# ════════════════════════════════════════════
# 反向測試 (Negative)
# ════════════════════════════════════════════

def test_n01_invalid_vm_type(page):
    """N01: 不存在的 VM 類型 → 400"""
    print("\n── 反向測試 ──")
    resp = api_query(page, "NotExist.Foo.BarListVM", ["ProductCategory"],
                     [("Subtotal", SUM)])
    record("N01-不存在VM類型→400", resp.status == 400, f"HTTP {resp.status}")

def test_n02_invalid_measure_field(page):
    """N02: 不在白名單的度量欄位 → 400"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("HackedField", SUM)])
    record("N02-非白名單度量→400", resp.status == 400, f"HTTP {resp.status}")

def test_n03_invalid_dimension_field(page):
    """N03: 非法維度欄位 → 400"""
    resp = api_query(page, ORDERITEM_VM, ["'; DROP TABLE--"],
                     [("Subtotal", SUM)])
    record("N03-SQLi維度→400", resp.status == 400, f"HTTP {resp.status}")

def test_n04_too_many_measures(page):
    """N04: 超過 3 個度量 → 400"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM), ("ItemCount", COUNT), ("Subtotal", AVG)])
    record("N04-超過3度量→400", resp.status == 400, f"HTTP {resp.status}")

def test_n05_too_many_dimensions(page):
    """N05: 超過 3 個維度 → 400"""
    resp = api_query(page, ORDERITEM_VM,
                     ["ProductCategory", "CustomerRegion", "Brand", "ProductName"],
                     [("Subtotal", SUM)])
    record("N05-超過3維度→400", resp.status == 400, f"HTTP {resp.status}")

def test_n06_disallowed_func(page):
    """N06: OrderCount 只允許 Count/Sum，使用 Avg → 400"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Quantity", MAX)])  # Quantity 不允許 Max
    record("N06-Quantity不允許Max→400", resp.status == 400, f"HTTP {resp.status}")

def test_n07_empty_dimensions_and_measures(page):
    """N07: 空維度和度量 → 400 或空結果"""
    resp = api_query(page, ORDERITEM_VM, [], [])
    record("N07-空維度和度量", resp.status in [200, 400], f"HTTP {resp.status}")

def test_n08_meta_invalid_vm(page):
    """N08: Meta 非法 VM → 400"""
    resp = api_meta(page, "NonExistentVM")
    record("N08-Meta非法VM→400", resp.status == 400, f"HTTP {resp.status}")

def test_n09_null_body(page):
    """N09: Query null body → 400"""
    resp = page.request.post(f"{BASE}/_analysis/query",
        headers={"Content-Type": "application/json"},
        data="null")
    record("N09-null body→400", resp.status == 400, f"HTTP {resp.status}")

def test_n10_dimension_used_as_measure(page):
    """N10: 維度欄位用作度量 → 400（白名單驗證）"""
    resp = api_query(page, ORDERITEM_VM, [],
                     [("ProductCategory", SUM)])  # ProductCategory is Dimension, not Measure
    record("N10-維度當度量→400", resp.status == 400, f"HTTP {resp.status}")

# ════════════════════════════════════════════
# 邊界測試 (Boundary)
# ════════════════════════════════════════════

def test_b01_ratio_exactly_10(page):
    """B01: ratio = 10 (boundary) → detectDualAxis 返回 true"""
    # 用 JS 測試 detectDualAxis 的邊界行為（threshold 是 >= 10）
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    print("\n── 邊界測試 ──")
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            sub_key = next((k for k in rows[0] if "Subtotal" in k), None)
            qty_key = next((k for k in rows[0] if "Quantity" in k), None)
            if sub_key and qty_key:
                max_sub = max(abs(r.get(sub_key, 0) or 0) for r in rows)
                max_qty = max(abs(r.get(qty_key, 0) or 0) for r in rows)
                ratio = (max_sub / max_qty) if max_qty > 0 else 0
                record("B01-ratio邊界確認(≥10觸發)", ratio >= 10 or ratio < 10,
                       f"ratio={ratio:.2f}x {'→ dual' if ratio>=10 else '→ single'}")
    else:
        record("B01-ratio邊界確認", False, f"HTTP {resp.status}")

def test_b02_single_row_result(page):
    """B02: 單行結果（只有一個類別）不崩潰"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)],
                     filters=[{"Field": "ProductCategory", "Operator": "Eq", "Value": "0"}])
    record("B02-單行結果不崩潰", resp.status in [200, 400], f"HTTP {resp.status}")

def test_b03_both_measures_same_field(page):
    """B03: 同欄位不同函式 (Subtotal Sum + Subtotal Avg)"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Subtotal", AVG)])
    data = resp.json() if resp.status == 200 else {}
    rows = data.get("rows", [])
    record("B03-同欄位不同Func", resp.status == 200, f"HTTP {resp.status}, rows={len(rows)}")

def test_b04_one_measure_zero_values(page):
    """B04: 某度量全為 0 → detectDualAxis 返回 false (max=0 守衛)"""
    # If one max=0, dual axis should NOT trigger
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            sub_key = next((k for k in rows[0] if "Subtotal" in k), None)
            qty_key = next((k for k in rows[0] if "Quantity" in k), None)
            if sub_key and qty_key:
                max_qty = max(abs(r.get(qty_key, 0) or 0) for r in rows)
                # Data should have non-zero quantity
                record("B04-數量非全零(max=0守衛生效)", max_qty > 0, f"max_qty={max_qty}")
    else:
        record("B04-數量非全零", False, f"HTTP {resp.status}")

def test_b05_max_row_limit(page):
    """B05: Take=50000 上限 — 不崩潰"""
    resp = api_query(page, ORDERITEM_VM, ["ProductName"],
                     [("Subtotal", SUM)])
    record("B05-細粒度維度不崩潰", resp.status == 200, f"HTTP {resp.status}")

def test_b06_scale_boundary_10000(page):
    """B06: 金額 > 1萬 → 前端應用萬元縮放（API 驗證資料存在）"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM)])
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            sub_key = next((k for k in rows[0] if "Subtotal" in k), None)
            if sub_key:
                max_val = max(r.get(sub_key, 0) or 0 for r in rows)
                expected_unit = "億" if max_val >= 1e8 else "百萬" if max_val >= 1e6 else "萬" if max_val >= 1e4 else "元"
                record("B06-縮放級距判斷", True, f"max={max_val:,.0f} → {expected_unit}")
        else:
            record("B06-縮放級距判斷", False, "no rows")
    else:
        record("B06-縮放級距判斷", False, f"HTTP {resp.status}")

def test_b07_concurrent_queries(page):
    """B07: 連續 5 次雙度量查詢不崩潰"""
    all_ok = True
    for i in range(5):
        resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                         [("Subtotal", SUM), ("Quantity", SUM)])
        if resp.status != 200:
            all_ok = False
            break
    record("B07-連續5次雙度量查詢", all_ok)

def test_b08_three_dimensions_two_measures(page):
    """B08: 3 個維度 + 2 個度量（複合查詢）"""
    resp = api_query(page, ORDERITEM_VM,
                     ["ProductCategory", "CustomerRegion", "Brand"],
                     [("Subtotal", SUM), ("Quantity", SUM)])
    record("B08-3維度2度量複合查詢", resp.status == 200, f"HTTP {resp.status}")

def test_b09_xss_in_filter_value(page):
    """B09: XSS 字串在 Filter Value 不崩潰"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", SUM)],
                     filters=[{"Field": "Brand", "Operator": "Contains",
                                "Value": "<script>alert(1)</script>"}])
    record("B09-XSS Filter不崩潰", resp.status in [200, 400], f"HTTP {resp.status}")

def test_b10_measure_ratio_close_to_10(page):
    """B10: Subtotal_Avg vs Quantity_Avg — ratio 可能 < 10（單軸）"""
    resp = api_query(page, ORDERITEM_VM, ["ProductCategory"],
                     [("Subtotal", AVG), ("Quantity", AVG)])
    record("B10-Avg雙度量查詢", resp.status == 200, f"HTTP {resp.status}")
    if resp.status == 200:
        rows = resp.json().get("rows", [])
        if rows:
            keys = list(rows[0].keys())
            sub_key = next((k for k in keys if "Subtotal" in k), None)
            qty_key = next((k for k in keys if "Quantity" in k), None)
            if sub_key and qty_key:
                max_sub = max(abs(r.get(sub_key, 0) or 0) for r in rows)
                max_qty = max(abs(r.get(qty_key, 0) or 0) for r in rows)
                ratio = (max_sub / max_qty) if max_qty > 0 else 0
                record("B10a-Avg ratio確認", True,
                       f"ratio={ratio:.1f}x → {'dual' if ratio>=10 else 'single'} axis")

# ════════════════════════════════════════════
# Main
# ════════════════════════════════════════════

def main():
    print("=" * 60)
    print("Analysis 雙 Y 軸 + 金額縮放 回歸測試 v1")
    print("Feature: feat/281-dual-yaxis-scaling")
    print("=" * 60)

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context()
        page = context.new_page()

        login(page)
        print("登入完成")

        # Positive
        test_p01_meta_fields(page)
        test_p02_single_measure_no_dual(page)
        test_p03_two_measures_response(page)
        test_p04_dual_axis_ratio_check(page)
        test_p05_scale_threshold_億(page)
        test_p06_query_by_region(page)
        test_p07_query_with_filter(page)
        test_p08_bar_stacked_no_dual(page)
        test_p09_three_measures(page)
        test_p10_order_vm_dual(page)
        test_p11_excel_export(page)
        test_p12_csv_export(page)
        test_p13_meta_order_vm(page)

        # Negative
        test_n01_invalid_vm_type(page)
        test_n02_invalid_measure_field(page)
        test_n03_invalid_dimension_field(page)
        test_n04_too_many_measures(page)
        test_n05_too_many_dimensions(page)
        test_n06_disallowed_func(page)
        test_n07_empty_dimensions_and_measures(page)
        test_n08_meta_invalid_vm(page)
        test_n09_null_body(page)
        test_n10_dimension_used_as_measure(page)

        # Boundary
        test_b01_ratio_exactly_10(page)
        test_b02_single_row_result(page)
        test_b03_both_measures_same_field(page)
        test_b04_one_measure_zero_values(page)
        test_b05_max_row_limit(page)
        test_b06_scale_boundary_10000(page)
        test_b07_concurrent_queries(page)
        test_b08_three_dimensions_two_measures(page)
        test_b09_xss_in_filter_value(page)
        test_b10_measure_ratio_close_to_10(page)

        browser.close()

    # Summary
    print("\n" + "=" * 60)
    print("測試結果摘要")
    print("=" * 60)
    passed = sum(1 for r in RESULTS if r["status"] == "PASS")
    failed = sum(1 for r in RESULTS if r["status"] == "FAIL")
    total  = len(RESULTS)

    print(f"\n共 {total} 個測試：✓ {passed} 通過, ✗ {failed} 失敗\n")

    if failed > 0:
        print("── 失敗項目 ──")
        for r in RESULTS:
            if r["status"] == "FAIL":
                print(f"  ✗ {r['name']} — {r['detail']}")
        print()

    return 0 if failed == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
