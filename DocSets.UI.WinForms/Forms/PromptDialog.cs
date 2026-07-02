using System.Windows;
using System.Windows.Controls;

namespace DocSets
{
    internal sealed class PromptDialog : Window
    {
        private readonly TextBox textBox;

        public string Value => textBox.Text;

        public PromptDialog(string title, string label, string value = "")
        {
            Title = title;
            Width = 360;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            textBox = new TextBox { Text = value, MinWidth = 320 };
            Grid.SetRow(textBox, 1);
            root.Children.Add(textBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (_, __) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", Width = 75, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, __) => { textBox.Focus(); textBox.SelectAll(); };
        }

        public static string Ask(Window owner, string title, string label, string value = "")
        {
            var dialog = new PromptDialog(title, label, value) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Value?.Trim() : null;
        }
    }
}
