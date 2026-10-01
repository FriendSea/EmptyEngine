using Microsoft.Extensions.DependencyInjection;

namespace EmptyEngine.Serialization;

/// <summary>コンストラクタによるアセットのインスタンス作成と依存関係の解決</summary>
internal static class InstanceActivator
{
    /// <summary><paramref name="restored"/> と同じ型の新しいインスタンスを作成する</summary>
    /// <param name="services">ctor 引数の解決に使うコンテナ。<c>null</c> なら引数なし ctor だけが通る。</param>
    public static object Create(object restored, IServiceProvider? services)
        => Create(restored, services, deep: false);

    /// <summary>契約メンバを深く複製する <see cref="Create(object, IServiceProvider)"/></summary>
    public static object CreateDeep(object source, IServiceProvider? services)
        => Create(source, services, deep: true);

    /// <summary>blob を読み込む先の実体の作成</summary>
    /// <remarks>ctor を通すので、blob にキーの無いメンバは ctor の値のまま残る</remarks>
    /// <param name="services">ctor 引数の解決に使うコンテナ。<c>null</c> なら引数なし ctor だけが通る。</param>
    public static object Construct(ITypeSerializer serializer, IServiceProvider? services)
        => serializer.Construct(services ?? NoServices.Instance);

    /// <summary><paramref name="source"/> のシリアライズ対象メンバだけの <paramref name="destination"/> への写し</summary>
    public static void CopySerializedMembers(object destination, object source)
        => TypeSerializers.For(destination.GetType()).CopyShallow(destination, source);

    private static object Create(object source, IServiceProvider? services, bool deep)
    {
        ITypeSerializer serializer = TypeSerializers.For(source.GetType());
        object instance = serializer.Construct(services ?? NoServices.Instance);
        if (deep)
            serializer.CopyDeep(instance, source);
        else
            serializer.CopyShallow(instance, source);
        return instance;
    }

    /// <summary>コンテナを渡さないホスト（テスト・依存を持たないゲーム）向けの空コンテナ</summary>
    private sealed class NoServices : IServiceProvider, IServiceProviderIsService
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProviderIsService) ? this : null;

        public bool IsService(Type serviceType) => false;
    }
}
