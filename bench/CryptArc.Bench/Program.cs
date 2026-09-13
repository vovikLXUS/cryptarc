using System.Reflection;
using BenchmarkDotNet.Running;

// Run every benchmark:            dotnet run -c Release --project bench/CryptArc.Bench -- --filter *
// Run one class:                  dotnet run -c Release --project bench/CryptArc.Bench -- --filter *Crc32*
// Results land in bench/BenchmarkDotNet.Artifacts (git-ignored); export with --exporters github
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
