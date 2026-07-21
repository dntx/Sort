using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TopKFinder;

partial class MainForm
{
    // Read-only, borderless, selectable text box used for the live stat panels so users can
    // select and copy the metric text (a plain Label is not selectable).
    private static TextBox CreateStatTextBox(string text, Font? font = null)
    {
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
            ScrollBars = ScrollBars.None,
            TabStop = false,
            Cursor = Cursors.IBeam,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        if (font is not null)
            box.Font = font;
        box.Text = NormalizeStatLines(text);
        return box;
    }

    private static void SetStatText(TextBox box, string text)
    {
        // Skip the refresh while the user is actively selecting text in this box, so a
        // selection made mid-run is not cleared by the periodic update and can be copied.
        // Updates resume automatically once the selection is cleared or focus moves away.
        if (box.Focused && box.SelectionLength > 0)
            return;

        string normalized = NormalizeStatLines(text);
        if (box.Text != normalized)
            box.Text = normalized;
    }

    private static string NormalizeStatLines(string text)
        => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static int MeasureStatHeight(TextBox box)
        => TextRenderer.MeasureText(box.Text, box.Font).Height + 6;

    private static Control CreateLabeledInput(string labelText, Control inputControl)
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
        panel.Controls.Add(inputControl);
        return panel;
    }

    private static Panel CreateSectionPanel(string title, Control body, bool fillCell = false)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = !fillCell,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, -1, -1),
        };

        var sectionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = !fillCell,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        sectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sectionLayout.RowStyles.Add(fillCell
            ? new RowStyle(SizeType.Percent, 100f)
            : new RowStyle(SizeType.AutoSize));
        sectionLayout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Margin = Padding.Empty,
            Font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold),
        }, 0, 0);

        body.Margin = new Padding(0, 8, 0, 0);
        sectionLayout.Controls.Add(body, 0, 1);
        panel.Controls.Add(sectionLayout);
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

}
