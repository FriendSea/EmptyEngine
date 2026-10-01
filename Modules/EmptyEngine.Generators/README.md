# EmptyEngine.Generators

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This package is part of an example .NET implementation of a runtime for use with EmptyEngine.

A source generator that produces an asset reference property and its resolution code from private fields marked with `[ResolveAsset]`.

```cs
public partial class Enemy
{
    // Generates a Data property and the code that resolves it into _data
    [ResolveAsset]
    private EnemyData? _data;
}
```
