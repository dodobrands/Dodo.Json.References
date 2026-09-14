using BenchmarkDotNet.Running;

namespace Dodo.Json.References.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args is ["sizes", ..])
        {
            PayloadSizes.Print();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
