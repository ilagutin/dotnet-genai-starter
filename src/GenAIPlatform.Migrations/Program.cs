using GenAIPlatform.Migrations;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Cancel cooperatively: the runner releases the advisory lock and leaves the journal alone.
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await MigrationCliApplication.RunAsync(
    args,
    Console.Out,
    Console.Error,
    cancellation.Token);
