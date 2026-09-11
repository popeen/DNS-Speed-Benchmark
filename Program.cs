using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DnsSpeedBench;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BenchForm());
    }
}

internal sealed class BenchForm : Form
{
    private readonly TextBox _servers = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Text = "1.1.1.1\r\n8.8.8.8\r\n9.9.9.9",
        Dock = DockStyle.Fill,
    };
    private readonly TextBox _domain = new() { Text = "example.com", Dock = DockStyle.Fill };
    private readonly NumericUpDown _runs = new() { Minimum = 1, Maximum = 50, Value = 5, Dock = DockStyle.Left, Width = 90 };
    private readonly Button _runButton = new() { Text = "Run benchmark", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready" };
    private readonly DataGridView _results = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };

    public BenchForm()
    {
        Text = "DNS SpeedBench";
        MinimumSize = new Size(760, 560);
        Size = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        _results.Columns.Add("Server", "DNS server");
        _results.Columns.Add("Average", "Average");
        _results.Columns.Add("Fastest", "Fastest");
        _results.Columns.Add("Slowest", "Slowest");
        _results.Columns.Add("Success", "Success");
        _results.Columns.Add("Status", "Status");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "DNS servers\n(one address per line)", AutoSize = true, Padding = new Padding(0, 4, 10, 0) }, 0, 0);
        layout.Controls.Add(_servers, 1, 0);
        layout.Controls.Add(new Label { Text = "Domain to query", AutoSize = true, Padding = new Padding(0, 7, 10, 0) }, 0, 1);
        layout.Controls.Add(_domain, 1, 1);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        controls.Controls.Add(new Label { Text = "Queries per server", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        controls.Controls.Add(_runs);
        controls.Controls.Add(_runButton);
        controls.Controls.Add(_status);
        layout.Controls.Add(controls, 1, 2);
        layout.SetColumnSpan(controls, 1);
        layout.Controls.Add(_results, 0, 3);
        layout.SetColumnSpan(_results, 2);
        Controls.Add(layout);

        _runButton.Click += async (_, _) => await RunBenchmarkAsync();
    }

    private async Task RunBenchmarkAsync()
    {
        var servers = _servers.Lines
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var domain = _domain.Text.Trim().TrimEnd('.');

        if (servers.Count == 0)
        {
            MessageBox.Show(this, "Add at least one DNS server address.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(domain) || domain.Split('.').Any(label => label.Length is < 1 or > 63))
        {
            MessageBox.Show(this, "Enter a valid domain name, such as example.com.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _runButton.Enabled = false;
        _results.Rows.Clear();
        _status.Text = "Testing resolvers…";
        try
        {
            var count = (int)_runs.Value;
            var results = await Task.WhenAll(servers.Select(server => BenchmarkServerAsync(server, domain, count)));
            foreach (var result in results.OrderBy(result => result.Average ?? double.MaxValue))
            {
                _results.Rows.Add(
                    result.Server,
                    result.Average is null ? "—" : $"{result.Average:0.0} ms",
                    result.Fastest is null ? "—" : $"{result.Fastest:0.0} ms",
                    result.Slowest is null ? "—" : $"{result.Slowest:0.0} ms",
                    $"{result.Successful}/{count}",
                    result.Status);
            }
            _status.Text = $"Finished · {servers.Count} server{(servers.Count == 1 ? string.Empty : "s")}";
        }
        finally
        {
            _runButton.Enabled = true;
        }
    }

    private static async Task<BenchmarkResult> BenchmarkServerAsync(string server, string domain, int count)
    {
        if (!IPAddress.TryParse(server, out var address))
            return new BenchmarkResult(server, [], "Invalid IP address");

        var times = new List<double>();
        var errors = new List<string>();
        for (var attempt = 0; attempt < count; attempt++)
        {
            try
            {
                times.Add(await QueryAsync(address, domain));
            }
            catch (Exception error)
            {
                errors.Add(error is TimeoutException ? "Timed out" : "Query failed");
            }
        }
        var status = times.Count == count ? "OK" : times.Count == 0 ? errors.FirstOrDefault() ?? "Unavailable" : $"{count - times.Count} failed";
        return new BenchmarkResult(server, times, status);
    }

    private static async Task<double> QueryAsync(IPAddress server, string domain)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        var query = BuildQuery(id, domain);
        using var client = new UdpClient(server.AddressFamily);
        client.Connect(new IPEndPoint(server, 53));
        var stopwatch = Stopwatch.StartNew();
        await client.SendAsync(query);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await client.ReceiveAsync(timeout.Token);
        stopwatch.Stop();
        if (response.Buffer.Length < 12 || response.Buffer[0] != query[0] || response.Buffer[1] != query[1])
            throw new InvalidDataException("Invalid DNS response.");
        var responseCode = response.Buffer[3] & 0x0F;
        if (responseCode != 0)
            throw new InvalidDataException($"DNS returned code {responseCode}.");
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static byte[] BuildQuery(ushort id, string domain)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)(id >> 8));
        stream.WriteByte((byte)id);
        stream.WriteByte(0x01);
        stream.WriteByte(0x00);
        stream.Write([0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        foreach (var label in domain.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        stream.Write([0x00, 0x01, 0x00, 0x01]);
        return stream.ToArray();
    }

    private sealed record BenchmarkResult(string Server, List<double> Times, string Status)
    {
        public double? Average => Times.Count == 0 ? null : Times.Average();
        public double? Fastest => Times.Count == 0 ? null : Times.Min();
        public double? Slowest => Times.Count == 0 ? null : Times.Max();
        public int Successful => Times.Count;
    }
}
