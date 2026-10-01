// .NET 5 で BCL に入った同名クラスの netstandard 版。
// IEqualityComparer<T> は反変なので、object 版 1 つでどの参照型のコレクションにも渡せる。
using System.Runtime.CompilerServices;

namespace System.Collections.Generic;

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj);
}
