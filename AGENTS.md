# Listing Studio development rules

Listing Studio is a commercial real estate marketing SaaS. Follow the owner-provided [build plan](docs/build-plan.md) and its acceptance criteria.

## Technology and architecture

- Use .NET 10, C#, Blazor, ASP.NET Core, EF Core and PostgreSQL.
- Keep a modular monolith. Do not introduce microservices without the owner's explicit approval.
- Respect project dependency boundaries. Keep provider-specific OpenAI, voice, Stripe, Azure and AI video types out of Domain and Application.
- Put external services behind interfaces.

## Security and data

- Never commit secrets, local credentials, property photos, or customer data.
- Scope every business entity and query to its Organization; test cross-organization access.
- Never manufacture real estate facts. Ground generated marketing claims in verified property data.

## Reliability, cost and quality

- Make background operations idempotent where appropriate; cache expensive AI results when reusable.
- Do not silently catch exceptions. Use structured logging and avoid logging secrets or personal data.
- Add meaningful tests for significant features. Run `dotnet build ListingStudio.slnx` and `dotnet test ListingStudio.slnx` before declaring a coding task complete. State when a command cannot run.
- Do not change unrelated code. Explain major architectural choices when requirements are ambiguous.
- Keep each milestone in a reviewable PR. Do not merge, deploy, or enable paid external services as part of an unattended coding run.
