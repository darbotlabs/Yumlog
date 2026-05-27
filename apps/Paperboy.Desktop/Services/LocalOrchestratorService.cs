using System.IO;

namespace Paperboy.Desktop.Services;

public sealed record OrchestratorEndpoint(string Name, string Path, string Kind);

public sealed class LocalOrchestratorService
{
    private static readonly string[] CandidateRoots =
    [
        @"E:\dayourbot",
        @"E:\DAYOURBOT",
        @"E:\dlm-lib",
        @"E:\DLM-Lib",
        @"D:\dayourbot",
        @"D:\dlm-lib"
    ];

    public IReadOnlyList<OrchestratorEndpoint> Discover()
    {
        var endpoints = new List<OrchestratorEndpoint>();
        foreach (var root in CandidateRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var mcpServer = System.IO.Path.Combine(root, "mcp-server.js");
            var stdioBridge = System.IO.Path.Combine(root, "mcp-stdio-bridge.js");
            var psLauncher = Directory.EnumerateFiles(root, "*.ps1", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (File.Exists(mcpServer))
            {
                endpoints.Add(new OrchestratorEndpoint(System.IO.Path.GetFileName(root), mcpServer, "mcp-server"));
            }

            if (File.Exists(stdioBridge))
            {
                endpoints.Add(new OrchestratorEndpoint(System.IO.Path.GetFileName(root), stdioBridge, "stdio-bridge"));
            }

            if (psLauncher is not null)
            {
                endpoints.Add(new OrchestratorEndpoint(System.IO.Path.GetFileName(root), psLauncher, "powershell"));
            }
        }

        return endpoints
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }
}
