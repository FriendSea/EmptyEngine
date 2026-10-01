namespace EmptyEngine.Editor.ViewModels.Build;

/// <summary>デプロイ対象シーンリストの 1 項目</summary>
public sealed class BuildSceneItemViewModel : ViewModelBase
{
    private string _displayName;
    private bool _isStartup;

    public BuildSceneItemViewModel(string key, string displayName)
    {
        Key = key;
        _displayName = displayName;
    }

    /// <summary>シーンアセットキー（<c>&lt;guid&gt;.scene</c>）</summary>
    public string Key { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value)) OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>このシーンがリスト先頭＝起動シーンか</summary>
    public bool IsStartup
    {
        get => _isStartup;
        set
        {
            if (SetProperty(ref _isStartup, value)) OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>リスト表示用ラベル</summary>
    public string Label => _isStartup ? $"▶ {_displayName}" : _displayName;
}
