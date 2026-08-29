using EasyWindowsTerminalControl;
using EasyWindowsTerminalControl.Internals;
using KaneCode.Services;
using Microsoft.Terminal.Wpf;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KaneCode.Controls;

/// <summary>Hosts a full interactive PowerShell terminal backed by Windows ConPTY.</summary>
public partial class PowerShellPanel : UserControl, IDisposable
{
    private readonly object _sessionLock = new();
    private readonly IProcessFactory _processFactory = new ConPtyProcessFactory();
    private TermPTY? _session;
    private DispatcherTerminalConnection? _connection;
    private string _workingDirectory = Environment.CurrentDirectory;
    private bool _hasActivated;
    private bool _disposed;

    public PowerShellPanel()
    {
        InitializeComponent();
    }

    public void SetWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        _workingDirectory = Path.GetFullPath(workingDirectory);
        if (_hasActivated && IsVisible)
        {
            RestartSession();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsVisibleChanged -= PowerShellPanel_IsVisibleChanged;
        PreviewMouseDown -= PowerShellPanel_PreviewMouseDown;
        StopSession();
        GC.SuppressFinalize(this);
    }

    private void PowerShellPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed || e.NewValue is not true)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_disposed || !IsVisible)
            {
                return;
            }

            TerminalHost.UpdateLayout();
            ApplyTheme();
            if (!_hasActivated)
            {
                _hasActivated = true;
                StartSession();
            }
            else
            {
                FocusTerminal();
            }
        });
    }

    private void StartSession()
    {
        int columns = Math.Max(TerminalHost.Columns, 20);
        int rows = Math.Max(TerminalHost.Rows, 5);
        TermPTY session = new TermPTY();
        DispatcherTerminalConnection connection = new DispatcherTerminalConnection(session, Dispatcher);

        session.TermReady += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_disposed || !ReferenceEquals(_session, session))
            {
                StopSession(session);
                return;
            }

            TerminalHost.Connection = connection;
            TerminalStatusText.Text = "ConPTY terminal - interactive console applications supported";
            session.Resize(Math.Max(TerminalHost.Columns, 20), Math.Max(TerminalHost.Rows, 5));

            // PowerShell works reliably with normal VT input. Forcing private
            // mode 9001 here left the terminal unable to deliver keystrokes
            // until a full terminal reset disabled that mode again.
            QueueTerminalFocus(session);
        });

        lock (_sessionLock)
        {
            _session = session;
            _connection = connection;
        }

        string workingDirectory = _workingDirectory;
        _ = Task.Run(() => StartSession(session, columns, rows, workingDirectory));
    }

    private void StartSession(TermPTY session, int columns, int rows, string workingDirectory)
    {
        try
        {
            session.Start(
                "powershell.exe -NoLogo",
                columns,
                rows,
                false,
                _processFactory,
                workingDirectory);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"PowerShell terminal failed to start: {exception}");
            Dispatcher.BeginInvoke(() => ShowStartupFailure(session, exception));
        }
    }

    private void ShowStartupFailure(TermPTY session, Exception exception)
    {
        if (_disposed || !ReferenceEquals(_session, session))
        {
            return;
        }

        TerminalStatusText.Text = $"PowerShell failed to start: {exception.Message}";
    }

    private void RestartSession()
    {
        StopSession();
        ApplyTheme();
        StartSession();
    }

    private void StopSession()
    {
        TermPTY? session;
        DispatcherTerminalConnection? connection;
        lock (_sessionLock)
        {
            session = _session;
            connection = _connection;
            _session = null;
            _connection = null;
        }

        connection?.Dispose();
        StopSession(session);
    }

    private static void StopSession(TermPTY? session)
    {
        try
        {
            session?.CloseStdinToApp();
            session?.StopExternalTermOnly();
        }
        catch (InvalidOperationException)
        {
            // The terminal process exited while it was being stopped.
        }
    }

    private void ApplyTheme()
    {
        TerminalHost.SetTheme(CreateTerminalTheme(), "Cascadia Mono", 13, Color.FromRgb(12, 12, 12));
    }

    private static TerminalTheme CreateTerminalTheme()
    {
        return new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(Color.FromRgb(12, 12, 12)),
            DefaultForeground = EasyTerminalControl.ColorToVal(Color.FromRgb(204, 204, 204)),
            DefaultSelectionBackground = EasyTerminalControl.ColorToVal(Color.FromRgb(38, 79, 120)),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
            ]
        };
    }

    private void PowerShellPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusTerminal();
    }

    private void TerminalHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e.Key != Key.Tab)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != ModifierKeys.None)
        {
            return;
        }

        DispatcherTerminalConnection? connection;
        lock (_sessionLock)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return;
        }

        string input = modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t";
        try
        {
            connection.WriteInput(input);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // The session was restarted between retrieving the connection and
            // sending the key. The replacement session will receive focus when ready.
        }
    }

    private void FocusTerminal()
    {
        TerminalHost.Focus();
        Keyboard.Focus(TerminalHost);
    }

    private void QueueTerminalFocus(TermPTY session)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_disposed || !IsVisible || !ReferenceEquals(_session, session))
            {
                return;
            }

            FocusTerminal();
        });
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _session?.ClearUITerminal();
        FocusTerminal();
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        RestartSession();
    }

    private sealed class DispatcherTerminalConnection : ITerminalConnection, IDisposable
    {
        private readonly TermPTY _inner;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        public DispatcherTerminalConnection(TermPTY inner, Dispatcher dispatcher)
        {
            _inner = inner;
            _dispatcher = dispatcher;
            _inner.TerminalOutput += Inner_TerminalOutput;
        }

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public void Start() => ((ITerminalConnection)_inner).Start();

        public void WriteInput(string data) => ((ITerminalConnection)_inner).WriteInput(data);

        public void Resize(uint rows, uint columns) => ((ITerminalConnection)_inner).Resize(rows, columns);

        public void Close() => ((ITerminalConnection)_inner).Close();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.TerminalOutput -= Inner_TerminalOutput;
        }

        private void Inner_TerminalOutput(object? sender, TerminalOutputEventArgs e)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => TerminalOutput?.Invoke(this, e));
        }
    }
}
