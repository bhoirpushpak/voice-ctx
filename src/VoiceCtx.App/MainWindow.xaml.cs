using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace VoiceCtx.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Switches to the Type to speak tab and puts the cursor in the text box.</summary>
    public void FocusTypeToSpeak()
    {
        TypeToSpeakTab.IsSelected = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SpeakBox.Focus();
            Keyboard.Focus(SpeakBox);
        });
    }

    private void OnSpeakBoxKeyDown(object sender, KeyEventArgs e)
    {
        // Enter speaks; Shift+Enter inserts a new line; Esc stops speech.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (DataContext is ViewModels.MainViewModel vm && vm.TypeToSpeak.SpeakCommand.CanExecute(null))
                vm.TypeToSpeak.SpeakCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is ViewModels.MainViewModel vm2)
        {
            vm2.TypeToSpeak.Stop();
            e.Handled = true;
        }
    }
}
