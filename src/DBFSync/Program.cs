using System.Text;
using PeopleWorks.DBFSync;
using PeopleWorks.DBFSync.Cli;

Console.OutputEncoding = new UTF8Encoding(false);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    string[] cliArgs = L10n.Configure(args);
    var application = new CliApplication(new AppPaths());
    return await application.RunAsync(cliArgs, cancellation.Token);
}
catch (OperationCanceledException)
{
    CliPresentation.Warning(L10n.T("ExecutionCancelled"));
    return 130;
}
catch (Exception ex)
{
    CliPresentation.Error(ex.Message);
    return 1;
}
