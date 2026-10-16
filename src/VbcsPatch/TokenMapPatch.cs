using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using Microsoft.Cci;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeGen;

namespace VbcsPatch;

/// <summary>
/// Replaces <c>TokenMap.AddItem</c>, which serializes every token miss of a module on a single
/// <c>lock (_itemIdentityToToken)</c> and does <c>ConcurrentDictionary.TryAdd</c> + <c>Array.Resize</c> while holding it.
///
/// The replacement keeps the exact field layout and the invariants the unpatched readers rely on
/// (<c>GetOrAddTokenFor</c>'s lock-free fast path, <c>GetItem</c>, <c>GetAllItems</c>):
///   * a token only becomes visible in <c>_itemIdentityToToken</c> after <c>_items[token]</c> is written;
///   * tokens are dense: 0.._count-1, each slot written exactly once;
///   * once all adds have finished, <c>_items.Length >= _count</c>.
///
/// How:
///   * uniqueness per item comes from a striped lock chosen by the item's hash, so only adds of items
///     that hash to the same stripe (1/1024) contend, instead of every add in the module;
///   * the token is reserved with <c>Interlocked.Increment(ref _count)</c>;
///   * the item is stored with a publish-then-copy array growth protocol, so writers never need a lock
///     except for the rare (log n) resize.
///
/// <c>GetItem</c>/<c>GetAllItems</c> are only read by the metadata writer after all method bodies are compiled,
/// so the transient window where <c>_count</c> runs ahead of the stored items is never observed.
/// </summary>
internal static class TokenMapPatch
{
    private const int StripeBits = 10;
    private static readonly Lock[] s_stripes = CreateStripes();

    private static long s_anomalies;

    private static readonly bool s_verify = Environment.GetEnvironmentVariable("VBCSPATCH_VERIFY") == "1";
    private static readonly ConcurrentDictionary<TokenMap, StrongBox<int>> s_tracked = new(ReferenceEqualityComparer.Instance);

