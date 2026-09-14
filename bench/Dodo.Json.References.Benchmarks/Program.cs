using BenchmarkDotNet.Running;

namespace Dodo.Json.References.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["transform", var source, var destination])
        {
            var size = await RealPayload.Transform(source, destination).ConfigureAwait(false);
            Console.WriteLine($"transformed {size} bytes -> {destination}");
            return;
        }

        if (args is ["sizes", ..])
        {
            PayloadSizes.Print();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
