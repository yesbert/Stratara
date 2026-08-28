# Fixture

A type-level fence:

```csharp
public sealed record Widget(string Name);
```

A statement-level fence:

```csharp
var widget = new Widget("left-handed");
```

A fence in another language:

```jsonc
{ "Widgets": { "Enabled": true } }
```

<!-- stratara-snippet-ignore: fixture — an ignored fence -->
```csharp
this is not valid C# at all
```
