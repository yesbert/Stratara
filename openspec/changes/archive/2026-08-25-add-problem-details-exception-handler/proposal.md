> **Status:** approved

# Map framework failures to a standard HTTP problem response

## Why

The framework throws four failure types a web host has to answer for — validation, authorization,
permission and tenant-access denial — and ships a mapper for exactly one of them. Both consumers
wrote the missing half themselves, which is what put this in the backlog as **T2-11**.

Today `AuthorizationExceptionMiddleware` turns an authorization or tenant-access denial into a 403.
A validation failure gets nothing: `StrataraValidationException` was deliberately declared in the
abstractions package *so that* a consumer's global handler can catch it without depending on the
behaviour package — and then every consumer writes the same handler.

## Not a contradiction of ADR-0001

The validation decision record rejected **synthesising a `TResult`** to represent failure, because
the mediator cannot construct a meaningful instance of a consumer's own result type. It did not
reject mapping the exception at the transport boundary; it explicitly expected each consumer to do
that. Shipping an optional default mapper is the same idea with less duplication, and a consumer that
wants its own error model simply does not register it.

## The design question this change has to answer

There would then be **two** boundary mappers: the existing 403 middleware in
`Stratara.Infrastructure`, and this one. That is a worse outcome than either alone.

Options, to be settled in `design.md` before implementation:

1. The new handler subsumes the 403 mapping, and the middleware is deprecated in favour of it.
2. The new handler covers only what the middleware does not, and the two are documented as a pair.

Option 1 is cleaner and is breaking for anyone calling `UseAuthorizationExceptionTo403()`. Option 2
avoids that and leaves the framework with two answers to one question.

## Sequencing

Behind the three security changes and `compose-erasure-sweeps`.
