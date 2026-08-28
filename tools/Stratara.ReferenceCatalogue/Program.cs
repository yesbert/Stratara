using Stratara.ReferenceCatalogue;

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "llms-full.txt");

File.WriteAllText(outputPath, ReferenceCatalogue.Render(AppContext.BaseDirectory));
Console.WriteLine($"Wrote {outputPath}");
