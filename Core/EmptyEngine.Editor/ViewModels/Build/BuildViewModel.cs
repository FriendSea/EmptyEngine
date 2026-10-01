using System.ComponentModel;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Editor.State;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.ViewModels.Build;

/// <summary>ビルドの今の様子</summary>
public enum BuildPhase
{
    /// <summary>まだ押されていない／前回の結果を出し終えた</summary>
    Idle,

    /// <summary>走っている（取り込み・publish・デプロイのいずれか）</summary>
    Running,

    /// <summary>配布物ができた</summary>
    Succeeded,

    /// <summary>途中で落ちた</summary>
    Failed,

    /// <summary>配布ビルド非対応</summary>
    Unavailable,
}

/// <summary>配布ビルドの操作口（Build ペイン）</summary>
public sealed class BuildViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private readonly DistributionPipeline _pipeline;
    private readonly EditorStateStore _stateStore;
    private readonly ILogger _logger;

    private BuildTarget? _selectedTarget;
    private string _outputDirectory;
    private bool _isBuilding;
    private string _status;
    private BuildPhase _phase;

    public BuildViewModel(
        EditorViewModel editor,
        DistributionPipeline pipeline,
        EditorStateStore stateStore,
        ILogger<BuildViewModel> logger)
    {
        _editor = editor;
        _pipeline = pipeline;
        _stateStore = stateStore;
        _logger = logger;

        _selectedTarget = pipeline.FindTarget(stateStore.State.BuildTarget) ?? Targets.FirstOrDefault();
        _outputDirectory = RememberedOutput(_selectedTarget);

        bool available = pipeline.CanBuild;
        _phase = available ? BuildPhase.Idle : BuildPhase.Unavailable;
        _status = available
            ? "Not run yet"
            : "This project does not support distribution builds (nothing is declared in *.Editor.targets).";

        _editor.PropertyChanged += OnEditorChanged;
    }

    /// <summary>宣言されているビルド（宣言の並び順）</summary>
    public IReadOnlyList<BuildTarget> Targets => _pipeline.Targets;

    /// <summary>いま選んでいるビルド</summary>
    public BuildTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;

            OutputDirectory = RememberedOutput(value);
            Persist();
        }
    }

    /// <summary>選んでいるビルドの名前</summary>
    public string SelectedTargetName
    {
        get => _selectedTarget?.Name ?? string.Empty;
        set
        {
            if (_pipeline.FindTarget(value) is { } target) SelectedTarget = target;
        }
    }

    /// <summary>配布物を作る場所</summary>
    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (!SetProperty(ref _outputDirectory, value)) return;

            OnPropertyChanged(nameof(CanBuild));
            OnPropertyChanged(nameof(BlockedReason));
            Persist();
        }
    }

    /// <summary>選んでいるビルドが実際に叩くコマンド（画面では説明として出す）</summary>
    public string CommandPreview => _selectedTarget?.Command ?? string.Empty;

    /// <summary>出力先ピッカーの初期位置（保存値が空ならビルドの作業ディレクトリ）</summary>
    /// <remarks>覚えている値が相対なら、実行時と同じ基準（ビルドを走らせる場所）で絶対にしてから開く</remarks>
    public string OutputPickerStart
    {
        get
        {
            string baseDirectory = _pipeline.WorkingDirectory;
            string output = _outputDirectory.Trim();
            if (output.Length == 0) return baseDirectory;

            try
            {
                return Path.GetFullPath(output, baseDirectory);
            }
            catch (ArgumentException)
            {
                return baseDirectory;
            }
        }
    }

    /// <summary>いま走っているか</summary>
    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (!SetProperty(ref _isBuilding, value)) return;

            OnPropertyChanged(nameof(CanBuild));
            OnPropertyChanged(nameof(BlockedReason));
        }
    }

    /// <summary>直近の一行</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public BuildPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    /// <summary>いま押せるか</summary>
    public bool CanBuild =>
        _pipeline.CanBuild && _selectedTarget is not null
        && !string.IsNullOrWhiteSpace(_outputDirectory) && !_isBuilding && !_editor.IsPlaying;

    /// <summary>押せない理由</summary>
    public string? BlockedReason =>
        !_pipeline.CanBuild ? "No builds are declared in *.Editor.targets"
        : _selectedTarget is null ? "Select a build"
        : string.IsNullOrWhiteSpace(_outputDirectory) ? "Specify an output directory"
        : _isBuilding ? "A build is in progress"
        : _editor.IsPlaying ? "Asset import is deferred during play mode; stop playing before building"
        : null;

    /// <summary>ビルドの開始</summary>
    public void Start()
    {
        if (!CanBuild) return;

        IsBuilding = true;
        Phase = BuildPhase.Running;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        BuildTarget target = _selectedTarget!;
        string output = OutputDirectory.Trim();

        _editor.BeginPickerSuspension();
        try
        {
            Status = "Importing assets…";
            await _editor.ImportAssetsIfChangedAsync();
            _editor.RefreshAssetViews();

            Status = $"Building… ({target.Name})";
            await Task.Run(() => _pipeline.BuildAsync(target, output));

            Phase = BuildPhase.Succeeded;
            Status = $"Done: {output}";
        }
        catch (Exception ex)
        {
            Phase = BuildPhase.Failed;
            Status = $"Failed: {ex.Message}";
            _logger.LogError(ex, "Build failed");
        }
        finally
        {
            _editor.ResumePickerSuspension();
            IsBuilding = false;
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(CanBuild));
            OnPropertyChanged(nameof(BlockedReason));
        }
    }

    private void Persist()
    {
        _stateStore.State.BuildTarget = _selectedTarget?.Name;
        if (_selectedTarget is { } target)
        {
            string output = _outputDirectory.Trim();
            if (output.Length == 0) _stateStore.State.BuildOutputs.Remove(target.Name);
            else _stateStore.State.BuildOutputs[target.Name] = output;
        }

        _stateStore.Save();
    }

    private string RememberedOutput(BuildTarget? target) =>
        target is not null && _stateStore.State.BuildOutputs.TryGetValue(target.Name, out string? saved)
            ? saved
            : string.Empty;
}
