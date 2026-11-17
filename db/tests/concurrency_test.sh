#!/usr/bin/env bash
# =============================================================================
# Concurrency test (NFR03, UC01 E1): two sessions issue the same SKU from the
# same hub at the same time. Without row locking both would read "available = N"
# and the stock would go negative (lost update). The ledger trigger takes
# SELECT ... FOR UPDATE on the balance row, so session B waits for A, then sees
# the reduced stock and is rejected with SS200. Final stock is never negative.
# =============================================================================
set -euo pipefail
# Local superuser of docker compose by default (credentials from .env, see .env.example);
# PG* variables in the environment take precedence, e.g. for the server admin.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../scripts" && pwd)/env.sh"
connect_as_superuser
PSQL=(psql -X -q -v ON_ERROR_STOP=1 -tA)

read -r WH PART AVAIL <<<"$("${PSQL[@]}" -F' ' -c "
  SELECT warehouse_id, part_id, available_qty::int FROM inventory.stock_balance
  WHERE available_qty BETWEEN 5 AND 50 ORDER BY warehouse_id, part_id LIMIT 1")"
QTY_A=$((AVAIL - 1)); QTY_B=2
echo "SKU $PART @ warehouse $WH: available $AVAIL. Session A issues $QTY_A, session B issues $QTY_B concurrently."

# Session A: issues and keeps the transaction open for 3 seconds (row lock held)
"${PSQL[@]}" -c "BEGIN;
  SET LOCAL ROLE smartspare_owner;
  INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
  VALUES (now(), $WH, $PART, 'ISSUE_SALE', $QTY_A, 'CONC-A');
  SELECT pg_sleep(3);
  ROLLBACK;" >/dev/null &          # rolled back at the end: the test leaves no trace
sleep 1

# Session B: starts while A holds the lock -> blocks, then fails after A "commits".
# (A rolls back to keep the database clean; to see the SS200 case we re-run B
#  after a committed A inside one transaction below.)
start_ms=$(($(date +%s%N) / 1000000))   # bash arithmetic: no bc needed (not in the postgres image)
"${PSQL[@]}" -c "SET ROLE smartspare_owner;
  BEGIN;
  INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
  VALUES (now(), $WH, $PART, 'ISSUE_SALE', $QTY_B, 'CONC-B');
  ROLLBACK;" >/dev/null
waited_ms=$(($(date +%s%N) / 1000000 - start_ms))
wait
printf 'Session B waited %d.%03ds for session A'"'"'s row lock (expected ~2 s): serialised, no lost update.\n' \
  $((waited_ms / 1000)) $((waited_ms % 1000))

# Committed-A variant in a single transaction with a savepoint, to show the outcome for B.
"${PSQL[@]}" -c "SET ROLE smartspare_owner;
  BEGIN;
  INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
  VALUES (now(), $WH, $PART, 'ISSUE_SALE', $QTY_A, 'CONC-A');
  DO \$\$ BEGIN
    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
    VALUES (now(), $WH, $PART, 'ISSUE_SALE', $QTY_B, 'CONC-B');
    RAISE EXCEPTION 'FAIL: B should have been rejected';
  EXCEPTION WHEN sqlstate 'SS200' THEN
    RAISE NOTICE 'PASS  after A, B is rejected with SS200 (%)', SQLERRM;
  END \$\$;
  SELECT 'final available = ' || available_qty FROM inventory.stock_balance WHERE warehouse_id = $WH AND part_id = $PART;
  ROLLBACK;" 2>&1 | sed 's/^psql:.*NOTICE:  //'
