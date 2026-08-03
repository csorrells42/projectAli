namespace Ali.Modules.Orchestration.Work;

internal readonly record struct PersistentMerkleMapUpdate(
    bool Changed,
    int NodesVisited,
    int NodesRehashed);

/// <summary>
/// Persistent deterministic treap whose root is a domain-separated Merkle commitment. The
/// SHA-256-derived key priority makes the tree shape independent of insertion order, while the
/// ordinal key tie-breaker keeps the shape exact even in the theoretical priority-collision case.
/// Updating one key rebuilds only its authenticated search path.
/// </summary>
internal sealed class PersistentMerkleStringMap
{
    private static readonly string EmptyRootDigest = WorkIdentityCanonicalizer.DigestParts(
        "ali-work-graph-merkle-empty-v1");

    private readonly Node? _root;

    private PersistentMerkleStringMap(Node? root)
    {
        _root = root;
    }

    internal static PersistentMerkleStringMap Empty { get; } = new(root: null);

    internal int Count => _root?.Count ?? 0;

    internal string RootDigest => _root?.Digest ?? EmptyRootDigest;

    internal static PersistentMerkleStringMap Create(
        IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var map = Empty;
        foreach (var pair in values)
        {
            map = map.Set(pair.Key, pair.Value, out _);
        }

        return map;
    }

    internal string DomainDigest(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return WorkIdentityCanonicalizer.DigestParts(
            domain,
            Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RootDigest);
    }

    internal PersistentMerkleStringMap Set(
        string key,
        string value,
        out PersistentMerkleMapUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var nodesVisited = 0;
        var nodesRehashed = 0;
        var changed = false;
        var priority = WorkIdentityCanonicalizer.DigestParts(
            "ali-work-graph-merkle-key-priority-v1",
            key);
        var root = SetCore(
            _root,
            key,
            value,
            priority,
            ref nodesVisited,
            ref nodesRehashed,
            ref changed);
        update = new PersistentMerkleMapUpdate(changed, nodesVisited, nodesRehashed);
        return changed ? new PersistentMerkleStringMap(root) : this;
    }

    internal PersistentMerkleStringMap Remove(
        string key,
        out PersistentMerkleMapUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var nodesVisited = 0;
        var nodesRehashed = 0;
        var changed = false;
        var root = RemoveCore(
            _root,
            key,
            ref nodesVisited,
            ref nodesRehashed,
            ref changed);
        update = new PersistentMerkleMapUpdate(changed, nodesVisited, nodesRehashed);
        return changed ? new PersistentMerkleStringMap(root) : this;
    }

    private static Node SetCore(
        Node? node,
        string key,
        string value,
        string priority,
        ref int nodesVisited,
        ref int nodesRehashed,
        ref bool changed)
    {
        if (node is null)
        {
            changed = true;
            return MakeNode(key, value, priority, left: null, right: null, ref nodesRehashed);
        }

        nodesVisited++;
        var comparison = string.CompareOrdinal(key, node.Key);
        if (comparison == 0)
        {
            if (string.Equals(value, node.Value, StringComparison.Ordinal))
            {
                return node;
            }

            changed = true;
            return MakeNode(
                node.Key,
                value,
                node.Priority,
                node.Left,
                node.Right,
                ref nodesRehashed);
        }

        if (comparison < 0)
        {
            var left = SetCore(
                node.Left,
                key,
                value,
                priority,
                ref nodesVisited,
                ref nodesRehashed,
                ref changed);
            if (!changed)
            {
                return node;
            }

            var rebuilt = MakeNode(
                node.Key,
                node.Value,
                node.Priority,
                left,
                node.Right,
                ref nodesRehashed);
            return HasHigherPriority(left, rebuilt)
                ? RotateRight(rebuilt, ref nodesRehashed)
                : rebuilt;
        }

        var right = SetCore(
            node.Right,
            key,
            value,
            priority,
            ref nodesVisited,
            ref nodesRehashed,
            ref changed);
        if (!changed)
        {
            return node;
        }

        var updated = MakeNode(
            node.Key,
            node.Value,
            node.Priority,
            node.Left,
            right,
            ref nodesRehashed);
        return HasHigherPriority(right, updated)
            ? RotateLeft(updated, ref nodesRehashed)
            : updated;
    }

