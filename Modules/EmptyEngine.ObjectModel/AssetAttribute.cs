namespace EmptyEngine.ObjectModel;

/// <summary>アセットとして自己記述する型のマーカー</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AssetAttribute : Attribute;
