# Contributing

**Coffer isn't accepting outside contributions at present.** Please don't spend time
on a pull request — it won't be merged, and that's a poor trade for you.

That isn't unfriendliness, it's mechanics. Coffer is developed in a private
repository and published here as periodic source snapshots, so commits here share no
ancestry with upstream development: there is no branch a pull request could merge
into. Changing that would be a larger piece of work than most patches.

## What is useful

**Bug reports**, if issues are enabled. A good one has the version
(`/api/meta/version`, or the image tag), what you did, what happened, and what you
expected.

If it involves money math — balances, cost basis, realized gains — describe the
**shape** of the transactions rather than the amounts. Please keep real account
numbers, institution names and statements out of a public issue; it's your financial
data, and an issue tracker is forever.

**Security problems do not go in public issues.** Use this repository's private
vulnerability reporting — see [SECURITY.md](SECURITY.md).

## Forking

Encouraged. The [AGPL-3.0](LICENSE) applies, so modifying and running your own copy
is explicitly fine. Note the network clause: if you offer a modified version to
others over a network, you must make your modified source available to those users.

If you want Coffer to go somewhere it isn't going, a fork is a better use of your
effort than a pull request here.

## What the code is held to

Not an invitation — just the standards, if you're evaluating whether this is built
carefully: [docs/engineering-standards.md](docs/engineering-standards.md) covers the
testing posture, forward-only migrations, the documentation rules, and the "no hacks"
charter. Design decisions are recorded as ADRs in
[docs/decisions/](docs/decisions/) — including the ones that were tried and
rejected, which are usually the more informative half.
