# Building Production-Ready Modern APIs with .NET

Hands-on lab, VS Live Orlando (VSHOL01). You start with a working support ticketing API and evolve it into a production-ready system, one lab at a time.

## Requirements

- .NET 10 SDK
- Visual Studio 2026, VS Code with C# Dev Kit and REST Client, or JetBrains Rider
- Git and a modern browser
- A trusted dev cert: `dotnet dev-certs https --trust`

## How the labs work

Every lab has its own folder with a complete, runnable solution:

```
labs/
  Lab01/Start   <- open this to begin the lab
  Lab01/End     <- the finished lab, for comparing or catching up
  Lab02/Start   <- same code as Lab01/End, plus Lab 2's tests
  ...
```

Work in the lab's `Start` folder. When its tests go green, the lab is done. If you fall behind, close your solution and open the next lab's `Start` folder.

Each lab's instructions are in `labs/LabNN/README.md`, with a PDF copy beside it. The PDFs are for reading along. To copy code, use the `README.md` on GitHub: every code block has a copy button, and long listings come out in one piece instead of split across pages.

Two bonus labs follow Lab 8, for the afternoon if you're ahead, or for later. Both start from Lab 8's End, so do either, or both, in any order:

- `labs/BonusA`: **Safe to Retry**, idempotent ticket creation with an `Idempotency-Key` (`--filter "Lab=A"`)
- `labs/BonusB`: **Tell Other Systems**, signed webhooks with retries, delivered to a local receiver (`--filter "Lab=B"`)

Run just one lab's tests with:

```
dotnet test --filter "Lab=2"
```

## Get running (Module 0)

```
cd labs/Lab01/Start
dotnet build
dotnet test
dotnet run --project src/Ticketing.Api
```

Then open https://localhost:7001 for the lab client. This is the API you'll work on in Lab 1.

## What's in each solution

| Path | What it is |
|---|---|
| `src/Ticketing.Api` | The API you evolve all day |
| `src/Ticketing.Api/Chaos` | Fault injection for the store (latency, errors, outages). Lab infrastructure, you won't edit it |
| `src/Ticketing.Api/wwwroot` | The lab client: chaos panel, health strip, run cases. You won't edit it either |
| `tests/Ticketing.Api.Tests` | Integration tests, grouped by lab. Green means the lab is done |
| `http/` | `.http` files for poking at the API by hand |
| `make-tokens.ps1` / `make-tokens.sh` | From Lab 3 on: writes Development tokens for every lab user to `tokens.md` (git-ignored). Run it again when they expire |
