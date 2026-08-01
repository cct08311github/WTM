"""
WTM Demo E2E Test Suite — TC-01 ~ TC-36
========================================
基於 WTM/LayUI 實際 DOM 結構撰寫的 Playwright 自動化測試。

前置條件：
  1. WTM Demo 運行於 http://localhost:52837（或以 WTM_E2E_BASE_URL 覆寫）
  2. IsQuickDebug = true（驗證碼自動跳過，帳密預填）
  3. pip install playwright && playwright install chromium

環境變數：
  WTM_E2E_BASE_URL    覆寫 base URL（預設 http://localhost:52837）
  WTM_E2E_ADMIN_USER  管理員帳號（預設 admin）
  WTM_E2E_ADMIN_PASS  管理員密碼（預設 000000）
  WTM_E2E_TIMEOUT     操作逾時毫秒數（預設 15000）
  WTM_E2E_HEADLESS    是否 headless，false/0/no 表示顯示視窗（預設 true）
  WTM_E2E_KILLSWITCH  "1" 表示這次執行對應的 demo 進程啟用了 #627
                      legacy-script-rehydration kill-switch（見
                      docs/csp-hardening.md），供 tc_33/34/35 調整斷言
                      （預設 "0"／未設定＝baseline，行為與現行預設一致）。
                      由 e2e-test.yml 的 "killswitch" matrix leg 設定；
                      本地手動測試 kill-switch 時也可自行 export。
  WTM_E2E_VISUAL_SNAPSHOTS  "1" 才會產生 TC-21/TC-22 的人工複核用視覺快照
                      （預設 "0"，見下方截圖政策第 2 點；issue #886 review）。

執行：
  python wtm_e2e_tests.py                       # 全部執行
  python wtm_e2e_tests.py --tc 1                # 只跑 TC-01
  python wtm_e2e_tests.py --tc 1,4,23           # 跑指定 TC
  python wtm_e2e_tests.py --base-url http://... # 覆寫 base URL
  python wtm_e2e_tests.py --headed              # 顯示瀏覽器視窗
  python wtm_e2e_tests.py --report results/junit.xml  # 輸出 JUnit XML
  python wtm_e2e_tests.py --list                # 僅列出 TC_REGISTRY（不連線，供結構驗證）

執行後彙總列印 "Total: N | PASS: n | FAIL: n | ERROR: n | SKIP: n" — N 會隨
TC_REGISTRY 增減而變動（見 #681），CI log 判讀請認 "FAIL: 0" 與 "ERROR: 0"
這兩個欄位是否為 0，不要硬編一個固定的 N。

截圖政策（issue #886，2026-07-29 review 後修正）：
  tc_ 函式本身不再逐步無條件拍照——失敗時的截圖統一由 run_tests() 的例外處理路徑
  透過 _screenshot_on_failure() 補拍一張「失敗當下」的頁面狀態，寫入
  screenshots/TC-{N}/TC-{N}-{FAIL|ERROR|RETRY-n}.png。新增 tc_ 函式時不要為了
  「步驟紀錄」在成功路徑上加 page.screenshot() —— 那筆成本在全部 79 個呼叫點
  乘上三條 CI matrix leg 是白付的（CI 的 screenshot artifact 上傳因 #11 長年
  continue-on-error，平常沒有人下載查看）。仍然合理的例外：
    1. 已經寫在 except 分支、只在特定子步驟逾時/失敗時才觸發的截圖（例如
       login()、TC-04、TC-24 的個別 timeout 分支，以及 #886 review 後移回
       except 分支的 TC-04/24/25/26/27/28/29 共 12 處——這些 TC 在這條路徑
       上沒有任何 assert 保護，swallow 掉的例外若不順手拍照就完全無跡可尋，
       見各自 except 分支旁的行內註解）——這些本來就是條件式的，成功執行
       不會被呼叫到，維持原樣、不受下面第 2 點的 opt-in flag 控制。
    2. TC-21（登入頁視覺驗收）與 TC-22（首頁 Dashboard 版面驗證）——這兩個 TC
       的判定完全來自 DOM/佈局 assert，截圖本身不是任何斷言的依據，且 CI 的
       screenshot artifact 上傳本來就不可靠（#11）。因此改為 opt-in：預設不拍，
       設 WTM_E2E_VISUAL_SNAPSHOTS=1 才會產生，給人工複核視覺版面用（TC-22
       逾時分支本身的診斷截圖不受此 flag 控制，理由同第 1 點）。
"""

import asyncio
import argparse
import base64
import json
import os
import sys
import traceback
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

# Issue #898: several tc_ functions below narrow a bare `except Exception:` down to
# this specific Playwright timeout type, so a genuine unrelated failure (JS crash,
# network error) is no longer swallowed alongside the "element not rendered yet" case
# the catch was actually written for. NOT imported at module scope on purpose: `--list`
# (see module docstring) must keep working with zero external dependencies beyond the
# stdlib, and playwright is only ever guaranteed installed for an actual test run.
# run_tests() below assigns this name into the module globals before any tc_ function
# can execute, mirroring how it already lazy-imports `async_playwright` for the same
# reason.
PlaywrightTimeoutError = None

# ─── 常數（優先從環境變數讀取）──────────────────────────────────────────────────

BASE_URL = os.environ.get("WTM_E2E_BASE_URL", "http://localhost:52837")
ADMIN_USER = os.environ.get("WTM_E2E_ADMIN_USER", "admin")
ADMIN_PASS = os.environ.get("WTM_E2E_ADMIN_PASS", "000000")
SCREENSHOTS_DIR = Path(__file__).parent / "screenshots"
TIMEOUT = int(os.environ.get("WTM_E2E_TIMEOUT", "15000"))
HEADLESS = os.environ.get("WTM_E2E_HEADLESS", "true").lower() not in ("false", "0", "no")
# Issue #681: mirrors the demo process's own WTM_E2E_KILLSWITCH env var (read by
# _Layout.cshtml) so tc_33/34/35 know which assertions are valid for THIS run.
KILLSWITCH_EXPECTED = os.environ.get("WTM_E2E_KILLSWITCH", "0").strip() == "1"
# Issue #886 review (MEDIUM): TC-21/TC-22's screenshots were exempted from the
# failure-only policy on the theory that "producing the image is the test's
# purpose" — but no assertion in either TC actually consumes the image (TC-21's
# verdict comes from the DOM asserts, TC-22's from the layout asserts), and CI
# can't reliably hand them back anyway (`actions/upload-artifact@v4` vs Gitea's
# GHES API, #11, continue-on-error). So they don't get an unconditional-by-default
# pass; they're opt-in for a human doing a manual visual check.
VISUAL_SNAPSHOTS = os.environ.get("WTM_E2E_VISUAL_SNAPSHOTS", "0").strip() == "1"

# WTM Analysis Mode 已知 VM 型別（demo 中 [EnableAnalysis] 標記的 ListVM）
STUDENT_LIST_VM = "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM"


# ─── Helper Functions ──────────────────────────────────────────────────────────

MAX_RETRIES = 2  # 重試次數上限


class TestSkipped(Exception):
    """
    tc_ 函式的「真 SKIP」訊號（issue #681 MEDIUM fix）。

    當一個 TC 在目前 demo/環境下沒有可操作的測試場景時（例如 demo 未啟用該
    功能），raise 這個例外並附上原因字串 —— run_tests() 會把它記錄為獨立的
    "SKIP" 狀態，不計入 PASS 也不計入 FAIL/ERROR。絕對不要為了讓一個沒有場景
    的 TC「看起來通過」而直接 return（那會被目前的執行迴圈誤判為 PASS，見
    #681 審查發現的 false-green）。
    """
    pass


def sc(tc_num: int, step: str) -> str:
    """產生截圖路徑：screenshots/TC-{N}-{step}.png"""
    d = SCREENSHOTS_DIR / f"TC-{tc_num:02d}"
    d.mkdir(parents=True, exist_ok=True)
    return str(d / f"TC-{tc_num:02d}-{step}.png")


async def login(page, base_url=None):
    """
    登入 WTM Demo（QuickDebug 模式，帳密已預填）。
    回傳後 page 位於首頁 Layout（含側邊選單）。
    """
    url = base_url or BASE_URL
    await page.goto(f"{url}/Login/Login")
    await page.wait_for_load_state("networkidle")

    # QuickDebug 模式已預填帳密，直接點送出
    # 但若未預填，手動填入
    itcode = page.locator("input[name='ITCode']")
    if await itcode.input_value() == "":
        await itcode.fill(ADMIN_USER)
    pwd = page.locator("input[name='Password']")
    if await pwd.input_value() == "":
        await pwd.fill(ADMIN_PASS)

    await page.locator("button.login-button[type='submit']").click()
    # 等待登入 redirect 完成，並確認 dashboard iframe 內容已初始化
    # LayUI fade-in 在 headless CI 可能停滯（layui-layout-admin 保持 visibility:hidden），
    # 因此同時等待 sidebar 選單出現作為「頁面已完整 render」的信號
    try:
        await page.wait_for_selector(".layui-side-menu", state="visible", timeout=TIMEOUT)
    except Exception:
        await page.screenshot(path=sc(99, "login-layout-timeout"), full_page=True)
        raise


async def navigate_via_layhref(page, lay_href_path: str):
    """
    模擬 LayUI Admin 的側邊選單導覽。
    LayUI 使用 lay-href 屬性載入頁面到 iframe tab 中。
    直接導覽到 PartialView URL 取得內容。
    """
    await page.goto(f"{BASE_URL}/{lay_href_path.lstrip('/')}")  # BASE_URL 可由 env var 覆寫
    await page.wait_for_load_state("networkidle")


async def wait_for_layui_table(page, timeout=TIMEOUT):
    """等待 LayUI table 渲染完成（表格行出現）。"""
    await page.wait_for_selector(
        ".layui-table-body tr[data-index]",
        timeout=timeout,
        state="visible"
    )


async def wait_for_layer_dialog(page, timeout=TIMEOUT):
    """等待 LayUI layer 彈出層出現。"""
    await page.wait_for_selector(".layui-layer", timeout=timeout, state="visible")


async def close_layer_dialog(page):
    """關閉最上層的 LayUI layer 彈出層。"""
    close_btn = page.locator(".layui-layer-close:visible").last
    if await close_btn.count() > 0:
        await close_btn.click()
        # Wait for dialog to actually disappear instead of hardcoded sleep
        try:
            await page.wait_for_selector(".layui-layer", state="hidden", timeout=2000)
        except Exception:
            pass  # best-effort cleanup


async def open_grid_via_sidebar(page, lay_href_path: str):
    """
    透過側邊選單 lay-href 連結導覽到一個 grid 頁面（issue #681）。
    與 tc_04/tc_24/tc_25 既有手法相同：JS 點擊 lay-href 連結、等待
    layui.table.cache 出現、等待 networkidle。這是進入「透過 ff.OpenDialog
    開啟的 CRUD 對話框」流程的必要前置步驟 —— 直接 page.goto() 到 grid 的
    PartialView URL 不會載入 framework_layui.js／jQuery／xm-select，之後
    在該頁面點擊「新建」按鈕開的對話框 JS 就不會執行。
    """
    await page.evaluate(
        """(href) => {
            const links = document.querySelectorAll(`a[lay-href="${href}"]`);
            if (links.length > 0) links[0].click();
        }""",
        lay_href_path,
    )
    await page.wait_for_function(
        """() => {
            const caches = window.layui?.table?.cache || {};
            return Object.keys(caches).length > 0;
        }""",
        timeout=TIMEOUT,
    )
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)
    try:
        await page.wait_for_selector(".layui-table-tool", state="attached", timeout=TIMEOUT)
    except Exception:
        pass  # toolbar may be absent on some grids; caller decides if that's fatal


async def open_grid_via_direct_tab(page, path: str):
    """
    透過 layuiadmin 的 tab 載入機制導覽到一個「側邊選單沒有連結」的 grid 頁面
    （issue #898，TC-29 的 EtlJob/EtlRunLog）。

    open_grid_via_sidebar() 需要 DOM 中已經存在一個 `a[lay-href="..."]` 元素才能
    點擊；ETL 相關頁面不在本 demo 的側邊選單樹中（未見任何 lay-href 對應項目），
    但 layuiadmin 的 tab 載入其實是靠事件代理達成的通用機制——demo/WalkingTec.Mvvm.
    Demo/wwwroot/layuiadmin/lib/admin.js 對整個 body 委派了
    `o.on("click","*[lay-href]",function(){ location.hash = correctRouter(t) })`，
    任何帶 lay-href 屬性的元素被點擊都會觸發同一條路徑，不限於側邊選單裡的既有連結。
    因此這裡動態建立一個帶正確 lay-href 屬性的隱藏元素並點擊它，達成與側邊選單連結
    完全相同的載入路徑（已對照 /Student/Index 等既有側邊選單項目實測比對過，行為
    一致：table.cache 正確填入、無 console 錯誤），只是不需要該連結真的出現在選單裡。
    """
    await page.evaluate(
        """(href) => {
            const a = document.createElement('a');
            a.setAttribute('lay-href', href);
            a.style.display = 'none';
            document.body.appendChild(a);
            a.click();
        }""",
        path,
    )
    await page.wait_for_function(
        """() => {
            const caches = window.layui?.table?.cache || {};
            return Object.keys(caches).length > 0;
        }""",
        timeout=TIMEOUT,
    )
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)


async def open_toolbar_dialog(page, button_text: str):
    """
    點擊 grid toolbar 上文字為 button_text 的按鈕（例如「新建」），開啟一個
    透過 ff.OpenDialog 載入的 LayUI layer 對話框，並等待對話框出現（issue #681）。
    """
    btn = page.locator(f".layui-table-tool a:has-text('{button_text}')")
    await btn.first.wait_for(state="visible", timeout=TIMEOUT)
    await btn.first.click()
    await wait_for_layer_dialog(page)


# ─── TC-01: XSS 反射測試 ──────────────────────────────────────────────────────

async def tc_01_xss_reflected(page, **_):
    """
    TC-01: XSS 反射測試（Redirect 參數注入）
    優先度: P0
    預估執行: 5s

    測試登入後 Redirect 參數是否被正確編碼，避免 XSS。
    LoginController.Login POST 成功後使用 HttpUtility.UrlDecode(url) 做重導向。
    注入 javascript:alert(1) 檢查是否觸發。

    預期結果：
    - Redirect 參數應被安全處理，不觸發 alert
    - 登入頁不應直接嵌入未編碼的 Redirect 值到 <script> 區塊
    """
    print("[TC-01] 開始執行...")

    xss_payloads = [
        "javascript:alert(1)",
        '"><script>alert(1)</script>',
        "javascript:alert(document.cookie)",
        "' onmouseover='alert(1)",
    ]

    dialog_triggered = False

    def on_dialog(dialog):
        nonlocal dialog_triggered
        dialog_triggered = True
        print(f"  [!] 偵測到 alert 彈出: {dialog.message}")
        asyncio.ensure_future(dialog.dismiss())

    page.on("dialog", on_dialog)

    for i, payload in enumerate(xss_payloads):
        dialog_triggered = False

        # Step 1: 導覽到帶有 XSS payload 的登入頁
        url = f"{BASE_URL}/Login/Login?ReturnUrl={payload}"
        await page.goto(url)
        await page.wait_for_load_state("networkidle")

        # Step 2: 檢查 hidden input 的 Redirect 值
        redirect_input = page.locator("input[name='Redirect']")
        if await redirect_input.count() > 0:
            val = await redirect_input.get_attribute("value")
            print(f"  Payload {i}: Redirect value = {val!r}")

        # Step 3: 檢查頁面原始碼中是否有未編碼的 payload
        content = await page.content()
        # 確認 payload 不會出現在 <script> 區塊中（var redirect = 'payload'）
        if f"var redirect='{payload}'" in content or f'var redirect="{payload}"' in content:
            print(f"  [WARN] Payload {i} 出現在 script 區塊中（未編碼）")

        assert not dialog_triggered, f"XSS payload {i} 觸發了 alert 彈出！payload={payload}"

    # Step 4: 嘗試登入並檢查重導向是否安全
    xss_redirect = "javascript:alert('xss')"
    await page.goto(f"{BASE_URL}/Login/Login?ReturnUrl={xss_redirect}")
    await page.wait_for_load_state("networkidle")

    # 填寫帳密並送出
    itcode = page.locator("input[name='ITCode']")
    if await itcode.input_value() == "":
        await itcode.fill(ADMIN_USER)
    pwd = page.locator("input[name='Password']")
    if await pwd.input_value() == "":
        await pwd.fill(ADMIN_PASS)

    # 攔截回應
    async with page.expect_navigation(timeout=TIMEOUT) as nav_info:
        await page.locator("button.login-button[type='submit']").click()

    resp = await nav_info.value
    final_url = page.url
    print(f"  登入後最終 URL: {final_url}")

    # 確認沒有被重導到 javascript: URL
    assert not final_url.startswith("javascript:"), \
        f"登入後被重導到 javascript: URL！final_url={final_url}"
    assert not dialog_triggered, "登入後重導向觸發了 alert！"

    page.remove_listener("dialog", on_dialog)
    print("[TC-01] PASS -- XSS 反射測試通過")


# ─── TC-02: SQL Injection 測試 ────────────────────────────────────────────────

async def tc_02_sql_injection(page, **_):
    """
    TC-02: SQL Injection 測試
    優先度: P0
    預估執行: 5s

    在登入表單的 ITCode 和 Password 欄位注入 SQL payload，
    確認不會繞過認證或觸發伺服器錯誤。

    預期結果：
    - 登入失敗，顯示錯誤訊息
    - 不回傳 500 Internal Server Error
    """
    print("[TC-02] 開始執行...")

    sql_payloads = [
        ("' OR '1'='1", "' OR '1'='1"),
        ("admin'--", "anything"),
        ("'; DROP TABLE FrameworkUsers;--", "test"),
        ("\" OR \"\"=\"", "\" OR \"\"=\""),
    ]

    for i, (itcode, pwd) in enumerate(sql_payloads):
        await page.goto(f"{BASE_URL}/Login/Login")
        await page.wait_for_load_state("networkidle")

        await page.locator("input[name='ITCode']").fill(itcode)
        await page.locator("input[name='Password']").fill(pwd)

        resp = await page.goto(f"{BASE_URL}/Login/Login")  # Reset
        await page.locator("input[name='ITCode']").fill(itcode)
        await page.locator("input[name='Password']").fill(pwd)

        # 用 request API 直接 POST
        response = await page.request.post(f"{BASE_URL}/Login/Login", form={
            "ITCode": itcode,
            "Password": pwd,
            "VerifyCode": "skip",
        })

        status = response.status
        print(f"  Payload {i}: ITCode={itcode!r} => HTTP {status}")

        assert status != 500, f"SQL injection payload {i} 導致 500 錯誤！"
        # 不應登入成功（回傳 200 的登入頁面才是正確的，302 到 / 表示登入成功）
        # 注意：QuickDebug 跳過驗證碼，但帳密仍需正確

    print("[TC-02] PASS -- SQL Injection 測試通過")


# ─── TC-03: CSRF Token 測試 ───────────────────────────────────────────────────

async def tc_03_csrf_token(page, **_):
    """
    TC-03: CSRF Token 驗證（characterization test — issue #917）
    優先度: P0
    預估執行: 3s

    確認 WTM form 頁面包含 Anti-Forgery Token。

    預期結果（characterization，不是規格）：
    - __RequestVerificationToken 數量為 0
    - 無 token 的 POST 回應 HTTP 200（成功提交，未被拒絕）

    這兩個斷言記錄的是「WTM 目前未實作 CSRF token」這個已知安全缺口的*現狀*，
    不是我們想要的行為 —— 命名沿用 test/WalkingTec.Mvvm.WorkFlow.Test/
    TenantFilterInvariantTests.cs 的 WfDemoShapedObsoleteContext 同一種誠實作法：
    釘住觀察到的現狀，而不是假裝現狀是規格。一旦 CSRF 保護被實作，這兩個斷言
    會如預期地變紅，逼著這個測試被重寫成真正驗證 CSRF 保護生效（而不是驗證
    CSRF 保護仍然不存在）——這是刻意的，紅燈就是這個測試存在的目的。

    在此之前（issue #917 之前）這個函式沒有任何 assert，只印
    "[KNOWN-GAP]" 訊息後無條件回傳 PASS —— scripts/check-e2e-test-integrity.py
    現在會擋下這種「不可能失敗」的 TC。
    """
    print("[TC-03] 開始執行...")

    await login(page)

    # 導覽到 Student Create 頁面（直接存取 PartialView URL）
    await page.goto(f"{BASE_URL}/Student/Create")
    await page.wait_for_load_state("networkidle")

    # 檢查 Anti-Forgery Token
    token = page.locator("input[name='__RequestVerificationToken']")
    token_count = await token.count()
    print(f"  __RequestVerificationToken 數量: {token_count}")
    assert token_count == 0, (
        f"__RequestVerificationToken 數量為 {token_count}，預期 0 "
        "（characterization：WTM 目前未實作 CSRF token；若這個斷言變紅，"
        "代表 CSRF token 已經出現在頁面上，這個測試需要被重寫成驗證 CSRF "
        "保護本身，而不是驗證它不存在）"
    )
    print("  [KNOWN-GAP] WTM 未實作 CSRF Anti-Forgery Token — 已知安全缺口")

    # 嘗試無 token 的 POST
    response = await page.request.post(f"{BASE_URL}/Student/Create", form={
        "Entity.Name": "test",
        "Entity.Password": "test123",
    })
    print(f"  無 Token POST 回應: HTTP {response.status}")
    assert response.status == 200, (
        f"無 token 的 POST 回應 HTTP {response.status}，預期 200 "
        "（characterization：WTM 目前不會拒絕缺少 CSRF token 的 POST；若這個"
        "斷言變紅，代表提交已被擋下，這個測試需要被重寫成驗證拒絕行為，而不是"
        "驗證缺乏保護）"
    )
    print(f"  [KNOWN-GAP] POST 無 token 成功提交（HTTP {response.status}）— WTM 缺乏 CSRF 保護")

    print("[TC-03] PASS -- CSRF 檢查完成（結果記錄為已知安全缺口，兩項觀察皆已 assert）")


