#!/usr/bin/env bash
# 檢查上游 dotnetcore/WTM 的 dotnet10 是否有本 fork 尚未包含的 commit，
# 有的話開（或更新）一張 upstream-sync Issue。只讀、只開 Issue，絕不 merge / push。
#
# 環境變數：
#   UPSTREAM_URL  上游 repo（預設 https://github.com/dotnetcore/WTM.git）
#   UPSTREAM_REF  上游分支（預設 dotnet10）
#   BODY_FILE     Issue 內容輸出檔（預設 body.md）
#   DRY_RUN       非空時只產生 BODY_FILE，不呼叫 gh
#   GH_TOKEN      gh CLI 使用的 token（非 DRY_RUN 時需要）
#
# 安全：上游 commit subject 與檔名都是不受信任的資料，只經由檔案傳遞，
# 不 eval、不放進命令列；寫入 Markdown 前移除反引號，避免跳出 code fence。
set -euo pipefail

UPSTREAM_URL="${UPSTREAM_URL:-https://github.com/dotnetcore/WTM.git}"
UPSTREAM_REF="${UPSTREAM_REF:-dotnet10}"
BODY_FILE="${BODY_FILE:-body.md}"
DRY_RUN="${DRY_RUN:-}"
MAX_FILES=300      # 清單最多列幾個檔案（GitHub Issue 本文上限 65536 字元）
MAX_BODY_BYTES=60000
TITLE_PREFIX="upstream-sync:"
LABEL="upstream-sync"

LAYUI_REGEX='^(src/WalkingTec\.Mvvm\.TagHelpers\.LayUI/|src/WalkingTec\.Mvvm\.Mvc/framework_layui\.js$|src/WalkingTec\.Mvvm\.Mvc/Views/|src/WalkingTec\.Mvvm\.Mvc/GeneratorFiles/Mvc/|src/WalkingTec\.Mvvm\.Mvc/_FrameworkController\.cs$|src/WalkingTec\.Mvvm\.Mvc/Filters/PrivilegeFilter\.cs$|src/WalkingTec\.Mvvm\.Mvc/Helper/FResultExtension\.cs$|src/WalkingTec\.Mvvm\.Core/.*(BasePagedListVM|BaseTemplateVM|BaseImportVM|LayUIMenu|MenuExtension))'

# FORK.md 第 6 節分岔清單（新增分岔時需同步更新這裡）
DIVERGED_FILES=(
  'common.props'
  'src/WalkingTec.Mvvm.Core/WTMContext.cs'
  'src/WalkingTec.Mvvm.Core/ConfigOptions/JwtOptions.cs'
  'src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs'
  'test/WalkingTec.Mvvm.Test.Mock/MockWtmContext.cs'
  'demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/wtmbuild.ts'
)

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if git remote get-url upstream >/dev/null 2>&1; then
  git remote set-url upstream "$UPSTREAM_URL"
else
  git remote add upstream "$UPSTREAM_URL"
fi
git fetch --no-tags upstream "$UPSTREAM_REF"

UP="refs/remotes/upstream/${UPSTREAM_REF}"
BEHIND="$(git rev-list --count "HEAD..${UP}")"
if [ "$BEHIND" -eq 0 ]; then
  echo "up to date: upstream/${UPSTREAM_REF} 沒有新的 commit"
  exit 0
fi

UP_SHA="$(git rev-parse "$UP")"
BASE="$(git merge-base HEAD "$UP")"
echo "upstream/${UPSTREAM_REF} 領先 ${BEHIND} 個 commit（head ${UP_SHA}）"

# 清單檔：移除反引號，避免不受信任的文字跳出 code fence
git log -n 100 --oneline --no-merges "HEAD..${UP}" | tr -d '`' | cut -c1-200 > "$tmp/commits.txt"
NO_MERGE_COUNT="$(git rev-list --count --no-merges "HEAD..${UP}")"
git diff --name-only "$BASE" "$UP" | tr -d '`' | cut -c1-300 > "$tmp/files.txt"
FILE_COUNT="$(wc -l < "$tmp/files.txt" | tr -d ' ')"
grep -E "$LAYUI_REGEX" "$tmp/files.txt" | head -n "$MAX_FILES" > "$tmp/layui.txt" || true
printf '%s\n' "${DIVERGED_FILES[@]}" > "$tmp/diverged_all.txt"
grep -Fxf "$tmp/diverged_all.txt" "$tmp/files.txt" > "$tmp/diverged.txt" || true
head -n "$MAX_FILES" "$tmp/files.txt" > "$tmp/files_shown.txt"

