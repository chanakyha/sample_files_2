using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Translator.Services;

namespace Translator
{
    /// <summary>Shows the network checks for the translation service in a table.</summary>
    public class ConnectionCheckForm : Form
    {
        private readonly DataGridView _grid;
        private readonly Button _runButton;
        private readonly Label _statusLabel;

        public ConnectionCheckForm()
        {
            Text = "Check Connection";
            Width = 1000;
            Height = 450;
            StartPosition = FormStartPosition.CenterParent;

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.Columns.Add("Check", "Check");
            _grid.Columns.Add("Status", "Status");
            _grid.Columns.Add("Details", "Details");
            _grid.Columns.Add("Meaning", "What it means");
            _grid.Columns["Check"].FillWeight = 18;
            _grid.Columns["Status"].FillWeight = 10;
            _grid.Columns["Details"].FillWeight = 36;
            _grid.Columns["Meaning"].FillWeight = 36;

            _statusLabel = new Label { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };

            _runButton = new Button { Text = "Run again", Dock = DockStyle.Right, Width = 100 };
            _runButton.Click += async (s, e) => await RunChecksAsync();

            var closeButton = new Button { Text = "Close", Dock = DockStyle.Right, Width = 100, DialogResult = DialogResult.Cancel };
            CancelButton = closeButton;

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 35 };
            bottom.Controls.Add(_statusLabel);
            bottom.Controls.Add(_runButton);
            bottom.Controls.Add(closeButton);

            Controls.Add(_grid);
            Controls.Add(bottom);

            Shown += async (s, e) => await RunChecksAsync();
        }

        private async Task RunChecksAsync()
        {
            _runButton.Enabled = false;
            UseWaitCursor = true;
            _statusLabel.Text = "Running checks...";
            _grid.Rows.Clear();

            try
            {
                var results = await ConnectionChecker.RunAsync();
                foreach (var r in results)
                    _grid.Rows.Add(r.Check, r.Status, r.Details, r.Meaning);

                _statusLabel.Text = "Checked at " + DateTime.Now.ToString("T");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Checks failed: " + ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
                _runButton.Enabled = true;
            }
        }
    }
}