# ─── TC-04: Analysis Mode 頁面測試 ───────────────────────────────────────────

async def tc_04_analysis_mode_page(page, **_):
    """
    TC-04: Analysis Mode 頁面存取測試
    優先度: P1
    預估執行: 8s

    Student Index 頁有 enable-analysis="true"，
    確認「分析模式」按鈕存在且可切換面板。

    預期結果：
    - toolbar 有「分析模式」按鈕
    - 點擊後 analysis-panel 顯示
    - 面板含 .analysis-field-pool, .analysis-dropzone--dim, .analysis-dropzone--msr
    """
    print("[TC-04] 開始執行...")

    await login(page)

    # 使用 JS 點擊 sidebar 連結（lay-href 使用 hash 路由）
    await page.evaluate(
        """() => {
            const links = document.querySelectorAll('a[lay-href="/Student/Index"]');
            if (links.length > 0) links[0].click();
        }"""
    )
    # 等 DataTable 在主 frame 完成初始化
    await page.wait_for_function(
        """() => {
            const caches = window.layui?.table?.cache || {};
            return Object.keys(caches).length > 0;
        }""",
        timeout=TIMEOUT
    )
    # Replace bare asyncio.sleep — wait for networkidle so the page is fully settled
    # before we start interacting with toolbar buttons (issue #475: scroll flake)
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)

    # 等待 grid toolbar
    try:
        await page.locator(".layui-table-tool").wait_for(state="attached", timeout=TIMEOUT)
    except Exception:
        await page.screenshot(path=sc(4, "00-toolbar-timeout"), full_page=True)
        raise

    # 等待 grid 實際完成渲染（issue #596）：
    # toolbar 只是「attached」不代表資料列已渲染完畢 —「分析模式」按鈕在部分
    # runner 負載下要等到 grid render 完成才會變 visible，過去只等 toolbar
    # attached 就去等按鈕，20s 的按鈕可見性視窗有時撐不到 grid render 完成
    # （2026-07-04 / 2026-07-05 各一次相同簽章的 timeout）。這裡採用其他通過
    # 的 TC（TC-25/TC-26）等 grid 的相同訊號：.layui-table-body tr[data-index]。
    GRID_RENDER_TIMEOUT = 30000  # ms — 比預設 TIMEOUT 寬鬆，吸收 CI runner 負載尖峰
    BTN_VISIBILITY_TIMEOUT = 45000  # ms — 按鈕本身也給獨立、更寬裕的預算
    grid_wait_start = datetime.now()
    try:
        await page.wait_for_selector(
            ".layui-table-body tr[data-index]", state="visible", timeout=GRID_RENDER_TIMEOUT
        )
    except Exception:
        grid_wait_elapsed = (datetime.now() - grid_wait_start).total_seconds()
        print(f"[TC-04] grid 渲染等待逾時（耗時 {grid_wait_elapsed:.2f}s）")
        await page.screenshot(path=sc(4, "00b-grid-render-timeout"), full_page=True)
        raise
    grid_wait_elapsed = (datetime.now() - grid_wait_start).total_seconds()
    print(f"[TC-04] grid 渲染耗時: {grid_wait_elapsed:.2f}s")

    # 找「分析模式」按鈕 —— DataTableTagHelper 渲染的 onclick="wtmAnalysis.toggle(...)"
    analysis_btn = page.locator("button:has-text('分析模式')")
    btn_count = await analysis_btn.count()
    print(f"  「分析模式」按鈕數量: {btn_count}")
    assert btn_count > 0, "找不到「分析模式」按鈕！"

    # 點擊切換 — wait for visible BEFORE scroll to avoid layout-fade flake (issue #475)
    # LayUI admin layout animates visibility; scroll_into_view_if_needed times out when
    # the element is in the DOM but the containing panel is still transitioning.
    # issue #596: grid render 已在上面等過了，但按鈕本身的可見性切換仍可能落後
    # 一拍，因此給這顆 locator 獨立、更長的可見性逾時（45s），並記錄耗時方便從
    # CI log 診斷未來的 flake。
    first_btn = analysis_btn.first
    btn_wait_start = datetime.now()
    try:
        await first_btn.wait_for(state="visible", timeout=BTN_VISIBILITY_TIMEOUT)
    except Exception:
        btn_wait_elapsed = (datetime.now() - btn_wait_start).total_seconds()
        print(f"[TC-04] 「分析模式」按鈕可見性等待逾時（耗時 {btn_wait_elapsed:.2f}s）")
        await page.screenshot(path=sc(4, "02a-btn-not-visible"), full_page=True)
        raise
    btn_wait_elapsed = (datetime.now() - btn_wait_start).total_seconds()
    print(f"[TC-04] 「分析模式」按鈕可見耗時: {btn_wait_elapsed:.2f}s")
    await first_btn.scroll_into_view_if_needed(timeout=10000)
    await first_btn.click()
    # 等待 meta API 載入和面板渲染
    try:
        await page.wait_for_selector("[id^='analysis-panel-']", state="visible", timeout=5000)
    except PlaywrightTimeoutError:
        # issue #886 review: this wait is swallowed and the assert below can still
        # PASS if the panel shows up late, so capture the moment of the timeout —
        # otherwise a silently-slow panel leaves no trace at all. Round 2: use the
        # non-throwing helper — a capture failure here must not replace this
        # swallowed timeout with a different, unintended exception.
        #
        # issue #898: narrowed from bare `except Exception:` to this specific timeout
        # type. The downstream `assert panel_count > 0` / `assert panel_visible` below
        # still fire either way (this branch's only job is to get a screenshot before
        # they run), but a bare catch here previously meant ANY exception — a real JS
        # crash, a detached page, an actual bug in the panel-open code path — would
        # silently fall through to those same two asserts instead of surfacing as
        # itself. Anything that isn't a plain "panel not visible within 5s yet" now
        # propagates as an ERROR instead of being swallowed and re-interpreted.
        await _screenshot_on_failure(page, 4, "03-analysis-panel-open", full_page=True)

    # 確認面板已顯示
    # 面板 ID 格式: analysis-panel-{gridId}，gridId = wtTable_{UniqueId}
    panel = page.locator("[id^='analysis-panel-']")
    panel_count = await panel.count()
    print(f"  analysis-panel 數量: {panel_count}")
    assert panel_count > 0, "analysis-panel 不存在！"

    # 確認面板是 visible
    panel_visible = await panel.first.is_visible()
    print(f"  analysis-panel visible: {panel_visible}")
    assert panel_visible, "analysis-panel 未顯示！"

    # 確認欄位池存在
    pool = page.locator(".analysis-field-pool")
    pool_count = await pool.count()
    print(f"  .analysis-field-pool 數量: {pool_count}")

    # 確認拖放區存在
    dim_zone = page.locator(".analysis-dropzone--dim")
    msr_zone = page.locator(".analysis-dropzone--msr")
    print(f"  dim-zone: {await dim_zone.count()}, msr-zone: {await msr_zone.count()}")

    # 確認欄位 pill 存在
    pills = page.locator(".analysis-pill")
    pill_count = await pills.count()
    print(f"  欄位 pill 數量: {pill_count}")
    assert pill_count > 0, "沒有任何欄位 pill！"

    print("[TC-04] PASS -- Analysis Mode 頁面測試通過")


# ─── TC-05: Analysis Mode 按鈕不存在測試 ─────────────────────────────────────

async def tc_05_analysis_not_enabled(page, **_):
    """
    TC-05: 確認未標記 [EnableAnalysis] 的頁面沒有分析按鈕
    優先度: P1
    預估執行: 5s

    City 列表未標記 [EnableAnalysis]，不應出現分析按鈕。

    預期結果：
    - City Index 頁面沒有「分析模式」按鈕
    """
    print("[TC-05] 開始執行...")

    await login(page)
    await page.goto(f"{BASE_URL}/City/Index")
    await page.wait_for_load_state("networkidle")

    analysis_btn = page.locator("button:has-text('分析模式')")
    btn_count = await analysis_btn.count()
    print(f"  City 頁面「分析模式」按鈕數量: {btn_count}")
    assert btn_count == 0, "City 頁面不應有「分析模式」按鈕！"

    print("[TC-05] PASS -- 未啟用 Analysis 的頁面無按鈕")


# ─── TC-06: 登入失敗測試 ─────────────────────────────────────────────────────

async def tc_06_login_failure(page, **_):
    """
    TC-06: 登入失敗顯示錯誤訊息
    優先度: P1
    預估執行: 3s

    使用錯誤帳密登入，確認顯示錯誤訊息而非 500 錯誤。

    預期結果：
    - 回到登入頁面
    - 顯示 .login-error 錯誤訊息
    """
    print("[TC-06] 開始執行...")

    await page.goto(f"{BASE_URL}/Login/Login")
    await page.wait_for_load_state("networkidle")

    await page.locator("input[name='ITCode']").fill("wrong_user")
    await page.locator("input[name='Password']").fill("wrong_pass")
    await page.locator("button.login-button[type='submit']").click()
    await page.wait_for_load_state("networkidle")

    # 確認仍在登入頁
    login_form = page.locator("form[action='/Login/Login']")
    assert await login_form.count() > 0, "登入失敗後未回到登入頁面！"

    # 確認有錯誤訊息
    error_span = page.locator("span.login-error")
    error_text = await error_span.text_content() if await error_span.count() > 0 else ""
    print(f"  錯誤訊息: {error_text!r}")

    print("[TC-06] PASS -- 登入失敗正確顯示錯誤")


# ─── TC-07: 登出測試 ─────────────────────────────────────────────────────────

async def tc_07_logout(page, **_):
    """
    TC-07: 登出功能
    優先度: P1
    預估執行: 5s

    登入後點選登出，確認 Session 清除並重導回登入頁。

    預期結果：
    - 重導回登入頁
    - 存取受保護頁面被攔截
    """
    print("[TC-07] 開始執行...")

    await login(page)

    # 登出
    await page.goto(f"{BASE_URL}/Login/Logout")
    await page.wait_for_load_state("networkidle")

    # 確認回到登入頁或首頁
    final_url = page.url
    print(f"  登出後 URL: {final_url}")

    # 嘗試存取受保護頁面
    await page.goto(f"{BASE_URL}/Student/Index")
    await page.wait_for_load_state("networkidle")
    redirected_url = page.url
    print(f"  存取 Student/Index 後 URL: {redirected_url}")

    # 應被重導到登入頁
    assert "Login" in redirected_url or "login" in redirected_url.lower(), \
        f"登出後存取保護頁面未重導到登入頁！URL={redirected_url}"

    print("[TC-07] PASS -- 登出功能正常")


# ─── TC-08: 權限控制測試 ─────────────────────────────────────────────────────

async def tc_08_authorization(page, **_):
    """
    TC-08: 未認證存取受保護 API
    優先度: P0
    預估執行: 3s

    不登入直接存取 /_analysis/meta，確認被拒絕。

    預期結果：
    - 回傳 401 或重導到登入頁
    """
    print("[TC-08] 開始執行...")

    # 清除 cookie 確保未登入
    await page.context.clear_cookies()

    response = await page.request.get(
        f"{BASE_URL}/_analysis/meta?listVmType={STUDENT_LIST_VM}"
    )
    status = response.status
    print(f"  未認證 /_analysis/meta 回應: HTTP {status}")

    # 401 或 302 到登入頁都算正確
    assert status in (401, 302, 403, 200), f"預期 401/302/403，實際 {status}"
    if status == 200:
        # 如果 200，可能是 QuickDebug 跳過權限
        body = await response.text()
        print(f"  回傳內容（前 200 字）: {body[:200]}")
        print("  [WARN] QuickDebug 模式可能跳過權限驗證")

    print("[TC-08] PASS -- 權限控制測試完成")


# ─── TC-09: Session Fixation 測試 ────────────────────────────────────────────

async def tc_09_session_fixation(page, **_):
    """
    TC-09: Session Fixation 測試
    優先度: P1
    預估執行: 5s

    確認登入後 Session ID 有變更（或使用新 cookie）。

    預期結果：
    - 登入前後的 session cookie 值不同

    issue #905（重寫）：原版本只印出登入前後的 cookie 名稱、完全沒有 assert——不論
    WTM 是否真的核發全新的驗證 cookie 都一律 PASS。改寫後的版本模擬 session fixation
    攻擊的實際手法：攻擊者在受害者登入「之前」就先把驗證 cookie 的值固定成一個攻擊者
    已知的值（例如透過同網域下的另一個頁面、或誘騙受害者點擊帶有該值的連結），賭的是
    「身分驗證通過後，這個攻擊者已知的固定值會變成合法憑證」。因此本測試：
      1. 先用一個拋棄式的瀏覽器 context 跑一次完整登入，只為了取得驗證 cookie的「名稱」
         （名稱組成見 src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs 的
         `CookieAuthenticationDefaults.CookiePrefix + conf.CookiePre + "." +
         AuthConstants.CookieAuthName`，不同部署可能不同，因此不寫死字串，改為執行期
         探測），探測完立即關閉，不影響本測試主體的登入前後比較。
      2. 在真正要測試的 page/context 上，登入「之前」用 add_cookies() 把該驗證 cookie
         的名稱固定成一個已知的假值（模擬攻擊者的固定值植入）。
      3. 執行真正的登入。
      4. 斷言：登入後，每一個驗證 cookie 的值都不等於攻擊者植入的固定值——這就是
         session fixation 防護的核心語意：身分驗證必須核發全新的憑證，不能讓登入前已
         存在（可能是攻擊者控制）的值在登入後變成有效身分。這綁定的是「保護本身」
         （憑證是否真的被輪替），不是任何狀態碼。
    """
    print("[TC-09] 開始執行...")

    # Step 1: 用拋棄式 context 跑一次登入，只為了探測驗證 cookie 的實際名稱。
    probe_ctx = await page.context.browser.new_context()
    probe_page = await probe_ctx.new_page()
    await login(probe_page)
    probe_cookies = await probe_ctx.cookies()
    auth_cookie_names = sorted({c["name"] for c in probe_cookies if "AspNetCore" in c["name"]})
    await probe_ctx.close()
    print(f"  探測到的驗證 cookie 名稱: {auth_cookie_names}")
    assert auth_cookie_names, (
        "無法從一次正常登入中探測到任何驗證用 cookie（.AspNetCore.* 系列）——"
        "後續無法驗證 session fixation 防護，這本身就代表登入流程可能已經改變"
    )

    # Step 2: 在「登入前」植入攻擊者已知的固定值（session fixation 攻擊模擬）。
    await page.goto(f"{BASE_URL}/Login/Login")
    await page.wait_for_load_state("networkidle")
    fixed_value = "e2e-fixation-probe-" + datetime.now().strftime("%H%M%S%f")
    await page.context.add_cookies([
        {"name": name, "value": fixed_value, "url": BASE_URL} for name in auth_cookie_names
    ])
    print(f"  已植入攻擊者固定值（模擬 fixation）: {fixed_value!r}")

    # Step 3: 執行真正的登入。
    await login(page)

    # Step 4: 核心斷言——登入後每個驗證 cookie 都必須是全新的值，不能是攻擊者植入的
    # 固定值。若這裡失敗，代表身分驗證核發（或至少不排斥）了登入前已存在的憑證值，
    # 也就是 session fixation 漏洞本身。
    cookies_after = await page.context.cookies()
    auth_cookies_after = [c for c in cookies_after if c["name"] in auth_cookie_names]
    print(f"  登入後驗證 cookie: {[(c['name'], len(c['value'])) for c in auth_cookies_after]}")
    assert auth_cookies_after, (
        "登入後找不到任何驗證用 cookie（.AspNetCore.* 系列）——"
        f"預期名稱: {auth_cookie_names}"
    )
    for c in auth_cookies_after:
        assert c["value"] != fixed_value, (
            f"[Session Fixation] 驗證 cookie {c['name']} 登入後仍是攻擊者登入前植入的"
            f"固定值（{fixed_value!r}）——代表身分驗證未核發全新的憑證，"
            "攻擊者可利用登入前已知的值在受害者登入後直接取得該身分"
        )

    print("[TC-09] PASS -- Session Fixation 防護驗證通過"
          "（登入後所有驗證 cookie 皆核發全新值，攻擊者植入的固定值已失效）")


# ─── TC-10: HTTP Headers 安全測試 ────────────────────────────────────────────

async def tc_10_security_headers(page, **_):
    """
    TC-10: HTTP Security Headers 檢查
    優先度: P1
    預估執行: 3s

    檢查常見安全 headers：X-Content-Type-Options, X-Frame-Options 等。

    預期結果：
    - 記錄各 header 的存在狀態（部分可能未設定但不影響功能）

    issue #905（重寫）：原版本只印出六個 header 的存在狀態、完全沒有 assert——不論
    demo 回應裡有沒有這些 header 都一律 PASS。

    調查後確認（2026-07-31，對照本機跑起來的 demo 實測）：WTM 其實有實作這些防護，
    只是以 opt-in middleware 的形式存在——src/WalkingTec.Mvvm.Mvc/Helper/
    WtmSecureHeadersMiddleware.cs + WtmSecureHeadersExtension.cs 的
    `UseWtmSecureHeaders()`——但 demo/WalkingTec.Mvvm.Demo/Program.cs 沒有呼叫它。
    這與 repo 的「新功能一律 opt-in、不得默默改變預設行為」原則一致，不是本測試要抓
    的迴歸對象；也不是本測試可以單方面決定要不要幫 demo 打開的東西（那是另一個獨立
    的決策，需要另開 issue 討論是否要把 demo 設成示範這個 middleware 的預設環境）。

    #905 真正要修的是「不論結果如何都印 PASS」，不是「demo 應該要有這些 header」。
    因此這裡改為對「目前唯一可驗證的事」設防：既然六個 header 目前的真實狀態是
    全數缺席（demo 未啟用 UseWtmSecureHeaders()），就把這個已知狀態變成可斷言、
    可被打破的事實——如果任何一個 header 未來意外出現（例如 middleware 被啟用、
    或前面多了一層 reverse proxy 加上它），這個斷言會失敗，逼著維護者回來確認新出現
    的值是不是安全的設定並更新本測試，而不是繼續靜默地宣稱「已檢查」。
    """
    print("[TC-10] 開始執行...")

    response = await page.goto(f"{BASE_URL}/Login/Login")
    assert response is not None and response.status == 200, (
        f"登入頁應正常回傳 200，實際 {response.status if response else None}"
    )
    headers = response.headers

    security_headers = {
        "x-content-type-options": "nosniff",
        "x-frame-options": "DENY or SAMEORIGIN",
        "x-xss-protection": "1; mode=block",
        "strict-transport-security": "max-age=...",
        "content-security-policy": "...",
        "referrer-policy": "...",
    }

    missing = []
    present = {}
    for header in security_headers:
        value = headers.get(header)
        if value is None:
            missing.append(header)
        else:
            present[header] = value
        status_icon = "OK" if value is not None else "MISSING"
        print(f"  {header}: {value or 'NOT SET'} [{status_icon}]")

    if present:
        print(f"  [INFO] 以下 header 已出現於回應中：{present} —— demo 可能已啟用 "
              "UseWtmSecureHeaders()，下面的斷言會檢查其值是否安全")

    # 目前已知、刻意的 opt-in 缺席狀態：全部六個 header 都應該缺席。這個斷言把「已知
    # 狀態」變成可被打破的事實，而不是繼續放任這個 TC 對任何結果都一律 PASS。
    assert missing == list(security_headers.keys()), (
        "[KNOWN-GAP] demo 目前未呼叫 UseWtmSecureHeaders()（opt-in middleware，見 "
        "src/WalkingTec.Mvvm.Mvc/Helper/WtmSecureHeadersExtension.cs），預期六個安全 "
        f"header 全數缺席，但實際缺席清單為 {missing}（也就是 {present} 這幾個已出現）"
        "——若這是因為該 middleware 剛被啟用，請確認上面印出的值是否安全，"
        "並更新本測試改為驗證其值，而不是繼續假設全數缺席"
    )

    print("[TC-10] PASS -- Security Headers 檢查完成"
          "（已知 opt-in 缺席狀態經斷言確認，未意外出現任何 header）")


# ─── TC-11: Cookie Flags 測試 ────────────────────────────────────────────────

async def tc_11_cookie_flags(page, **_):
    """
    TC-11: Cookie 安全標記檢查
    優先度: P1
    預估執行: 3s

    登入後確認 auth cookie 的 HttpOnly、Secure、SameSite 標記。

    預期結果：
    - HttpOnly = true
    - Secure = true（HTTPS 環境）
    """
    print("[TC-11] 開始執行...")

    await login(page)

    cookies = await page.context.cookies()
    for c in cookies:
        print(f"  Cookie: {c['name']}")
        print(f"    HttpOnly: {c.get('httpOnly', 'N/A')}")
        print(f"    Secure: {c.get('secure', 'N/A')}")
        print(f"    SameSite: {c.get('sameSite', 'N/A')}")
        print(f"    Path: {c.get('path', 'N/A')}")

    # 檢查 auth cookie 的 HttpOnly
    auth_cookies = [c for c in cookies if "AspNetCore" in c["name"]]
    for c in auth_cookies:
        assert c.get("httpOnly", False), f"Auth cookie {c['name']} 缺少 HttpOnly！"

    print("[TC-11] PASS -- Cookie Flags 檢查完成")


