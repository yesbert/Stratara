#!/usr/bin/env python3
"""Check that every Stratara API symbol named in a doc actually exists in src/.

Extracts candidate symbols from markdown code fences and inline backticks, then
verifies each one is declared somewhere under src/. Reports symbols that resolve
to nothing (fabricated) and symbols that resolve only to an `internal` declaration
(documented as consumable but not nameable by a consumer).

Usage:
    scripts/check-doc-symbols.py <file-or-dir> [<file-or-dir> ...]

Exit code 1 if any unresolved symbol is found.
"""

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"

# Symbols that are real but not declared in src/: BCL, ASP.NET Core, EF Core, Polly, OTel.
EXTERNAL = {
    "AddCookie", "AddAuthentication", "AddAuthorization", "AddAuthorizationBuilder",
    "AddDbContext", "AddDbContextFactory", "AddIdentityCore", "AddEntityFrameworkStores",
    "AddSingleton", "AddScoped", "AddTransient", "AddHostedService", "AddOptions",
    "AddControllers", "AddOpenApi", "AddLogging", "AddHttpClient", "AddMemoryCache",
    "AddPolicyScheme", "AddScheme", "AddJwtBearer", "AddOpenIdConnect", "AddSerilog",
    "AddEnvironmentVariables", "AddJsonFile", "AddUserSecrets", "AddInMemoryCollection",
    "AddConsoleExporter", "AddOtlpExporter", "AddSource", "AddMeter", "AddService",
    "MapGet", "MapPost", "MapPut", "MapDelete", "MapControllers", "MapOpenApi",
    "MapHealthChecks", "MapFallbackToFile", "MapRazorPages", "MapHub",
    "IServiceCollection", "IServiceProvider", "IConfiguration", "IHostApplicationBuilder",
    "WebApplication", "WebApplicationBuilder", "IHostedService", "BackgroundService",
    "DbContext", "DbContextOptions", "ModelBuilder", "IdentityDbContext", "IdentityUser",
    "UserManager", "SignInManager", "ClaimsPrincipal", "ClaimTypes", "IdentityConstants",
    "Tracer", "TracerProvider", "ActivitySource", "Meter", "ILogger", "LoggerMessage",
    "CancellationToken", "Task", "ValueTask", "Guid", "IReadOnlyList", "IReadOnlySet",
    "TimeProvider", "IOptions", "ServiceBusClient", "DefaultAzureCredential",
    "CryptographicException", "AuthenticationTagMismatchException", "AesGcm",
    "InvalidOperationException", "ArgumentException", "UnauthorizedAccessException",
    "JsonSerializer", "JsonNode", "SHA256", "IncrementalHash",
    "AddHealthChecks", "IHealthChecksBuilder", "AddLocalization", "UseRequestLocalization",
    "UseMiddleware", "AddRedis", "AddRedisClient", "AddRedisInstrumentation",
    "IConnectionMultiplexer", "IEmailSender", "IStringLocalizer", "IAsyncDisposable",
    "AddHttpContextAccessor", "IUserClaimsPrincipalFactory", "AddAsync",
    "UseExceptionHandler", "AddProblemDetails", "AddExceptionHandler", "ProblemDetails",
    "ValidationProblemDetails", "IExceptionHandler",
}

# Placeholder tokens and sample-local type names that legitimately appear in docs
# but are not Stratara framework surface. Kept explicit so the CI gate stays meaningful.
DOC_LOCAL = {
    "AddXxxWorkerServices",   # literal placeholder in a "pick your worker" sentence
    "MapAccountEndpoints",    # sample-local endpoint-group method
}

# Symbols that existed and no longer do, named by a migration note so a consumer arriving from the
# version that had them finds the sentence that retires them. The gate is about calls a reader would
# copy verbatim; a note reading "removed in 4.0.0, call X instead" is the opposite of that. An entry
# earns its place only next to a doc sentence that retires it — without one, delete the entry rather
# than let it excuse a genuinely fabricated call.
RETIRED = {
    "AddNpsqlWriteDbContextFactory",   # misspelling of AddNpgsqlWriteDbContextFactory; renamed in 3.2.0, removed in 4.0.0
    "UseAuthorizationExceptionTo403",  # bare-403 middleware superseded by AddStrataraProblemDetails; removed in 4.0.0
}

