using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Harmonie.HarmonieApi;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// One track's Discogs-style probability vector, sparse over the 400
/// styles. Vectors are L2-normalised so cosine similarity equals the
/// dot product, which keeps the clustering math simple and cheap.
/// </summary>
public sealed class StyleVector
{
    private StyleVector(IReadOnlyDictionary<string, double> components)
    {
        Components = components;
    }

    /// <summary>
    /// Sparse style → weight map. Read-only.
    /// </summary>
    public IReadOnlyDictionary<string, double> Components { get; }

    /// <summary>
    /// True when no styles cleared the construction filter.
    /// </summary>
    public bool IsEmpty => Components.Count == 0;

    /// <summary>
    /// Builds a normalised vector from a list of style scores. Returns
    /// an empty vector if the input is empty or all weights are zero.
    /// </summary>
    public static StyleVector FromStyles(IEnumerable<StyleScore> styles)
    {
        var dict = new Dictionary<string, double>();
        foreach (var s in styles)
        {
            if (string.IsNullOrEmpty(s.Style) || s.Probability <= 0)
            {
                continue;
            }

            // If a style appears more than once (shouldn't, but harmless),
            // keep the larger value.
            if (!dict.TryGetValue(s.Style, out var existing) || s.Probability > existing)
            {
                dict[s.Style] = s.Probability;
            }
        }

        return Normalise(dict);
    }

    /// <summary>
    /// Returns the centroid of a set of vectors (mean of components,
    /// then L2-normalised so it's directly comparable to a member).
    /// </summary>
    public static StyleVector Mean(IReadOnlyList<StyleVector> vectors)
        => WeightedMean(vectors, Enumerable.Repeat(1.0, vectors.Count).ToArray());

    /// <summary>
    /// Returns the weighted centroid of a set of vectors.
    /// </summary>
    public static StyleVector WeightedMean(
        IReadOnlyList<StyleVector> vectors,
        IReadOnlyList<double> weights)
    {
        if (vectors.Count == 0)
        {
            return new StyleVector(new Dictionary<string, double>());
        }

        if (weights.Count != vectors.Count)
        {
            throw new ArgumentException("weights must match vectors", nameof(weights));
        }

        var sum = new Dictionary<string, double>();
        double totalWeight = 0;
        for (var i = 0; i < vectors.Count; i++)
        {
            var weight = weights[i];
            if (!double.IsFinite(weight) || weight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(weights), "weights must be finite and positive");
            }

            totalWeight += weight;
            foreach (var kv in vectors[i].Components)
            {
                sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + (kv.Value * weight);
            }
        }

        var mean = new Dictionary<string, double>(sum.Count);
        foreach (var kv in sum)
        {
            mean[kv.Key] = kv.Value / totalWeight;
        }

        return Normalise(mean);
    }

    /// <summary>
    /// Cosine similarity. Both vectors are unit-length, so this is just
    /// the dot product. Range [0, 1] for non-negative components.
    /// </summary>
    public double CosineSimilarity(StyleVector other)
    {
        // Iterate the smaller side so the dot product is O(min) sparse.
        var (small, big) = Components.Count <= other.Components.Count
            ? (Components, other.Components)
            : (other.Components, Components);

        double sum = 0;
        foreach (var kv in small)
        {
            if (big.TryGetValue(kv.Key, out var ov))
            {
                sum += kv.Value * ov;
            }
        }

        return sum;
    }

    private static StyleVector Normalise(Dictionary<string, double> components)
    {
        double sumSq = 0;
        foreach (var v in components.Values)
        {
            sumSq += v * v;
        }

        if (sumSq <= 0)
        {
            return new StyleVector(new Dictionary<string, double>());
        }

        var mag = Math.Sqrt(sumSq);
        var normalised = new Dictionary<string, double>(components.Count);
        foreach (var kv in components)
        {
            normalised[kv.Key] = kv.Value / mag;
        }

        return new StyleVector(normalised);
    }
}