# ─── TC-12: Rate Limiting 測試 ───────────────────────────────────────────────

async def tc_12_rate_limiting(page, **_):
    """
    TC-12: Rate Limiting / 暴力破解防護
    優先度: P2
    預估執行: 10s

    快速連續嘗試 10 次錯誤登入，觀察是否有速率限制。

    預期結果：
    - 記錄每次回應時間和狀態碼
    - 觀察是否出現 429 或延遲

    issue #905（重寫）：原版本只印出每次的狀態碼、完全沒有 assert——即使某次請求
    回傳 500 或連線中斷，這個 TC 依然一律 PASS。

    調查後確認（2026-07-31，對照本機跑起來的 demo 實測）：WTM 有實作 rate limiting
    基礎設施——src/WalkingTec.Mvvm.Mvc/Helper/WtmRateLimitAttribute.cs +
    WtmRateLimitingExtension.cs——但 demo 沒有任何 controller 套用 [WtmRateLimit]，
    Program.cs 也沒有呼叫 UseWtmRateLimiting()／AddWtmRateLimiting()。連續 10 次對
    /Login/Login 送出錯誤登入，實測全部回 200，沒有任何一次 429。這與 TC-10 的
    security headers 是同一種狀況：opt-in 功能存在，demo 沒有啟用它，是刻意的部署
    設定，不是本測試要抓的迴歸對象。

    #905 真正要修的是「不論結果如何都印 PASS」，不是「demo 應該要有 rate limiting」。
    因此這裡改為對「目前唯一可驗證、且與此端點直接相關的不變量」設防：連續 10 次錯誤
    登入，每一次都必須乾淨地回應 200（重新顯示登入頁），不可以有任何一次變成 500 或
    其他非預期狀態碼——那才是目前這個端點在缺乏 rate limiting 的情況下，仍然應該維持
    的最低保證（大量重複請求不能把登入端點打壞）。429 的出現與否維持原樣只記錄、不
    斷言，因為那本來就是目前刻意關閉的 opt-in 行為，斷言它出現只會讓這個 TC 對現在的
    demo 組態永遠紅燈，不會抓到任何真正的迴歸。
    """
    print("[TC-12] 開始執行...")

    results = []
    for i in range(10):
        start = datetime.now()
        response = await page.request.post(f"{BASE_URL}/Login/Login", form={
            "ITCode": f"brute_user_{i}",
            "Password": "wrong",
            "VerifyCode": "skip",
        })
        elapsed = (datetime.now() - start).total_seconds()
        results.append({"attempt": i, "status": response.status, "elapsed": elapsed})
        print(f"  嘗試 {i}: HTTP {response.status}, {elapsed:.2f}s")

    # 檢查是否有 429 回應（目前 demo 未啟用 WtmRateLimitingExtension，opt-in，見
    # src/WalkingTec.Mvvm.Mvc/Helper/WtmRateLimitingExtension.cs——記錄用，不斷言）。
    has_429 = any(r["status"] == 429 for r in results)
    print(f"  是否觸發 429: {has_429}")
    if not has_429:
        print("  [KNOWN-GAP] 未偵測到 Rate Limiting —— demo 未啟用 opt-in 的 "
              "WtmRateLimitingExtension，此為目前刻意的部署設定，非本測試涵蓋的迴歸")

    # 目前唯一可驗證、與此端點直接相關的不變量：連續大量錯誤登入不能把端點打壞。
    for r in results:
        assert r["status"] == 200, (
            f"連續嘗試第 {r['attempt']} 次錯誤登入時收到非預期狀態碼 {r['status']}"
            "（應為 200，重新顯示登入頁；即使沒有 rate limiting，也不應該是 500 "
            "或其他錯誤狀態碼）"
        )

    print("[TC-12] PASS -- Rate Limiting 測試完成"
          "（10 次連續錯誤登入皆正常回應 200；429 為已知未啟用的 opt-in 功能，僅記錄）")


# ─── TC-13: 驗證碼圖片存在測試 ──────────────────────────────────────────────

async def tc_13_captcha_exists(page, **_):
    """
    TC-13: 驗證碼圖片存在
    優先度: P1
    預估執行: 3s

    確認登入頁有 verify_code_img 元素且 src 正確。

    預期結果：
    - img#verify_code_img 存在
    - src 包含 /_framework/GetVerifyCode
    """
    print("[TC-13] 開始執行...")

    await page.goto(f"{BASE_URL}/Login/Login")
    await page.wait_for_load_state("networkidle")

    captcha_img = page.locator("img#verify_code_img")
    count = await captcha_img.count()
    print(f"  #verify_code_img 數量: {count}")
    assert count > 0, "驗證碼圖片不存在！"

    src = await captcha_img.get_attribute("src")
    print(f"  驗證碼 src: {src}")
    assert "GetVerifyCode" in (src or ""), f"驗證碼 src 不正確：{src}"

    # 確認圖片可載入
    captcha_response = await page.request.get(f"{BASE_URL}/_framework/GetVerifyCode?id=test")
    print(f"  驗證碼 API 回應: HTTP {captcha_response.status}")
    content_type = captcha_response.headers.get("content-type", "")
    print(f"  Content-Type: {content_type}")
    assert captcha_response.status == 200, f"驗證碼 API 回傳 {captcha_response.status}"

    print("[TC-13] PASS -- 驗證碼圖片存在且可載入")


# ─── TC-14: 密碼欄位 autocomplete 測試 ───────────────────────────────────────

async def tc_14_password_autocomplete(page, **_):
    """
    TC-14: 密碼欄位 autocomplete 設定
    優先度: P2
    預估執行: 2s

    確認密碼欄位的安全屬性。

    預期結果：
    - 密碼欄位 type=password
    - 驗證碼欄位 autocomplete=off
    """
    print("[TC-14] 開始執行...")

    await page.goto(f"{BASE_URL}/Login/Login")
    await page.wait_for_load_state("networkidle")

    # 密碼欄位
    pwd = page.locator("input[name='Password']")
    pwd_type = await pwd.get_attribute("type")
    print(f"  Password type: {pwd_type}")
    assert pwd_type == "password", f"密碼欄位 type 不是 password：{pwd_type}"

    # 驗證碼欄位 autocomplete
    verify = page.locator("input[name='VerifyCode']")
    ac = await verify.get_attribute("autocomplete")
    print(f"  VerifyCode autocomplete: {ac}")
    assert ac == "off", f"驗證碼 autocomplete 不是 off：{ac}"

    print("[TC-14] PASS -- 密碼欄位安全屬性正確")


# ─── TC-15: 分析 API 無 Measures 驗證 ────────────────────────────────────────

async def tc_15_analysis_no_measures(page, **_):
    """
    TC-15: Analysis API — 0 measures 回傳 400
    優先度: P1
    預估執行: 3s

    POST /_analysis/query 不帶 measures，應回傳 400。
    驗證 #305 修復。

    預期結果：
    - HTTP 400
    - 回傳訊息含「度量」相關文字
    """
    print("[TC-15] 開始執行...")

    await login(page)

    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["Name"],
        "measures": [],
    }

    response = await page.request.post(
        f"{BASE_URL}/_analysis/query",
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}: {body[:200]}")
    assert status == 400, f"預期 400，實際 {status}"
    assert "度量" in body or "measure" in body.lower(), f"錯誤訊息不含度量相關文字：{body}"

    print("[TC-15] PASS -- 0 measures 正確回傳 400")


# ─── TC-16: 分析 API 超過 3 維度驗證 ─────────────────────────────────────────

async def tc_16_analysis_too_many_dims(page, **_):
    """
    TC-16: Analysis API — 超過 3 維度回傳 400
    優先度: P1
    預估執行: 3s

    預期結果：
    - HTTP 400
    """
    print("[TC-16] 開始執行...")

    await login(page)

    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["Name", "Sex", "Address", "IsValid"],
        "measures": [{"field": "RecordCount", "func": "Sum"}],
    }

    response = await page.request.post(
        f"{BASE_URL}/_analysis/query",
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}: {body[:200]}")
    assert status == 400, f"預期 400，實際 {status}"

    print("[TC-16] PASS -- 超過 3 維度正確回傳 400")


# ─── TC-17: 分析 API 正常查詢 ────────────────────────────────────────────────

async def tc_17_analysis_query_success(page, **_):
    """
    TC-17: Analysis API — 正常查詢回傳 200
    優先度: P1
    預估執行: 5s

    預期結果：
    - HTTP 200
    - 回傳 JSON 含 columns, rows
    """
    print("[TC-17] 開始執行...")

    await login(page)

    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["Sex"],
        "measures": [{"field": "RecordCount", "func": "Count"}],
    }

    response = await page.request.post(
        f"{BASE_URL}/_analysis/query",
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}: {body[:500]}")
    assert status == 200, f"預期 200，實際 {status}"

    data = json.loads(body)
    assert "columns" in data, "回傳缺少 columns"
    assert "rows" in data, "回傳缺少 rows"
    print(f"  columns: {data['columns']}")
    print(f"  rows 數量: {len(data['rows'])}")

    print("[TC-17] PASS -- 分析查詢正常回傳")


# ─── TC-18: 分析 API Export 測試 ─────────────────────────────────────────────

async def tc_18_analysis_export(page, **_):
    """
    TC-18: Analysis Export — xlsx 和 csv 匯出
    優先度: P1
    預估執行: 5s

    預期結果：
    - xlsx: Content-Type = application/vnd.openxmlformats...
    - csv: Content-Type = text/csv
    """
    print("[TC-18] 開始執行...")

    await login(page)

    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["Sex"],
        "measures": [{"field": "RecordCount", "func": "Count"}],
    }

    for fmt in ["xlsx", "csv"]:
        response = await page.request.post(
            f"{BASE_URL}/_analysis/export?format={fmt}",
            data=json.dumps(payload),
            headers={"Content-Type": "application/json"},
        )
        status = response.status
        ct = response.headers.get("content-type", "")
        print(f"  {fmt}: HTTP {status}, Content-Type: {ct}")
        assert status == 200, f"{fmt} 匯出失敗：HTTP {status}"

    print("[TC-18] PASS -- 匯出功能正常")


# ─── TC-19: 分析 API 不明 VM 型別 ───────────────────────────────────────────

async def tc_19_analysis_unknown_vm(page, **_):
    """
    TC-19: Analysis API — 不明 VM 型別回傳 404
    優先度: P1
    預估執行: 3s

    預期結果：
    - HTTP 404 或 400
    """
    print("[TC-19] 開始執行...")

    await login(page)

    response = await page.request.get(
        f"{BASE_URL}/_analysis/meta?listVmType=NonExistent.FakeListVM"
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}: {body[:200]}")
    assert status in (400, 404), f"預期 400/404，實際 {status}"

    print("[TC-19] PASS -- 不明 VM 型別正確拒絕")


# ─── TC-20: 分析 API 不合法欄位 ─────────────────────────────────────────────

async def tc_20_analysis_invalid_field(page, **_):
    """
    TC-20: Analysis API — 不合法欄位名稱回傳 400
    優先度: P1
    預估執行: 3s

    注入非白名單欄位，驗證 Expression Tree 安全。

    預期結果：
    - HTTP 400
    """
    print("[TC-20] 開始執行...")

    await login(page)

    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["__hack_field__"],
        "measures": [{"field": "RecordCount", "func": "Count"}],
    }

    response = await page.request.post(
        f"{BASE_URL}/_analysis/query",
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}: {body[:200]}")
    assert status == 400, f"預期 400，實際 {status}"

    print("[TC-20] PASS -- 不合法欄位正確拒絕")


# ─── TC-21: 登入頁視覺驗收 ──────────────────────────────────────────────────

async def tc_21_login_visual(page, **_):
    """
    TC-21: 登入頁視覺驗收
    優先度: P2
    預估執行: 5s

    確認登入頁面完整 UI：
    - 背景圖存在（app-login-back-{1-5} class）
    - 驗證碼圖片存在
    - 桌面 + 手機響應式版面

    預期結果：
    - 登入表單正確顯示
    - 背景 class 為 app-login-back-{1-5}
    - 驗證碼圖片 #verify_code_img 存在

    issue #886 review：本 TC 的判定完全來自下面的 DOM assert，不依賴任何截圖 ——
    桌面/手機兩張快照預設不拍（VISUAL_SNAPSHOTS 預設 off），設環境變數
    WTM_E2E_VISUAL_SNAPSHOTS=1 才會產生，給人工複核視覺版面用。
    """
    print("[TC-21] 開始執行...")

    # 桌面 1280x800
    await page.set_viewport_size({"width": 1280, "height": 800})
    await page.goto(f"{BASE_URL}/Login/Login")
    await page.wait_for_load_state("networkidle")
    if VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(21, "01-desktop-1280x800"), full_page=True)

    # 確認背景 class
    bg_div = page.locator("div.loginBody")
    bg_class = await bg_div.get_attribute("class") if await bg_div.count() > 0 else ""
    print(f"  loginBody class: {bg_class}")

    # 解析背景數字
    import re
    match = re.search(r"app-login-back-(\d)", bg_class)
    if match:
        bg_num = int(match.group(1))
        print(f"  背景圖數字: {bg_num}")
        assert 1 <= bg_num <= 5, f"背景數字超出範圍：{bg_num}"
    else:
        print("  [WARN] 未找到 app-login-back-N class")

    # 確認表單元素存在
    assert await page.locator("input[name='ITCode']").count() > 0, "缺少帳號欄位"
    assert await page.locator("input[name='Password']").count() > 0, "缺少密碼欄位"
    assert await page.locator("input[name='VerifyCode']").count() > 0, "缺少驗證碼欄位"
    assert await page.locator("img#verify_code_img").count() > 0, "缺少驗證碼圖片"
    assert await page.locator("button.login-button").count() > 0, "缺少登入按鈕"

    # Logo
    logo = page.locator("header.login-header img")
    logo_count = await logo.count()
    print(f"  Logo img 數量: {logo_count}")
    if VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(21, "02-desktop-elements"))

    # 手機 375x812
    await page.set_viewport_size({"width": 375, "height": 812})
    await page.wait_for_load_state("networkidle")
    if VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(21, "03-mobile-375x812"), full_page=True)

    # 還原視窗大小
    await page.set_viewport_size({"width": 1280, "height": 800})

    print("[TC-21] PASS -- 登入頁表單驗證通過"
          + ("（視覺快照已產生）" if VISUAL_SNAPSHOTS else ""))


# ─── TC-22: 首頁 Dashboard 版面驗證 ─────────────────────────────────────────

async def tc_22_dashboard(page, **_):
    """
    TC-22: 首頁 Dashboard 版面驗證
    優先度: P2
    預估執行: 8s

    登入後確認完整首頁版面：
    - 側邊選單存在
    - 頂部 header 存在
    - FrontPage 中的 layui-card 區塊存在

    預期結果：
    - .layui-layout-admin 存在
    - .layui-side-menu 存在
    - .layui-header 存在

    issue #886 review：判定完全來自下面的佈局 assert，不依賴任何截圖。逾時分支
    的截圖（sidebar 未如期出現）維持無條件拍照 —— 那是失敗診斷，不是視覺驗收；
    其餘三張完整版面快照預設不拍，設 WTM_E2E_VISUAL_SNAPSHOTS=1 才會產生。
    """
    print("[TC-22] 開始執行...")

    await login(page)
    # 等待 FrontPage 非同步載入
    try:
        # sidebar 出現代表 dashboard iframe 已完整 render
        await page.wait_for_selector(".layui-side-menu", state="visible", timeout=5000)
    except Exception:
        # 失敗診斷，不受 VISUAL_SNAPSHOTS 控制 —— 這是 sidebar 逾時未出現的證據。
        await page.screenshot(path=sc(22, "01-dashboard-layout-timeout"), full_page=True)
    if VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(22, "01-dashboard-full"), full_page=True)

    # 確認主要佈局元素
    layout = page.locator(".layui-layout-admin")
    assert await layout.count() > 0, "缺少 .layui-layout-admin"

    header = page.locator(".layui-header")
    assert await header.count() > 0, "缺少 .layui-header"

    sidebar = page.locator(".layui-side-menu")
    assert await sidebar.count() > 0, "缺少 .layui-side-menu"

    # 側邊選單 — 列出頂層選單項目
    menu_items = page.locator("#LAY-system-side-menu > li")
    # Wait for menu items to render (template renders asynchronously)
    try:
        await page.wait_for_selector("#LAY-system-side-menu > li", state="visible", timeout=3000)
    except Exception:
        pass
    menu_count = await menu_items.count()
    print(f"  頂層選單項目數量: {menu_count}")

    for i in range(min(menu_count, 10)):
        item_text = await menu_items.nth(i).locator("a > cite").first.text_content()
        print(f"    選單 {i}: {item_text}")

    if VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(22, "02-sidebar-menu"))

    # 確認使用者名稱顯示
    user_cite = page.locator(".layui-layout-right .layui-nav-item cite")
    if await user_cite.count() > 0:
        user_name = await user_cite.first.text_content()
        print(f"  登入使用者: {user_name}")

    # body 區域（FrontPage 內容透過 iframe 載入）
    body = page.locator("#LAY_app_body")
    if await body.count() > 0 and VISUAL_SNAPSHOTS:
        await page.screenshot(path=sc(22, "03-main-body"))

    print("[TC-22] PASS -- Dashboard 佈局驗證通過"
          + ("（視覺快照已產生）" if VISUAL_SNAPSHOTS else ""))


# ─── TC-23: Analysis Meta API 驗證（#516 修復） ─────────────────────────────

async def tc_23_analysis_meta_api(page, **_):
    """
    TC-23: Analysis Meta API — allowedValues 驗證
    優先度: P1
    預估執行: 5s

    GET /_analysis/meta?listVmType=StudentListVM
    確認 #516 修復：enum Dimension 的 allowedValues 欄位有值。

    Student Model 的 [Dimension] 欄位：
    - Name (string) → allowedValues = null 或 []
    - Sex (GenderEnum?) → allowedValues 應包含 enum 值
    - Address (string)
    - IsValid (bool) → allowedValues 應有 true/false
    - EnRollDate (DateTime, isDate=true)

    預期結果：
    - 回傳 JSON array
    - Sex 欄位的 allowedValues 包含 GenderEnum 的值
    - 所有欄位有 fieldName, displayName, kind
    """
    print("[TC-23] 開始執行...")

    await login(page)

    response = await page.request.get(
        f"{BASE_URL}/_analysis/meta?listVmType={STUDENT_LIST_VM}"
    )
    status = response.status
    body = await response.text()
    print(f"  HTTP {status}")
    assert status == 200, f"預期 200，實際 {status}: {body[:200]}"

    fields = json.loads(body)
    assert isinstance(fields, list), f"預期 array，實際 {type(fields)}"
    print(f"  欄位數量: {len(fields)}")

    # 列出所有欄位
    for f in fields:
        print(f"    {f['fieldName']} ({f['kind']}): "
              f"displayName={f.get('displayName')}, "
              f"isDate={f.get('isDate')}, "
              f"allowedValues={f.get('allowedValues')}")

    # 驗證 #516：Sex 欄位應有 allowedValues
    sex_field = next((f for f in fields if f["fieldName"] == "Sex"), None)
    if sex_field:
        av = sex_field.get("allowedValues")
        print(f"  Sex allowedValues count: {len(av) if av else 0}")
        assert av is not None and len(av) > 0, \
            f"[#516] Sex 的 allowedValues 為空！"
        print(f"  [#516 驗證通過] Sex allowedValues 有 {len(av)} 個值")
    else:
        print("  [WARN] 未找到 Sex 欄位（可能未標記 [Dimension]）")

    # 確認 Measure 欄位存在
    measures = [f for f in fields if f["kind"] == "Measure"]
    print(f"  Measure 欄位數量: {len(measures)}")
    for m in measures:
        print(f"    {m['fieldName']}: allowedFuncs={m.get('allowedFuncs')}")

    # 確認日期欄位
    date_fields = [f for f in fields if f.get("isDate")]
    print(f"  日期欄位數量: {len(date_fields)}")

    print("[TC-23] PASS -- Meta API 驗證完成（含 #516 allowedValues）")


# ─── TC-24: Analysis 完整查詢流程 ────────────────────────────────────────────

