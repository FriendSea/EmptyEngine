using System.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EmptyEngine.Serialization.Generator;

/// <summary>順序と要素で等しさが決まる配列</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public bool Equals(EquatableArray<T> other)
    {
        T[] left = _items ?? Array.Empty<T>();
        T[] right = other._items ?? Array.Empty<T>();
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in _items ?? Array.Empty<T>())
            hash = unchecked((hash * 31) + item.GetHashCode());
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>報告する診断 1 件</summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    private readonly DiagnosticDescriptor _descriptor;
    private readonly string? _filePath;
    private readonly TextSpan _span;
    private readonly LinePositionSpan _lineSpan;
    private readonly string _typeName;
    private readonly string _reason;

    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location? location, string typeName, string reason)
    {
        _descriptor = descriptor;
        _typeName = typeName;
        _reason = reason;
        if (location is { } present && present.Kind == LocationKind.SourceFile)
        {
            _filePath = present.SourceTree?.FilePath;
            _span = present.SourceSpan;
            _lineSpan = present.GetLineSpan().Span;
        }
    }

    public Diagnostic ToDiagnostic()
    {
        Location? location = _filePath is null ? null : Location.Create(_filePath, _span, _lineSpan);
        return Diagnostic.Create(_descriptor, location, _typeName, _reason);
    }

    public bool Equals(DiagnosticInfo? other) =>
        other is not null
        && ReferenceEquals(_descriptor, other._descriptor)
        && _filePath == other._filePath
        && _span == other._span
        && _lineSpan.Equals(other._lineSpan)
        && _typeName == other._typeName
        && _reason == other._reason;

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode()
    {
        int hash = _descriptor.Id.GetHashCode();
        hash = unchecked((hash * 31) + (_filePath?.GetHashCode() ?? 0));
        hash = unchecked((hash * 31) + _span.GetHashCode());
        hash = unchecked((hash * 31) + _typeName.GetHashCode());
        hash = unchecked((hash * 31) + _reason.GetHashCode());
        return hash;
    }
}

/// <summary>型の解析から得られた生成コードと診断</summary>
internal sealed class GeneratorOutput
{
    public GeneratorOutput(string source, EquatableArray<DiagnosticInfo> diagnostics)
    {
        Source = source;
        Diagnostics = diagnostics;
    }

    /// <summary>生成されたコンパイル単位（生成対象が無ければ空文字）</summary>
    public string Source { get; }

    public EquatableArray<DiagnosticInfo> Diagnostics { get; }
}