/// <summary>
/// One cluster produced by <see cref="StyleClusterer"/>. Carries the
/// member indices into the original input list, the centroid vector,
/// and a derived human-readable label.
/// </summary>
public sealed class StyleCluster
{
    public IReadOnlyList<int> MemberIndices { get; init; } = Array.Empty<int>();

    public StyleVector Centroid { get; init; } = StyleVector.FromStyles(Array.Empty<StyleScore>());

    /// <summary>
    /// Derived label, e.g. "House" for a single-style cluster or
    /// "House-Funk" when two styles share the centroid.
    /// </summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// K-means clustering over style probability vectors. Configured for
/// the per-user style-cluster playlists feature: deterministic
/// initialisation (k-means++ with a fixed seed) so scheduled refreshes
/// give stable assignments when the input doesn't change, and label
/// derivation that produces a non-empty, distinct title for every
/// returned cluster.
///
/// Hard clustering: each track is assigned to one cluster. Tracks can
/// still appear in multiple cluster *playlists* downstream (harmonie's
/// similar-mode response will overlap when the seeds overlap), which
/// reflects "this track really lives in both moods" naturally.
/// </summary>
public static class StyleClusterer
{
    // Cosine-similarity convergence threshold. K-means stops when no
    // assignment changes (handled by the loop) or after this many iters.
    private const int MaxIterations = 25;

    // If the top-1 style's share of the centroid's top-2 weight exceeds
    // this, the cluster is labeled with just that single style. Below
    // this, we use a hyphenated multi-style label.
    private const double SingleStyleDominance = 0.65;

    /// <summary>
    /// Clusters the input vectors into at most <paramref name="k"/>
    /// groups. Empty clusters (no members assigned) are dropped, so the
    /// returned list may have fewer than <paramref name="k"/> entries.
    /// Vectors that are empty (no styles cleared the filter) are
    /// excluded from clustering — they have nothing to contribute.
    /// </summary>
    /// <param name="vectors">One vector per input track.</param>
    /// <param name="k">Maximum number of clusters to produce.</param>
    /// <param name="randomSeed">
    /// Seed for k-means++ initialisation. Pass a stable value for
    /// stable scheduled refreshes; pass null for a fresh seed each call.
    /// </param>
    /// <param name="weights">Optional positive play-count weight per input vector.</param>
    public static IReadOnlyList<StyleCluster> Cluster(
        IReadOnlyList<StyleVector> vectors,
        int k,
        int? randomSeed = 42,
        IReadOnlyList<double>? weights = null)
    {
        if (k <= 0 || vectors.Count == 0)
        {
            return Array.Empty<StyleCluster>();
        }

        if (weights is not null && weights.Count != vectors.Count)
        {
            throw new ArgumentException("weights must match vectors", nameof(weights));
        }

        if (weights is not null && weights.Any(weight => !double.IsFinite(weight) || weight <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(weights), "weights must be finite and positive");
        }

        // Filter out empty vectors but keep the original indices so the
        // caller can map clusters back to their input tracks.
        var liveIndices = new List<int>(vectors.Count);
        var liveVectors = new List<StyleVector>(vectors.Count);
        var liveWeights = new List<double>(vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            if (!vectors[i].IsEmpty)
            {
                liveIndices.Add(i);
                liveVectors.Add(vectors[i]);
                var weight = weights?[i] ?? 1.0;
                liveWeights.Add(weight);
            }
        }

        if (liveVectors.Count == 0)
        {
            return Array.Empty<StyleCluster>();
        }

        // k can't exceed the number of distinct points we have.
        var actualK = Math.Min(k, liveVectors.Count);

        var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var centroids = KMeansPlusPlusInit(liveVectors, actualK, rng, weights is null ? null : liveWeights);

        var assignments = new int[liveVectors.Count];
        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var changed = false;
            for (var i = 0; i < liveVectors.Count; i++)
            {
                var best = 0;
                var bestSim = double.NegativeInfinity;
                for (var c = 0; c < centroids.Count; c++)
                {
                    var sim = liveVectors[i].CosineSimilarity(centroids[c]);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        best = c;
                    }
                }

                if (assignments[i] != best)
                {
                    assignments[i] = best;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }

            for (var c = 0; c < centroids.Count; c++)
            {
                var members = new List<StyleVector>();
                var memberWeights = new List<double>();
                for (var i = 0; i < liveVectors.Count; i++)
                {
                    if (assignments[i] == c)
                    {
                        members.Add(liveVectors[i]);
                        memberWeights.Add(liveWeights[i]);
                    }
                }

                if (members.Count > 0)
                {
                    centroids[c] = StyleVector.WeightedMean(members, memberWeights);
                }
            }
        }