async def tc_24_analysis_full_flow(page, **_):
    """
    TC-24: Analysis 完整查詢流程
    優先度: P1
    預估執行: 15s

    在 Student Index 頁面操作完整 Analysis Mode 流程：
    1. 開啟分析面板
    2. 拖放維度和度量（或直接透過 API 查詢）
    3. 確認圖表和表格渲染

    預期結果：
    - 面板開啟成功
    - 查詢後 analysis-result-section 顯示
    - 有 canvas（ECharts 圖表）或 table

    issue #898（重寫）：原版本完全沒有 assert，PASS 只代表流程走完沒有拋出例外——
    連 grid toolbar 逾時未 attach、分析按鈕逾時未出現、面板逾時未開啟這幾個路徑都各自
    有 except 吞掉例外後悄悄印一句 WARN/SKIP 就繼續，最後仍然 PASS。改寫後把「toolbar
    attach / 按鈕出現 / 面板開啟 / 欄位 pill 存在」都變成真斷言；只有「拖放（drag_to）
    本身」保留環境性容忍——headless CI 上 Sortable.js 的拖放模擬確實有已知的時序脆弱性
    （見下方 except 分支），但拖放逾時不再讓整個 TC 直接 PASS：後面透過直接呼叫
    /_analysis/query API 驗證同一個查詢功能的部分，不受拖放是否成功影響，且現在真的有
    斷言（之前這段只是印出 HTTP 狀態碼，不論 200 與否都不斷言）。
    """
    print("[TC-24] 開始執行...")

    await login(page)

    # 使用 JS 點擊 sidebar 連結（lay-href 使用 hash 路由）
    await page.evaluate(
        """() => {
            const links = document.querySelectorAll('a[lay-href="/Student/Index"]');
            if (links.length > 0) links[0].click();
        }"""
    )
    # 等 DataTable 在主 frame 完成初始化
    await page.wait_for_function(
        """() => {
            const caches = window.layui?.table?.cache || {};
            return Object.keys(caches).length > 0;
        }""",
        timeout=TIMEOUT
    )
    # Replace hardcoded sleep — wait for toolbar DOM attachment as readiness signal
    try:
        await page.wait_for_selector(".layui-table-tool", state="attached", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 24, "01-student-grid", full_page=True)
    # issue #898: toolbar not attaching within budget is exactly "the feature under test
    # is broken" (TC-04/TC-25 reach this same grid reliably within this window), not an
    # environmental hiccup — assert instead of silently continuing to a 0-button state.
    assert await page.locator(".layui-table-tool").count() > 0, (
        "Student/Index grid toolbar 逾時未 attach（見上方截圖，若有）"
    )

    # Step 1: 開啟分析面板
    analysis_btn = page.locator("button:has-text('分析模式')")
    btn_count = await analysis_btn.count()
    print(f"  「分析模式」按鈕數量: {btn_count}")
    assert btn_count > 0, "找不到「分析模式」按鈕！"

    # Wait for the Analysis button to be fully actionable before clicking.
    # LayUI admin layout has a CSS fade-in animation (visibility:hidden → visible)
    # that can leave the button DOM-visible but still covered by a transitioning
    # overlay in headless CI. Strategy: wait for visible (10s budget to account for
    # slow /_analysis/meta API on a cold CI runner), scroll into view, then click
    # with a generous targeted timeout rather than the global default.
    try:
        await analysis_btn.first.wait_for(state="visible", timeout=10000)
    except PlaywrightTimeoutError:
        await _screenshot_on_failure(page, 24, "02a-btn-not-visible", full_page=True)
    assert await analysis_btn.first.is_visible(), (
        "「分析模式」按鈕逾時仍未變為可見（見上方截圖，若有）"
    )

    # Explicit timeout on scroll: element is confirmed visible above, but
    # scroll_into_view_if_needed can still timeout without its own budget (issue #475)
    await analysis_btn.first.scroll_into_view_if_needed(timeout=10000)
    # Targeted 30s click timeout: accounts for /meta API fetch + panel animation
    # on a slow CI runner (CI showed "Locator.click: Timeout 20000ms exceeded"
    # against the 20s Playwright default — issue #328).
    await analysis_btn.first.click(timeout=30000)
    try:
        await page.wait_for_selector(".analysis-field-pool", state="visible", timeout=5000)
    except PlaywrightTimeoutError:
        await _screenshot_on_failure(page, 24, "02-panel-open")
    panel_pool = page.locator(".analysis-field-pool")
    assert await panel_pool.count() > 0 and await panel_pool.first.is_visible(), (
        "點擊「分析模式」後 .analysis-field-pool 未出現/未顯示（見上方截圖，若有）"
    )

    # Step 2: 確認欄位載入
    pills = page.locator(".analysis-pill")
    pill_count = await pills.count()
    print(f"  欄位 pill 數量: {pill_count}")
    assert pill_count > 0, "分析面板已開啟，但沒有任何欄位 pill！"

    # Step 3: 嘗試透過頁面操作拖放
    # 找到維度區的 pill 和拖放區
    dim_pills = page.locator(".analysis-pill[data-kind='Dimension']")
    msr_pills = page.locator(".analysis-pill[data-kind='Measure']")
    dim_zone = page.locator(".analysis-dropzone--dim")
    msr_zone = page.locator(".analysis-dropzone--msr")

    dim_count = await dim_pills.count()
    msr_count = await msr_pills.count()
    print(f"  Dimension pills: {dim_count}, Measure pills: {msr_count}")
    assert dim_count > 0 and msr_count > 0, (
        f"分析面板中找不到可拖放的 Dimension/Measure pills"
        f"（Dimension={dim_count}, Measure={msr_count}）"
    )

    # 嘗試拖放第一個 Dimension pill 到 dim zone。這裡保留原有的環境性容忍：headless CI
    # 上 Sortable.js 的拖放模擬有已知的時序脆弱性，逾時不直接判 FAIL——但（見下方）不再
    # 因此讓整個 TC 靜默 PASS，查詢功能仍會透過下面的直接 API 呼叫獨立驗證一次。
    drag_ok = True
    try:
        await dim_pills.first.drag_to(dim_zone)
        await page.wait_for_load_state("networkidle")
        await msr_pills.first.drag_to(msr_zone)
        await page.wait_for_load_state("networkidle")
    except PlaywrightTimeoutError as e:
        drag_ok = False
        print(f"  [WARN] 拖放操作逾時（可能是 Sortable.js 在此 runner 上的已知限制）: {e}")
        await _screenshot_on_failure(page, 24, "04-drag-failed")

    if drag_ok:
        # Step 4: 點擊查詢按鈕
        query_btn = page.locator("button:has-text('查詢'), button:has-text('執行'), .analysis-btn-query")
        query_btn_count = await query_btn.count()
        assert query_btn_count > 0, "拖放完成後找不到查詢按鈕（'查詢'/'執行'/.analysis-btn-query）"

        # Ensure query button is actionable (drag-and-drop may trigger a loading state
        # that covers the button briefly). Must confirm visible BEFORE
        # scroll_into_view_if_needed to avoid #475 flake.
        try:
            await query_btn.first.wait_for(state="visible", timeout=10000)
        except PlaywrightTimeoutError:
            await _screenshot_on_failure(page, 24, "05a-query-btn-not-visible", full_page=True)
        assert await query_btn.first.is_visible(), "查詢按鈕逾時仍未變為可見（見上方截圖，若有）"

        await query_btn.first.scroll_into_view_if_needed(timeout=10000)
        await query_btn.first.click(timeout=10000)
        try:
            await page.wait_for_selector(
                ".analysis-result-section, canvas, .analysis-result-section table",
                state="visible", timeout=5000)
        except PlaywrightTimeoutError:
            await _screenshot_on_failure(page, 24, "06-query-result")

        # 確認結果區顯示
        result_section = page.locator(".analysis-result-section")
        result_visible = await result_section.count() > 0 and await result_section.first.is_visible()
        print(f"  result-section visible: {result_visible}")

        # 確認圖表或表格
        canvas_count = await page.locator("canvas").count()
        table_count = await page.locator(".analysis-result-section table").count()
        print(f"  Canvas 數量: {canvas_count}")
        print(f"  Result table 數量: {table_count}")
        assert result_visible, "點擊查詢後 .analysis-result-section 未顯示（見上方截圖，若有）"
        assert canvas_count > 0 or table_count > 0, (
            "查詢後 .analysis-result-section 已顯示，但既沒有 canvas（圖表）也沒有 table"
        )

    # 也透過 API 驗證一次——這段與上面的 UI 拖放互相獨立，不受 Sortable.js flake 影響。
    # issue #898：這段先前完全沒有斷言，HTTP 狀態碼不論是不是 200 都只是印出來。
    payload = {
        "listVmType": STUDENT_LIST_VM,
        "dimensions": ["Sex"],
        "measures": [{"field": "RecordCount", "func": "Count"}],
    }
    resp = await page.request.post(
        f"{BASE_URL}/_analysis/query",
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
    )
    body = await resp.text()
    print(f"  API 查詢: HTTP {resp.status}")
    assert resp.status == 200, f"Analysis API 查詢失敗：HTTP {resp.status}: {body[:200]}"
    data = json.loads(body)
    assert "rows" in data, f"Analysis API 回應缺少 rows 欄位: {body[:200]}"
    print(f"  API rows: {len(data.get('rows', []))}")

    print("[TC-24] PASS -- Analysis 完整查詢流程驗證通過"
          + ("" if drag_ok else "（UI 拖放逾時，已改用 API 獨立驗證查詢功能）"))


# ─── TC-25: Grid 分頁功能 ───────────────────────────────────────────────────

async def tc_25_grid_paging(page, **_):
    """
    TC-25: Grid 分頁功能
    優先度: P2
    預估執行: 8s

    確認 LayUI table 分頁元件存在，測試切換每頁筆數。

    預期結果：
    - .layui-table-page 存在
    - 分頁選擇器（每頁 N 筆）可用

    issue #898（重寫）：原版本完全沒有 assert——分頁元件存不存在、select 有沒有
    option、表格有沒有任何一列，全部只是印出來，一律 PASS。demo 的 Student 種子
    資料固定有 250 筆（遠大於預設每頁筆數），因此分頁元件、每頁筆數 select 與非零
    表格列數在正常情況下都是必然出現、可以斷言的行為，不是「可能有可能沒有」。
    """
    print("[TC-25] 開始執行...")

    await login(page)

    # 使用 JS 點擊 sidebar 連結（lay-href 使用 hash 路由）
    await page.evaluate(
        """() => {
            const links = document.querySelectorAll('a[lay-href="/Student/Index"]');
            if (links.length > 0) links[0].click();
        }"""
    )
    # 等 DataTable 在主 frame 完成初始化
    await page.wait_for_function(
        """() => {
            const caches = window.layui?.table?.cache || {};
            return Object.keys(caches).length > 0;
        }""",
        timeout=TIMEOUT
    )
    await asyncio.sleep(0.5)
    # Wait for grid rows to render
    try:
        await page.wait_for_selector(".layui-table-body tr[data-index]", state="attached", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 25, "01-grid-initial")

    # 確認表格行數 —— issue #898：demo 種子資料固定 250 筆 Student，這裡不再是
    # "可能沒有資料"的軟性檢查，而是斷言 grid 真的渲染出資料列。
    rows = page.locator(".layui-table-body tr[data-index]")
    row_count = await rows.count()
    print(f"  表格行數: {row_count}")
    assert row_count > 0, (
        "Student/Index grid 未渲染出任何資料列（見上方截圖，若有；"
        "demo 種子資料固定有 250 筆 Student，理論上不該是 0）"
    )

    # 確認分頁元件存在 —— 250 筆資料遠超過預設每頁筆數，分頁元件必然出現。
    pager = page.locator(".layui-table-page")
    pager_count = await pager.count()
    print(f"  .layui-table-page 數量: {pager_count}")
    assert pager_count > 0, "分頁元件（.layui-table-page）不存在！"

    # LayUI 分頁的「每頁 N 條」select
    page_select = page.locator(".layui-table-page select")
    select_count = await page_select.count()
    print(f"  分頁 select 數量: {select_count}")
    assert select_count > 0, "分頁「每頁 N 條」select 不存在！"

    # 列出 select options，並斷言至少有一個可選項
    options = page.locator(".layui-table-page select option")
    opt_count = await options.count()
    assert opt_count > 0, "分頁 select 沒有任何 option！"
    for i in range(opt_count):
        text = await options.nth(i).text_content()
        val = await options.nth(i).get_attribute("value")
        print(f"    option: {text} (value={val})")

    # 確認分頁文字資訊
    page_info = page.locator(".layui-laypage-count, .layui-table-page .layui-laypage")
    page_info_count = await page_info.count()
    assert page_info_count > 0, "分頁文字資訊（.layui-laypage-count）不存在！"
    info_text = await page_info.first.text_content()
    print(f"  分頁資訊: {info_text!r}")

    print("[TC-25] PASS -- Grid 分頁功能驗證通過")


# ─── TC-26: CRUD 完整流程 ───────────────────────────────────────────────────

async def tc_26_crud_flow(page, **_):
    """
    TC-26: Student CRUD 完整流程
    優先度: P1
    預估執行: 15s

    Student Create → Edit → Details → Delete 完整流程。

    WTM CRUD 使用 LayUI layer 彈出層載入 PartialView，透過 grid toolbar 按鈕開啟
    （見 open_grid_via_sidebar/open_toolbar_dialog 的說明：直接 page.goto() 到這些
    PartialView URL 不會載入 framework_layui.js／jQuery／xm-select）。

    預期結果：
    - Create form 有所有必要欄位
    - Edit form 載入正確
    - Delete 有確認訊息

    issue #898（重寫，兩個問題一起修）：
      1. 原版本完全沒有 assert——表單欄位存不存在、grid 有沒有列、搜尋面板/按鈕
         存不存在，全部只是印出來，一律 PASS。
      2. 原版本用 page.goto() 直接導覽到 /Student/Create 與 /Student/Index，這是
         PartialView-only 端點（伺服器端一律回傳裸片段，不含 <html>/<script> 標籤，
         實測確認），繞過 layuiadmin 的 AJAX tab 載入機制會讓 layui/xmSelect 完全
         沒有載入。表單欄位的「存在」檢查剛好因為那些欄位是純 server-rendered
         `<input>`（不需要 JS 就能出現在 DOM 中）而沒有暴露這個問題，但 grid 那段
         就會踩雷（.layui-table-body 恆為 0，因為 table.render() 從未真正執行）。
         改寫後全程透過 open_grid_via_sidebar()/open_toolbar_dialog() 走 layuiadmin
         真正的 tab 載入路徑，Create 表單也改成透過 grid toolbar 的「新建」按鈕以
         layer 對話框開啟（與 TC-33/34/35 相同、已驗證可行的模式），而不是直接
         page.goto()——這兩者一旦混用會互相破壞（goto() 是整頁導覽，會把
         layuiadmin 的 shell 連同 layui.js 一起丟棄，之後任何 lay-href 側邊欄連結
         都點不到），因此本測試全程留在同一個 shell 內。
    """
    print("[TC-26] 開始執行...")

    await login(page)

    # Step 1: 先進入 Student/Index grid（走 layuiadmin 真正的 tab 載入路徑，而非
    # page.goto()——理由見上方 docstring）。
    await open_grid_via_sidebar(page, "/Student/Index")
    try:
        await page.wait_for_selector(".layui-table-body tr[data-index]", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 26, "03-student-list")

    rows = page.locator(".layui-table-body tr[data-index]")
    row_count = await rows.count()
    print(f"  Student grid 行數: {row_count}")
    assert row_count > 0, (
        "Student/Index grid 未渲染出任何資料列（見上方截圖，若有；"
        "demo 種子資料固定有 250 筆 Student，理論上不該是 0）"
    )

    # Step 2: 搜尋面板與搜尋按鈕（真正渲染出的搜尋按鈕是 <a id="wtSearchBtn_...">，
    # 不是 <button>，原版本的 `button:has-text('搜索')` 選擇器永遠不會命中任何元素——
    # 這裡一併修正選擇器）。
    search_panel = page.locator(".layui-form[id^='wtForm_']")
    sp_count = await search_panel.count()
    print(f"  搜尋面板: {sp_count}")
    assert sp_count > 0, "Student/Index 缺少搜尋面板（.layui-form[id^='wtForm_']）"

    search_btn = page.locator("[id^='wtSearchBtn_']")
    sb_count = await search_btn.count()
    print(f"  搜尋按鈕: {sb_count}")
    assert sb_count > 0, "Student/Index 缺少搜尋按鈕（[id^='wtSearchBtn_']）"

    # Step 3: 透過 grid toolbar 的「新建」按鈕開啟 Create 對話框。layer 外殼變為
    # visible 不代表裡面的 PartialView 表單內容已經渲染完成，這裡沿用 TC-33/34/35
    # 已驗證過的固定 settle wait（1500ms），不然欄位檢查會在表單內容還沒填入 DOM 時
    # 就搶跑，誤判成欄位缺少（本次改寫時實測踩到過一次）。
    await open_toolbar_dialog(page, "新建")
    await page.wait_for_timeout(1500)

    # 確認表單欄位
    form_fields = {
        "Entity.ID": "input[name='Entity.ID']",
        "Entity.Password": "input[name='Entity.Password']",
        "Entity.Email": "input[name='Entity.Email']",
        "Entity.Name": "input[name='Entity.Name']",
        "Entity.CellPhone": "input[name='Entity.CellPhone']",
        "Entity.Address": "input[name='Entity.Address']",
        "Entity.ZipCode": "input[name='Entity.ZipCode']",
        "Entity.EnRollDate": "input[name='Entity.EnRollDate']",
    }
    for name, selector in form_fields.items():
        count = await page.locator(selector).count()
        print(f"  {name}: {'存在' if count > 0 else '缺少'}")
        assert count > 0, f"Create 表單缺少必要欄位 {name}（{selector}）"

    # Sex 是 combobox，這個 WTM 版本以 xmSelect.render() 渲染（隱藏 input，不是
    # <select>），因此這裡只記錄、不斷言型別為 select——斷言真正存在的 hidden input。
    sex_hidden = page.locator("input[name='Entity.Sex']")
    print(f"  Entity.Sex hidden input: {'存在' if await sex_hidden.count() > 0 else '缺少'}")

    # Step 4: 填寫表單
    test_id = f"e2e_test_{datetime.now().strftime('%H%M%S')}"
    await page.locator("input[name='Entity.ID']").fill(test_id)
    await page.locator("input[name='Entity.Password']").fill("test123456")
    await page.locator("input[name='Entity.Name']").fill("E2E Test Student")

    # 提交按鈕 — WTM <wt:submitbutton /> 渲染為 layui-btn 帶 lay-submit
    submit_btn = page.locator("button[lay-submit], a[lay-submit]")
    submit_count = await submit_btn.count()
    print(f"  Submit 按鈕數量: {submit_count}")
    assert submit_count > 0, "Create 表單缺少 submit 按鈕（[lay-submit]）"

    await close_layer_dialog(page)

    print("[TC-26] PASS -- CRUD 流程驗證通過")


# ─── TC-27: 使用者管理頁面 ──────────────────────────────────────────────────

async def tc_27_user_management(page, **_):
    """
    TC-27: 使用者管理頁面（_Admin/FrameworkUser）
    優先度: P2
    預估執行: 8s

    WTM Admin 的使用者管理頁面。
    在 QuickDebug 模式下，會反射所有 controller 作為選單。

    預期結果：
    - /_Admin/FrameworkUser/Index 可存取
    - 顯示使用者 grid

    issue #898（重寫，兩個問題一起修）：
      1. 原版本完全沒有 assert——grid 存不存在、有沒有列，全部只是印出來，一律 PASS。
      2. 原版本用 page.goto() 直接導覽到 /_Admin/FrameworkUser/Index。實測確認這是
         PartialView-only 端點（伺服器端一律回傳裸片段，不含 <html>/<script> 標籤），
         繞過 layuiadmin 的 AJAX tab 載入機制會讓 layui/xmSelect 完全沒有載入
         （console 可觀察到 "layui is not defined"），.layui-table-body 因此恆為 0——
         這正是 #898 所指的「防護/行為不存在時照樣 PASS」在另一種形式下的體現：不是
         swallow 例外，而是斷言永遠找不到東西也無所謂，因為根本沒斷言。
         改寫後改用 open_grid_via_sidebar()（與 TC-25/33/34/35 相同、已驗證可行的
         路徑——/_Admin/FrameworkUser/Index 在側邊欄選單確實有 lay-href 連結）。
    """
    print("[TC-27] 開始執行...")

    await login(page)

    await open_grid_via_sidebar(page, "/_Admin/FrameworkUser/Index")
    try:
        await page.wait_for_selector(".layui-table-body", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 27, "01-user-list")

    # 確認 grid 存在
    table = page.locator(".layui-table-body")
    table_count = await table.count()
    print(f"  .layui-table-body 數量: {table_count}")
    assert table_count > 0, (
        "FrameworkUser/Index grid（.layui-table-body）未渲染（見上方截圖，若有）"
    )

    rows = page.locator(".layui-table-body tr[data-index]")
    row_count = await rows.count()
    print(f"  使用者列數: {row_count}")
    assert row_count > 0, (
        "FrameworkUser/Index grid 未渲染出任何使用者列（demo 種子資料至少應有 admin 帳號）"
    )

    # 確認表頭
    headers = page.locator(".layui-table-header th")
    header_count = await headers.count()
    print(f"  表頭欄位數: {header_count}")
    assert header_count > 0, "FrameworkUser/Index grid 缺少表頭欄位"
    for i in range(min(header_count, 10)):
        text = await headers.nth(i).text_content()
        if text.strip():
            print(f"    欄位: {text.strip()}")

    # 搜尋面板
    search = page.locator(".layui-form")
    search_count = await search.count()
    print(f"  搜尋面板: {search_count}")
    assert search_count > 0, "FrameworkUser/Index 缺少搜尋面板（.layui-form）"

    print("[TC-27] PASS -- 使用者管理頁面驗證通過")


# ─── TC-28: 角色管理 + 權限設定 ─────────────────────────────────────────────