emit_list() { # $1=檔案 $2=無內容時的文字
  if [ -s "$1" ]; then
    echo '```text'
    cat "$1"
    echo '```'
  else
    echo "$2"
  fi
}

{
  echo "上游 \`dotnetcore/WTM\` 的 \`${UPSTREAM_REF}\` 有 **${BEHIND}** 個本 fork 尚未包含的 commit（非 merge commit：${NO_MERGE_COUNT}）。"
  echo
  echo "- 上游 HEAD：\`${UP_SHA}\`"
  echo "- 共同祖先：\`${BASE}\`"
  echo "- 此 Issue 由 \`.github/workflows/upstream-check.yml\` 自動建立，workflow 不會自動合併。"
  echo
  echo "## 新增的 commit（最多顯示 100 筆）"
  echo
  emit_list "$tmp/commits.txt" "（只有 merge commit）"
  echo
  echo "## 變動的檔案（共 ${FILE_COUNT} 個，最多顯示 ${MAX_FILES} 個）"
  echo
  emit_list "$tmp/files_shown.txt" "（無）"
  echo
  echo "## ⚠️ 動到 LayUI 受保護路徑"
  echo
  if [ -s "$tmp/layui.txt" ]; then
    echo "以下檔案屬於 FORK.md 第 3 節的 LayUI 受保護路徑，同步時逐一檢查；上游的刪除、棄用與不相容修改不接受："
    echo
    emit_list "$tmp/layui.txt" ""
  else
    echo "無。"
  fi
  echo
  echo "## 與分岔清單重疊的檔案"
  echo
  if [ -s "$tmp/diverged.txt" ]; then
    echo "以下檔案同時被本 fork 修改過（FORK.md 第 6 節），合併時很可能衝突，請保留 fork 的修正並吸收上游變更："
    echo
    emit_list "$tmp/diverged.txt" ""
  else
    echo "無。"
  fi
  echo
  echo "## 後續"
  echo
  echo "依 FORK.md 第 5 節 runbook 同步；同步 PR 須用 merge commit、視為高風險。"
} > "$tmp/body.md"

# 保險：本文過長時截斷；標記行一律放最後，供下次去重比對
if [ "$(wc -c < "$tmp/body.md")" -gt "$MAX_BODY_BYTES" ]; then
  head -c "$MAX_BODY_BYTES" "$tmp/body.md" | iconv -f UTF-8 -t UTF-8 -c > "$BODY_FILE"
  printf '\n\n（內容過長已截斷，請在本機以 git log / git diff 查看完整差異）\n' >> "$BODY_FILE"
else
  cp "$tmp/body.md" "$BODY_FILE"
fi
printf '\nupstream-head: %s\n' "$UP_SHA" >> "$BODY_FILE"

echo "已產生 ${BODY_FILE}"

if [ -n "$DRY_RUN" ]; then
  echo "DRY_RUN：略過 gh"
  exit 0
fi

# 去重：已有 open 的 upstream-sync Issue 就更新它，不重複開
EXISTING="$(gh issue list --label "$LABEL" --state open --json number,title \
  --jq "[.[] | select(.title | startswith(\"${TITLE_PREFIX}\"))] | sort_by(.number) | .[0].number // empty")"

if [ -z "$EXISTING" ]; then
  gh issue create \
    --title "${TITLE_PREFIX} 上游 ${UPSTREAM_REF} 有 ${BEHIND} 個新 commit" \
    --label "$LABEL" \
    --body-file "$BODY_FILE"
  exit 0
fi

# 取 Issue 本文與所有留言中最後一個 upstream-head 標記，SHA 沒變就不重複通知
gh issue view "$EXISTING" --json body,comments \
  --jq '[.body, (.comments[].body)] | .[]' > "$tmp/issue_texts.txt"
LAST_SHA="$(grep -oE '^upstream-head: [0-9a-f]{40}$' "$tmp/issue_texts.txt" | tail -n 1 | awk '{print $2}' || true)"

if [ "$LAST_SHA" = "$UP_SHA" ]; then
  echo "Issue #${EXISTING} 已回報過 ${UP_SHA}，略過"
  exit 0
fi

gh issue comment "$EXISTING" --body-file "$BODY_FILE"
echo "已在 Issue #${EXISTING} 補充新的上游狀態"
