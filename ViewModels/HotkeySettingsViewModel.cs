using KaneCode.Infrastructure;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace KaneCode.ViewModels;

/// <summary>
/// View model for the Hotkeys settings page. Exposes the list of bindings
/// and supports recording a new gesture for a selected action.
/// </summary>
internal sealed class HotkeySettingsViewModel : ObservableObject
{
    private HotkeyBinding? _selectedBinding;
    private bool _isRecording;
    private string _recordingText = string.Empty;

    public HotkeySettingsViewModel()
    {
        Bindings = new ObservableCollection<HotkeyBinding>(HotkeyManager.Bindings);
        ResetSelectedCommand = new RelayCommand(_ => ResetSelected(), _ => SelectedBinding is not null);
        ResetAllCommand = new RelayCommand(_ => ResetAll());
        ClearSelectedCommand = new RelayCommand(_ => ClearSelected(), _ => SelectedBinding is not null);
    }

    public ObservableCollection<HotkeyBinding> Bindings { get; }

    public HotkeyBinding? SelectedBinding
    {
        get => _selectedBinding;
        set
        {
            if (SetProperty(ref _selectedBinding, value))
            {
                StopRecording();
            }
        }
    }

    /// <summary>
    /// Whether the UI is currently capturing a key press to assign.
    /// </summary>
    public bool IsRecording
    {
        get => _isRecording;
        private set => SetProperty(ref _isRecording, value);
    }

    /// <summary>
    /// Text shown during recording (e.g. "Press a key combination...").
    /// </summary>
    public string RecordingText
    {
        get => _recordingText;
        private set => SetProperty(ref _recordingText, value);
    }

    public ICommand ResetSelectedCommand { get; }
    public ICommand ResetAllCommand { get; }
    public ICommand ClearSelectedCommand { get; }

    /// <summary>
    /// Enters recording mode for the currently selected binding.
    /// </summary>
    public void StartRecording()
    {
        if (SelectedBinding is null)
        {
            return;
        }

        IsRecording = true;
        RecordingText = "Press a key combination...";
    }

    /// <summary>
    /// Exits recording mode without applying changes.
    /// </summary>
    public void StopRecording()
    {
        IsRecording = false;
        RecordingText = string.Empty;
    }

    /// <summary>
    /// Applies a captured key gesture to the selected binding.
    /// Returns true if the gesture was applied.
    /// </summary>
    public bool ApplyRecordedGesture(Key key, ModifierKeys modifiers)
    {
        if (!IsRecording || SelectedBinding is null)
        {
            return false;
        }

        // Ignore bare modifier keys — wait for the actual key
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System)
        {
            RecordingText = HotkeyBinding.FormatGesture(modifiers, Key.None) + "...";
            return false;
        }

        // Escape cancels recording
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            return true;
        }

        HotkeyManager.SetGesture(SelectedBinding.Action, key, modifiers);
        StopRecording();
        return true;
    }

    private void ResetSelected()
    {
        if (SelectedBinding is null)
        {
            return;
        }

        HotkeyManager.ResetToDefault(SelectedBinding.Action);
    }

    private void ResetAll()
    {
        HotkeyManager.ResetAllToDefaults();
    }

    private void ClearSelected()
    {
        if (SelectedBinding is null)
        {
            return;
        }

        HotkeyManager.SetGesture(SelectedBinding.Action, Key.None, ModifierKeys.None);
    }
}
