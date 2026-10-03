# Listing Studio architecture

Listing Studio is a clean modular monolith: one deployable web application and one background worker, with capability adapters kept in independently testable projects.

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

## Identity and tenant data

ASP.NET Core Identity and the EF Core `ApplicationDbContext` are Infrastructure concerns. The framework-independent Domain owns `Organization`, `OrganizationMember`, and membership roles. Application exposes the account-registration use case; Infrastructure implements it with Identity and persists the user, organization, and initial owner membership in one PostgreSQL transaction.

`OrganizationMember` has a unique `(OrganizationId, UserId)` index and foreign keys to both its organization and Identity user. Every future business aggregate must include a required `OrganizationId`, an organization foreign key, and tenant-aware indexes. Application use cases must derive organization scope from the authenticated membership instead of accepting an unrestricted tenant identifier from the client.

Password reset uses Identity data-protection tokens. Development builds keep the generated reset link only in process memory and display it on the confirmation page for local testing; a production email adapter must be configured before production use.

## Tests

Unit tests target inward layers. Integration tests use disposable PostgreSQL 17 containers, apply real EF Core migrations, and exercise the ASP.NET Core composition root plus Identity and organization persistence. Additional module-specific test projects can be introduced beside these as features are added.
