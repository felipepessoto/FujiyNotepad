using System.Diagnostics.CodeAnalysis;

// This assembly contains only shared test doubles / helpers (in-memory byte source, ILineSource fakes), not
// production code, so exclude the whole assembly from code-coverage reports. Coverlet honours
// [ExcludeFromCodeCoverage] by default, so the XPlat Code Coverage collector used in CI drops it without any
// extra runsettings.
[assembly: ExcludeFromCodeCoverage]
