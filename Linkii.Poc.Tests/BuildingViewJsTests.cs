using System.Diagnostics;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// Logique de la vue du bâtiment (player/building-view.js, §199) : lance les tests Node de <c>js/building-view.test.js</c>.
/// Sans Node sur la machine, le test ne fait rien (le player n'en a pas besoin).
/// </summary>
public class BuildingViewJsTests
{
    [Fact]
    public void Building_view_logic_passes_its_node_tests()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var file = Path.Combine(root, "Linkii.Poc.Tests", "js", "building-view.test.js");
        Assert.True(File.Exists(file), file);
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo("node", $"--test \"{file}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root,
            });
        }
        catch (System.ComponentModel.Win32Exception) { return; }   // Node absent
        if (p == null) return;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "node --test n'a pas fini en 60 s");
        Assert.True(p.ExitCode == 0, output.Result + errors.Result);
    }
}
