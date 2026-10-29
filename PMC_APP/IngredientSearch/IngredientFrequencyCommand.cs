namespace PMC_APP.IngredientSearch;

public static class IngredientFrequencyCommand
{
    public static async Task RunAsync(string[] args)
    {
        var options = new IngredientFrequencyOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            switch (args[i])
            {
                case "--keywords": options.KeywordsPath = Value(); break;
                case "--output": options.OutputDirectory = Path.GetFullPath(Value()); break;
                case "--mongo-uri": options.MongoUri = Value(); break;
                case "--database": options.Database = Value(); break;
                case "--collection": options.Collection = Value(); break;
                case "--batch-size": options.BatchSize = int.Parse(Value()); break;
                case "--migration-batch-size": options.MigrationBatchSize = int.Parse(Value()); break;
                case "--migration-start-after": options.MigrationStartAfter = int.Parse(Value()); break;
                case "--threads": options.Threads = int.Parse(Value()); break;
                case "--readers": options.Readers = int.Parse(Value()); break;
                case "--report-only": options.ReportOnly = true; break;
                case "--resume-labeling": options.ResumeLabeling = true; break;
                default: throw new ArgumentException($"Unknown ingredient-frequency option: {args[i]}");
            }
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { await new PmcIngredientFrequencyLabeler(options).RunAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("Cancelled. Saved article labels are preserved; rerun to resume.");
            Environment.ExitCode = 130;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
