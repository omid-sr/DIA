#!/usr/bin/env bash
set -euo pipefail

base="${1:-http://localhost:8080}"
user_id="11111111-1111-1111-1111-111111111111"
event_one="$(cat /proc/sys/kernel/random/uuid)"
event_two="$(cat /proc/sys/kernel/random/uuid)"

curl --location --fail --silent --show-error \
  --request POST "${base}/api/demo/wallets/${user_id}/reset" \
  --header "Content-Type: application/json" \
  --data '{"balance":1100}'
echo

curl --location --silent --show-error \
  --request POST "${base}/api/wallets/${user_id}/debits" \
  --header "Content-Type: application/json" \
  --data "{\"eventId\":\"${event_one}\",\"amount\":1000}" > /tmp/dia-debit-1.json &

curl --location --silent --show-error \
  --request POST "${base}/api/wallets/${user_id}/debits" \
  --header "Content-Type: application/json" \
  --data "{\"eventId\":\"${event_two}\",\"amount\":1000}" > /tmp/dia-debit-2.json &

wait
echo "debit 1"
cat /tmp/dia-debit-1.json
echo
echo "debit 2"
cat /tmp/dia-debit-2.json
echo
echo "wallet"
curl --location --fail --silent --show-error "${base}/api/wallets/${user_id}"
echo
