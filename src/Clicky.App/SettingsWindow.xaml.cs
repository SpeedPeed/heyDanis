using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Clicky.Companion;

namespace Clicky.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>Raised after a successful save (bubbles from the ViewModel).</summary>
    public event EventHandler? SettingsSaved;

    private readonly bool _isFirstRun;
    private bool _savedSuccessfully;

    public SettingsWindow(SettingsViewModel viewModel, bool isFirstRun = false)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _isFirstRun = isFirstRun;
        DataContext = _viewModel;

        VersionText.Text = $"Clicky {VersionInfo.Current}";

        if (isFirstRun)
        {
            HeaderText.Text = "Welcome to Clicky \u2014 let\u2019s set up your API keys";
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.SettingsSaved += OnViewModelSettingsSaved;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSavedKeyVisibility();
        UpdateTestButtonStates();

        // Highlight missing required fields on first-run or partial config.
        if (_isFirstRun)
        {
            _viewModel.ValidateRequiredFields();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.AnthropicKeyIsSaved) or
            nameof(SettingsViewModel.OpenAiKeyIsSaved) or
            nameof(SettingsViewModel.ZaiKeyIsSaved) or
            nameof(SettingsViewModel.AssemblyAiKeyIsSaved) or
            nameof(SettingsViewModel.ElevenLabsKeyIsSaved))
        {
            Dispatcher.Invoke(UpdateSavedKeyVisibility);
        }

        if (e.PropertyName is nameof(SettingsViewModel.AnthropicTestState) or
            nameof(SettingsViewModel.OpenAiTestState) or
            nameof(SettingsViewModel.ZaiTestState) or
            nameof(SettingsViewModel.AssemblyAiTestState) or
            nameof(SettingsViewModel.ElevenLabsTestState))
        {
            Dispatcher.Invoke(UpdateTestButtonStates);
        }
    }

    private void OnViewModelSettingsSaved(object? sender, EventArgs e)
    {
        _savedSuccessfully = true;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        Dispatcher.Invoke(Close);
    }

    // -- Saved key placeholder toggle --

    private void UpdateSavedKeyVisibility()
    {
        SetSavedKeyState(AnthropicKeyBox, AnthropicSavedPlaceholder, AnthropicChangeBtn, _viewModel.AnthropicKeyIsSaved);
        SetSavedKeyState(OpenAiKeyBox, OpenAiSavedPlaceholder, OpenAiChangeBtn, _viewModel.OpenAiKeyIsSaved);
        SetSavedKeyState(ZaiKeyBox, ZaiSavedPlaceholder, ZaiChangeBtn, _viewModel.ZaiKeyIsSaved);
        SetSavedKeyState(AssemblyAiKeyBox, AssemblyAiSavedPlaceholder, AssemblyAiChangeBtn, _viewModel.AssemblyAiKeyIsSaved);
        SetSavedKeyState(ElevenLabsKeyBox, ElevenLabsSavedPlaceholder, ElevenLabsChangeBtn, _viewModel.ElevenLabsKeyIsSaved);
    }

    private static void SetSavedKeyState(
        System.Windows.Controls.PasswordBox passwordBox,
        System.Windows.Controls.TextBox placeholder,
        System.Windows.Controls.Button changeBtn,
        bool isSaved)
    {
        if (isSaved)
        {
            passwordBox.Visibility = Visibility.Collapsed;
            placeholder.Visibility = Visibility.Visible;
            changeBtn.Visibility = Visibility.Visible;
        }
        else
        {
            passwordBox.Visibility = Visibility.Visible;
            placeholder.Visibility = Visibility.Collapsed;
            changeBtn.Visibility = Visibility.Collapsed;
        }
    }

    // -- Test button state --

    private void UpdateTestButtonStates()
    {
        ApplyTestState(AnthropicTestBtn, _viewModel.AnthropicTestState, _viewModel.AnthropicTestError);
        ApplyTestState(OpenAiTestBtn, _viewModel.OpenAiTestState, _viewModel.OpenAiTestError);
        ApplyTestState(ZaiTestBtn, _viewModel.ZaiTestState, _viewModel.ZaiTestError);
        ApplyTestState(AssemblyAiTestBtn, _viewModel.AssemblyAiTestState, _viewModel.AssemblyAiTestError);
        ApplyTestState(ElevenLabsTestBtn, _viewModel.ElevenLabsTestState, _viewModel.ElevenLabsTestError);
    }

    private void ApplyTestState(System.Windows.Controls.Button btn, TestState state, string? errorMessage)
    {
        switch (state)
        {
            case TestState.None:
                btn.Content = "Test";
                btn.Foreground = (Brush)FindResource("TextSecondary");
                btn.ToolTip = null;
                btn.IsEnabled = true;
                break;
            case TestState.Testing:
                btn.Content = "...";
                btn.IsEnabled = false;
                break;
            case TestState.Success:
                btn.Content = "\u2713";
                btn.Foreground = (Brush)FindResource("Success");
                btn.ToolTip = "Key is valid";
                btn.IsEnabled = true;
                break;
            case TestState.Failure:
                btn.Content = "\u2717";
                btn.Foreground = (Brush)FindResource("Failure");
                btn.ToolTip = errorMessage ?? "Check your key";
                btn.IsEnabled = true;
                break;
        }
    }

    // -- Password box change handlers (PasswordBox doesn't support binding) --

    private void AnthropicKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.AnthropicApiKey = AnthropicKeyBox.Password;

    private void ZaiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.ZaiApiKey = ZaiKeyBox.Password;

    private void OpenAiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.OpenAiApiKey = OpenAiKeyBox.Password;

    private void AssemblyAiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.AssemblyAiApiKey = AssemblyAiKeyBox.Password;

    private void ElevenLabsKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.ElevenLabsApiKey = ElevenLabsKeyBox.Password;

    // -- Change buttons (clear saved placeholder, show password box) --

    private void AnthropicChange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearAnthropicKey();
        AnthropicKeyBox.Focus();
    }

    private void ZaiChange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearZaiKey();
        ZaiKeyBox.Focus();
    }

    private void OpenAiChange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearOpenAiKey();
        OpenAiKeyBox.Focus();
    }

    private void AssemblyAiChange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearAssemblyAiKey();
        AssemblyAiKeyBox.Focus();
    }

    private void ElevenLabsChange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearElevenLabsKey();
        ElevenLabsKeyBox.Focus();
    }

    // -- Test buttons --

    private async void AnthropicTest_Click(object sender, RoutedEventArgs e)
        => await _viewModel.TestAnthropicAsync();

    private async void ZaiTest_Click(object sender, RoutedEventArgs e)
        => await _viewModel.TestZaiAsync();

    private async void OpenAiTest_Click(object sender, RoutedEventArgs e)
        => await _viewModel.TestOpenAiAsync();

    private async void AssemblyAiTest_Click(object sender, RoutedEventArgs e)
        => await _viewModel.TestAssemblyAiAsync();

    private async void ElevenLabsTest_Click(object sender, RoutedEventArgs e)
        => await _viewModel.TestElevenLabsAsync();

    // -- Get key links --

    private void AnthropicLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://console.anthropic.com/settings/keys");

    private void ZaiLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://z.ai/manage-apikey/apikey-list");

    private void OpenAiLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://platform.openai.com/api-keys");

    private void AssemblyAiLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://www.assemblyai.com/app/account");

    private void ElevenLabsLink_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://elevenlabs.io/app/settings/api-keys");

    // -- Save / Quit --

    private void Save_Click(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // -- Helpers --

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — don't crash if the browser fails to open.
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // If the user closes the window during first-run without saving,
        // exit the app cleanly — never leave the app half-initialized.
        if (_isFirstRun && !_savedSuccessfully)
        {
            Application.Current.Shutdown();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.SettingsSaved -= OnViewModelSettingsSaved;
        base.OnClosed(e);
    }
}
