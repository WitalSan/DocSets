using System.Drawing;
using System.Windows.Forms;

namespace DocSets
{
    public sealed class PromptDialog : Form
    {
        private readonly TextBox _valueTextBox;

        private PromptDialog(string caption, string label, string value)
        {
            Text = caption ?? "DocSets";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(420, 122);

            var labelControl = new Label
            {
                AutoSize = true,
                Location = new Point(12, 14),
                Text = label ?? string.Empty
            };
            _valueTextBox = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(15, 38),
                Width = 390,
                Text = value ?? string.Empty
            };
            var okButton = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
                Location = new Point(249, 82),
                Size = new Size(75, 27),
                Text = "OK"
            };
            var cancelButton = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
                Location = new Point(330, 82),
                Size = new Size(75, 27),
                Text = "Отмена"
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(labelControl);
            Controls.Add(_valueTextBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
        }

        public static string Ask(
            IWin32Window owner, string caption, string label, string value = "")
        {
            using (var dialog = new PromptDialog(caption, label, value))
            {
                dialog.Shown += (_, __) =>
                {
                    dialog._valueTextBox.SelectAll();
                    dialog._valueTextBox.Focus();
                };
                return dialog.ShowDialog(owner) == DialogResult.OK
                    ? dialog._valueTextBox.Text
                    : null;
            }
        }
    }
}
