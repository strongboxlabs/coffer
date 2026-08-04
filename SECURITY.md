# Security policy

Coffer handles personal financial data — bank balances, transaction history,
investment holdings. The author takes the security of the application
seriously.

## Reporting a vulnerability

Do **not** open a public GitHub issue for a security problem.

Instead, report it privately through GitHub's **private vulnerability reporting**:
the **Report a vulnerability** button on the repository's **Security** tab. That
opens a private advisory only the maintainer can see — no public issue, no email
needed.

Please include:

1. A clear description of the issue.
2. Steps to reproduce, or a minimal proof of concept.
3. Your assessment of impact (data exposure, code execution, denial of service,
   etc.).
4. Any suggested mitigation, if you have one.

You should expect an acknowledgement within a few business days. The author is
the sole maintainer; response speed depends on availability, but security
issues are prioritized over feature work.

## Scope

In scope:

- The application code (`/api`, `/web`, `/sync`, `/importer`).
- The database schema, migrations, and triggers.
- The Docker Compose configuration and any deployment guidance in
  [docs/operations.md](docs/operations.md).
- Authentication / authorization paths.

Out of scope:

- Third-party services (SimpleFIN, Plaid, MX) — report to those
  vendors.
- Issues that require a privileged attacker who already has shell access to
  the host or the database.
- Vulnerabilities in container base images that have a documented patch
  pipeline (we track upstream advisories and update on a reasonable cadence).

## Disclosure timeline

The author aims for coordinated disclosure. Once a fix is shipped, details may
be published in the relevant ADR or in a GitHub Security Advisory if the repo
is public.
