using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace VbcsPatch;

/// <summary>
/// Replaces <c>AnalyzerExecutor.OnOperationBlockActionsExecuted</c>, whose <c>_lazyControlFlowGraphMap?.Count &gt; 0</c>
/// gate takes every stripe lock of the ConcurrentDictionary on each operation block.
/// <c>IsEmpty</c> is not a fix either (it also takes all locks when the map is empty), so probe each root with the
/// lock-free <c>ContainsKey</c> and only take a single stripe lock when there is something to remove.
/// </summary>
internal static class AnalyzerExecutorPatch
{
    public static Hook Apply()
    {
        var type = typeof(AnalyzerExecutor);
        Patcher.RequireField(type, "_lazyControlFlowGraphMap", typeof(ConcurrentDictionary<IOperation, ControlFlowGraph>));

        var original = type.GetMethod("OnOperationBlockActionsExecuted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(ImmutableArray<IOperation>)])
            ?? throw new MissingMethodException(type.FullName, "OnOperationBlockActionsExecuted");

        var replacement = typeof(AnalyzerExecutorPatch).GetMethod(nameof(OnOperationBlockActionsExecuted), BindingFlags.Static | BindingFlags.NonPublic)!;
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

        return new Hook(original, replacement);
    }

    /// <summary>Replacement for <c>AnalyzerExecutor.OnOperationBlockActionsExecuted</c>; first parameter is <c>this</c>.</summary>
    private static void OnOperationBlockActionsExecuted(AnalyzerExecutor executor, ImmutableArray<IOperation> operationBlocks)
    {
        var map = executor._lazyControlFlowGraphMap;
        if (map is null || operationBlocks.IsDefaultOrEmpty)
            return;

        foreach (var operationBlock in operationBlocks)
        {
            var root = operationBlock;
            while (root.Parent is { } parent)
                root = parent;

            if (map.ContainsKey(root))
                map.TryRemove(root, out _);
        }
    }
}
