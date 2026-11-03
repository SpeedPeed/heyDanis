using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HeyDanis.App;

/// <summary>
/// Small input box (Strg+Alt+Leertaste) to type a question instead of speaking it.
/// Opens next to the mouse, sends on Enter, closes on Esc or when it loses focus.
/// </summary>
public partial class TextQuestionWindow : Window
{
    /// <summary>Raised with the typed question after the window has been hidden.</summary>
    public event EventHandler<string>? QuestionSubmitted;

    private bool _isClosingOrSubmitting;

    public TextQuestionWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => CloseQuietly();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlaceNearMouse();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        BringToFront();
        QuestionBox.Focus();
        Keyboard.Focus(QuestionBox);
    }

    private void QuestionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseQuietly();
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+Enter: new line instead of sending.
            e.Handled = true;
            var caret = QuestionBox.CaretIndex;
            QuestionBox.Text = QuestionBox.Text.Insert(caret, Environment.NewLine);
            QuestionBox.CaretIndex = caret + Environment.NewLine.Length;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var question = QuestionBox.Text.Trim();
            if (question.Length == 0)
            {
                CloseQuietly();
                return;
            }

            _isClosingOrSubmitting = true;
            // Hide first so the box is not on the screenshot that goes to Claude.
            Hide();
            QuestionSubmitted?.Invoke(this, question);
            Close();
        }
    }

    private void CloseQuietly()
    {
        if (_isClosingOrSubmitting) return;
        _isClosingOrSubmitting = true;
        Close();
    }

    /// <summary>Positions the box just below/right of the mouse, kept inside the screen.</summary>
    private void PlaceNearMouse()
    {
        GetCursorPos(out var cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = cursor.X / dpi.DpiScaleX + 16;
        var top = cursor.Y / dpi.DpiScaleY + 16;

        var screenRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Left = Math.Min(left, screenRight - Width - 8);
        Top = Math.Min(top, screenBottom - 140);
    }

    /// <summary>
    /// Windows only lets the foreground app hand out focus. Attaching to the foreground
    /// thread's input queue for a moment lets our box take focus so the user can type
    /// immediately.
    /// </summary>
    private void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        var ourThread = GetCurrentThreadId();

        if (foregroundThread != ourThread)
        {
            AttachThreadInput(ourThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(ourThread, foregroundThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
        Activate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