    public static Hook Apply()
    {
        var type = typeof(TokenMap);
        Patcher.RequireField(type, "_itemIdentityToToken", typeof(ConcurrentDictionary<IReferenceOrISignature, uint>));
        Patcher.RequireField(type, "_items", typeof(object[]));
        Patcher.RequireField(type, "_count", typeof(int));

        var original = type.GetMethod("AddItem", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IReferenceOrISignature), typeof(bool).MakeByRefType()])
            ?? throw new MissingMethodException(type.FullName, "AddItem");
        if (original.ReturnType != typeof(uint))
            throw new InvalidOperationException($"TokenMap.AddItem returns {original.ReturnType}, expected uint");

        var replacement = typeof(TokenMapPatch).GetMethod(nameof(AddItem), BindingFlags.Static | BindingFlags.NonPublic)!;

        // JIT our side first so any binding problem against this Roslyn build surfaces here, not mid-compilation.
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(TokenMapPatch).GetMethod(nameof(StoreItem), BindingFlags.Static | BindingFlags.NonPublic)!.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(TokenMapPatch).GetMethod(nameof(Grow), BindingFlags.Static | BindingFlags.NonPublic)!.MethodHandle);

        // No 'orig' parameter: the original body never runs again.
        return new Hook(original, replacement);
    }

    public static bool VerifyMode => s_verify;

    /// <summary>Replacement for <c>TokenMap.AddItem(IReferenceOrISignature, out bool)</c>; first parameter is <c>this</c>.</summary>
    private static uint AddItem(TokenMap map, IReferenceOrISignature item, out bool referenceAdded)
    {
        var tokens = map._itemIdentityToToken;
        uint token;

        lock (s_stripes[StripeOf(item)])
        {
            if (tokens.TryGetValue(item, out token))
            {
                referenceAdded = false;
                return token;
            }

            token = (uint)(Interlocked.Increment(ref map._count) - 1);
            StoreItem(map, (int)token, item.AsObject());
            referenceAdded = tokens.TryAdd(item, token);
        }

        if (!referenceAdded)
            return OnForeignAdd(tokens, item, token);

        if (s_verify)
            Interlocked.Increment(ref s_tracked.GetOrAdd(map, static _ => new StrongBox<int>()).Value);

        return token;
    }

    private static int StripeOf(IReferenceOrISignature item)
        => (int)(((uint)item.GetHashCode() * 0x9E3779B9u) >> (32 - StripeBits));

    /// <summary>
    /// Writes <paramref name="value"/> to <c>_items[index]</c>. Each index has exactly one writer.
    /// Growth publishes the new array first and then copies the old one; a writer that raced with a
    /// growth sees the new array on its re-check and repeats the write there. Both sides use full fences
    /// (Interlocked), so either the copier sees the writer's value or the writer sees the new array.
    /// </summary>
    private static void StoreItem(TokenMap map, int index, object value)
    {
        var items = Volatile.Read(ref map._items);
        while (true)
        {
            if ((uint)index < (uint)items.Length)
            {
                Interlocked.Exchange(ref items[index], value);
                var current = Volatile.Read(ref map._items);
                if (ReferenceEquals(current, items))
                    return;

                items = current;
                continue;
            }

            items = Grow(map, index);
        }
    }

    private static object[] Grow(TokenMap map, int index)
    {
        // Nothing else ever locks on a TokenMap instance (the original locks _itemIdentityToToken).
        lock (map)
        {
            var items = map._items;
            if (index < items.Length)
                return items;

            var grown = new object[Math.Max(8, Math.Max(index + 1, items.Length * 2))];
            Interlocked.Exchange(ref map._items, grown);
            for (var i = 0; i < items.Length; i++)
            {
                var value = Volatile.Read(ref items[i]);
                if (value is not null)
                    Interlocked.CompareExchange(ref grown[i], value, null);
            }

            return grown;
        }
    }

    /// <summary>
    /// TryAdd failed although we held the item's stripe: something outside this protocol (an inlined copy of the
    /// original AddItem) added it. Keep the token that is already published. Should never happen; logged once.
    /// </summary>
    private static uint OnForeignAdd(ConcurrentDictionary<IReferenceOrISignature, uint> tokens, IReferenceOrISignature item, uint ourToken)
    {
        if (Interlocked.Increment(ref s_anomalies) == 1)
            Log.Write("WARNING: TokenMap item was added outside the patched AddItem (inlined original?). Output is still consistent but has a duplicate reference slot.");

        return tokens.TryGetValue(item, out var existing) ? existing : ourToken;
    }

    internal static string VerifyMaps()
    {
        if (!s_verify)
            return "verify mode off (set VBCSPATCH_VERIFY=1)";

        int maps = 0, bad = 0;
        long totalTokens = 0;
        string? firstProblem = null;

        foreach (var (map, assigned) in s_tracked)
        {
            maps++;
            var problem = Check(map, assigned.Value);
            totalTokens += map._count;
            if (problem is not null)
            {
                bad++;
                firstProblem ??= problem;
            }
        }

        s_tracked.Clear();
        return $"maps={maps} tokens={totalTokens} bad={bad} anomalies={Interlocked.Read(ref s_anomalies)}" + (firstProblem is null ? "" : $" first: {firstProblem}");
    }

    private static string? Check(TokenMap map, int assignedByPatch)
    {
        var count = Volatile.Read(ref map._count);
        var items = Volatile.Read(ref map._items);
        var tokens = map._itemIdentityToToken;

        if (count != assignedByPatch)
            return $"_count={count} but patch assigned {assignedByPatch} (original AddItem ran?)";
        if (items.Length < count)
            return $"_items.Length={items.Length} < _count={count}";
        if (tokens.Count != count)
            return $"dictionary has {tokens.Count} entries, _count={count}";

        for (var i = 0; i < count; i++)
        {
            if (items[i] is null)
                return $"_items[{i}] is null";
        }

        foreach (var (item, token) in tokens)
        {
            if (token >= (uint)count || !ReferenceEquals(items[token], item.AsObject()))
                return $"token {token} does not map back to its item";
        }

        return null;
    }

    private static Lock[] CreateStripes()
    {
        var stripes = new Lock[1 << StripeBits];
        for (var i = 0; i < stripes.Length; i++)
            stripes[i] = new Lock();
        return stripes;
    }
}
