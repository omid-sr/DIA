# DIA wallet

`Api.DIA` is the wallet service. `Producer` is a console app that publishes wallet charges. SQL Server is the source of truth. RabbitMQ carries the commands. The API is stateless, so it can run as many replicas as you need (the demo starts two; the same image scales to ten).

The demo user starts with **$1100**. The producer publishes **two different charges of $1000**, then publishes the first charge again. One charge is applied, the other is rejected because $100 is left, and the replay does not move the balance. The ending balance is **$100**.

An in-memory lock cannot do this when ten replicas are running. Two replicas can read $1100 at the same time and both decide the charge fits. The decision is made inside one SQL Server transaction:

1. Insert the event id into `InboxMessages`. The primary key is the idempotency key. A second delivery of the same id waits, then receives the outcome that was already stored.
2. Lock that user's wallet row with `UPDLOCK, ROWLOCK, HOLDLOCK`. Every replica blocks on the same row.
3. Charge only when the locked balance is enough. The balance column also has `CHECK (Balance >= 0)`.
4. Write the ledger row and the final inbox status in the same transaction.
5. Acknowledge the RabbitMQ message only after that commit. A crash before the ack redelivers the message, and step 1 makes the redelivery a no-op.

## Run the demo

Docker is required. Give Docker at least 4 GB of memory. SQL Server inside the compose file is capped at 1536 MB, and the API images are built on top of that.

### Windows

1. Install [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) and start it. Wait until it says the engine is running. WSL 2 is the backend Docker Desktop uses on Windows.
2. Install [Git for Windows](https://git-scm.com/download/win) if `git` is not already on the machine.
3. Open PowerShell and clone the repository:

```powershell
git clone https://github.com/omid-sr/DIA.git
cd DIA
git switch cursor/wallet-race-rabbitmq-sql-8b1c
```

4. Start two API replicas and run the producer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\demo.ps1 -Replicas 2
```

Use `-Replicas 10` to start ten API replicas. The producer prints `PASS` when one $1000 charge is applied and the other is rejected. The wallet HTTP API is at <http://localhost:8080>. RabbitMQ management is at <http://localhost:15672> (`guest` / `guest`).

If port 1433, 5672, 8080, or 15672 is already in use, stop the other program or change that host port in `compose.yaml`.

Stop everything from the same folder:

```powershell
docker compose down
```

### Linux or macOS

```bash
./scripts/demo.sh 2
```

`2` is the number of API replicas. Use `10` to start ten consumers on the same queue. RabbitMQ delivers each message to one of them. SQL Server still allows only one of the two $1000 charges.

When it finishes, the producer prints `PASS` and exits 0. Stop everything with `docker compose down`.

## Call the API from Postman

Import these as raw curl. The demo reset endpoint is enabled in the compose file. It is off unless `Demo:AllowReset` is true.

Reset the demo wallet to $1100:

```bash
curl --location --request POST 'http://localhost:8080/api/demo/wallets/11111111-1111-1111-1111-111111111111/reset' \
--header 'Content-Type: application/json' \
--data '{
  "balance": 1100
}'
```

Read the wallet:

```bash
curl --location 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111'
```

Charge $1000. Run the next two requests at the same time, in two Postman tabs or two terminals. The event ids must be different. One response is `Applied` with balance `100`. The other is HTTP 409 `RejectedInsufficientFunds`.

```bash
curl --location --request POST 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/debits' \
--header 'Content-Type: application/json' \
--data '{
  "eventId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1",
  "amount": 1000
}'
```

```bash
curl --location --request POST 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/debits' \
--header 'Content-Type: application/json' \
--data '{
  "eventId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2",
  "amount": 1000
}'
```

Send the first event id again. The response is the original outcome with `duplicateDelivery: true`, and the balance stays `100`.

```bash
curl --location --request POST 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/debits' \
--header 'Content-Type: application/json' \
--data '{
  "eventId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1",
  "amount": 1000
}'
```

List what was decided, and the ledger of charges that actually moved money:

```bash
curl --location 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/debits'
```

```bash
curl --location 'http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/ledger'
```

`./scripts/parallel-debit.sh` resets the wallet and fires the two charges together.

OpenAPI is served at <http://localhost:8080/openapi/v1.json>.

## Projects

| Project | Role |
| --- | --- |
| `src/Api.DIA` | ASP.NET Core wallet API and RabbitMQ consumer |
| `src/Producer` | Publishes the two charges and checks the result |
| `src/Dia.Contracts` | Message contracts and queue names shared by both |
| `tests/Api.DIA.Tests` | Parallel charges against a real SQL Server container |

HTTP `POST /api/wallets/{userId}/debits` and the RabbitMQ consumer both call `WalletDebitService`, so the race rules are the same on either path.

## Messages

Exchange `dia.wallet` (direct).

| Queue | Routing key | Who uses it |
| --- | --- | --- |
| `wallet.debit.requested` | `wallet.debit.requested` | Producer publishes, every API replica consumes |
| `wallet.debit.results` | `wallet.debit.results` | API publishes the outcome, producer listens |
| `wallet.debit.requested.dlq` | dead letter | Invalid payloads |

A charge message looks like this:

```json
{
  "eventId": "3f1c1c2e-7b1a-4f0e-9c2a-6d5b4a3e2f10",
  "userId": "11111111-1111-1111-1111-111111111111",
  "amount": 1000,
  "occurredAt": "2026-10-23T07:00:00Z"
}
```

## Tests

The concurrency tests start SQL Server and run many charges at once:

```bash
dotnet test
```

## Local configuration

SQL Server password in this sample: `Dia_Wallet_Str0ng!`. RabbitMQ user: `guest` / `guest`. Change both before any real deployment, and set `Demo__AllowReset=false` outside the demo.