DECL = re.compile(
    r"\b(?:class|interface|record|struct|enum|delegate)\s+(\w+)"
    r"|\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?"
    r"[\w<>\[\],\.\?\s]+?\s(\w+)\s*(?:<[^(]*>)?\s*\("
)


def declarations():
    """Map symbol -> set of visibilities it is declared with, across src/."""
    out = {}
    for cs in SRC.rglob("*.cs"):
        if "/obj/" in str(cs) or "/bin/" in str(cs):
            continue
        try:
            text = cs.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for line in text.splitlines():
            s = line.strip()
            if s.startswith("///") or s.startswith("//"):
                continue
            m = re.search(r"\b(class|interface|record|struct|enum)\s+(\w+)", s)
            if m:
                vis = "internal" if re.search(r"\binternal\b", s) else "public"
                out.setdefault(m.group(2), set()).add(vis)
            # `static` is optional: C# 14 extension blocks declare members without it.
            m2 = re.search(r"\b(public|internal)\s+(?:static\s+)?[\w<>\[\],\.\?]+\s+(\w+)\s*(?:<[^(]*>)?\s*\(", s)
            if m2:
                out.setdefault(m2.group(2), set()).add(m2.group(1))
            m3 = re.search(r"\b(public|internal)\s+(?:readonly\s+)?(?:record\s+struct|record|class|interface)\s+(\w+)", s)
            if m3:
                out.setdefault(m3.group(2), set()).add(m3.group(1))
    return out


def candidates(text):
    """Stratara-shaped API symbols mentioned in the doc."""
    found = set()
    # Add*/Map*/Use* extension calls, with or without generics.
    for m in re.finditer(r"\b((?:Add|Map|Use)[A-Z]\w+)\s*(?:<[^>(]*>)?\s*\(", text):
        found.add(m.group(1))
    # Stratara-ish type names in backticks.
    for m in re.finditer(r"`([A-Z]\w+(?:<[^`>]*>)?)`", text):
        name = m.group(1).split("<")[0]
        if re.match(r"^(I?[A-Z][a-z]+)", name) and len(name) > 3:
            found.add(name)
    return found


def main():
    targets = []
    for arg in sys.argv[1:]:
        p = Path(arg)
        if p.is_dir():
            targets.extend(sorted(p.rglob("*.md")))
        elif p.suffix == ".md":
            targets.append(p)
    if not targets:
        print("usage: check-doc-symbols.py <file-or-dir> ...", file=sys.stderr)
        return 2

    decls = declarations()
    failures = 0
    for t in targets:
        text = t.read_text(encoding="utf-8", errors="ignore")
        missing, internal_only = [], []
        for sym in sorted(candidates(text)):
            if sym in EXTERNAL or sym in DOC_LOCAL or sym in RETIRED:
                continue
            vis = decls.get(sym)
            if vis is None:
                # A documented extension call that resolves to nothing is a hard failure:
                # readers copy these verbatim. Bare type mentions are too noisy to gate on
                # (enum members, sample-local types), so only fail on Add*/Map*/Use* calls.
                if re.match(r"^(Add|Map|Use)[A-Z]", sym):
                    missing.append(sym)
            elif vis == {"internal"}:
                internal_only.append(sym)
        if missing or internal_only:
            try:
                rel = t.resolve().relative_to(ROOT)
            except ValueError:
                rel = t
            print(f"\n{rel}")
            for s in missing:
                print(f"  NOT IN src/   {s}   <-- fabricated extension call (gate fails)")
                failures += 1
            for s in internal_only:
                # Informational only: an internal type mentioned in a doc is often a legitimate
                # description of a runtime component, not a wire-up instruction. Not gated.
                print(f"  note: internal  {s}")
    print(f"\n{failures} fabricated extension call(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
