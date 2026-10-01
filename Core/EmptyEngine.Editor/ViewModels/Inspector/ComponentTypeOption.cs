namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>インスペクタの Add Component 候補</summary>
/// <param name="TypeName">カタログのキー（名前空間つき）</param>
/// <param name="Name">カタログの title</param>
public sealed record ComponentTypeOption(string TypeName, string Name, Func<AuthoringObject> Factory);
