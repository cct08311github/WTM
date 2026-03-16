"""
Dashboard Mode 回歸測試
正向 / 反向 / 邊界 測試案例

涵蓋：
- 管理層/BA 視角的 Dashboard 存取與操作
- Widget 資料綁定 (ETL / Analysis)
- 佈局與錯誤邊界測試

Demo app: http://localhost:52838
"""
import json, sys, time
from playwright.sync_api import sync_playwright

BASE = "http://localhost:52838"

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

def api_get_dashboards(page):
    return page.request.get(f"{BASE}/api/dashboard/list")

def api_get_dashboard(page, id):
    return page.request.get(f"{BASE}/api/dashboard/{id}")

def api_save_dashboard(page, id, payload):
    return page.request.post(f"{BASE}/api/dashboard/{id}",
        headers={"Content-Type": "application/json"},
        data=json.dumps(payload))

# ════════════════════════════════════════════
# 正向測試 (Positive)
# ════════════════════════════════════════════

def test_p01_dashboard_list(page):
    """P01: 能夠取得 Dashboard 列表，且格式正確"""
    print("\n── 正向測試 ──")
    resp = api_get_dashboards(page)
    data = resp.json() if resp.status == 200 else []
    record("P01-取得Dashboard列表", resp.status == 200 and isinstance(data, list))

def test_p02_dashboard_load(page):
    """P02: 讀取特定 Dashboard 定義，含 Layout 與 Widgets"""
    resp = api_get_dashboards(page)
    if resp.status == 200 and len(resp.json()) > 0:
        db_id = resp.json()[0]["id"]
        load_resp = api_get_dashboard(page, db_id)
        record("P02-讀取Dashboard定義", load_resp.status == 200 and "widgets" in load_resp.json())
    else:
        record("P02-讀取Dashboard定義", False, "無可用 Dashboard")

def test_p03_widget_data_analysis(page):
    """P03: Widget 綁定 Analysis DataSource 可以成功取得資料"""
    # 模擬呼叫 Widget Data API
    payload = {
        "dataSource": "analysis",
        "parameters": {
            "listVmType": "WalkingTec.Mvvm.Demo.ViewModels.ECommerceVMs.OrderItemListVM",
            "dimensions": '["ProductCategory"]',
            "measures": '[{"Field":"Subtotal","Func":2}]'
        }
    }
    resp = page.request.post(f"{BASE}/api/dashboard/widgetData",
        headers={"Content-Type": "application/json"},
        data=json.dumps(payload))
    record("P03-Widget取得Analysis資料", resp.status == 200)

def test_p04_dashboard_save(page):
    """P04: 可以儲存 Dashboard 的 Layout 變更"""
    payload = {
        "id": "test-dash",
        "title": "BA Test Dashboard",
        "widgets": [],
        "layout": {}
    }
    resp = api_save_dashboard(page, "test-dash", payload)
    record("P04-儲存Dashboard變更", resp.status == 200)

# ════════════════════════════════════════════
# 反向測試 (Negative)
# ════════════════════════════════════════════

def test_n01_load_nonexistent_dashboard(page):
    """N01: 讀取不存在的 Dashboard 應回傳 404"""
    print("\n── 反向測試 ──")
    resp = api_get_dashboard(page, "invalid-id-9999")
    record("N01-讀取不存在Dashboard", resp.status == 404 or resp.status == 400)

def test_n02_invalid_widget_datasource(page):
    """N02: Widget 綁定未知的 DataSource 應報錯"""
    payload = {
        "dataSource": "unknown_source",
        "parameters": {}
    }
    resp = page.request.post(f"{BASE}/api/dashboard/widgetData",
        headers={"Content-Type": "application/json"},
        data=json.dumps(payload))
    record("N02-綁定未知DataSource", resp.status in [400, 404, 500])

def test_n03_save_invalid_layout(page):
    """N03: 儲存格式錯誤的 Dashboard 應被阻擋"""
    payload = {
        "id": "test-dash-2",
        "title": "", # Empty title
        "widgets": "not-a-list" 
    }
    resp = api_save_dashboard(page, "test-dash-2", payload)
    record("N03-儲存無效的Dashboard定義", resp.status >= 400)

# ════════════════════════════════════════════
# 邊界測試 (Boundary)
# ════════════════════════════════════════════

def test_b01_dashboard_max_widgets(page):
    """B01: Dashboard 包含大量 Widget (壓力測試)"""
    print("\n── 邊界測試 ──")
    widgets = [{"id": f"w_{i}", "type": "chart"} for i in range(100)]
    payload = {
        "id": "stress-dash",
        "title": "Stress Dashboard",
        "widgets": widgets,
        "layout": {}
    }
    resp = api_save_dashboard(page, "stress-dash", payload)
    record("B01-Dashboard大量Widget", resp.status == 200)

def test_b02_long_dashboard_title(page):
    """B02: Dashboard 標題達到極限長度"""
    long_title = "A" * 255
    payload = {
        "id": "long-title-dash",
        "title": long_title,
        "widgets": [],
        "layout": {}
    }
    resp = api_save_dashboard(page, "long-title-dash", payload)
    record("B02-Dashboard極限長度標題", resp.status == 200)