        // Build cluster objects: members mapped back to original indices,
        // centroids recomputed from final assignments, and labels derived
        // with a per-batch dedup pass at the end.
        var clusters = new List<StyleCluster>(centroids.Count);
        for (var c = 0; c < centroids.Count; c++)
        {
            var memberIndices = new List<int>();
            var memberVectors = new List<StyleVector>();
            var memberWeights = new List<double>();
            for (var i = 0; i < liveVectors.Count; i++)
            {
                if (assignments[i] == c)
                {
                    memberIndices.Add(liveIndices[i]);
                    memberVectors.Add(liveVectors[i]);
                    memberWeights.Add(liveWeights[i]);
                }
            }

            if (memberIndices.Count == 0)
            {
                continue;
            }

            clusters.Add(new StyleCluster
            {
                MemberIndices = memberIndices,
                Centroid = StyleVector.WeightedMean(memberVectors, memberWeights),
            });
        }

        return AssignDistinctLabels(clusters);
    }

    // ---------------------------------------------------------------
    // k-means++ initialisation. Picks the first centroid uniformly,
    // then each subsequent centroid weighted by squared cosine
    // distance to the nearest already-picked centroid. Spreads
    // centroids apart and avoids the empty-cluster pathology of
    // uniform random init.
    // ---------------------------------------------------------------

    private static List<StyleVector> KMeansPlusPlusInit(
        List<StyleVector> vectors,
        int k,
        Random rng,
        List<double>? weights)
    {
        var centroids = new List<StyleVector>(k);
        centroids.Add(vectors[weights is null
            ? rng.Next(vectors.Count)
            : WeightedRandomIndex(weights, rng)]);

        var distSq = new double[vectors.Count];
        while (centroids.Count < k)
        {
            double total = 0;
            for (var i = 0; i < vectors.Count; i++)
            {
                var nearest = double.NegativeInfinity;
                foreach (var c in centroids)
                {
                    var sim = vectors[i].CosineSimilarity(c);
                    if (sim > nearest)
                    {
                        nearest = sim;
                    }
                }

                // Cosine distance, squared to favour far-away points.
                var d = 1 - nearest;
                distSq[i] = d * d * (weights?[i] ?? 1.0);
                total += distSq[i];
            }

            if (total <= 0)
            {
                // All remaining points are co-located with existing
                // centroids — can't meaningfully add another. Stop
                // early; the outer loop will get fewer than k centroids.
                break;
            }

            var pick = rng.NextDouble() * total;
            double accum = 0;
            for (var i = 0; i < vectors.Count; i++)
            {
                accum += distSq[i];
                if (accum >= pick)
                {
                    centroids.Add(vectors[i]);
                    break;
                }
            }
        }

        return centroids;
    }

    private static int WeightedRandomIndex(List<double> weights, Random rng)
    {
        var total = weights.Sum();
        var pick = rng.NextDouble() * total;
        double cumulative = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            cumulative += weights[i];
            if (cumulative >= pick)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }

