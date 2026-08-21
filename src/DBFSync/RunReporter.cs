using System.Text;

namespace PeopleWorks.DBFSync.Cli;

internal sealed class RunReporter : IDisposable
{
    private readonly StreamWriter _writer;

    public RunReporter(string logsDirectory, string operation, string profile)
    {
        Directory.CreateDirectory(logsDirectory);
        string safeProfile = string.Concat(profile.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        Path = System.IO.Path.Combine(
            logsDirectory,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{operation}-{safeProfile}-{Environment.ProcessId}.log");
        _writer = new StreamWriter(Path, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public void Info(string message)
    {
        CliPresentation.Info(message);
        Record(message);
    }

    public void Success(string message)
    {
        CliPresentation.Success(message);
        Record(message);
    }

    public void Record(string message) =>
        _writer.WriteLine($"{DateTime.UtcNow:O} {message}");

    public void Dispose() => _writer.Dispose();
}
