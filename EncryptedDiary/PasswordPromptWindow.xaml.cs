using System.Windows;

namespace EncryptedDiary;

public partial class PasswordPromptWindow : Window
{
    private readonly bool _existingVault;

    public PasswordPromptWindow(bool existingVault)
    {
        InitializeComponent();
        _existingVault = existingVault;

        if (_existingVault)
        {
            Title = "Unlock Diary";
            PromptText.Text = "Enter your master password to unlock your encrypted diary.";
            ConfirmPanel.Visibility = Visibility.Collapsed;
            ActionButton.Content = "Unlock";
        }
        else
        {
            Title = "Create Diary Password";
            PromptText.Text = "Create a master password for your new encrypted diary vault.";
            ConfirmPanel.Visibility = Visibility.Visible;
            ActionButton.Content = "Create Vault";
        }

        Loaded += (_, _) => PrimaryPasswordBox.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        string primary = PrimaryPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(primary))
        {
            MessageBox.Show(this, "Password cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_existingVault && primary != ConfirmPasswordBox.Password)
        {
            MessageBox.Show(this, "Passwords do not match.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Password = primary;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