    // ---------------------------------------------------------------
    // Label derivation: drop genre prefix from harmonie's
    // "Genre---Style" labels, pick top-1 or top-2 components from the
    // centroid, and ensure no two clusters in the batch share a label.
    // Empty labels are never returned.
    // ---------------------------------------------------------------

    private static List<StyleCluster> AssignDistinctLabels(List<StyleCluster> clusters)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var withLabels = new List<StyleCluster>(clusters.Count);
        foreach (var c in clusters)
        {
            var label = DeriveCentroidLabel(c.Centroid);
            // If the candidate is taken by an earlier cluster, fall
            // through to the unique-suffix path below.
            if (string.IsNullOrEmpty(label) || used.Contains(label))
            {
                label = DeriveDistinctLabel(c.Centroid, used);
            }

            used.Add(label);
            withLabels.Add(new StyleCluster
            {
                MemberIndices = c.MemberIndices,
                Centroid = c.Centroid,
                Label = label,
            });
        }

        return withLabels;
    }

    private static string DeriveCentroidLabel(StyleVector centroid)
    {
        var top2 = centroid.Components
            .OrderByDescending(kv => kv.Value)
            .Take(2)
            .ToList();

        if (top2.Count == 0)
        {
            return string.Empty;
        }

        var first = StripGenrePrefix(top2[0].Key);
        if (top2.Count == 1)
        {
            return first;
        }

        var totalTop2 = top2[0].Value + top2[1].Value;
        if (totalTop2 <= 0)
        {
            return first;
        }

        var dominance = top2[0].Value / totalTop2;
        if (dominance >= SingleStyleDominance)
        {
            return first;
        }

        var second = StripGenrePrefix(top2[1].Key);
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return first;
        }

        return $"{first}-{second}";
    }

    /// <summary>
    /// Falls back to the centroid's third-best style or numeric suffix
    /// when the natural label is taken or empty. Always returns a
    /// non-empty string.
    /// </summary>
    private static string DeriveDistinctLabel(StyleVector centroid, HashSet<string> used)
    {
        var ranked = centroid.Components
            .OrderByDescending(kv => kv.Value)
            .Select(kv => StripGenrePrefix(kv.Key))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        // Try: top1, top1-top2, top1-top3, top2, top2-top3, top1-top2-top3.
        var candidates = new List<string>();
        if (ranked.Count >= 1)
        {
            candidates.Add(ranked[0]);
        }

        if (ranked.Count >= 2)
        {
            candidates.Add($"{ranked[0]}-{ranked[1]}");
        }

        if (ranked.Count >= 3)
        {
            candidates.Add($"{ranked[0]}-{ranked[2]}");
            candidates.Add(ranked[2]);
            candidates.Add($"{ranked[1]}-{ranked[2]}");
        }

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && !used.Contains(c))
            {
                return c;
            }
        }

        // Last resort: numeric suffix on the first non-empty candidate.
        // Guarantees uniqueness and non-emptiness.
        var basis = candidates.Count > 0 ? candidates[0] : "Mix";
        for (var i = 2; i < 100; i++)
        {
            var attempt = $"{basis} {i}";
            if (!used.Contains(attempt))
            {
                return attempt;
            }
        }

        return $"{basis} {Guid.NewGuid():N}".Substring(0, Math.Min(40, basis.Length + 9));
    }

    /// <summary>
    /// Drops harmonie's <c>"Genre---"</c> prefix so labels read
    /// naturally: <c>"Electronic---House"</c> becomes <c>"House"</c>.
    /// Single-segment labels are returned unchanged.
    /// </summary>
    public static string StripGenrePrefix(string style)
    {
        if (string.IsNullOrEmpty(style))
        {
            return string.Empty;
        }

        var idx = style.IndexOf("---", StringComparison.Ordinal);
        return idx < 0 ? style : style[(idx + 3)..];
    }
}
