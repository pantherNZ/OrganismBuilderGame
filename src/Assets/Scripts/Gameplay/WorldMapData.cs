using System.Collections.Generic;
using Runtime.Game;
using UnityEngine;

// ---------------------------------------------------------------------------
// MapPoint — lightweight grid coordinate that works as a dictionary key.
// Kept in the Simulation layer so there is no UnityEngine dependency.
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable 2-D integer grid coordinate used as the key in
/// <see cref="WorldMapData.Nodes"/>.
/// </summary>
public readonly struct MapPoint : System.IEquatable<MapPoint>
{
    public readonly int X;
    public readonly int Y;

    public MapPoint(int x, int y) { X = x; Y = y; }

    public bool Equals(MapPoint other) => X == other.X && Y == other.Y;
    public override bool Equals(object obj) => obj is MapPoint p && Equals(p);
    public override int GetHashCode() => X * 397 ^ Y;
    public override string ToString() => $"({X},{Y})";
    public static implicit operator UnityEngine.Vector2Int(MapPoint p) => new(p.X, p.Y);

    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Y + b.Y);
}

// ---------------------------------------------------------------------------
// WorldMapData
// ---------------------------------------------------------------------------

/// <summary>
/// Generates and owns the full world map for a run.
/// Call <see cref="WorldMapData(WeightedSelector{EncounterSchema}, int?, IReadOnlyList{EncounterSchema})"/>
/// once at game start and pass it to the <c>WorldMapPanel</c>.
/// </summary>
public class WorldMapData
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Distance between grid cells in canvas pixels.
    /// The UI multiplies node positions by this value when placing elements.
    /// </summary>
    public const int CellSize = 200;

    public const int EncounterRingCount = 3;
    public const int FinalBossNodeCount = 4;
    const int FirstRingCapacity = 8;
    const int StartingNodeClearanceSquared = 9;

    // ── Data ──────────────────────────────────────────────────────────────────

    /// <summary>All nodes, keyed by their logical grid position.</summary>
    public IReadOnlyDictionary<MapPoint, WorldMapNode> Nodes => _nodes;

    /// <summary>The special start node where the player begins.</summary>
    public WorldMapNode StartNode { get; private set; }

    /// <summary>The node the player is currently at.</summary>
    public WorldMapNode PlayerNode { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────────

    readonly Dictionary<MapPoint, WorldMapNode> _nodes = new();
    WorldMapNode _firstStartingNode;
    WorldMapNode _secondStartingNode;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a world map from the provided encounter list.
    /// Encounters are laid out in fairly even rings around the start node.
    /// </summary>
    /// <param name="encounters">Weighted encounter selector for this run.</param>
    /// <param name="seed">Optional RNG seed (reproducible maps for a given run).</param>
    /// <param name="startingEncounters">Ordered encounter schemas that should appear first on the map.</param>
    /// <param name="shopSchema">Optional configured shop inserted once in a seeded ring 2-4.</param>
    public WorldMapData(
        WeightedSelector<EncounterSchema> encounters,
        int? seed = null,
        IReadOnlyList<EncounterSchema> startingEncounters = null,
        ShopEncounterSchema shopSchema = null)
    {
        Utility.IRandom rng = seed.HasValue ? new Utility.SeededRandom(seed.Value) : new Utility.UnityRandom();
        startingEncounters ??= System.Array.Empty<EncounterSchema>();
        GenerateLayout(encounters, rng, startingEncounters, shopSchema);
    }

    // ── Layout generation ─────────────────────────────────────────────────────

    void GenerateLayout(
        WeightedSelector<EncounterSchema> encounters,
        Utility.IRandom rng,
        IReadOnlyList<EncounterSchema> startingEncounters,
        ShopEncounterSchema shopSchema)
    {
        _nodes.Clear();
        _firstStartingNode = null;
        _secondStartingNode = null;

        var startPos = new MapPoint(0, 0);

        StartNode = new WorldMapNode
        {
            Position = startPos,
            Type = WorldMapNodeType.Start,
            IsVisited = true,
            IsAccessible = true,
        };
        Add(StartNode);
        PlayerNode = StartNode;

        var availableSchemas = new List<EncounterSchema>();
        var seenSchemas = new HashSet<EncounterSchema>();
        if (encounters != null)
        {
            foreach (var schema in encounters.Items)
            {
                if (schema == null || schema is ShopEncounterSchema || schema.StartEncounter
                    || !schema.HasEnemyDefinition || schema.Weight <= 0
                    || schema.DifficultyMultiplier <= 0f || float.IsNaN(schema.DifficultyMultiplier)
                    || float.IsInfinity(schema.DifficultyMultiplier) || !seenSchemas.Add(schema))
                {
                    continue;
                }

                availableSchemas.Add(schema);
            }
        }

        int locationCount = availableSchemas.Count > 0
            ? GetEncounterCountForRings()
            : startingEncounters.Count + (shopSchema != null ? 1 : 0);

        const float baseRadius = 4f;
        const float ringSpacing = 3f;
        const int initiallyAccessibleEncounters = 1;

        var locations = new List<WorldMapNode>(locationCount);
        var startingPositions = new[]
        {
            new MapPoint(2, 0),
            new MapPoint(4, 1),
        };
        int reservedStartingCount = Mathf.Min(startingEncounters.Count, startingPositions.Length);
        for (int index = 0; index < reservedStartingCount; index++)
        {
            var node = new WorldMapNode
            {
                Position = startingPositions[index],
                Depth = 1,
                DifficultyScale = 1f,
            };
            Add(node);
            locations.Add(node);
            if (index == 0)
            {
                _firstStartingNode = node;
            }
            else if (index == 1)
            {
                _secondStartingNode = node;
            }
        }

        int ringIndex = 0;

        // Reserve every location before drawing any encounter schemas. Enemy selection
        // must not change the random values used to generate subsequent ring positions.
        while (locations.Count < locationCount)
        {
            ringIndex++;
            int ringCapacity = FirstRingCapacity * ringIndex;
            int existingRingNodeCount = CountNodesInRing(locations, ringIndex);
            int remainingRingCapacity = Mathf.Max(0, ringCapacity - existingRingNodeCount);
            int ringNodeCount = Mathf.Min(remainingRingCapacity, locationCount - locations.Count);

            float ringOffset = rng.value * (Mathf.PI * 2.0f);
            float angleJitterMult = 0.5f;
            float radiusJitterMult = 0.15f;

            for (int i = 0; i < ringNodeCount; i++)
            {
                float t = i / (float)ringCapacity;
                float angle = ringOffset + t * (Mathf.PI * 2.0f);
                float angleJitter = (rng.value - 0.5f) * ((Mathf.PI * 2.0f) / (ringCapacity * 6.0f)) * angleJitterMult;
                angle += angleJitter;

                float radius = baseRadius + (ringIndex - 1) * ringSpacing + (rng.value - 0.5f) * radiusJitterMult;
                int x = (int)Mathf.Round(Mathf.Cos(angle) * radius);
                int y = (int)Mathf.Round(Mathf.Sin(angle) * radius);

                var desiredPos = new MapPoint(x, y);
                var pos = FindNearestFreePosition(desiredPos);
                var node = new WorldMapNode
                {
                    Position = pos,
                    Depth = ringIndex,
                };
                Add(node);
                locations.Add(node);
            }
        }

        int shopIndex = -1;
        if (shopSchema != null)
        {
            // Prefer outer rings, but never displace the ordered starting encounters.
            int minimumIndex = Mathf.Max(startingEncounters.Count, Mathf.Min(FirstRingCapacity, locationCount - 1));
            int maximumIndex = Mathf.Max(minimumIndex, Mathf.Min(locationCount - 1, 79));
            shopIndex = rng.Range(minimumIndex, maximumIndex + 1);
        }

        var populatedLocations = new List<WorldMapNode>(locationCount);
        for (int index = 0; index < locations.Count; index++)
        {
            var node = locations[index];
            float distanceFromStart = ((Vector2Int)node.Position).magnitude;
            bool isStartingEncounter = index < startingEncounters.Count;
            bool isShop = index == shopIndex;
            Encounter encounter;
            if (isStartingEncounter)
            {
                encounter = startingEncounters[index].CreateEncounter();
            }
            else if (isShop)
            {
                encounter = shopSchema.CreateEncounter();
            }
            else
            {
                var difficultyCurve = GlobalConstantsHandler.Constants?.EncounterDifficultyScaling;
                float difficultyToSpend = (difficultyCurve?.Evaluate(distanceFromStart) ?? 0f) * GlobalConstantsHandler.Constants.DifficultyScalerModifier;
                encounter = PurchaseEncounter(availableSchemas, difficultyToSpend, rng);
                if (encounter == null)
                {
                    // No affordable enemies: do not create an empty combat encounter.
                    _nodes.Remove(node.Position);
                    continue;
                }

                node.DifficultyScale = difficultyToSpend;
                ScaleEncounter(encounter, distanceFromStart);
            }

            node.Encounter = encounter;
            node.Type = isShop ? WorldMapNodeType.Shop : WorldMapNodeType.Combat;
            node.IsVisited = encounter.IsCompleted && encounter.PlayerWon;
            node.IsAccessible = populatedLocations.Count < initiallyAccessibleEncounters;
            populatedLocations.Add(node);
            if (node.IsVisited)
            {
                PlayerNode = node;
            }
        }

        List<WorldMapNode> finalBossNodes = CreateFinalBossNodes(
            availableSchemas,
            rng,
            baseRadius + EncounterRingCount * ringSpacing);

        var rings = new SortedDictionary<int, List<WorldMapNode>>();
        for (int index = 0; index < populatedLocations.Count; index++)
        {
            var node = populatedLocations[index];
            if (node.Type != WorldMapNodeType.Shop)
            {
                node.Type = index % 4 == 3 ? WorldMapNodeType.Elite : WorldMapNodeType.Combat;
            }

            if (!rings.TryGetValue(node.Depth, out List<WorldMapNode> ringNodes))
            {
                ringNodes = new List<WorldMapNode>();
                rings.Add(node.Depth, ringNodes);
            }

            ringNodes.Add(node);
        }

        List<WorldMapNode> previousRing = null;
        foreach (var ring in rings)
        {
            List<WorldMapNode> connectionRing = ring.Value;
            if (ring.Key == 1 && _firstStartingNode != null && _secondStartingNode != null)
            {
                connectionRing = new List<WorldMapNode>(ring.Value);
                connectionRing.Remove(_firstStartingNode);
                Connect(_firstStartingNode, _secondStartingNode);
            }

            connectionRing.Sort(CompareNodesByAngle);
            for (int index = 0; index < connectionRing.Count; index++)
            {
                if (connectionRing.Count > 1)
                {
                    Connect(connectionRing[index], connectionRing[(index + 1) % connectionRing.Count]);
                }
            }

            if (previousRing == null)
            {
                WorldMapNode startNeighbor = null;
                foreach (var candidate in ring.Value)
                {
                    if (candidate.IsAccessible)
                    {
                        startNeighbor = candidate;
                        break;
                    }
                }

                Connect(StartNode, startNeighbor ?? FindNearestNode(StartNode, ring.Value));
            }
            else
            {
                foreach (var outerNode in connectionRing)
                {
                    Connect(outerNode, FindNearestNode(outerNode, previousRing));
                }
            }

            previousRing = connectionRing;
        }

        foreach (var bossNode in finalBossNodes)
        {
            Connect(bossNode, FindNearestNode(bossNode, previousRing));
        }
    }

    List<WorldMapNode> CreateFinalBossNodes(
        IReadOnlyList<EncounterSchema> availableSchemas,
        Utility.IRandom rng,
        float radius)
    {
        var bossSchemas = new List<EncounterSchema>();
        foreach (var schema in availableSchemas)
            if (schema.encounterType == EncounterType.Boss)
                bossSchemas.AddRange(availableSchemas);

        if (bossSchemas.Count == 0)
        {
            return new List<WorldMapNode>();
        }

        int bossRadius = Mathf.CeilToInt(radius);
        var positions = new[]
        {
            new MapPoint(0, -bossRadius),
            new MapPoint(0, bossRadius),
            new MapPoint(-bossRadius, 0),
            new MapPoint(bossRadius, 0),
        };

        var bossNodes = new List<WorldMapNode>(FinalBossNodeCount);
        foreach (MapPoint position in positions)
        {
            var bossNode = new WorldMapNode
            {
                Position = FindNearestFreePosition(position),
                Depth = EncounterRingCount + 1,
                Type = WorldMapNodeType.Boss,
            };

            float distanceFromStart = ((Vector2Int)bossNode.Position).magnitude;
            var difficultyCurve = GlobalConstantsHandler.Constants?.EncounterDifficultyScaling;
            float difficultyToSpend = (difficultyCurve?.Evaluate(distanceFromStart) ?? 0f)
                * GlobalConstantsHandler.Constants.DifficultyScalerModifier;
            Encounter encounter = PurchaseEncounter(bossSchemas, difficultyToSpend, rng);
            if (encounter == null)
            {
                EncounterSchema fallbackSchema = bossSchemas[rng.Range(0, bossSchemas.Count)];
                encounter = fallbackSchema.CreateEncounter();
            }

            bossNode.Encounter = encounter;
            bossNode.DifficultyScale = difficultyToSpend;
            bossNode.IsVisited = encounter.IsCompleted && encounter.PlayerWon;
            ScaleEncounter(encounter, distanceFromStart);
            Add(bossNode);
            bossNodes.Add(bossNode);
        }

        return bossNodes;
    }

    static Encounter PurchaseEncounter(
        IReadOnlyList<EncounterSchema> schemas, float difficultyToSpend, Utility.IRandom rng)
    {
        if (float.IsNaN(difficultyToSpend) || float.IsInfinity(difficultyToSpend))
        {
            return null;
        }

        var enemies = new SpeciesGroup("Generated encounter");
        var selector = new WeightedSelector<EncounterSchema>(rng);
        while (difficultyToSpend > 0f)
        {
            // Rebuild after each purchase so an expensive draw cannot overspend
            // or prevent a cheaper affordable entry from using the remainder.
            selector.Clear();
            foreach (var schema in schemas)
            {
                if (schema.DifficultyMultiplier <= difficultyToSpend
                    && difficultyToSpend - schema.DifficultyMultiplier < difficultyToSpend)
                {
                    selector.AddItem(schema, schema.Weight);
                }
            }

            if (!selector.HasResult())
            {
                break;
            }

            var selected = selector.GetResult();
            var purchasedEncounter = selected.CreateEncounter();
            foreach (var species in purchasedEncounter.EnemyGroup.Members)
            {
                enemies.Add(species);
            }

            difficultyToSpend -= selected.DifficultyMultiplier;
        }

        return enemies.Members.Count > 0 ? new Encounter(enemies) : null;
    }

    static void ScaleEncounter(Encounter encounter, float distanceFromStart)
    {
        if (encounter?.EnemyGroup?.Members == null || distanceFromStart <= 0f)
            return;

        var constants = GlobalConstantsHandler.Constants;
        foreach (var species in encounter.EnemyGroup.Members)
        {
            if (species == null)
                continue;

            species.BaseHealth = ScaleStat(species.BaseHealth, constants?.HPScaling?.Evaluate(distanceFromStart) ?? 1f);
            species.CurrentHealth = species.BaseHealth;
            species.BaseSize = ScaleStat(species.BaseSize, constants?.SizeScaling?.Evaluate(distanceFromStart) ?? 1f);
            species.BaseAttack = ScaleStat(species.BaseAttack, constants?.AttackScaling?.Evaluate(distanceFromStart) ?? 1f);
            species.BaseDefense = ScaleStat(species.BaseDefense, constants?.DefenseScaling?.Evaluate(distanceFromStart) ?? 1f);
            species.BaseForage = ScaleStat(species.BaseForage, constants?.ForageScaling?.Evaluate(distanceFromStart) ?? 1f);
        }
    }

    static int ScaleStat(int value, float distanceFromStart, AnimationCurve scalingCurve)
    {
        if (value <= 0 || scalingCurve == null)
            return value;

        float multiplier = 1f + scalingCurve.Evaluate(distanceFromStart);
        return ScaleStat(value, multiplier);
    }

    static int ScaleStat(int value, float multiplier)
    {
        if (value <= 0)
            return value;

        return Mathf.Max(value, (int)Mathf.Round(value * multiplier));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static int GetEncounterCountForRings()
    {
        int encounterCount = 0;
        for (int ringIndex = 1; ringIndex <= EncounterRingCount; ringIndex++)
        {
            encounterCount += FirstRingCapacity * ringIndex;
        }

        return encounterCount;
    }

    static int CountNodesInRing(IReadOnlyList<WorldMapNode> nodes, int ringIndex)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node.Depth == ringIndex)
            {
                count++;
            }
        }

        return count;
    }

    static int CompareNodesByAngle(WorldMapNode first, WorldMapNode second)
    {
        float firstAngle = Mathf.Atan2(first.Position.Y, first.Position.X);
        float secondAngle = Mathf.Atan2(second.Position.Y, second.Position.X);
        int angleComparison = firstAngle.CompareTo(secondAngle);
        if (angleComparison != 0)
        {
            return angleComparison;
        }

        int distanceComparison = DistanceSquared(first.Position, new MapPoint(0, 0))
            .CompareTo(DistanceSquared(second.Position, new MapPoint(0, 0)));
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        int xComparison = first.Position.X.CompareTo(second.Position.X);
        return xComparison != 0 ? xComparison : first.Position.Y.CompareTo(second.Position.Y);
    }

    static WorldMapNode FindNearestNode(WorldMapNode origin, IReadOnlyList<WorldMapNode> candidates)
    {
        if (origin == null || candidates == null || candidates.Count == 0)
        {
            return null;
        }

        WorldMapNode nearest = null;
        int nearestDistanceSquared = int.MaxValue;

        foreach (var candidate in candidates)
        {
            int distanceSquared = DistanceSquared(origin.Position, candidate.Position);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearest = candidate;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearest;
    }

    void Add(WorldMapNode node) => _nodes[node.Position] = node;

    void Connect(WorldMapNode a, WorldMapNode b)
    {
        if (a == null || b == null)
            return;

        if (!a.Connections.Contains(b.Position))
            a.Connections.Add(b.Position);

        if (!b.Connections.Contains(a.Position))
            b.Connections.Add(a.Position);
    }

    MapPoint FindNearestFreePosition(MapPoint desired)
    {
        for (int radius = 0; ; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius > 0 && Mathf.Abs(dx) != radius && Mathf.Abs(dy) != radius)
                        continue;

                    var candidate = new MapPoint(desired.X + dx, desired.Y + dy);
                    if (IsPositionAvailable(candidate))
                        return candidate;
                }
            }
        }
    }

    bool IsPositionAvailable(MapPoint candidate)
    {
        if (_nodes.ContainsKey(candidate))
            return false;

        if (_firstStartingNode != null
            && DistanceSquared(candidate, _firstStartingNode.Position) < StartingNodeClearanceSquared)
        {
            return false;
        }

        if (_secondStartingNode != null
            && DistanceSquared(candidate, _secondStartingNode.Position) < StartingNodeClearanceSquared)
        {
            return false;
        }

        return true;
    }

    static int DistanceSquared(MapPoint a, MapPoint b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // ── Mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Update the player's current position on the map.
    /// Also marks the destination node as visited and updates accessibility of
    /// all subsequent nodes.
    /// </summary>
    public void MovePlayerTo(WorldMapNode destination)
    {
        PlayerNode = destination;
        destination.IsVisited = true;

        if (destination == _firstStartingNode && _secondStartingNode != null && !_secondStartingNode.IsVisited)
        {
            _secondStartingNode.IsAccessible = true;
            return;
        }

        // Open up direct neighbours of the new position.
        foreach (var connPos in destination.Connections)
        {
            if (_nodes.TryGetValue(connPos, out var neighbour) && !neighbour.IsVisited)
                neighbour.IsAccessible = true;
        }
    }
}
