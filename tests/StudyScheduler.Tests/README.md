# StudyScheduler.Tests

**Unit tests** — fast, isolated, **no Docker or database**. They cover business rules and the
security-critical auth validator. Safe to run in any IDE test runner on every save.

## Layout (mirrors `src/`)

```
Core/Authentication/
  TelegramInitDataFactory.cs        Independently mints signed init data (a test "oracle")
  TelegramInitDataValidatorTests.cs Valid/tampered/missing init data cases
Domain/Students/
  StudentTests.cs                   Factory validation, updates, status changes
```

## Notes

- `TelegramInitDataFactory` re-implements the Telegram signing algorithm independently, so the
  validator is exercised as a true black box. It encodes the contract that the HMAC covers every field
  **except `hash`** (the `signature` field is included — verified against real init data).

## Run

```bash
dotnet test tests/StudyScheduler.Tests/StudyScheduler.Tests.csproj
```