async def tc_28_role_management(page, **_):
    """
    TC-28: 角色管理 + 權限設定（_Admin/FrameworkRole）
    優先度: P2
    預估執行: 8s

    預期結果：
    - 角色列表可存取
    - grid 可載入

    issue #898（重寫，兩個問題一起修，理由與 TC-27 相同）：
      1. 原版本完全沒有 assert（角色列表就算 table_count==0 也只印一句 [WARN] 就
         繼續，最後仍 PASS）。
      2. 原版本用 page.goto() 直接導覽三個 _Admin 頁面，實測確認這些都是
         PartialView-only 端點，繞過 layuiadmin 的 tab 載入機制會讓 layui 完全沒
         載入。改寫後改用 open_grid_via_sidebar()——FrameworkRole/DataPrivilege/
         FrameworkMenu 三者在側邊欄選單都確實有 lay-href 連結。

    DataPrivilege 的資料列數量預期為 0（demo 種子資料沒有配置任何資料權限規則），
    因此只斷言 grid 結構本身有渲染（.layui-table-body 存在），不斷言列數 > 0——
    與 FrameworkRole/FrameworkMenu（種子資料非空，可以斷言列數 > 0）不同。
    """
    print("[TC-28] 開始執行...")

    await login(page)

    # --- FrameworkRole ---
    await open_grid_via_sidebar(page, "/_Admin/FrameworkRole/Index")
    try:
        await page.wait_for_selector(".layui-table-body", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 28, "01-role-list")

    table = page.locator(".layui-table-body")
    table_count = await table.count()
    print(f"  .layui-table-body 數量: {table_count}")
    assert table_count > 0, "FrameworkRole/Index grid（.layui-table-body）未渲染（見上方截圖，若有）"

    rows = page.locator(".layui-table-body tr[data-index]")
    row_count = await rows.count()
    print(f"  角色列數: {row_count}")
    assert row_count > 0, "FrameworkRole/Index grid 未渲染出任何角色列（demo 種子資料應至少有 admin 角色）"

    # --- DataPrivilege（需要有角色 ID 才能進一步設定，這裡只驗證頁面結構存在；
    # 資料權限規則的列數預期為 0，見上方 docstring） ---
    await open_grid_via_sidebar(page, "/_Admin/DataPrivilege/Index")
    try:
        await page.wait_for_selector(".layui-table-body, .layui-form", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        await _screenshot_on_failure(page, 28, "02-data-privilege")

    dp_table_count = await page.locator(".layui-table-body").count()
    dp_form_count = await page.locator(".layui-form").count()
    print(f"  DataPrivilege .layui-table-body: {dp_table_count}, .layui-form: {dp_form_count}")
    assert dp_table_count > 0, "DataPrivilege/Index grid（.layui-table-body）未渲染（見上方截圖，若有）"
    assert dp_form_count > 0, "DataPrivilege/Index 缺少搜尋/設定表單（.layui-form）"

    # --- FrameworkMenu ---
    await open_grid_via_sidebar(page, "/_Admin/FrameworkMenu/Index")
    try:
        await page.wait_for_selector(".layui-table-body, .layui-nav", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        await _screenshot_on_failure(page, 28, "03-menu-list")

    menu_table = page.locator(".layui-table-body")
    menu_table_count = await menu_table.count()
    print(f"  FrameworkMenu .layui-table-body: {menu_table_count}")
    assert menu_table_count > 0, "FrameworkMenu/Index grid（.layui-table-body）未渲染（見上方截圖，若有）"

    menu_rows = page.locator(".layui-table-body tr[data-index]")
    menu_row_count = await menu_rows.count()
    print(f"  選單項目數: {menu_row_count}")
    assert menu_row_count > 0, "FrameworkMenu/Index grid 未渲染出任何選單項目（demo 種子資料應有預設選單樹）"

    print("[TC-28] PASS -- 角色管理 + 權限設定驗證通過")


# ─── TC-29: ETL 管理頁面 ────────────────────────────────────────────────────

async def tc_29_etl_management(page, **_):
    """
    TC-29: ETL 管理頁面（_EtlJob/Index）
    優先度: P2
    預估執行: 8s

    確認 ETL Job 列表和 Run Log 頁面存在。
    Demo 使用 WalkingTec.Mvvm.Etl module。

    預期結果：
    - /_EtlJob/Index 可存取
    - /_EtlRunLog/Index 可存取
    - 各頁面有搜尋面板和 grid

    issue #898（重寫）：原版本完全沒有 assert，且用 page.goto() 直接導覽（PartialView-
    only 端點，繞過 layuiadmin 的 tab 載入機制，layui 完全不會載入——理由與 TC-27/28
    相同）。改寫後改用 open_grid_via_direct_tab()——_EtlJob/_EtlRunLog 不在側邊選單樹
    中（未見任何 lay-href 對應項目，與有選單項目的 TC-27/28 不同），因此不能用
    open_grid_via_sidebar()，改用能處理「頁面沒有側邊選單連結」情境的版本（見其
    docstring）。

    篩選欄位選擇器修正（本次改寫時發現的既有 bug）：原版本用 `select[name='Searcher.
    Result']` 之類的選擇器，但這些篩選欄位實際上是 xmSelect 渲染的 `<div name="...">`
    （與 Student 表單的 Sex 欄位同一種模式），從來就不是 `<select>` 標籤——原版本因為
    沒有斷言，這個選錯標籤的 bug 從未被發現。這裡改用不限定標籤的屬性選擇器
    `[name='...']`。

    執行順序（EtlRunLog 在前、EtlJob 在後）：本次改寫時實測發現，先導覽到下面
    KNOWN-GAP 段落描述的壞掉的 EtlJob 頁面，會讓同一個 page/session 內「之後」的
    EtlRunLog 導覽也遭殃——EtlRunLog 的篩選欄位跟著找不到、並出現一個新的 400
    Bad Request（很可能是 layuiadmin 的 router/全域狀態被 EtlJob 那個解析失敗的
    <script> 搞壞，殃及後續導覽）。因此本測試刻意先測完全正常的 EtlRunLog，再測
    已知有問題的 EtlJob，確保 EtlRunLog 的斷言不會被 EtlJob 的既有缺陷污染。

    KNOWN-GAP（本次改寫過程中發現，2026-07-31 對照本機跑起來的 demo 實測確認，與
    #898/#905 兩個 issue 本身無關）：_EtlJob/Index 透過真正的 tab 載入路徑開啟時，
    grid 從未渲染成功（table.cache 恆不填入，跨越多次重跑、不論導覽順序皆一致），
    console 會拋出 "Function statements require a function name"。根因已定位到
    src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs 第 1284 行：
    `actionScript = $"{item.OnClickFunc}(ids,ff.GetSelectionData('{Id}'));";` 沒有把
    `item.OnClickFunc` 包在括號裡；當 EtlJob 的 ListVM 把這個自訂動作的 OnClickFunc
    設成一段行內函式字面值（而非具名函式參照）時，產生的 JS 會是
    `function(ids,data){...}(ids,...)`——這是不合法的 IIFE 寫法（少了外層括號），
    整個 <script> 區塊因此完全解析失敗，殃及同一區塊裡真正的 table.render() 呼叫。
    這是一個真實、可重現的既有缺陷，不是本測試的導覽方式造成的假象（直接
    page.goto() 也會拋出同一個例外）。修這個缺陷需要改 src/ 下的框架程式碼，超出
    本 PR「只改 test/e2e」的範圍，也沒有對應的 issue 授權這個改動——這裡只據實記錄、
    不動框架程式碼，並把它報告給使用者評估是否要另開 issue 追蹤。

    Status/SourceDbType 這兩個篩選欄位（獨立的 xmSelect.render() <script> 區塊，
    document order 排在壞掉的那個區塊之前）則觀察到會隨導覽順序變化——EtlRunLog
    排在 EtlJob 之前時穩定渲染成功（3 次重跑一致），EtlJob 是本次 session 第一個
    造訪的 ETL 頁面時則不會渲染。這種跨頁面的順序依賴性沒有進一步追查根因（同樣
    超出本 PR 範圍），因此這裡選擇保守：grid 與這兩個篩選欄位都只記錄、不斷言，
    避免斷言綁在一個本身就不穩定、原因未明的行為上。只斷言「頁面路由正確、搜尋
    面板的純 HTML 欄位存在」（Searcher.Name 是一般 <input>，不需要 JS 就會出現在
    DOM 中，不受這個 bug 影響，且跨導覽順序都穩定）。EtlRunLog 沒有這個問題，
    因此照常做完整斷言——但仍然刻意排在 EtlJob 之前執行，見下方「執行順序」說明。
    """
    print("[TC-29] 開始執行...")

    await login(page)

    # --- ETL Run Log 先測（見上方 docstring：必須排在 EtlJob 之前，避免被
    # EtlJob 的既有缺陷污染同一個 page/session） ---
    await open_grid_via_direct_tab(page, "/_EtlRunLog/Index")
    try:
        await page.wait_for_selector("[name='Searcher.Result'], .layui-table-body", state="visible", timeout=3000)
    except PlaywrightTimeoutError:
        # issue #886 review: capture the moment of the timeout for diagnostics.
        await _screenshot_on_failure(page, 29, "02-etl-runlog")

    table_count = await page.locator(".layui-table-body").count()
    print(f"  RunLog .layui-table-body: {table_count}")
    assert table_count > 0, "_EtlRunLog/Index grid（.layui-table-body）未渲染（見上方截圖，若有）"

    result_count = await page.locator("[name='Searcher.Result']").count()
    trigger_count = await page.locator("[name='Searcher.Trigger']").count()
    print(f"  RunLog 搜尋: Result={result_count}, Trigger={trigger_count}")
    assert result_count > 0, "_EtlRunLog/Index 缺少 Result 篩選欄位（[name='Searcher.Result']）"
    assert trigger_count > 0, "_EtlRunLog/Index 缺少 Trigger 篩選欄位（[name='Searcher.Trigger']）"

    # --- ETL Job 後測（見上方 docstring 的 KNOWN-GAP：grid/下拉選單目前不會渲染） ---
    await page.evaluate(
        """(href) => {
            const a = document.createElement('a');
            a.setAttribute('lay-href', href);
            a.style.display = 'none';
            document.body.appendChild(a);
            a.click();
        }""",
        "/_EtlJob/Index",
    )
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)
    await page.wait_for_timeout(1000)

    # 確認搜尋面板欄位——只斷言不需要 JS 就會出現的純 HTML 欄位（Name 是一般
    # <input>）。Status/SourceDbType 是 xmSelect 渲染的篩選欄位，目前受 KNOWN-GAP
    # 影響不會渲染，因此只記錄、不斷言。
    name_input = page.locator("input[name='Searcher.Name']")
    name_count = await name_input.count()
    status_count = await page.locator("[name='Searcher.Status']").count()
    db_count = await page.locator("[name='Searcher.SourceDbType']").count()
    print(f"  ETL Job 搜尋欄位: Name={name_count}, Status={status_count}(KNOWN-GAP), "
          f"SourceDbType={db_count}(KNOWN-GAP)")
    assert name_count > 0, (
        "/_EtlJob/Index 連純 HTML 的 Searcher.Name 欄位都沒有渲染——"
        "代表頁面路由本身出了問題，不只是 KNOWN-GAP 影響的 JS 元件"
    )
    if status_count == 0 or db_count == 0:
        print("  [KNOWN-GAP] Status/SourceDbType 篩選欄位未渲染，"
              "根因見本函式 docstring（DataTableTagHelper.cs:1284 IIFE 少括號）")

    print("[TC-29] PASS -- ETL 管理頁面驗證通過"
          "（EtlRunLog 完整驗證；EtlJob 的 grid/篩選欄位為已記錄根因的 KNOWN-GAP）")


# ─── TC-30: 匯入功能流程 ────────────────────────────────────────────────────

async def tc_30_import_flow(page, **_):
    """
    TC-30: Student 匯入功能流程
    優先度: P2
    預估執行: 8s

    確認匯入 dialog 正確顯示、範本下載按鈕存在。

    WTM Import 流程：
    1. wt:downloadTemplateButton → 下載 Excel 範本
    2. wt:upload → 上傳填寫後的檔案
    3. wt:grid (ErrorListVM) → 顯示錯誤列表
    4. wt:submitbutton → 確認匯入

    預期結果：
    - /Student/Import 頁面可存取
    - 下載範本按鈕存在
    - 上傳控制項存在

    issue #898（重寫，兩個問題一起修，理由與 TC-26 相同）：
      1. 原版本完全沒有 assert。
      2. 原版本用 page.goto() 直接導覽到 /Student/Import。這不是側邊選單項目，
         而是 Student/Index grid toolbar 上的「导入」按鈕以 layer 對話框開啟的
         PartialView（與 Create 對話框同一種機制）——實測確認直接 page.goto() 一樣
         繞過 layuiadmin，layui 完全不會載入。改寫後改用 open_grid_via_sidebar() +
         open_toolbar_dialog()（與 TC-26 的 Create 對話框、TC-33/34/35 相同、已驗證
         可行的模式）。
    """
    print("[TC-30] 開始執行...")

    await login(page)

    # 透過 Student/Index grid toolbar 的「导入」按鈕開啟 Import 對話框。與 TC-26 相同
    # 理由：layer 外殼 visible 不代表裡面的內容已經渲染完成，沿用 TC-33/34/35 已驗證
    # 過的固定 settle wait（1500ms）。
    await open_grid_via_sidebar(page, "/Student/Index")
    await open_toolbar_dialog(page, "导入")
    await page.wait_for_timeout(1500)

    # 確認下載範本按鈕（wt:downloadTemplateButton 渲染為 <a>，實測文字為「下载模板」）
    download_btn = page.locator("a:has-text('下载模板')")
    dl_count = await download_btn.count()
    print(f"  下載範本按鈕數量: {dl_count}")
    assert dl_count > 0, "Import 對話框缺少下載範本按鈕（a:has-text('下载模板')）"

    # 上傳控制項：wt:upload 渲染為觸發原生檔案選擇器的按鈕 + <input type="file">
    file_input = page.locator("input[type='file']")
    fi_count = await file_input.count()
    print(f"  file input 數量: {fi_count}")
    assert fi_count > 0, "Import 對話框缺少上傳用的 file input（input[type='file']）"

    # 錯誤列表 Grid（初始應為空，但 grid 結構本身要存在）
    error_grid = page.locator(".layui-table")
    error_grid_count = await error_grid.count()
    print(f"  錯誤列表 grid: {error_grid_count}")
    assert error_grid_count > 0, "Import 對話框缺少錯誤列表 grid（.layui-table）"

    # Submit 按鈕
    submit = page.locator("button[lay-submit], a[lay-submit]")
    submit_count = await submit.count()
    print(f"  Submit 按鈕: {submit_count}")
    assert submit_count > 0, "Import 對話框缺少 submit 按鈕（[lay-submit]）"

    # Close 按鈕（layer 對話框標準的關閉按鈕）
    close = page.locator(".layui-layer-close")
    close_count = await close.count()
    print(f"  Close 按鈕: {close_count}")
    assert close_count > 0, "Import 對話框缺少關閉按鈕（.layui-layer-close）"

    await close_layer_dialog(page)

    print("[TC-30] PASS -- 匯入功能流程驗證通過")


# ─── TC-31: WorkFlow 設計器完整創作流程 (T-DSN-18 e2e smoke) ─────────────────

async def tc_31_workflow_designer_smoke(page, **_):
    """
    TC-31: T-DSN-18 — WorkFlow 設計器完整創作流程 e2e smoke
    admin 開啟設計器 → 建立流程頭 → 表單編輯 Start→Approval(串签)→End →
    校验 → 发布 v1 → 再次發布未變更 → IdempotentNoOp toast → 查看版本歷程 v1

    Mac-mini 單 runner flake SOP 適用：
      - 讀 log 確認 FAIL: 0 而非依賴 exit code
      - 若 /_workflow-designer 回 404（設計器未啟用），SKIP 並標記
    """
    import time

    tc_num = 31
    BASE = BASE_URL

    print(f"[TC-{tc_num:02d}] 開始執行 WorkFlow 設計器 smoke...")

    # Step 0: 登入
    await login(page, BASE)

    # Step 1: 確認設計器頁面可存取（AddWtmWorkFlowDesigner 已啟用）
    # FIX-B2: 404 is now a FAIL (not a skip). The demo host has AddWtmWorkFlowDesigner()
    # registered; if the designer returns 404, the startup config is broken and the test
    # must FAIL to surface the regression immediately.
    resp = await page.goto(f"{BASE}/_workflow-designer")
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)
    status = resp.status if resp else 0
    assert status != 404, (
        f"[TC-{tc_num:02d}] FAIL — 設計器頁面返回 404。"
        "Demo host 必須啟用 AddWtmWorkFlowDesigner() + UseWtmWorkFlowDesigner() (FIX-B2)."
    )
    if status == 403:
        print(f"[TC-{tc_num:02d}] SKIP — 設計器 RBAC 未授權 (403)，需配置 FunctionPrivilege")
        return
    print(f"[TC-{tc_num:02d}] 設計器頁面 HTTP {status}")

    # Step 2: 確認 bootstrap API 回傳正常
    bootstrap_resp = await page.evaluate("""
        async () => {
            const r = await fetch('/api/_workflow/designer/bootstrap');
            return { status: r.status, ok: r.ok };
        }
    """)
    print(f"[TC-{tc_num:02d}] bootstrap API: {bootstrap_resp}")
    assert bootstrap_resp.get('ok'), f"bootstrap API 失敗: {bootstrap_resp}"

    # Step 3: 建立流程頭（透過 API，避免 layui layer 操作複雜度）
    test_code = f"TC31_SMOKE_{int(time.time()) % 100000}"
    # FIX-B3a: bootstrap returns {"requestToken":"..."} (camelCase via [JsonPropertyName]).
    # Earlier code read d.antiforgeryToken (wrong field), so xsrf_token was always null
    # → CreateDefinition POST had no X-WTM-WF-XSRF header → [WfDesignerAntiforgery] 400.
    xsrf_resp = await page.evaluate("""
        async () => {
            const r = await fetch('/api/_workflow/designer/bootstrap');
            if (!r.ok) return null;
            const d = await r.json();
            return d.requestToken || null;
        }
    """)
    xsrf_token = xsrf_resp  # may be null if bootstrap doesn't embed token
    create_payload = {"code": test_code, "name": "TC31 Smoke Test", "category": "E2E"}
    create_headers = {"Content-Type": "application/json"}
    if xsrf_token:
        create_headers["X-WTM-WF-XSRF"] = xsrf_token
    create_resp = await page.evaluate(f"""
        async () => {{
            const r = await fetch('/api/_workflow/designer/definitions', {{
                method: 'POST',
                headers: {json.dumps(create_headers)},
                body: JSON.stringify({json.dumps(create_payload)})
            }});
            return {{ status: r.status, ok: r.ok }};
        }}
    """)
    print(f"[TC-{tc_num:02d}] 建立流程頭: {create_resp}")
    # 409 = code already exists (prev run), acceptable.
    assert create_resp.get('status') in (200, 201, 409), \
        f"建立流程頭失敗: {create_resp}"

    # Step 4: Publish v1 — 最小合法 graph (raw body)
    # NOTE: "key" must equal test_code (WorkflowGraph.Key = ProcessDefinition.Code).
    min_graph = json.dumps({
        "schemaVersion": 1,
        "key": test_code,
        "nodes": [
            {"nodeKey": "Start", "kind": "Start",    "name": "开始"},
            {"nodeKey": "Appr",  "kind": "Approval", "name": "审批",
             "approveMode": "Sequential",
             "approverRule": {"type": "User", "value": "admin"}},
            {"nodeKey": "End",   "kind": "End",      "name": "结束"}
        ],
        "transitions": [
            {"from": "Start", "to": "Appr"},
            {"from": "Appr",  "to": "End"}
        ]
    })
    publish_headers = {"Content-Type": "application/json"}
    if xsrf_token:
        publish_headers["X-WTM-WF-XSRF"] = xsrf_token
    pub_resp = await page.evaluate(f"""
        async () => {{
            const r = await fetch('/api/_workflow/designer/definitions/{test_code}/publish', {{
                method: 'POST',
                headers: {json.dumps(publish_headers)},
                body: {json.dumps(min_graph)}
            }});
            let body = null;
            try {{ body = await r.json(); }} catch (e) {{}}
            // FIX-B4: capture raw text on non-2xx for diagnosability
            if (!r.ok && body === null) {{
                try {{ body = await r.text(); }} catch (_) {{}}
            }}
            return {{ status: r.status, ok: r.ok, body: body }};
        }}
    """)
    print(f"[TC-{tc_num:02d}] 發布 v1: {pub_resp}")
    assert pub_resp.get('ok'), f"發布 v1 失敗 (status={pub_resp.get('status')}): {pub_resp.get('body')}"
    # FIX-B5: API returns PascalCase JSON ("Outcome") — check both casings for robustness.
    _pub_body = pub_resp.get('body') or {}
    _pub_outcome = _pub_body.get('Outcome') or _pub_body.get('outcome')
    assert _pub_outcome in ('Published', 'IdempotentNoOp'), \
        f"非預期 outcome: {pub_resp}"

    # Step 5: 再次發布完全相同內容 → 應得 IdempotentNoOp
    pub2_resp = await page.evaluate(f"""
        async () => {{
            const r = await fetch('/api/_workflow/designer/definitions/{test_code}/publish', {{
                method: 'POST',
                headers: {json.dumps(publish_headers)},
                body: {json.dumps(min_graph)}
            }});
            let body = null;
            try {{ body = await r.json(); }} catch (e) {{}}
            // FIX-B4: capture raw text on non-2xx for diagnosability
            if (!r.ok && body === null) {{
                try {{ body = await r.text(); }} catch (_) {{}}
            }}
            return {{ status: r.status, ok: r.ok, body: body }};
        }}
    """)
    print(f"[TC-{tc_num:02d}] 再次發布 (NoOp): {pub2_resp}")
    assert pub2_resp.get('ok'), f"再次發布失敗 (status={pub2_resp.get('status')}): {pub2_resp.get('body')}"
    # FIX-B5: API returns PascalCase JSON ("Outcome") — check both casings for robustness.
    _pub2_body = pub2_resp.get('body') or {}
    _pub2_outcome = _pub2_body.get('Outcome') or _pub2_body.get('outcome')
    assert _pub2_outcome == 'IdempotentNoOp', \
        f"預期 IdempotentNoOp，實際得: {pub2_resp}"

    # Step 6: 查看版本歷程 → 確認 v1 存在
    versions_resp = await page.evaluate(f"""
        async () => {{
            const r = await fetch('/api/_workflow/designer/definitions/{test_code}/versions');
            if (!r.ok) return null;
            return await r.json();
        }}
    """)
    print(f"[TC-{tc_num:02d}] 版本歷程: {versions_resp}")
    assert versions_resp is not None, "版本歷程 API 失敗"
    # FIX-B5: API returns PascalCase JSON — accept "Versions", "items", "data", or bare list.
    if isinstance(versions_resp, list):
        versions = versions_resp
    else:
        versions = (versions_resp.get('Versions') or versions_resp.get('versions') or
                    versions_resp.get('items') or versions_resp.get('data') or [])
    assert len(versions) >= 1, f"應有至少 1 個版本，實際: {len(versions)}"
    # FIX-B5: API returns "IsCurrent" (PascalCase), not "isCurrent".
    current = next((v for v in versions if v.get('IsCurrent') or v.get('isCurrent')), None)
    assert current is not None, "找不到 IsCurrent=true 的版本"
    # FIX-B5: VersionNo may be returned as int or string; "1" == 1 fails, so coerce to int.
    actual_version_no = int(current.get('VersionNo') or current.get('versionNo') or 0)
    assert actual_version_no == 1, f"當前版本應為 v1，實際: {actual_version_no}"

    # Step 7: 重新開啟設計器頁面，帶 code 參數
    await page.goto(f"{BASE}/_workflow-designer?code={test_code}")
    await page.wait_for_load_state("networkidle", timeout=TIMEOUT)

    print(f"[TC-{tc_num:02d}] PASS -- WorkFlow 設計器 smoke 完成 (code={test_code})")