    private static Node? RemoveCore(
        Node? node,
        string key,
        ref int nodesVisited,
        ref int nodesRehashed,
        ref bool changed)
    {
        if (node is null)
        {
            return null;
        }

        nodesVisited++;
        var comparison = string.CompareOrdinal(key, node.Key);
        if (comparison == 0)
        {
            changed = true;
            return Merge(node.Left, node.Right, ref nodesVisited, ref nodesRehashed);
        }

        if (comparison < 0)
        {
            var left = RemoveCore(
                node.Left,
                key,
                ref nodesVisited,
                ref nodesRehashed,
                ref changed);
            return changed
                ? MakeNode(
                    node.Key,
                    node.Value,
                    node.Priority,
                    left,
                    node.Right,
                    ref nodesRehashed)
                : node;
        }

        var right = RemoveCore(
            node.Right,
            key,
            ref nodesVisited,
            ref nodesRehashed,
            ref changed);
        return changed
            ? MakeNode(
                node.Key,
                node.Value,
                node.Priority,
                node.Left,
                right,
                ref nodesRehashed)
            : node;
    }

    private static Node? Merge(
        Node? left,
        Node? right,
        ref int nodesVisited,
        ref int nodesRehashed)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        nodesVisited++;
        if (HasHigherPriority(left, right))
        {
            var mergedRight = Merge(
                left.Right,
                right,
                ref nodesVisited,
                ref nodesRehashed);
            return MakeNode(
                left.Key,
                left.Value,
                left.Priority,
                left.Left,
                mergedRight,
                ref nodesRehashed);
        }

        var mergedLeft = Merge(
            left,
            right.Left,
            ref nodesVisited,
            ref nodesRehashed);
        return MakeNode(
            right.Key,
            right.Value,
            right.Priority,
            mergedLeft,
            right.Right,
            ref nodesRehashed);
    }

    private static Node RotateRight(Node node, ref int nodesRehashed)
    {
        var promoted = node.Left
            ?? throw new InvalidOperationException(
                "A Merkle treap right rotation requires a left child.");
        var demoted = MakeNode(
            node.Key,
            node.Value,
            node.Priority,
            promoted.Right,
            node.Right,
            ref nodesRehashed);
        return MakeNode(
            promoted.Key,
            promoted.Value,
            promoted.Priority,
            promoted.Left,
            demoted,
            ref nodesRehashed);
    }

    private static Node RotateLeft(Node node, ref int nodesRehashed)
    {
        var promoted = node.Right
            ?? throw new InvalidOperationException(
                "A Merkle treap left rotation requires a right child.");
        var demoted = MakeNode(
            node.Key,
            node.Value,
            node.Priority,
            node.Left,
            promoted.Left,
            ref nodesRehashed);
        return MakeNode(
            promoted.Key,
            promoted.Value,
            promoted.Priority,
            demoted,
            promoted.Right,
            ref nodesRehashed);
    }

    private static bool HasHigherPriority(Node candidate, Node current)
    {
        var priorityComparison = string.CompareOrdinal(candidate.Priority, current.Priority);
        return priorityComparison < 0
               || (priorityComparison == 0
                   && string.CompareOrdinal(candidate.Key, current.Key) < 0);
    }

    private static Node MakeNode(
        string key,
        string value,
        string priority,
        Node? left,
        Node? right,
        ref int nodesRehashed)
    {
        nodesRehashed++;
        return new Node(key, value, priority, left, right);
    }

    private sealed class Node
    {
        internal Node(
            string key,
            string value,
            string priority,
            Node? left,
            Node? right)
        {
            Key = key;
            Value = value;
            Priority = priority;
            Left = left;
            Right = right;
            Count = 1 + (left?.Count ?? 0) + (right?.Count ?? 0);
            Digest = WorkIdentityCanonicalizer.DigestParts(
                "ali-work-graph-merkle-node-v1",
                Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                left?.Digest ?? EmptyRootDigest,
                key,
                priority,
                value,
                right?.Digest ?? EmptyRootDigest);
        }

        internal string Key { get; }

        internal string Value { get; }

        internal string Priority { get; }

        internal Node? Left { get; }

        internal Node? Right { get; }

        internal int Count { get; }

        internal string Digest { get; }
    }
}
