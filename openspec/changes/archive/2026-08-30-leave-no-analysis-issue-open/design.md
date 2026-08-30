## Context

See `proposal.md` — *Why*. Two things shape how the work is done rather than what it covers.

**The analyser's own suppression list is inert.** `.github/workflows/sonar.yml` passes
`sonar.issue.ignore.multicriteria` with four entries, one of them `CA1859` scoped to `**`, and a
`CA1859` finding is in the report anyway. The list was carried over unchanged from the pipeline that
preceded it, so it has probably never worked. Nothing here may depend on it until somebody proves it
does.

**Most of the findings are in tests.** Roughly forty files, and the changes are small and repetitive.
The risk is not difficulty, it is a careless sweep: a mechanical edit repeated thirty times is
exactly where a test quietly stops testing.

## Goals / Non-Goals

**Goals:**

- The quality gate shows nothing but the six deprecation reminders.
- Every suppression carries its reason at the place it applies.
- Every edited test still fails when the thing it covers breaks.

**Non-Goals:**

- Changing the server's new-code period. The owner's answer to "which issues count" is "all of them".
- Removing the deprecated API. That is `4.0.0` work, named in the proposal.
- Chasing the rules Sonar does not currently report. This change closes what is open, not everything
  that could ever be raised.

## Decisions

### Fix by default, suppress by exception

An analyser finding is a claim that something could be clearer, and clearer is usually worth the
edit. Suppression is for the two cases where the rule is wrong about this code: it misreads text as
a credential, or it contradicts a rule the project holds deliberately.

*Alternative rejected.* Suppressing broadly by rule id would reach zero faster and teach the next
reader nothing — and on the evidence above it would not even work.

### Suppress in source, not in analyser configuration

`[SuppressMessage]` with a justification, at the member it applies to.

*Alternatives rejected.* The `multicriteria` list does not demonstrably work and holds the reason
three files away from the code. Marking an issue "won't fix" on the server puts the reasoning
somewhere the repository cannot see and a fresh analysis of a new branch does not inherit.

*Consequence:* a reader of `RabbitMqBus` sees why the analyser was overruled without leaving the
file.

### The "does not throw" tests get an explicit assertion rather than an exemption

`Assert.Null(await Record.ExceptionAsync(() => …))` is the xUnit-idiomatic spelling, and it turns an
implicit claim into a written one. The tests were not wrong; they were quiet.

*Alternative rejected.* Exempting `S2699` for `tests/**` would silence the rule everywhere,
including on a test that genuinely asserts nothing — which is a defect worth catching.

### `CA1859` is examined before it is suppressed

It fires on a **private** helper, where the project's `IReadOnlyList<T>` rule — which is about public
surfaces — does not obviously apply. The proposal assumed a conflict of conventions; that assumption
needs checking against the call site before it becomes a suppression. If the concrete type flows in
unchanged, the fix is a parameter type.

## Risks / Trade-offs

**A mechanical edit repeated thirty times stops a test from testing.** → `Assert.Single(x)` returns
the element, so the substitution is total rather than a deletion. The suite runs after each group,
and the gauntlet before the pull request.

**The gate stays red anyway, because of the six reminders.** → That is the intended state and the
proposal says so. A gate at six with a named reason is not the same as a gate at fifty-one with none.

**A rule fires on the replacement.** → `[GeneratedRegex]` requires a `partial` method in a `partial`
type and .NET's own analyser is strict about it; if a conversion is awkward, leaving that regex alone
and saying so beats a contorted one.

## Migration Plan

None. No consumer-visible surface changes and no version bump follows.