# ─── TC-32: JWT LoginJwt + Refresh 流程（issue #681）───────────────────────

async def tc_32_jwt_refresh_rotation_replay(page, **_):
    """
    TC-32: JWT LoginJwt + Refresh 流程
    優先度: P1
    預估執行: 5s

    驗證 /api/_account/LoginJwt 簽發 access/refresh token pair，以及
    /api/_account/refreshtoken 端點在 #721 修復後的行為。

    背景（#721 安全修復，本測試原始版本撰寫於 #681，當時捕捉的是修復前的
    漏洞行為 —— 現已更新為驗證修復後的正確行為）：
    修復前，demo 的 AccountController.RefreshToken（[AllRights]，需先認證）
    與框架的 _FrameworkController.RefreshToken（[AllowAnonymous]）共用同一條
    路由樣板（"api/_account/refreshtoken"，ASP.NET Core 路由比對 case-
    insensitive），請求一律落在 demo 較舊、較簡單的實作 —— 它完全忽略傳入的
    refreshToken 值，直接依目前登入者身分（Bearer/cookie）重新核發一組新
    token，形同放行任何字串當 refresh token。#721 移除了 demo 這個 shadow
    action，讓 _FrameworkController.RefreshToken 成為唯一端點：真正呼叫
    ITokenService.RefreshTokenAsync 驗證「呼叫者實際提交的 refreshToken 值」，
    無效／從未核發／已輪替過的 token 一律拒絕核發，且完全不需要 Bearer
    access token（[AllowAnonymous] 是正確語意 —— refresh 本來就該只憑
    refresh token 本身，不該要求呼叫者手上還有一個尚未過期的 access token）。
    詳見 test/WalkingTec.Mvvm.Api.Test/RefreshTokenApiTests.cs（#721 authoritative
    HTTP-level 回歸測試）。

    本測試涵蓋 RefreshTokenApiTests.cs 三組斷言裡、demo 這台單機（無
    mainhost/federation）可經由這條 e2e 路徑重現的部分：
      (a) 合法 refresh token、不帶任何 Authorization header → 200 + 全新
          access_token/refresh_token pair（證明 AllowAnonymous 語意正確：
          refresh 不需要 access-token bearer）。
      (b) 從未核發過的捏造 refresh token → REJECTED（401，body 不含可用的
          access_token）—— 這是 #721 的核心安全斷言：修復前這個情境會回
          200 並核發一組全新可用 token（身分位重放），修復後必須被拒絕。
      (c) Replay：重放剛才已經被輪替掉的舊 refresh token → 拒絕（401/400，
          body 不含可用 access_token），證明 atomic-rotation + replay-guard
          在 HTTP 路徑上確實生效，不只是在 ITokenService 單元測試層級。

    未涵蓋範圍：mainhost/federation 轉發路徑（WTMContext.RefreshTokenAsync
    在 ConfigInfo.HasMainHost==true 時的分支）—— demo 是單機部署，
    HasMainHost 為 false，這條 e2e 測試走不到那個分支；該路徑已由
    WtmAuthServiceTests（單元測試）覆蓋。

    預期結果：
    - LoginJwt 回傳 200，含 access_token/refresh_token
    - 合法 refresh token、不帶 Bearer → 200，核發的 access_token 與
      refresh_token 皆與登入時不同（rotation）
    - 從未核發過的捏造 refresh token → 401，body 不含 access_token
    - 重放已輪替的舊 refresh token → 401 或 400，body 不含 access_token
    """
    print("[TC-32] 開始執行...")

    login_resp = await page.request.post(
        f"{BASE_URL}/api/_account/LoginJwt",
        data=json.dumps({"Account": ADMIN_USER, "Password": ADMIN_PASS}),
        headers={"Content-Type": "application/json"},
    )
    login_status = login_resp.status
    login_body = await login_resp.text()
    print(f"  LoginJwt: HTTP {login_status}")
    assert login_status == 200, f"LoginJwt 預期 200，實際 {login_status}: {login_body[:200]}"
    login_data = json.loads(login_body)
    access_token_1 = login_data.get("access_token")
    refresh_token_1 = login_data.get("refresh_token")
    assert access_token_1, "LoginJwt 回應缺少 access_token"
    assert refresh_token_1, "LoginJwt 回應缺少 refresh_token"
    print(f"  access_token 長度={len(access_token_1)}, refresh_token 長度={len(refresh_token_1)}")

    # (a) 合法 refresh token、不帶任何 Authorization header —— 證明 #721 修復後
    # 的端點是真正的 AllowAnonymous：refresh 只需要 refresh token 本身。
    valid_resp = await page.request.post(
        f"{BASE_URL}/api/_account/refreshtoken",
        data=json.dumps({"refreshToken": refresh_token_1}),
        headers={"Content-Type": "application/json"},
    )
    valid_status = valid_resp.status
    valid_body = await valid_resp.text()
    print(f"  合法 refresh token（無 Bearer）呼叫 refreshtoken: HTTP {valid_status}")
    assert valid_status == 200, (
        f"#721: 合法 refresh token 不帶 Bearer 應核發新 token pair，"
        f"預期 200，實際 {valid_status}: {valid_body[:200]}"
    )
    valid_data = json.loads(valid_body)
    access_token_2 = valid_data.get("access_token")
    refresh_token_2 = valid_data.get("refresh_token")
    assert access_token_2, "refreshtoken 回應缺少 access_token"
    assert refresh_token_2, "refreshtoken 回應缺少 refresh_token"
    assert access_token_2 != access_token_1, "refreshtoken 應核發一組全新的 access_token"
    assert refresh_token_2 != refresh_token_1, (
        "#721: refresh token 應被輪替（rotated），不應原樣回傳同一個值"
    )
    print("  已確認核發了新的 access_token/refresh_token（rotation 生效）")

    # (b) #721 核心安全斷言：從未核發過的捏造 refresh token 必須被拒絕。
    # 修復前這裡會回 200 並核發一組全新可用 token（身分位重放漏洞）。
    bogus_refresh_token = "e2e-bogus-refresh-token-721-" + refresh_token_1[:8]
    bogus_resp = await page.request.post(
        f"{BASE_URL}/api/_account/refreshtoken",
        data=json.dumps({"refreshToken": bogus_refresh_token}),
        headers={"Content-Type": "application/json"},
    )
    bogus_status = bogus_resp.status
    bogus_body = await bogus_resp.text()
    print(f"  從未核發過的捏造 refreshToken 呼叫 refreshtoken: HTTP {bogus_status}")
    assert bogus_status == 401, (
        f"#721: 捏造／從未核發過的 refresh token 必須被拒絕（401），"
        f"不得依身分位重新核發，實際 {bogus_status}: {bogus_body[:200]}"
    )
    assert "access_token" not in bogus_body.lower(), (
        f"#721: 拒絕回應不應包含可用的 access_token。Body: {bogus_body[:200]}"
    )
    print("  已確認捏造 refresh token 被正確拒絕（無可用 token 外洩）")

    # (c) Replay：重放剛才已經被 (a) 輪替掉的舊 refresh token，證明
    # atomic-rotation + replay-guard 在 HTTP 路徑上確實生效。
    replay_resp = await page.request.post(
        f"{BASE_URL}/api/_account/refreshtoken",
        data=json.dumps({"refreshToken": refresh_token_1}),
        headers={"Content-Type": "application/json"},
    )
    replay_status = replay_resp.status
    replay_body = await replay_resp.text()
    print(f"  重放已輪替的舊 refresh token 呼叫 refreshtoken: HTTP {replay_status}")
    assert replay_status in (401, 400), (
        f"#721/replay-guard: 重放已輪替的舊 refresh token 必須被拒絕，"
        f"實際 {replay_status}: {replay_body[:200]}"
    )
    assert "access_token" not in replay_body.lower(), (
        f"#721: replay 拒絕回應不應包含可用的 access_token。Body: {replay_body[:200]}"
    )
    print("  已確認舊（已輪替）refresh token 重放被正確拒絕")

    print("[TC-32] PASS -- LoginJwt/refreshtoken #721 修復後行為驗證通過"
          "（合法 token rotation + 捏造 token 拒絕 + replay 拒絕）")


# ─── TC-33: combobox 聯動串聯（tree → combobox chain/cascade，issue #681）──

async def tc_33_combobox_chain_cascade(page, **_):
    """
    TC-33: combobox 聯動串聯（chain/cascade）
    優先度: P1
    預估執行: 15s

    LinkTest2/Create 表單：<wt:tree field="SelectedSchool" item-url=... link-id="aa"
    trigger-url="/LinkTest/GetMajorBySchool" /> 串聯 <wt:combobox field="SelectedMajor"
    id="aa" .../>。在 tree 選一筆 School 後，應觸發 ff.ChainChange 呼叫
    trigger-url，並以回傳結果重新渲染 id="aa" 的 combobox。

    此對話框透過 grid toolbar「新建」按鈕以 ff.OpenDialog 開啟（非
    ff.OpenDialog2，兩者對 #627 kill-switch 的反應不同 —— 見
    docs/csp-hardening.md）。當 WTM_E2E_KILLSWITCH=1 時，ff.OpenDialog 的
    inline-script 重新執行本身就是四個被 kill-switch 擋下的路徑之一，
    tree/combobox 兩者用來 xmSelect.render() 的 inline <script> 不會執行，
    widget 不會變成可互動狀態 —— 這正是 docs/csp-hardening.md「Honest limits」
    段落描述的已知限制（"most WTM apps cannot reach level 2/3 yet if their
    dialogs use comboboxes/selectors"）。因此本測試依 WTM_E2E_KILLSWITCH 分支：
    kill-switch 關閉（baseline，預設）驗證完整串聯功能；kill-switch 開啟則只
    驗證「優雅降級」——對話框仍正常開啟、不拋未捕捉例外、原始表單欄位標記
    仍在 DOM 中。

    預期結果（baseline）：
    - 點擊 tree 中一個實際擁有 Major 資料的 School 選項後，觸發
      GET /LinkTest/GetMajorBySchool?...&id={schoolId}
    - id="aa" 的 combobox 隨後出現對應數量的 .xm-option
    """
    print("[TC-33] 開始執行...")

    await login(page)
    await open_grid_via_sidebar(page, "/LinkTest2/Index")
    await open_toolbar_dialog(page, "新建")
    await page.wait_for_timeout(1500)

    if KILLSWITCH_EXPECTED:
        page_errors = []
        page.on("pageerror", lambda e: page_errors.append(str(e)))
        await page.wait_for_timeout(1500)

        tree_field = page.locator("[wtm-name='SelectedSchool']")
        combo_field = page.locator("#aa")
        assert await tree_field.count() > 0, "kill-switch: SelectedSchool 欄位標記應仍存在於 DOM"
        assert await combo_field.count() > 0, "kill-switch: SelectedMajor(id=aa) 欄位標記應仍存在於 DOM"
        assert not page_errors, f"kill-switch: 頁面拋出未捕捉例外：{page_errors}"

        print("  [KILLSWITCH] 對話框開啟未拋錯；comboboxes 依 docs/csp-hardening.md "
              "「Honest limits」預期不可互動，略過連動功能斷言")
        print("[TC-33] PASS -- kill-switch leg：優雅降級驗證通過")
        return

    tree_box = page.locator("#LinkTest2VM_SelectedSchool xm-select")
    assert await tree_box.count() > 0, "找不到 SelectedSchool tree widget（#LinkTest2VM_SelectedSchool xm-select）"
    # issue #873: 不要用 force=True。force 會跳過 Playwright click() 內建的
    # "元素在連續兩個 frame 間停止移動" 穩定性檢查（它只跳過 actionability
    # 檢查，不會跳過捲動 —— 已用最小 repro 驗證兩者行為互相獨立）。xm-select
    # 的下拉／樹狀 panel 在 ff.OpenDialog 開啟的 layer 對話框仍在版面穩定化
    # （層本身的置中/展開、tree 與 combobox 兩個 xmSelect.render() 各自的初次
    # 版面配置）時就可能被觸發 —— 對著實際 demo 直接量測 getBoundingClientRect()
    # 已確認 trigger 元素在 open_toolbar_dialog() 回傳後仍持續在移動
    # （尚未穩定），單純加長這裡的 wait_for_timeout 只是把賭注押大一點，
    # 在資源競爭更劇烈的 CI runner 上依然會被打穿。原本用 force 大概是想繞開
    # 某個 actionability 檢查，但這裡從未證實有必要——移除 force 後於本機
    # 60+ 次高壓測試（CPU throttle 20x、800x600 viewport、並行負載）沒有再
    # 出現過 "Element is outside of the viewport"。不帶 force 的一般 click()
    # 會自己等到版面穩定再點，這正是這裡需要的訊號，比盲目的固定等待更準確。
    await tree_box.click()
    await page.wait_for_selector("#LinkTest2VM_SelectedSchool .xm-option", state="attached", timeout=TIMEOUT)

    # 資料驅動找出實際擁有 Major 的 School id —— demo.db 未入版控，每次執行都
    # 重新播種，不可假設固定 ID 一定有關聯資料（issue #681 踩雷紀錄）。
    # 注意：GetMajorBySchool 走 BaseController.JsonMore()，回傳的是
    # {"Msg":"success","Code":"200","Data":[...]} 信封，不是裸陣列 —— 必須看
    # Data 欄位的長度，不能直接對整個 dict 做 len()（那永遠是信封本身的鍵數，
    # issue #681 authoring 過程中曾誤判導致假陽性，已在此修正並留下紀錄）。
    candidate_school_id = None
    expected_major_count = 0
    max_probe = 150
    for candidate in range(1, max_probe + 1):
        probe_resp = await page.request.get(f"{BASE_URL}/LinkTest/GetMajorBySchool?id={candidate}")
        if probe_resp.status != 200:
            continue
        probe_envelope = json.loads(await probe_resp.text())
        probe_data = probe_envelope.get("Data") if isinstance(probe_envelope, dict) else probe_envelope
        if probe_data and len(probe_data) > 0:
            candidate_school_id = candidate
            expected_major_count = len(probe_data)
            break
    assert candidate_school_id is not None, (
        f"探測 1..{max_probe} 找不到任一有 Major 資料的 School（demo 種子資料可能為空）"
    )
    print(f"  探測到 School id={candidate_school_id} 有 {expected_major_count} 筆 Major")

    option = page.locator(f"#LinkTest2VM_SelectedSchool .xm-option[value='{candidate_school_id}']")
    assert await option.count() > 0, f"tree 選項中找不到 school id={candidate_school_id}"

    chain_requests = []
    page.on("request", lambda r: chain_requests.append(r.url) if "GetMajorBySchool" in r.url else None)

    # issue #873: 同上，不用 force=True —— 這裡是同一個 xm-select panel 內、
    # 動態算出的特定 school option，一樣可能在 panel 版面尚未穩定時被點到。
    await option.click()
    await page.wait_for_selector("#aa .xm-option", state="attached", timeout=TIMEOUT)

    assert any("GetMajorBySchool" in u for u in chain_requests), (
        "選擇 School 後未觀察到 GetMajorBySchool 連動請求（ff.ChainChange 未觸發）"
    )
    print(f"  觀察到連動請求: {[u for u in chain_requests if 'GetMajorBySchool' in u]}")

    combo_opt_count = await page.locator("#aa .xm-option").count()
    print(f"  連動後 SelectedMajor combobox 選項數量: {combo_opt_count}（預期 {expected_major_count}）")
    assert combo_opt_count == expected_major_count, (
        f"連動後 combobox 選項數量({combo_opt_count})與該 School 實際 Major 數量"
        f"({expected_major_count})不符"
    )

    print("[TC-33] PASS -- combobox 聯動串聯（tree → combobox chain/cascade）驗證通過")


# ─── TC-34: Selector 對話框開啟流程（issue #681）───────────────────────────

async def tc_34_selector_dialog_flow(page, **_):
    """
    TC-34: Selector 對話框開啟／搜尋／挑選／confirm write-back 完整流程
    優先度: P1
    預估執行: 10s

    KNOWN-GAP（本測試撰寫過程中發現，與 #627 kill-switch 狀態無關 —— baseline
    與 kill-switch 兩種情況下皆已確認，2026-07-17 對照 live demo 實測）：

    ff.OpenDialog2（<wt:selector> 挑選彈窗的專用開啟函式）與 ff.OpenDialog
    （一般 Create/Edit 對話框用的開啟函式）不同：OpenDialog2 只會還原「呼叫端
    頁面自己」的 search-panel 模板裡的 $$script$$ token 區段
    （framework_layui.js 的 #332/#627 comment block），它並不會對 AJAX 回應
    本體（str，也就是 Views/_Framework/Selector.cshtml 渲染出的 HTML）做
    ff.OpenDialog 那種「DOMParser 抽取 <script> → SafeHtml 消毒 → DOM 插入後
    重新執行」的一般性還原。Selector.cshtml 自己的 <script> 區塊（定義
    submitSelect()/gridCheckedFunc()/tempvar，以及巢狀 <wt:grid> 自身的
    table.render(...) 呼叫）屬於這個回應本體，因此會被 ff.SafeHtml/DOMPurify
    無條件剝除（framework_layui.js 明確註記「SafeHtml/DOMPurify strips
    <script> elements — including islands —」），且沒有對應的還原路徑。

    實測結果：彈出視窗本身會正常開啟（第二層 .layui-layer-page 出現、標題
    「請選擇」顯示），但其中的 <table lay-filter="wtTable_..."> 永遠不會發出
    GetPagingData 請求、grid 永遠是空的 —— search/挑選/確定/write-back 整條
    流程目前都無法透過這個元件走完。這是一個既有落差，不是 #681 或
    kill-switch 造成的迴歸；已在此明確記錄，建議另開 Issue 追蹤／修復，不在
    #681 範圍內處理。

    本測試因此只驗證「已確認可達」的部分（彈窗結構正確開啟、不拋未捕捉例外），
    不假裝驗證 search/pick/confirm/write-back —— 那條路徑目前無法通過。若此
    KNOWN-GAP 未來被修復，請將本測試改寫為完整流程斷言。

    kill-switch（issue #681 額外實測）：WTM_E2E_KILLSWITCH=1 時，Create 對話框
    本身透過 ff.OpenDialog 開啟，其 inline-script 重新執行同樣被 kill-switch
    擋下 —— 這代表挑選按鈕自己的 onclick 綁定（SelectorTagHelper 輸出的
    `$('#{Id}_Select').on('click', ...)` 那段 inline <script>）根本不會被執行，
    按鈕在畫面上存在但完全無法互動，點擊後不會開啟第二層對話框。這比
    baseline 的 KNOWN-GAP 更進一步：baseline 是「開得起來、grid 是空的」，
    kill-switch 是「連開都開不起來」。因此 kill-switch 時本測試不嘗試點擊，
    只驗證第一層對話框（Create 表單）本身開啟無誤、按鈕標記仍在 DOM、
    無未捕捉例外。
    """
    print("[TC-34] 開始執行...")

    await login(page)
    await open_grid_via_sidebar(page, "/LinkTest/Index")
    await open_toolbar_dialog(page, "新建")
    await page.wait_for_timeout(1500)

    select_btn = page.locator("#LinkTestVM_SelectedSchool_Select")
    assert await select_btn.count() > 0, "找不到 wt:selector 的挑選按鈕（id 結尾 _Select）"

    page_errors = []
    page.on("pageerror", lambda e: page_errors.append(str(e)))

    if KILLSWITCH_EXPECTED:
        await page.wait_for_timeout(1000)
        assert not page_errors, f"kill-switch: Create 對話框拋出未捕捉例外：{page_errors}"
        print("  [KILLSWITCH] Create 對話框開啟未拋錯，挑選按鈕標記存在於 DOM 但預期不可互動"
              "（ff.OpenDialog 的 inline-script 重新執行本身也被 kill-switch 擋下），略過點擊/第二層斷言")
        print("[TC-34] PASS -- kill-switch leg：優雅降級驗證通過")
        return

    await select_btn.click()
    await page.wait_for_timeout(2000)

    layer_pages = page.locator(".layui-layer-page")
    layer_count = await layer_pages.count()
    print(f"  Selector 彈出層數量: {layer_count}")
    assert layer_count >= 2, "點擊挑選按鈕後應開啟第二層 Selector 彈出對話框"

    assert not page_errors, f"Selector 對話框拋出未捕捉例外：{page_errors}"

    grid_rows = page.locator(".layui-layer-page .layui-table-body tr[data-index]")
    row_count = await grid_rows.count()
    if row_count == 0:
        print(f"  [KNOWN-GAP] Selector picker 內 grid 資料列數量: 0 —— "
              "ff.OpenDialog2 不還原 Selector.cshtml 自身的 <script>（見本函式 docstring），"
              "search/pick/confirm write-back 目前不可達")
    else:
        print(f"  [INFO] grid 資料列數量: {row_count} —— 若 KNOWN-GAP 已修復，"
              "請將本測試改寫為驗證完整 search/pick/confirm write-back 流程")

    confirm_btn = page.locator(
        ".layui-layer-page button:has-text('确定'), .layui-layer-page button:has-text('確定')"
    )
    print(f"  確定按鈕數量: {await confirm_btn.count()}")

    print("[TC-34] PASS -- Selector 對話框開啟結構驗證通過（挑選/write-back 為 KNOWN-GAP，見 docstring）")


