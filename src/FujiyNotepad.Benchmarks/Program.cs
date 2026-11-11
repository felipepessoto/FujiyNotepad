using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using FujiyNotepad.Benchmarks;

// Run all benchmarks (or a subset via --filter). See README.md.
// A benchmark on a shared machine must not change its global power plan.
var job = Job.Default.DontEnforcePowerPlan();
if (args.Any(argument => argument.StartsWith("nativeaot", StringComparison.OrdinalIgnoreCase)))
{
    // BDN otherwise selects this host's ISA and favors speed, unlike the shipping app's SDK defaults.
    job = job.WithMsBuildArguments("/p:IlcInstructionSet=", "/p:IlcOptimizationPreference=", "/p:OptimizationPreference=");
}
var config = DefaultConfig.Instance.AddJob(job.AsMutator());
var summaries = BenchmarkSwitcher.FromAssembly(typeof(EngineBenchmarks).Assembly).Run(args, config);
return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
