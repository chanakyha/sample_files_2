using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;
using Translator.Services;

namespace Translator
{
    /// <summary>
    /// How this app connects to the Argos Translate server: server address and parallel requests.
    /// The server itself (load balancer, workers, languages, port) is set up and run separately, in
    /// ArgosTranslateServer\config.json + start.cmd; the app never starts, stops or configures it.
    /// </summary>
    public class ServerSettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly TextBox _urlBox;
        private readonly NumericUpDown _parallelBox;
        private readonly Label _resultLabel;
        private readonly Button _testButton;

        public ServerSettingsForm(AppSettings settings)
        {
            _settings = settings;

            Text = "Server Settings";
            Width = 640;
            Height = 270;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _urlBox = new TextBox { Dock = DockStyle.Fill, Text = settings.ServerUrl };
            layout.Controls.Add(new Label { Text = "Server address:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(_urlBox, 1, 0);

            // Client side only: how many requests this app sends at the same time
            _parallelBox = new NumericUpDown { Minimum = 0, Maximum = 128, Value = Math.Max(0, Math.Min(128, settings.ParallelRequests)), Width = 60 };
            layout.Controls.Add(new Label { Text = "Parallel requests:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(Row(_parallelBox, "0 = automatic (2 per healthy worker the load balancer reports)"), 1, 1);

            layout.Controls.Add(new Label
            {
                Text = @"The server runs separately: start it with ArgosTranslateServer\start.cmd. " +
                       @"Workers, languages and port are set in ArgosTranslateServer\config.json, not here.",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(440, 0)
            }, 1, 2);

            _testButton = new Button { Text = "Test Connection", AutoSize = true };
            _testButton.Click += async (s, e) => await TestAsync();
            layout.Controls.Add(_testButton, 0, 3);

            _resultLabel = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(440, 0) };
            layout.Controls.Add(_resultLabel, 1, 3);

            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            okButton.Click += (s, e) =>
            {
                _settings.ServerUrl = _urlBox.Text.Trim();
                _settings.ParallelRequests = (int)_parallelBox.Value;
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);

            Controls.Add(layout);
            Controls.Add(buttons);
        }

        private static Control Row(Control input, string hint)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(input);
            row.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new System.Drawing.Size(380, 0), Padding = new Padding(0, 4, 0, 0) });
            return row;
        }

        private async System.Threading.Tasks.Task TestAsync()
        {
            // No wait cursor: the dialog stays responsive and the label shows progress
            _testButton.Enabled = false;
            _resultLabel.Text = "Connecting...";
            try
            {
                var cache = new TranslationCache(Path.Combine(Path.GetTempPath(), "translator-test-" + Guid.NewGuid() + ".json"));
                var service = new ArgosTranslateService(_urlBox.Text, cache);
                var languages = await service.GetSupportedLanguagesAsync();
                var test = await service.TranslateAsync("Hello, how are you?", languages.Contains("fr") ? "fr" : languages.First());

                _resultLabel.Text = test.Success
                    ? $"Connected ({service.Workers} worker(s)). {languages.Count} of the app's languages available.\nTest: \"Hello, how are you?\" -> \"{test.TranslatedText}\""
                    : "Connected, but translation failed: " + test.Error;
            }
            catch (HttpRequestException ex)
            {
                _resultLabel.Text = "Cannot connect: " + ex.Message + Environment.NewLine +
                    @"Is the server running? Start it with ArgosTranslateServer\start.cmd.";
            }
            catch (Exception ex)
            {
                _resultLabel.Text = "Error: " + ex.Message;
            }
            finally
            {
                _testButton.Enabled = true;
            }
        }
    }
}