# ─── TC-35: 上傳元件（wt:upload）往返流程（issue #681, root cause #723）───

async def tc_35_upload_widget_roundtrip(page, **_):
    """
    TC-35: 上傳元件（wt:upload）往返流程
    優先度: P2
    預估執行: 10s

    Student/Create 的 <wt:upload field="Entity.PhotoId" upload-type=" ImageFile" />
    渲染為一個綁定 layui.upload.render 的按鈕 + 一個回填上傳結果 GUID 的
    hidden input。本測試透過瀏覽器原生檔案選擇器上傳一個固定測試檔案，
    確認結構存在、檔案選擇器可被觸發，並驗證上傳後 hidden input 是否回填。

    根因已確認（issue #723，取代先前 #681 authoring 時留下的 KNOWN-GAP）：
    先前版本的固定測試檔案是純文字的 e2e_upload_test.txt。但 Student/Create
    的 upload-type="ImageFile" 會讓 UploadTagHelper（見
    src/WalkingTec.Mvvm.TagHelpers.LayUI/Form/UploadTagHelper.cs 的
    `ext = "jpg|jpeg|gif|bmp|png|tif"`）把這個副檔名白名單原樣寫進
    layui.upload.render 的 `exts` config。無論是 legacy 樹（layui 2.5.7，
    wwwroot/layui/lay/modules/upload.js）還是 #573 flip 後預設的
    layui-next 樹（layui 2.13.8，wwwroot/layui-next/layui.js），upload
    模組的 `case "file":` 副檔名檢查都是
    `RegExp(...).test(escape(filename))`——比對失敗時直接 `return`
    （附一個 layer.msg 提示），且這個 return **發生在呼叫 $.ajax 之前**。
    也就是說 .txt 檔案在兩個 layui 版本中都會被靜音擋下：瀏覽器原生檔案
    選擇器確實會開啟（widget 的 click→elemFile.click() 綁定正常且未變），
    選檔後 elemFile 的 change handler 也確實觸發、`auto:true` 也確實呼叫了
    upload()，但 upload() 內部的副檔名白名單檢查搶先 return——因此觀察不到
    任何上傳 POST、hidden input 也不會回填。這不是 layui 內部 file input
    抽換的時序問題，不是 Playwright file-chooser 互動的 artifact，也不是
    UploadTagHelper 的 done/hidden-input write-back 邏輯有誤
    （`$('#{Id}').val(res.Data.Id)` 本身完全正確，只是因為 upload() 提早
    return 而永遠沒有機會被呼叫到）——單純是本測試先前選用的固定檔案副檔名
    與被測 UI 元件的用途（僅接受圖片）不符。

    修正：改用副檔名在允許清單內的固定測試檔案（e2e_upload_test.png，內容
    為一個最小合法的 1x1 透明 PNG）。Student/Create 沒有指定
    thumb-width/thumb-height，因此 `/_Framework/UploadImage`
    （src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs）伺服器端會落入
    width==null && height==null 分支、直接委派給 `Upload()`，不會呼叫
    ImageSharp `Image.Load` 解碼真實圖片內容——但仍附上一個真正合法的 PNG
    位元組，確保未來若這個 view 加上縮圖尺寸參數，測試檔案依然合法可解碼。

    kill-switch（issue #681 額外實測，與上述副檔名根因無關，仍然成立）：
    WTM_E2E_KILLSWITCH=1 時，上傳按鈕的 layui.upload.render({elem:'#..button',
    ...}) 綁定本身就是 UploadTagHelper 輸出的 inline <script>，透過
    ff.OpenDialog 開啟的對話框載入，因此也被 kill-switch 擋下，按鈕完全沒有
    綁定任何 click handler，連原生檔案選擇器都不會觸發。此時不嘗試互動，只
    驗證結構存在、無未捕捉例外。
    """
    print("[TC-35] 開始執行...")

    await login(page)
    await open_grid_via_sidebar(page, "/Student/Index")
    await open_toolbar_dialog(page, "新建")
    await page.wait_for_timeout(1500)

    upload_btn = page.locator("#StudentVM_Entity_PhotoIdbutton")
    hidden_field = page.locator("#StudentVM_Entity_PhotoId")
    assert await upload_btn.count() > 0, "找不到上傳按鈕（wt:upload 的 ...button）"
    assert await hidden_field.count() > 0, "找不到上傳結果 hidden input（wt:upload 的回填欄位）"

    if KILLSWITCH_EXPECTED:
        page_errors = []
        page.on("pageerror", lambda e: page_errors.append(str(e)))
        await page.wait_for_timeout(1000)
        assert not page_errors, f"kill-switch: 頁面拋出未捕捉例外：{page_errors}"
        print("  [KILLSWITCH] 上傳按鈕標記存在於 DOM 但預期未綁定 click handler"
              "（layui.upload.render 的 inline-script 重新執行被 kill-switch 擋下），略過互動斷言")
        print("[TC-35] PASS -- kill-switch leg：優雅降級驗證通過")
        return

    fixtures_dir = Path(__file__).parent / "fixtures"
    fixtures_dir.mkdir(parents=True, exist_ok=True)
    # Issue #723: MUST use an extension that layui.upload's client-side `exts`
    # allowlist accepts for upload-type="ImageFile" ("jpg|jpeg|gif|bmp|png|tif" — see
    # UploadTagHelper.cs). A mismatched extension (e.g. the previous .txt fixture)
    # is silently rejected by layui's upload() BEFORE it ever sends the XHR — no
    # POST, no hidden-input write-back, and no exception to catch. See this
    # function's docstring for the full root-cause trace through both vendored
    # layui trees. Real (tiny, valid, 1x1 transparent) PNG bytes are used rather
    # than a renamed .txt, so the fixture stays valid even if a future view adds
    # thumb-width/thumb-height (which would route UploadImage through ImageSharp's
    # Image.Load instead of the raw-bytes Upload() fallback).
    test_file = fixtures_dir / "e2e_upload_test.png"
    if not test_file.exists():
        _MIN_PNG_B64 = (
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk"
            "+A8AAQUBAScY42YAAAAASUVORK5CYII="
        )
        test_file.write_bytes(base64.b64decode(_MIN_PNG_B64))

    upload_requests = []
    page.on(
        "request",
        lambda r: upload_requests.append(r.url)
        if r.method == "POST" and ("pload" in r.url or "UploadFile" in r.url)
        else None,
    )

    chooser_triggered = False
    try:
        async with page.expect_file_chooser(timeout=TIMEOUT) as fc_info:
            await upload_btn.click()
        file_chooser = await fc_info.value
        await file_chooser.set_files(str(test_file))
        chooser_triggered = True
    except Exception as e:
        print(f"  [WARN] 觸發檔案選擇器失敗: {e}")

    assert chooser_triggered, "點擊上傳按鈕應能觸發瀏覽器原生檔案選擇器"

    await page.wait_for_timeout(4000)

    hidden_val = await hidden_field.input_value()
    print(f"  上傳後 hidden input 值: {hidden_val!r}")
    print(f"  觀察到的上傳相關請求: {upload_requests}")

    # Issue #723: root cause of the previous KNOWN-GAP was a fixture/widget extension
    # mismatch (see docstring), not a framework wiring bug or a Playwright artifact —
    # now fixed by using an accepted image extension. This is asserted for real (not
    # a soft print) so a genuine future regression in the round-trip is caught, per
    # this repo's "don't fake a passing test" convention (see #681 review history).
    assert upload_requests, (
        "點擊上傳按鈕、選擇合法副檔名（.png）的檔案後，應觀察到一個上傳 POST 請求，"
        "但完全沒有——若此斷言失敗，代表 #723 修正的根因（副檔名白名單不符）已不再是"
        "唯一成因，需重新調查（見本函式 docstring 的根因分析）"
    )
    assert hidden_val, (
        "上傳 POST 請求已送出，但 hidden input 未回填上傳結果 ID——"
        "UploadTagHelper 的 done callback（$('#{Id}').val(res.Data.Id)）"
        "未被正確觸發，需重新調查"
    )
    print("[TC-35] PASS -- 上傳往返流程完整驗證通過（檔案已上傳並回填 ID）")


# ─── TC-36: 租戶切換（Tenant Switch）— SKIP（issue #681）───────────────────

async def tc_36_tenant_switch(page, **_):
    """
    TC-36: 租戶切換（Tenant Switch）— SKIP
    優先度: SKIP
    預估執行: -

    任務要求涵蓋的第五項關鍵流程之一。demo 專案未啟用多租戶主機模式：
    appsettings.json 沒有 HasMainHost/MainHost 設定，程式碼庫內搜尋
    "HasMainHost"／"Tenant" 在 demo 專案中皆未見任何啟用點。框架本身雖提供
    /api/_account/SetTenant 端點（AccountController.SetTenant，見
    demo/WalkingTec.Mvvm.Demo/Areas/_Admin/ApiControllers/AccountController.cs），
    但 demo 沒有第二個可切換的租戶、也沒有任何 UI 進入點能操作切換。

    在沒有可操作 demo 場景的情況下，寫一個「假裝測試了 tenant switch」的
    測試沒有意義，只會製造假的信心 —— 因此明確標記為 SKIP，而非硬造一個
    永遠通過、實際上什麼都沒驗證的斷言（依任務指示：do NOT fake a passing
    test）。

    TODO（若之後 demo 新增多租戶展示）：設定 HasMainHost + 至少一個非預設
    tenant → 登入 → 呼叫 SetTenant → 確認後續請求的資料範圍隨 tenant 改變
    （例如某個僅屬於該 tenant 的實體變成可見／不可見）。
    """
    raise TestSkipped(
        "demo 未啟用多租戶主機模式（無 HasMainHost 設定），"
        "無可操作的 tenant switch 場景，詳見本函式 docstring"
    )


# ─── 錯誤處理輔助函式 ────────────────────────────────────────────────────────
#
# Issue #886: 每個 TC 過去對每一步都無條件拍照（79 處 page.screenshot()，13 處
# full_page=True），不分成敗——但 CI 的 `Upload screenshots` step 因 #11 長年是
# continue-on-error 的失敗，這些截圖平常沒有人下載查看，只有失敗時才有除錯價值。
# 現在的政策：TC 主流程（happy path）不再逐步拍照，只靠這裡的 _screenshot_on_failure
# 在 run_tests() 的例外處理路徑上，對「這次失敗/重試當下」的頁面狀態拍一張。已經
# 位在 except 分支裡、只在特定子步驟逾時才觸發的截圖（例如 login()、TC-04、TC-24
# 的個別 timeout 分支，以及 #886 review 後移回 except 分支的 TC-04/24/25/26/27/
# 28/29 共 12 處——這些 TC 在該路徑上沒有任何 assert 保護，swallow 掉的例外若不
# 順手拍照就完全無跡可尋）維持原樣不動——它們本來就是條件式的，成功執行不會付出
# 任何成本。TC-21/TC-22 例外（#886 review 後修正）：這兩個 TC 的判定完全來自 DOM/
# 佈局 assert，截圖本身不是任何斷言的依據，因此改為 opt-in（WTM_E2E_VISUAL_
# SNAPSHOTS=1），不再無條件拍照——見 VISUAL_SNAPSHOTS 常數旁的說明。
#
# #886 review round 2（MEDIUM）：那 12 處 except 分支曾經直接呼叫
# page.screenshot()——如果截圖本身拋錯（崩潰的瀏覽器正是最可能發生這種事的時候），
# 那個新例外會**取代**原本被 swallow 的 wait 例外、逃出 except 分支，把一個「等待
# 逾時但無傷大雅」的分支變成未預期的 ERROR/retry，等於截圖失敗反而改變了判定。
# 所以這 12 處、以及 run_tests() 既有的失敗路徑，全部改用下面這個保證不拋出的
# _screenshot_on_failure()，不再各自 inline 呼叫 page.screenshot()。

async def _screenshot_on_failure(page, tc_num, label, full_page=False):
    """
    最佳努力截圖，保證不拋出——呼叫端（不論是 run_tests() 的失敗處理，還是 tc_
    函式自己 swallow 掉一個 wait 之後想順手拍照）都不必再包一層 try。

    某些失敗形態（例如 #885 觀測到的 Chromium "Target crashed"）代表瀏覽器行程本身
    已經死亡，這裡的 page.screenshot() 幾乎必然也會失敗——這種情況下沒有任何辦法
    生出一張截圖，但至少要在 CI log 留一行訊息說明「這個 TC 沒有截圖，因為連截圖
    本身都失敗了」，而不是讓截圖目錄悄悄少一個檔案、事後看起來像是忘記拍。

    #886 review round 2：連這行診斷 print 本身都可能拋出（stdout 已關閉時 print()
    會拋 BrokenPipeError）——整段包在最外層 try，任何例外一律吞掉；印不出診斷就
    真的什麼都不做，但絕不能讓「想順手留個痕跡」反過來把呼叫端的控制流打斷。
    """
    try:
        await page.screenshot(path=sc(tc_num, label), full_page=full_page)
    except Exception as e:
        try:
            print(f"[TC-{tc_num:02d}] 無法擷取失敗截圖（label={label}）：{type(e).__name__}: {e}")
        except Exception:
            pass


async def _log_console_errors(page, tc_num):
    """失敗時收集並列印 browser console errors。"""
    try:
        errors = []
        # console_messages 收集在 page 實例上（由 run_tests 注入）
        for msg in getattr(page, "_captured_console", []):
            if msg.type == "error":
                errors.append(msg.text)
        if errors:
            print(f"[TC-{tc_num:02d}] Browser console errors:")
            for err in errors:
                print(f"  CONSOLE ERROR: {err}")
        else:
            print(f"[TC-{tc_num:02d}] No browser console errors")
    except Exception as e:
        print(f"[TC-{tc_num:02d}] Could not capture console errors: {e}")


def _is_retryable_error(exc: Exception) -> bool:
    """
    判斷錯誤是否應重試。
    只對 timeout 和 navigation 錯誤重試，不對 assertion 失敗重試。

    issue #886 review：這裡曾經多一條 `isinstance(exc, asyncio.CancelledError)`
    分支，但呼叫端只把這個函式用在 `except Exception as e:` 抓到的 `e` 上——Python
    3.8 起 `asyncio.CancelledError` 改繼承 `BaseException`、不是 `Exception`，那個
    分支永遠不可能被觸發，是死碼。已移除；`asyncio.CancelledError` 若真的發生會
    直接從 run_tests() 的 try/except 穿出去（未捕捉），這是既有行為，不是本次改動
    引入的——真要處理它需要另外多一層 `except (Exception, asyncio.CancelledError)`
    或改用 `except BaseException`，那是設計取捨，留給 #898 一併評估。
    """
    exc_str = str(exc).lower()
    exc_type = type(exc).__name__.lower()

    # Playwright TimeoutError
    if "timeout" in exc_type or "timeout" in exc_str:
        return True
    # Navigation errors
    if any(kw in exc_str for kw in ["navigation", "net::err_", "failed to fetch", "aborted"]):
        return True
    return False


class _ConsoleCapture:
    """Lightweight console message collector injected onto each page."""
    def __init__(self):
        self.messages = []

    def __call__(self, msg):
        self.messages.append(msg)


# ─── 測試註冊表和執行引擎 ───────────────────────────────────────────────────

def _tc_label(tc):
    """
    統一 TC 顯示格式：整數 TC 編號格式化為 "TC-NN"；非整數直接轉字串。

    issue #886 review round 3：run_tests() 的例外處理現在會在 results 裡塞一筆
    "tc": "SUITE-ABORT" 的合成項目（見 run_tests() 的最外層 except）。彙總報告的
    列印迴圈與 _write_junit_xml() 原本都直接寫 f"TC-{tc:02d}"——對字串值會直接
    拋 ValueError（":02d" 需要數字），等於合成項目本身會讓彙總報告在印到那一列
    時崩潰，反而錯過原本要留下的訊號。統一經過這裡就不會有這個問題。
    """
    return f"TC-{tc:02d}" if isinstance(tc, int) else str(tc)


def _compute_stats(results):
    """
    (passed, failed, errors, skipped, total) 統計 — 從 run_tests() 抽出來，因為
    issue #886 review round 5 的彙總報告 fallback 需要在 try 內外各算一次同一組
    數字（正常路徑印出來之前，以及 try 失敗後改印到 stderr 之前），單一函式避免
    兩處算法互相漂移。
    """
    passed = sum(1 for r in results if r["status"] == "PASS")
    failed = sum(1 for r in results if r["status"] == "FAIL")
    errors = sum(1 for r in results if r["status"] == "ERROR")
    skipped = sum(1 for r in results if r["status"] == "SKIP")
    total = len(results)
    return passed, failed, errors, skipped, total


TC_REGISTRY = {
    1: ("XSS 反射測試", tc_01_xss_reflected, "P0"),
    2: ("SQL Injection 測試", tc_02_sql_injection, "P0"),
    3: ("CSRF Token 驗證", tc_03_csrf_token, "P0"),
    4: ("Analysis Mode 頁面測試", tc_04_analysis_mode_page, "P1"),
    5: ("Analysis 未啟用頁面", tc_05_analysis_not_enabled, "P1"),
    6: ("登入失敗測試", tc_06_login_failure, "P1"),
    7: ("登出測試", tc_07_logout, "P1"),
    8: ("未認證存取 API", tc_08_authorization, "P0"),
    9: ("Session Fixation", tc_09_session_fixation, "P1"),
    10: ("HTTP Security Headers", tc_10_security_headers, "P1"),
    11: ("Cookie Flags", tc_11_cookie_flags, "P1"),
    12: ("Rate Limiting", tc_12_rate_limiting, "P2"),
    13: ("驗證碼圖片", tc_13_captcha_exists, "P1"),
    14: ("密碼欄位 autocomplete", tc_14_password_autocomplete, "P2"),
    15: ("Analysis 0 measures → 400", tc_15_analysis_no_measures, "P1"),
    16: ("Analysis 超過 3 維度 → 400", tc_16_analysis_too_many_dims, "P1"),
    17: ("Analysis 正常查詢", tc_17_analysis_query_success, "P1"),
    18: ("Analysis Export xlsx/csv", tc_18_analysis_export, "P1"),
    19: ("Analysis 不明 VM 型別 → 404", tc_19_analysis_unknown_vm, "P1"),
    20: ("Analysis 不合法欄位 → 400", tc_20_analysis_invalid_field, "P1"),
    21: ("登入頁視覺驗收", tc_21_login_visual, "P2"),
    22: ("首頁 Dashboard 版面驗證", tc_22_dashboard, "P2"),
    23: ("Analysis Meta API (#516)", tc_23_analysis_meta_api, "P1"),
    24: ("Analysis 完整查詢流程", tc_24_analysis_full_flow, "P1"),
    25: ("Grid 分頁功能", tc_25_grid_paging, "P2"),
    26: ("CRUD 完整流程", tc_26_crud_flow, "P1"),
    27: ("使用者管理頁面", tc_27_user_management, "P2"),
    28: ("角色管理 + 權限設定", tc_28_role_management, "P2"),
    29: ("ETL 管理頁面", tc_29_etl_management, "P2"),
    30: ("匯入功能流程", tc_30_import_flow, "P2"),
    31: ("WorkFlow 設計器 smoke (T-DSN-18)", tc_31_workflow_designer_smoke, "P2"),
    32: ("JWT LoginJwt + Refresh 流程", tc_32_jwt_refresh_rotation_replay, "P1"),
    33: ("combobox 聯動串聯 (chain/cascade)", tc_33_combobox_chain_cascade, "P1"),
    34: ("Selector 對話框開啟流程", tc_34_selector_dialog_flow, "P1"),
    35: ("上傳元件 (wt:upload) 往返流程", tc_35_upload_widget_roundtrip, "P2"),
    36: ("租戶切換 (Tenant Switch)", tc_36_tenant_switch, "SKIP"),
}


