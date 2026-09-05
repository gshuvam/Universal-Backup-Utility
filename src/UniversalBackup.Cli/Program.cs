using System.Diagnostics;
using UniversalBackup.Domain.Services;

Console.WriteLine("==========================================================");
Console.WriteLine(" Universal Backup Utility — Phase 0 Virtualization Spike");
Console.WriteLine("==========================================================");

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

long memBefore = GC.GetTotalMemory(true);
Console.WriteLine($"Baseline Memory: {memBefore / (1024.0 * 1024.0):F2} MB");

const int targetNodes = 250_000;
Console.WriteLine($"Generating {targetNodes:N0} synthetic nodes...");

var sw = Stopwatch.StartNew();
var tree = SyntheticTreeGenerator.GenerateTree(targetNodes);
sw.Stop();

long memAfter = GC.GetTotalMemory(false);
double allocatedMb = (memAfter - memBefore) / (1024.0 * 1024.0);
int actualNodes = SyntheticTreeGenerator.CountNodes(tree);

Console.WriteLine($"Nodes generated : {actualNodes:N0}");
Console.WriteLine($"Elapsed time    : {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"Memory delta    : {allocatedMb:F2} MB (Acceptance target: < 150 MB)");
Console.WriteLine($"Avg per node    : {(allocatedMb * 1024 * 1024) / actualNodes:F1} bytes/node");

// Benchmark tri-state cascading & bubbling
var bubbleSw = Stopwatch.StartNew();
tree[0].IsChecked = true;
bubbleSw.Stop();
Console.WriteLine($"Tri-state cascade & bubble: {bubbleSw.ElapsedMilliseconds} ms");

if (allocatedMb < 150.0 && actualNodes >= targetNodes)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(">>> ACCEPTANCE GATE PASSED: 250k nodes loaded smoothly under 150 MB memory.");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(">>> ACCEPTANCE GATE FAILED.");
    Console.ResetColor();
}

