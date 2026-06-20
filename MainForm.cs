using System;
using System.Drawing;
using System.Windows.Forms;

class MainForm : Form
{
    private readonly TextBox _nTextBox;
    private readonly TextBox _mTextBox;
    private readonly TextBox _kTextBox;
    private readonly TextBox _resultTextBox;
    private readonly Button _runButton;

    public MainForm()
    {
        Text = "Top-K Strategy Runner";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 700);
        Size = new Size(1000, 760);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var inputPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        _nTextBox = CreateInputTextBox("10");
        _mTextBox = CreateInputTextBox("3");
        _kTextBox = CreateInputTextBox("3");
        _runButton = new Button
        {
            Text = "Run",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(12, 18, 0, 0),
        };
        _runButton.Click += (_, _) => RunStrategy();

        inputPanel.Controls.Add(CreateLabeledInput("n", _nTextBox));
        inputPanel.Controls.Add(CreateLabeledInput("m", _mTextBox));
        inputPanel.Controls.Add(CreateLabeledInput("k", _kTextBox));
        inputPanel.Controls.Add(_runButton);

        var hintLabel = new Label
        {
            AutoSize = true,
            Text = "Enter n, m, k and click Run to generate the strategy output.",
            Margin = new Padding(0, 0, 0, 8),
        };

        _resultTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 10),
            WordWrap = false,
        };

        layout.Controls.Add(inputPanel, 0, 0);
        layout.Controls.Add(hintLabel, 0, 1);
        layout.Controls.Add(_resultTextBox, 0, 2);
        Controls.Add(layout);

        AcceptButton = _runButton;
    }

    private static Control CreateLabeledInput(string labelText, TextBox textBox)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 12, 0),
        };

        panel.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        });
        panel.Controls.Add(textBox);
        return panel;
    }

    private static TextBox CreateInputTextBox(string initialValue)
    {
        return new TextBox
        {
            Width = 80,
            Text = initialValue,
            Margin = new Padding(0, 4, 0, 0),
        };
    }

    private void RunStrategy()
    {
        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out string? error))
        {
            MessageBox.Show(this, error, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UseWaitCursor = true;
        _runButton.Enabled = false;

        try
        {
            _resultTextBox.Text = StrategyPrinter.Generate(n, m, k);
            _resultTextBox.SelectionStart = 0;
            _resultTextBox.SelectionLength = 0;
        }
        finally
        {
            _runButton.Enabled = true;
            UseWaitCursor = false;
        }
    }
}