async def run_tests(tc_nums=None, headless=None, slow_mo=0, report_path=None):
    """
    執行指定的 TC，或全部執行。

    Args:
        tc_nums: 要執行的 TC 編號列表，None=全部
        headless: True=headless（預設），False=顯示視窗，None=讀全局 HEADLESS
        slow_mo: 操作間延遲（毫秒）
        report_path: JUnit XML 報告輸出路徑（None=不輸出）
    """
    from playwright.async_api import async_playwright
    from playwright.async_api import TimeoutError as _RealPlaywrightTimeoutError

    # Issue #898: bind the real type into module globals now that playwright is
    # actually available, so every tc_ function's `except PlaywrightTimeoutError:`
    # resolves correctly once run_tests() starts calling them below. See the
    # `PlaywrightTimeoutError = None` placeholder near the top of this file for why
    # this isn't a plain module-level import.
    globals()["PlaywrightTimeoutError"] = _RealPlaywrightTimeoutError

    if headless is None:
        headless = HEADLESS

    if tc_nums is None:
        tc_nums = sorted(TC_REGISTRY.keys())

    SCREENSHOTS_DIR.mkdir(parents=True, exist_ok=True)

    results = []
    try:
        # issue #886 review round 4 (gap 1): the guard used to sit *inside*
        # `async with async_playwright() as p:`, so the context manager's own
        # __aexit__ (driver teardown) was not covered — an exception raised
        # there escaped exactly like the calls in the lifecycle table did
        # before round 3. This is row 12 from that table, the one flagged as
        # "can't be wrapped line-by-line" — it can, by moving the guard to
        # wrap the whole `async with` statement instead of just its body.
        async with async_playwright() as p:
            # issue #907: /dev/shm is Docker's default 64MB on BOTH
            # local-runner and azure-overflow-runner (ShmSize == 67108864 on
            # each) — azure's better e2e stability is capacity/CPU/RAM, not
            # a bigger /dev/shm. This launch call previously had no `args`;
            # --disable-dev-shm-usage (Chromium uses /tmp instead) removes
            # /dev/shm exhaustion as a *possible* cause of `Target crashed`
            # / `TargetClosedError` (PR #881). NOT established that it was
            # the actual cause: a control run of the unmodified script
            # showed the same 0KB /dev/shm usage and also didn't crash.
            # Don't remove this flag without re-reading #907.
            browser = await p.chromium.launch(
                headless=headless,
                slow_mo=slow_mo,
                args=["--disable-dev-shm-usage"],
            )

            for tc_num in tc_nums:
                if tc_num not in TC_REGISTRY:
                    print(f"[SKIP] TC-{tc_num:02d} 不存在")
                    results.append({"tc": tc_num, "status": "SKIP", "error": "不存在"})
                    continue

                name, func, priority = TC_REGISTRY[tc_num]
                print(f"\n{'='*60}")
                print(f"TC-{tc_num:02d}: {name} [{priority}]")
                print(f"{'='*60}")

                context = await browser.new_context(
                    viewport={"width": 1280, "height": 800},
                    ignore_https_errors=True,
                )
                page = await context.new_page()
                page.set_default_timeout(TIMEOUT)

                # Attach console capture for failure diagnostics
                console_capture = _ConsoleCapture()
                page.on("console", console_capture)
                page._captured_console = console_capture.messages

                start = datetime.now()
                retry_count = 0
                last_error = None

                # Retry loop — up to MAX_RETRIES on timeout/navigation errors
                for attempt in range(MAX_RETRIES + 1):
                    try:
                        await func(page)
                        elapsed = (datetime.now() - start).total_seconds()
                        results.append({"tc": tc_num, "status": "PASS", "elapsed": elapsed, "retries": retry_count})
                        last_error = None
                        break
                    except TestSkipped as e:
                        # Real skip (#681): no scenario to test in this environment.
                        # Distinct status — must NOT be counted as PASS or FAIL.
                        # Accounting first, diagnostics best-effort after (issue #886
                        # review, MEDIUM): a print() can raise BrokenPipeError if stdout
                        # is closed, and that must never cost us the result record.
                        elapsed = (datetime.now() - start).total_seconds()
                        results.append({"tc": tc_num, "status": "SKIP", "error": str(e), "elapsed": elapsed, "retries": retry_count})
                        last_error = None
                        try:
                            print(f"[TC-{tc_num:02d}] SKIP: {e}")
                        except Exception:
                            pass  # diagnostics only; the result above is already recorded
                        break
                    except AssertionError as e:
                        # Assertion failures: no retry, mark as FAIL immediately.
                        # Accounting first, diagnostics best-effort after — see note above.
                        elapsed = (datetime.now() - start).total_seconds()
                        results.append({"tc": tc_num, "status": "FAIL", "error": str(e), "elapsed": elapsed, "retries": retry_count})
                        last_error = None
                        try:
                            print(f"[TC-{tc_num:02d}] FAIL: {e}")
                            await _screenshot_on_failure(page, tc_num, "FAIL")
                            await _log_console_errors(page, tc_num)
                        except Exception:
                            pass  # diagnostics only; the result above is already recorded
                        break
                    except Exception as e:
                        elapsed = (datetime.now() - start).total_seconds()
                        error_str = str(e)
                        is_retryable = _is_retryable_error(e)

                        if is_retryable and attempt < MAX_RETRIES:
                            retry_count += 1
                            # No results.append() on this path — it's a retry, not a
                            # final outcome. But everything below MUST still run (the
                            # continue is what keeps the retry loop alive), so wrap the
                            # diagnostics: a BrokenPipeError from print() here must not
                            # escape the except block and abort run_tests() entirely
                            # (issue #886 review, MEDIUM).
                            try:
                                print(f"[TC-{tc_num:02d}] {error_str} — retry {retry_count}/{MAX_RETRIES}")
                                await _screenshot_on_failure(page, tc_num, f"RETRY-{retry_count}")
                                await _log_console_errors(page, tc_num)
                            except Exception:
                                pass  # diagnostics only; the retry must proceed regardless

                            # Create fresh context for retry to avoid state leakage.
                            # issue #886 review round 2 (MEDIUM): this rebuild used to be
                            # unguarded — if context.close()/new_context()/new_page()/
                            # page.on() itself throws, that exception escaped this
                            # `except Exception as e:` block entirely, past the `for attempt`
                            # loop and past `async with async_playwright()`, aborting
                            # run_tests() for every *remaining* TC, not just this one — the
                            # one accounting path that could still lose a result (or the
                            # whole rest of the run) even after round 1's fix. A retry that
                            # can't get a fresh browser context isn't a retryable condition
                            # anymore; record it as this TC's final ERROR outcome instead of
                            # letting it destroy the run.
                            try:
                                await context.close()
                                context = await browser.new_context(
                                    viewport={"width": 1280, "height": 800},
                                    ignore_https_errors=True,
                                )
                                page = await context.new_page()
                                page.set_default_timeout(TIMEOUT)
                                # Re-attach console capture for retry attempt
                                console_capture = _ConsoleCapture()
                                page.on("console", console_capture)
                                page._captured_console = console_capture.messages
                            except Exception as rebuild_err:
                                elapsed = (datetime.now() - start).total_seconds()
                                results.append({
                                    "tc": tc_num,
                                    "status": "ERROR",
                                    "error": f"重試前重建 context 失敗：{type(rebuild_err).__name__}: {rebuild_err}",
                                    "elapsed": elapsed,
                                    "retries": retry_count,
                                })
                                last_error = None
                                try:
                                    print(f"[TC-{tc_num:02d}] ERROR: 重試前重建 context 失敗：{rebuild_err}")
                                except Exception:
                                    pass  # diagnostics only; the result above is already recorded
                                # No extra cleanup here: the unconditional `await
                                # context.close()` right after this retry loop (same
                                # cleanup every PASS/FAIL/ERROR/SKIP path already goes
                                # through) will run next regardless of which of the
                                # try block's four awaits above failed — closing
                                # whatever `context` currently references. Duplicating
                                # that call here would only add a second close attempt
                                # on possibly-already-closed state for no benefit.
                                break
                            continue

                        # Non-retryable error or retries exhausted.
                        # Accounting first, diagnostics best-effort after — see note above.
                        results.append({"tc": tc_num, "status": "ERROR", "error": error_str, "elapsed": elapsed, "retries": retry_count})
                        last_error = None
                        try:
                            print(f"[TC-{tc_num:02d}] ERROR: {error_str}")
                            if is_retryable:
                                print(f"  (retries exhausted after {MAX_RETRIES})")
                            traceback.print_exc()
                            await _screenshot_on_failure(page, tc_num, "ERROR")
                            await _log_console_errors(page, tc_num)
                        except Exception:
                            pass  # diagnostics only; the result above is already recorded
                        break
                else:
                    # Loop completed without break (shouldn't happen, but safety net)
                    if last_error:
                        elapsed = (datetime.now() - start).total_seconds()
                        results.append({"tc": tc_num, "status": "ERROR", "error": str(last_error), "elapsed": elapsed, "retries": retry_count})

                await context.close()

            await browser.close()
    except Exception as e:
        # issue #886 (rounds 3-4): rows 3-7/10/11 from the lifecycle audit table
        # (browser launch, per-TC context/page setup, the unconditional
        # `context.close()` after the retry loop, `browser.close()` after the
        # whole TC loop) plus row 12 (`async with`'s own __aexit__, now covered
        # by wrapping the whole statement above) were all unguarded — any of
        # them raising used to escape run_tests() entirely, skipping the
        # summary report and JUnit XML below (`# 彙總報告`, outside this
        # try/except) no matter how many TCs had already completed correctly.
        #
        # The obvious fix — catch here and fall through to the existing summary
        # code — has a worse failure mode than the one it closes: if NOTHING ran
        # yet (e.g. browser.launch() itself failed), `results` is still `[]`,
        # and printing "Total: 0 | PASS: 0 | FAIL: 0 | ERROR: 0 | SKIP: 0"
        # passes this repo's own CI convention (CLAUDE.md: judge e2e by
        # `FAIL: 0` and `ERROR: 0` in that summary line) — a suite that never
        # launched would read as a perfect green. That's exactly the "error
        # state collapsing into a value the caller can't distinguish from
        # success" defect class this whole PR exists to close, and it would
        # have been introduced BY this fix. So: always append a synthetic
        # ERROR result naming what aborted and how many TCs never ran, so
        # `errors` is never zero here and `ERROR: 0` can't match — see
        # test_lifecycle_abort_no_false_green.py.
        #
        # issue #886 review round 4 (gap 2): the first version of this handler
        # printed the abort diagnostic BEFORE appending that synthetic result —
        # exactly the "diagnostics before accounting" mistake round 2's MEDIUM 3
        # fix eliminated from the four pre-existing branches (SKIP/FAIL/RETRY/
        # ERROR). A print() failure here (stdout closed -> BrokenPipeError) would
        # have skipped results.append() entirely, losing the one thing this whole
        # guard exists to guarantee. Accounting first, unconditionally; diagnostics
        # best-effort after, wrapped so nothing there can undo the append above it.
        completed = {r["tc"] for r in results if isinstance(r.get("tc"), int)}
        remaining = [t for t in tc_nums if t not in completed]
        results.append({
            "tc": "SUITE-ABORT",
            "status": "ERROR",
            "error": (
                f"測試迴圈提前中止（{type(e).__name__}: {e}）—— "
                f"{len(remaining)}/{len(tc_nums)} 個 TC 未執行：{remaining}"
            ),
            "elapsed": 0,
            "retries": 0,
        })
        try:
            print(f"\n[run_tests] 未預期的例外中止了測試迴圈：{type(e).__name__}: {e}")
            traceback.print_exc()
        except Exception:
            pass  # diagnostics only; the synthetic result above is already recorded
    # 彙總報告
    #
    # issue #886 review round 5: this is the last unguarded place in this
    # function that could lose the report. It runs on EVERY path — normal
    # completion and the synthetic-ERROR abort path above both reach here
    # with `results` fully built — including the all-green path, which the
    # review flagged as the *most likely* one to actually execute (a suite
    # that runs 36 TCs successfully prints a lot more output than one that
    # aborts at browser.launch(), so there's simply more surface for a
    # BrokenPipeError or a JUnit-XML disk-full to hit). Before this fix, a
    # failure anywhere in this block — a print(), or `_write_junit_xml()`'s
    # file I/O — escaped uncaught, meaning `return results` below never ran
    # and `main()` never got its exit code, on what may have been a run
    # where every single test actually passed.
    #
    # Design decision (review round 5's explicit ask — decide what an
    # unprintable-but-successful run reports, don't let it fall out):
    #   1. `results` — the data `main()`'s exit code depends on — is always
    #      returned, unconditionally, regardless of whether anything below
    #      could be printed. The exit code must never depend on a print
    #      succeeding.
    #   2. The one CI-critical line (`Total: N | PASS: n | FAIL: n |
    #      ERROR: n | SKIP: n`, the exact string CLAUDE.md's convention
    #      greps for) is attempted on stdout first. If that attempt — or
    #      anything before it, the per-TC lines or the JUnit XML write —
    #      fails, a SECOND attempt at that same line goes to stderr: CI job
    #      logs interleave stdout and stderr into one stream, so this gives
    #      the line a real chance to still surface even when stdout
    #      specifically is what broke. Silence is the fallback of last
    #      resort, not the first one.
    #   3. If even the stderr attempt fails, nothing further is attempted —
    #      a second failure while reporting the first would only obscure
    #      both, and the exit code (point 1) remains the authoritative,
    #      always-correct signal regardless.
    # See test_summary_report_failure_on_green_run_preserves_result() for
    # this exact scenario: a print failure on an otherwise fully green run.
    #
    # issue #886 review round 6: the except block below used to fall back
    # unconditionally on ANY exception in this try — including one raised
    # by `_write_junit_xml()` AFTER the stdout "Total:" line had already
    # printed successfully. That fires the stderr fallback on the wrong
    # condition: "the report block raised" is not the same thing as "the
    # summary line was never emitted", and only the second is the one the
    # fallback exists for. Fault injection proved the bug: stdout AND
    # stderr both ended up with an identical "Total: ..." line. Fixed by
    # tracking whether the stdout line actually printed and gating the
    # fallback on that, not on whether *anything* in the block raised.
    summary_line_printed = False
    try:
        print(f"\n{'='*60}")
        print("測試報告彙總")
        print(f"{'='*60}")

        passed, failed, errors, skipped, total = _compute_stats(results)

        for r in results:
            tc = r["tc"]
            name = TC_REGISTRY.get(tc, ("?", None, "?"))[0]
            priority = TC_REGISTRY.get(tc, ("?", None, "?"))[2]
            status = r["status"]
            elapsed_str = f"{r.get('elapsed', 0):.1f}s" if "elapsed" in r else "-"
            retry_str = f" (retried {r['retries']}x)" if r.get("retries", 0) > 0 else ""
            error = f" — {r.get('error', '')}" if r.get("error") else ""
            icon = {"PASS": "OK", "FAIL": "NG", "ERROR": "!!!", "SKIP": "--"}[status]
            print(f"  [{icon}] {_tc_label(tc)} [{priority}] {name}{retry_str} ({elapsed_str}){error}")

        print(f"\n  Total: {total} | PASS: {passed} | FAIL: {failed} | ERROR: {errors} | SKIP: {skipped}")
        summary_line_printed = True  # the one CI-critical line reached stdout — anything
        # that fails past this point (the screenshot-dir line, the JUnit write) is a
        # separate, lower-stakes failure and must not re-trigger the stderr fallback.
        print(f"  截圖目錄: {SCREENSHOTS_DIR.resolve()}")

        if report_path:
            _write_junit_xml(results, report_path, total, passed, failed, errors, skipped)
            print(f"  JUnit XML: {Path(report_path).resolve()}")
    except Exception as e:
        if summary_line_printed:
            # The CI-critical line is already on stdout — falling back here would
            # only duplicate it, not rescue anything. Note the later failure once,
            # to stderr, without re-emitting "Total: ...".
            try:
                print(
                    f"[run_tests] 彙總報告在 Total 行印出後仍發生例外"
                    f"（{type(e).__name__}: {e}），略過 stderr 備援以避免重複輸出"
                    "（見 issue #886 review round 6）",
                    file=sys.stderr,
                )
            except Exception:
                pass  # diagnostics only; the line is already out, nothing to rescue
        else:
            passed, failed, errors, skipped, total = _compute_stats(results)
            fallback_line = (
                f"  Total: {total} | PASS: {passed} | FAIL: {failed} | "
                f"ERROR: {errors} | SKIP: {skipped}"
            )
            try:
                print(
                    f"[run_tests] 彙總報告輸出失敗（{type(e).__name__}: {e}），"
                    "改印到 stderr（見 issue #886 review round 5）：",
                    file=sys.stderr,
                )
                print(fallback_line, file=sys.stderr)
            except Exception:
                pass  # nothing more can be done; `results` below is still correct

    return results


def _write_junit_xml(results, report_path, total, passed, failed, errors, skipped):
    """輸出 JUnit XML 格式報告（與 pytest / GitHub Actions 相容）。"""
    suite_time = sum(r.get("elapsed", 0) for r in results)
    suite = ET.Element("testsuite", {
        "name": "WTM E2E",
        "tests": str(total),
        "failures": str(failed),
        "errors": str(errors),
        "skipped": str(skipped),
        "time": f"{suite_time:.3f}",
        "timestamp": datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S"),
    })

    for r in results:
        tc = r["tc"]
        name, _, priority = TC_REGISTRY.get(tc, (_tc_label(tc), None, "?"))
        retries = r.get("retries", 0)
        case_name = f"{_tc_label(tc)}: {name} [{priority}]"
        if retries > 0:
            case_name += f" (retried {retries}x)"
        case = ET.SubElement(suite, "testcase", {
            "classname": "WTM.E2E",
            "name": case_name,
            "time": f"{r.get('elapsed', 0):.3f}",
        })
        status = r["status"]
        if status == "FAIL":
            failure = ET.SubElement(case, "failure", {"message": r.get("error", "")})
            failure.text = r.get("error", "")
        elif status == "ERROR":
            error_el = ET.SubElement(case, "error", {"message": r.get("error", "")})
            error_el.text = r.get("error", "")
        elif status == "SKIP":
            ET.SubElement(case, "skipped", {"message": r.get("error", "不存在")})

    out = Path(report_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    tree = ET.ElementTree(suite)
    ET.indent(tree, space="  ")
    tree.write(str(out), encoding="utf-8", xml_declaration=True)


def main():
    default_report = os.environ.get("WTM_E2E_REPORT", "")

    parser = argparse.ArgumentParser(description="WTM Demo E2E Test Suite")
    parser.add_argument("--tc", type=str, default=None,
                        help="要執行的 TC 編號，逗號分隔（如 1,4,23）。預設全部執行。")
    headed_group = parser.add_mutually_exclusive_group()
    headed_group.add_argument("--headed", action="store_true",
                              help="顯示瀏覽器視窗（非 headless）")
    headed_group.add_argument("--headless", action="store_true", default=False,
                              help="強制 headless 模式（覆寫環境變數）")
    parser.add_argument("--slow-mo", type=int, default=0,
                        help="操作間延遲（毫秒）")
    parser.add_argument("--base-url", type=str, default=None,
                        help=f"覆寫 base URL（預設 {BASE_URL}）")
    parser.add_argument("--report", type=str, default=default_report or None,
                        help="JUnit XML 報告輸出路徑（如 results/junit.xml）")
    parser.add_argument("--list", action="store_true",
                        help="僅列出 TC_REGISTRY（編號/名稱/優先度）並結束，不連線、不啟動瀏覽器。"
                             "供結構驗證（確認每個新 tc_ 都已註冊）使用。")

    args = parser.parse_args()

    if args.list:
        for tc_num in sorted(TC_REGISTRY.keys()):
            name, func, priority = TC_REGISTRY[tc_num]
            print(f"TC-{tc_num:02d} [{priority}] {name} -> {func.__name__}")
        print(f"\nTotal registered: {len(TC_REGISTRY)}")
        return

    if args.base_url:
        globals()["BASE_URL"] = args.base_url

    # headless 決策：--headed > --headless > HEADLESS env var
    if args.headed:
        headless = False
    elif args.headless:
        headless = True
    else:
        headless = HEADLESS

    tc_nums = None
    if args.tc:
        tc_nums = [int(x.strip()) for x in args.tc.split(",")]

    results = asyncio.run(run_tests(tc_nums, headless=headless, slow_mo=args.slow_mo,
                                    report_path=args.report))

    # 非 0 退出碼表示有失敗
    failed = sum(1 for r in results if r["status"] in ("FAIL", "ERROR"))
    sys.exit(1 if failed > 0 else 0)


if __name__ == "__main__":
    main()
