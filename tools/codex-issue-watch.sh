#!/usr/bin/env bash
set -euo pipefail

REPO="${REPO:-shadowjohn/mySQLPunk}"
WORKDIR="${WORKDIR:-/home/yangchen/projects/mySQLPunk}"
CODEX_MODEL="${CODEX_MODEL:-gpt-5.2}"
CODEX_REASONING_EFFORT="${CODEX_REASONING_EFFORT:-high}"
ISSUE_LIMIT="${ISSUE_LIMIT:-100}"
STATE_DIR="${STATE_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/mysqlpunk-codex-issue-watch}"
STATE_FILE="$STATE_DIR/open-issues.tsv"
FAILED_STATE_FILE="$STATE_DIR/failed-open-issues.tsv"
FAILED_AT_FILE="$STATE_DIR/failed-at"
LOCK_FILE="$STATE_DIR/watch.lock"
LOG_FILE="$STATE_DIR/watch.log"
FAILURE_RETRY_SECONDS="${FAILURE_RETRY_SECONDS:-3600}"

mkdir -p "$STATE_DIR"
touch "$LOG_FILE"

log() {
  printf '%s %s\n' "$(date -Is)" "$*" | tee -a "$LOG_FILE" >&2
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    log "missing required command: $1"
    exit 1
  fi
}

require_command gh
require_command jq
require_command codex
require_command flock

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  log "another watcher run is still active; skipping"
  exit 0
fi

if ! gh auth status -h github.com >/dev/null 2>&1; then
  log "gh is not authenticated; run: gh auth login"
  exit 1
fi

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

issues_json="$tmp_dir/open-issues.json"
current_tsv="$tmp_dir/open-issues.tsv"
changed_tsv="$tmp_dir/changed-issues.tsv"

gh issue list \
  --repo "$REPO" \
  --state open \
  --json number,updatedAt,title \
  --limit "$ISSUE_LIMIT" >"$issues_json"

jq -r '.[] | [.number, .updatedAt, .title] | @tsv' "$issues_json" >"$current_tsv"

if [[ ! -s "$current_tsv" ]]; then
  : >"$STATE_FILE"
  rm -f "$FAILED_STATE_FILE" "$FAILED_AT_FILE"
  log "no open issues"
  exit 0
fi

if [[ ! -f "$STATE_FILE" ]]; then
  : >"$STATE_FILE"
fi

awk -F '\t' '
  FILENAME == ARGV[1] {
    seen[$1 "\t" $2] = 1
    next
  }
  !seen[$1 "\t" $2] {
    print
  }
' "$STATE_FILE" "$current_tsv" >"$changed_tsv"

if [[ ! -s "$changed_tsv" ]]; then
  log "open issues unchanged; no Codex run needed"
  exit 0
fi

issue_list="$(awk -F '\t' '{ printf "#%s (%s)\n", $1, $3 }' "$changed_tsv")"
issue_numbers="$(awk -F '\t' '{ printf "#%s ", $1 }' "$changed_tsv" | sed 's/[[:space:]]*$//')"

if [[ -f "$FAILED_STATE_FILE" && -f "$FAILED_AT_FILE" ]] &&
   cmp -s "$changed_tsv" "$FAILED_STATE_FILE"; then
  failed_at="$(cat "$FAILED_AT_FILE" 2>/dev/null || printf '0')"
  now="$(date +%s)"
  if [[ "$failed_at" =~ ^[0-9]+$ ]] &&
     (( now - failed_at < FAILURE_RETRY_SECONDS )); then
    retry_at="$(date -d "@$((failed_at + FAILURE_RETRY_SECONDS))" -Is)"
    log "changed open issues previously failed: $issue_numbers; retry after $retry_at"
    exit 0
  fi
fi

log "changed open issues detected: $issue_numbers"

if [[ "${CODEX_DRY_RUN:-0}" == "1" ]]; then
  log "dry run: would start Codex for $issue_numbers"
  exit 0
fi

prompt="$(cat <<EOF
監控 GitHub repository $REPO 的 open issues。本次 shell watcher 已確認有新增或更新的 open issue，請優先處理以下 issue：

$issue_list

請優先使用本機 gh CLI（例如 gh auth status、gh issue list/view/comment/close）檢查、回覆與結案；若 gh 未授權，再嘗試 GitHub connector；若兩者都無法授權，回報需要重新登入 GitHub 並停止，不要假裝已檢查。

開始前先確認工作樹狀態；若有使用者既有修改，不要覆蓋或 revert。發現 open issue 時，先 git pull --rebase --autostash，閱讀 issue 內容、留言與專案目前程式碼。若 issue body 或留言包含圖片、附件、user-attachments URL 或可下載檔案，必須先下載或開啟實際查看內容，再判斷需求與修正方向。

若需求足夠明確，實作修正或功能，測試通過後再 pull 一次、commit、push。Commit message 必須使用繁體中文 Conventional Commit，並包含「原因：」「調整：」「影響：」三段。完成後用繁體中文在 issue 留言說明處理內容、測試結果與 commit，然後關閉 issue。

若 issue 資訊不足或需要外部憑證、無法安全完成，就用繁體中文留言要求補充或說明阻礙，不要關閉 issue。
EOF
)"

set +e
codex exec \
  -C "$WORKDIR" \
  -m "$CODEX_MODEL" \
  -c "model_reasoning_effort=\"$CODEX_REASONING_EFFORT\"" \
  --sandbox danger-full-access \
  "$prompt"
codex_status=$?
set -e

if [[ "$codex_status" -ne 0 ]]; then
  cp "$changed_tsv" "$FAILED_STATE_FILE"
  date +%s >"$FAILED_AT_FILE"
  log "Codex run failed with exit code $codex_status; issue state was not marked successful and will retry after cooldown"
  exit "$codex_status"
fi

gh issue list \
  --repo "$REPO" \
  --state open \
  --json number,updatedAt,title \
  --limit "$ISSUE_LIMIT" |
  jq -r '.[] | [.number, .updatedAt, .title] | @tsv' >"$STATE_FILE"
rm -f "$FAILED_STATE_FILE" "$FAILED_AT_FILE"

log "Codex run completed"
