# StudyScheduler.Domain

The **domain model** — the heart of the application. Pure C# with **no framework or infrastructure
dependencies** (no EF Core, no ASP.NET, no Aspire). Everything here is about business rules, not
plumbing.

## Layout

```
Primitives/
  Entity.cs            Base class — entities have a Guid Id and compare by identity
Students/
  Student.cs           A person a tutor teaches (name, rate, subject, contact, status)
  StudentStatus.cs     Active | Archived
  IStudentRepository.cs Persistence contract (implemented in the API's infrastructure)
```

## Conventions

- **Guarded creation.** Entities are created through static factories (e.g. `Student.Create(...)`)
  that validate invariants and throw on bad input. Constructors are private.
- **Encapsulated state.** Properties have private setters; state changes go through intention-revealing
  methods (`UpdateDetails`, `ChangeStatus`) rather than open setters.
- **Ownership by Telegram id.** A `Student` belongs to a tutor identified by `TutorTelegramId` (`long`)
  — the natural identity from Telegram. There is no surrogate `Account` entity.
- **Money is `decimal`, time is `DateTimeOffset` (UTC).**
- **The domain owns its contracts.** Repository interfaces (e.g. `IStudentRepository`) live here; the
  concrete EF Core implementation lives in `StudyScheduler.API`. The domain depends on nothing.

## Adding to the domain

New concepts (e.g. `Lesson`, `Payment`) go under their own folder with the entity, any value objects
/ enums, and the repository interface. Keep them dependency-free and push behaviour into the entities.
