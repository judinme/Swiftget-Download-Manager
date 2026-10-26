using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Diagnostics;

public interface IDiagnosticService
{
    Task<string> ExportDiagnosticBundleAsync(string outputPath, CancellationToken ct = default);
}
