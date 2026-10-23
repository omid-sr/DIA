# DIA wallet

`Api.DIA` is the wallet service. `Producer` is the Visual Studio console app that publishes wallet charges when you press F5. Your local SQL Server is the source of truth. RabbitMQ, running from the official image in Docker Desktop, carries the commands. Two API replicas run in Docker Desktop, and the same image can scale further. The race is still decided in SQL Server, so the replicas cannot both spend the same money.

The demo user starts with **$1100**. The producer publishes **two different charges of $1000**, then publishes the first charge again. One charge is applied, the other is rejected because $100 is left, and the replay does not move the balance. The ending balance is **$100**.

An in-memory lock cannot do this when ten replicas are running. Two replicas can read $1100 at the same time and both decide the charge fits. The decision is made inside one SQL Server transaction:

1. Insert the event id into `InboxMessages`. The primary key is the idempotency key. A second delivery of the same id waits, then receives the outcome that was already stored.
2. Lock that user's wallet row with `UPDLOCK, ROWLOCK, HOLDLOCK`. Every replica blocks on the same row.
3. Charge only when the locked balance is enough. The balance column also has `CHECK (Balance >= 0)`.
4. Write the ledger row and the final inbox status in the same transaction.
5. Acknowledge the RabbitMQ message only after that commit. A crash before the ack redelivers the message, and step 1 makes the redelivery a no-op.

## Run it

SQL Server stays on your machine. Docker Desktop runs RabbitMQ from the official `rabbitmq:4-management` image and two replicas of `Api.DIA`. The Producer is a console app: open it in Visual Studio and press F5 when you want the events published.

LocalDB cannot be reached from a container. Use SQL Server Express, Developer, or a full instance, and turn on TCP/IP.

1. In SQL Server Configuration Manager, enable TCP/IP for the instance and set it to port **1433**. Restart the SQL Server service.
2. Use Mixed Mode authentication. The API containers are Linux, so they cannot use Windows Authentication. A SQL login such as `sa` needs permission to create the `DiaWallet` database (`dbcreator` or `sysadmin`).
3. Allow inbound TCP 1433 in Windows Firewall for SQL Server, so Docker Desktop can connect through `host.docker.internal`.
4. Copy the sample environment file and put that SQL login in it:

```powershell
copy .env.example .env
```

5. Start Docker Desktop, then from the repo folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\demo.ps1 -Replicas 2
```

That starts one RabbitMQ container and two API containers. RabbitMQ management is at <http://localhost:15672> (`guest` / `guest`). Both API replicas are behind <http://localhost:8080>.

6. Open `DIA.slnx` in Visual Studio, set **Producer** as the startup project, and press F5. Each run resets the demo wallet to $1100, publishes two different $1000 charges, then publishes the first event again. The window prints `PASS` when one charge is applied and the other is rejected.

Stop the containers with `docker compose down`. Pass `-Replicas 10` to run ten API replicas. The Producer keeps talking to `localhost:5672` and `http://localhost:8080`, which are set in `src/Producer/appsettings.json`.

On Linux or macOS the same stack is `./scripts/demo.sh 2`, then run the Producer with `dotnet run --project src/Producer`.

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
| `src/Producer` | Visual Studio console app. Press F5 to publish the two charges |
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

`compose.yaml` reads `WALLET_CONNECTION_STRING` from `.env`. That string is what the API containers use to reach SQL Server on the host. `.env` is not committed.

RabbitMQ inside Docker Desktop is `guest` / `guest` on ports 5672 and 15672. The demo reset endpoint is on while the containers are running (`Demo__AllowReset=true`). Turn that off outside this demo.

`dotnet test` starts its own SQL Server container for the race tests. It does not connect to your local instance.
