# Listing Studio architecture

Listing Studio begins as a clean modular monolith: one deployable web application and one background worker, with capability adapters kept in independently testable projects. Business features are intentionally out of scope for this foundation.

## Dependency rule

Dependencies point inward:

```text
Domain <- Application <- Infrastructure
                    ^-- AI
                    ^-- Video

Web and Worker are composition roots and may reference Application plus all adapters.
```

- **Domain** contains enterprise rules and has no project dependencies.
- **Application** contains use cases and ports. It references Domain only.
- **Infrastructure** implements persistence, storage, payments, and other technical ports. It references Application and Domain.
- **AI** and **Video** are isolated outbound adapter modules. Each references Application only.
- **Web** hosts Blazor and ASP.NET Core. It composes modules but contains no business rules.
- **Worker** hosts asynchronous/background execution and uses the same module registrations.

Modules expose dependency injection through `AddApplication`, `AddInfrastructure`, `AddAI`, and `AddVideo`. Cross-module implementation references are prohibited; coordination belongs in Application abstractions. PostgreSQL is the transactional store, while external service credentials are supplied at runtime through configuration providers and never committed.

## Tests

Unit tests target inward layers. Integration tests exercise the ASP.NET Core composition root. Additional module-specific test projects can be introduced beside these as features are added.
