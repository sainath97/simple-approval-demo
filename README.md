# Simple Approval Request System — demo

A runnable demo scaffold for the Case Study 1 scope:
- Angular UI
- ASP.NET Core 8 Web API
- SQL Server via Docker Compose
- JWT bearer authentication with Employee / Manager roles
- Request submission with idempotency keys
- First-committed approval/rejection wins using SQL Server `rowversion` + conditional update
- Decision audit history
- Transactional outbox + background worker writing fake `.eml` notifications to a local folder
- Paginated request lists

## Prerequisites
- Docker Desktop (or Docker Engine + Compose)
- .NET 8 SDK
- Node.js 20.19+ and npm
- Angular CLI (optional; `npx ng` can be used)

## 1. Start SQL Server

From the repository root:

```bash
docker compose up -d sqlserver
```

Wait until SQL Server is healthy. The demo SQL Server password is intentionally a local-development value; do not reuse it.

## 2. Start the API

```bash
cd backend
dotnet restore
dotnet run
```

The API listens at `http://localhost:5080` and Swagger at `http://localhost:5080/swagger`.
The first run applies EF Core migrations automatically using `EnsureCreated` for demo convenience.

### Get demo JWTs

Swagger or curl:

```bash
curl -X POST http://localhost:5080/api/demo/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"11111111-1111-1111-1111-111111111111","displayName":"Employee One","role":"Employee"}'
```

```bash
curl -X POST http://localhost:5080/api/demo/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"22222222-2222-2222-2222-222222222222","displayName":"Manager One","role":"Manager"}'
```

This endpoint is **demo-only**. In a real deployment, tokens come from the organization's identity provider and this endpoint must be removed.

## 3. Start Angular

```bash
cd frontend
npm install
npm start
```

Open `http://localhost:4200`. Use the demo login dropdown to select Employee or Manager. The UI requests a demo JWT.

## 4. Demo walkthrough

1. Log in as Employee and submit a request.
2. The request appears as Pending. The API commits the request and outbox record in one transaction.
3. Check `./demo-notifications` for a generated `.eml` file. The worker creates it asynchronously.
4. Log in as Manager and open the pending queue.
5. Open the same request in two browser windows as two different managers. Approve/reject from both quickly. Only one decision commits; the other receives a conflict and refreshes.
6. Try rapid double-clicking Submit. The UI disables the button and uses `exhaustMap`; the API also uses an idempotency key.
7. Use page size and status filters to inspect the server-paginated lists.

## Important demo boundaries
- Attachments and request editing are intentionally excluded.
- Fake `.eml` files are not sent email and do not prove delivery/read receipt.
- The demo JWT issuer is not production identity management.
- SQL Server is authoritative. The UI is not a concurrency boundary.
- This is a compact runnable demonstration, not a complete enterprise production deployment. Before production: use an enterprise identity provider, secrets manager, migration pipeline, integration tests against SQL Server, rate limits, structured telemetry, retention jobs, and hardened outbox operations.
