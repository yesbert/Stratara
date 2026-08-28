# Write an Update Handler

> **Derived page.** The behaviour described here is specified by the `update-change-tracking`
> capability under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

## An update is compared three ways, not two

The obvious way to handle an edit is to compare what the user submitted against what is stored, and
write the differences. That is wrong the moment two people edit the same record, and it is wrong in
a way nobody notices: the second save silently reverts the first.

Stratara compares **three** states:

| State | What it is |
|---|---|
| `SourceValue` | The aggregate as it stood at the version the editor started from |
| `CurrentValue` | The aggregate as it stands now |
| `ChangeValue` | What the update submits |

<!-- stratara-snippet-ignore: narrative fragment - the compared states come from the surrounding text -->
```csharp
var changes = ChangeSetBuilder<CustomerView, UpdateCustomer>
    .CreateChangeSet(source: startedFrom, current: live, changes: submitted);
```

With three states, "the editor typed this" and "the editor never touched this and is carrying a
stale copy" become distinguishable — which they are not with two.

| Situation | Outcome |
|---|---|
| Editor changed it, nobody else did | The submitted value is taken |
| Editor did not change it, someone else did | **The current value is kept** — the stale copy does not overwrite it |
| Both changed it | The submitted value wins, and the discarded current value is reported so you can surface the conflict |
| Nothing changed | No change is produced |

The third row is the one to build UI around. The change is applied, but `CurrentValue` on that
`ChangeDetail` tells you what you overwrote — enough to show "this field was also changed by someone
else while you were editing" instead of losing it silently.

Only changed fields become events: one event per changed field, and nothing at all when the change
set is empty. An update that changes nothing writes nothing.

## Only properties present on both sides participate

This is the constraint that will surprise you, so it is worth stating plainly.

A property takes part in the comparison **only** where it exists on both the submitted values and
the aggregate, with compatible types and a settable target. Everything else is ignored — silently,
by design.

| Case | What happens |
|---|---|
| The update carries a property the aggregate does not declare | Ignored |
| The aggregate declares a property the update does not carry | Left alone — **an absent field is not an instruction to clear it** |
| The property exists on both sides with incompatible types | Skipped |
| The property exists on both sides but cannot be written | Skipped |

The practical consequence: **a typo in a property name is not an error.** Rename `PostalCode` to
`ZipCode` on your update DTO and forget the aggregate, and that field simply stops being updated.
No exception, no warning, no event — the save succeeds and does less than you asked. If a field
mysteriously refuses to change, check the names and types on both sides before anything else.

## Absent is a value, not a shrug

A property whose value is absent takes part in the comparison like any other. Clearing a field *is*
a change; it is not read as "no opinion".

That means a partial-update DTO where unset properties are meant to signal "leave this alone" does
**not** work: an unset property compares as absent, and if the aggregate had a value there, that is
a change to clear it. If you need partial-update semantics, model presence explicitly — carry only
the properties the caller actually sent, rather than a full DTO with holes in it.

A field that was absent before and is absent now produces no change, as you would expect.

## The version the editor started from

An update names the aggregate and the version it started from. That version is what makes
`SourceValue` retrievable, and it is what the three-way comparison rests on — without it there is no
"what the editor saw", and you are back to a two-way compare with all its silent reverts.

## See also

- [Write a Command Handler](write-a-command-handler.md) — where an update command is handled.
- [Use Resilience Policies](use-resilience-policies.md) — the concurrency-conflict policy, for
  handlers that re-read and re-apply on a version clash.
- [Write a Projection](write-a-projection.md) — the read model the per-field events feed.
