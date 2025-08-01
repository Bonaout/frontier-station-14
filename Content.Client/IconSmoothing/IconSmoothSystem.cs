using System.Numerics;
using Content.Client.Anomaly.Ui;
using Content.Client.Overlays;
using Content.Shared.IconSmoothing;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Enumerators;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client.IconSmoothing
{
    // TODO: just make this set appearance data?
    /// <summary>
    ///     Entity system implementing the logic for <see cref="IconSmoothComponent"/>
    /// </summary>
    [UsedImplicitly]
    public sealed partial class IconSmoothSystem : EntitySystem
    {
        [Dependency] private readonly SharedMapSystem _mapSystem = default!;
        [Dependency] private readonly SpriteSystem _sprite = default!;

        private readonly Queue<EntityUid> _dirtyEntities = new();
        private readonly Queue<EntityUid> _anchorChangedEntities = new();

        private int _generation;

        public void SetEnabled(EntityUid uid, bool value, IconSmoothComponent? component = null)
        {
            if (!Resolve(uid, ref component, false) || value == component.Enabled)
                return;

            component.Enabled = value;
            DirtyNeighbours(uid, component);
        }

        public override void Initialize()
        {
            base.Initialize();

            InitializeEdge();
            SubscribeLocalEvent<IconSmoothComponent, AnchorStateChangedEvent>(OnAnchorChanged);
            SubscribeLocalEvent<IconSmoothComponent, ComponentShutdown>(OnShutdown);
            SubscribeLocalEvent<IconSmoothComponent, ComponentStartup>(OnStartup);
        }

        private void OnStartup(EntityUid uid, IconSmoothComponent component, ComponentStartup args)
        {
            var xform = Transform(uid);
            if (xform.Anchored)
            {
                component.LastPosition = TryComp<MapGridComponent>(xform.GridUid, out var grid)
                    ? (xform.GridUid.Value, _mapSystem.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates))
                    : (null, new Vector2i(0, 0));

                DirtyNeighbours(uid, component);
            }

            if (component.Mode != IconSmoothingMode.Corners || !TryComp(uid, out SpriteComponent? sprite))
                return;

            SetCornerLayers((uid, sprite), component);

            if (component.Shader != null)
            {
                sprite.LayerSetShader(CornerLayers.SE, component.Shader);
                sprite.LayerSetShader(CornerLayers.NE, component.Shader);
                sprite.LayerSetShader(CornerLayers.NW, component.Shader);
                sprite.LayerSetShader(CornerLayers.SW, component.Shader);
            }
        }

        public void SetStateBase(EntityUid uid, IconSmoothComponent component, string newState)
        {
            if (!TryComp<SpriteComponent>(uid, out var sprite))
                return;

            component.StateBase = newState;
            SetCornerLayers((uid, sprite), component);
        }

        private void SetCornerLayers(Entity<SpriteComponent?> sprite, IconSmoothComponent component)
        {
            // Frontier: Allow overlays on entities using CornerLayers smoothing - don't remove layers, adjust existing ones or create new ones.
            var state0 = $"{component.StateBase}0";
            SetCornerLayerState(sprite, CornerLayers.SE, DirectionOffset.None, state0);
            SetCornerLayerState(sprite, CornerLayers.NE, DirectionOffset.CounterClockwise, state0);
            SetCornerLayerState(sprite, CornerLayers.NW, DirectionOffset.Flip, state0);
            SetCornerLayerState(sprite, CornerLayers.SW, DirectionOffset.Clockwise, state0);

            // _sprite.LayerMapRemove(sprite, CornerLayers.SE);
            // _sprite.LayerMapRemove(sprite, CornerLayers.NE);
            // _sprite.LayerMapRemove(sprite, CornerLayers.NW);
            // _sprite.LayerMapRemove(sprite, CornerLayers.SW);

            // var state0 = $"{component.StateBase}0";
            // _sprite.LayerMapSet(sprite, CornerLayers.SE, _sprite.AddRsiLayer(sprite, state0));
            // _sprite.LayerSetDirOffset(sprite, CornerLayers.SE, DirectionOffset.None);
            // _sprite.LayerMapSet(sprite, CornerLayers.NE, _sprite.AddRsiLayer(sprite, state0));
            // _sprite.LayerSetDirOffset(sprite, CornerLayers.NE, DirectionOffset.CounterClockwise);
            // _sprite.LayerMapSet(sprite, CornerLayers.NW, _sprite.AddRsiLayer(sprite, state0));
            // _sprite.LayerSetDirOffset(sprite, CornerLayers.NW, DirectionOffset.Flip);
            // _sprite.LayerMapSet(sprite, CornerLayers.SW, _sprite.AddRsiLayer(sprite, state0));
            // _sprite.LayerSetDirOffset(sprite, CornerLayers.SW, DirectionOffset.Clockwise);
            // End Frontier: Allow overlays on entities using CornerLayers smoothing - don't remove layers, adjust existing ones or create new ones.
        }

        // Frontier: set layer function to remove redundancy
        private void SetCornerLayerState(Entity<SpriteComponent?> sprite, CornerLayers corner, DirectionOffset offset, string state)
        {
            if (_sprite.LayerMapTryGet(sprite, corner, out var index, false))
                _sprite.LayerSetRsiState(sprite, index, state);
            else
                _sprite.LayerMapSet(sprite, corner, _sprite.AddRsiLayer(sprite, state));

            _sprite.LayerSetDirOffset(sprite, corner, offset);
        }
        // End Frontier: set layer function to remove redundancy

        private void OnShutdown(EntityUid uid, IconSmoothComponent component, ComponentShutdown args)
        {
            _dirtyEntities.Enqueue(uid);
            DirtyNeighbours(uid, component);
        }

        public override void FrameUpdate(float frameTime)
        {
            base.FrameUpdate(frameTime);

            var xformQuery = GetEntityQuery<TransformComponent>();
            var smoothQuery = GetEntityQuery<IconSmoothComponent>();

            // first process anchor state changes.
            while (_anchorChangedEntities.TryDequeue(out var uid))
            {
                if (!xformQuery.TryGetComponent(uid, out var xform))
                    continue;

                if (xform.MapID == MapId.Nullspace)
                {
                    // in null-space. Almost certainly because it left PVS. If something ever gets sent to null-space
                    // for reasons other than this (or entity deletion), then maybe we still need to update ex-neighbor
                    // smoothing here.
                    continue;
                }

                DirtyNeighbours(uid, comp: null, xform, smoothQuery);
            }

            // Next, update actual sprites.
            if (_dirtyEntities.Count == 0)
                return;

            _generation += 1;

            var spriteQuery = GetEntityQuery<SpriteComponent>();

            // Performance: This could be spread over multiple updates, or made parallel.
            while (_dirtyEntities.TryDequeue(out var uid))
            {
                CalculateNewSprite(uid, spriteQuery, smoothQuery, xformQuery);
            }
        }

        public void DirtyNeighbours(EntityUid uid, IconSmoothComponent? comp = null, TransformComponent? transform = null, EntityQuery<IconSmoothComponent>? smoothQuery = null)
        {
            smoothQuery ??= GetEntityQuery<IconSmoothComponent>();
            if (!smoothQuery.Value.Resolve(uid, ref comp) || !comp.Running)
                return;

            _dirtyEntities.Enqueue(uid);

            if (!Resolve(uid, ref transform))
                return;

            Vector2i pos;

            EntityUid entityUid;

            if (transform.Anchored && TryComp<MapGridComponent>(transform.GridUid, out var grid))
            {
                entityUid = transform.GridUid.Value;
                pos = _mapSystem.CoordinatesToTile(transform.GridUid.Value, grid, transform.Coordinates);
            }
            else
            {
                // Entity is no longer valid, update around the last position it was at.
                if (comp.LastPosition is not (EntityUid gridId, Vector2i oldPos))
                    return;

                if (!TryComp(gridId, out grid))
                    return;

                entityUid = gridId;
                pos = oldPos;
            }

            // Yes, we updates ALL smoothing entities surrounding us even if they would never smooth with us.
            DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(1, 0)));
            DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(-1, 0)));
            DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(0, 1)));
            DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(0, -1)));

            if (comp.Mode is IconSmoothingMode.Corners or IconSmoothingMode.NoSprite or IconSmoothingMode.Diagonal or IconSmoothingMode.DiagonalNF) // Frontier: add DiagonalNF
            {
                DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(1, 1)));
                DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(-1, -1)));
                DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(-1, 1)));
                DirtyEntities(_mapSystem.GetAnchoredEntitiesEnumerator(entityUid, grid, pos + new Vector2i(1, -1)));
            }
        }

        private void DirtyEntities(AnchoredEntitiesEnumerator entities)
        {
            // Instead of doing HasComp -> Enqueue -> TryGetComp, we will just enqueue all entities. Generally when
            // dealing with walls neighboring anchored entities will also be walls, and in those instances that will
            // require one less component fetch/check.
            while (entities.MoveNext(out var entity))
            {
                _dirtyEntities.Enqueue(entity.Value);
            }
        }

        private void OnAnchorChanged(EntityUid uid, IconSmoothComponent component, ref AnchorStateChangedEvent args)
        {
            if (!args.Detaching)
                _anchorChangedEntities.Enqueue(uid);
        }

        private void CalculateNewSprite(EntityUid uid,
            EntityQuery<SpriteComponent> spriteQuery,
            EntityQuery<IconSmoothComponent> smoothQuery,
            EntityQuery<TransformComponent> xformQuery,
            IconSmoothComponent? smooth = null)
        {
            TransformComponent? xform;
            Entity<MapGridComponent>? gridEntity = null;

            // The generation check prevents updating an entity multiple times per tick.
            // As it stands now, it's totally possible for something to get queued twice.
            // Generation on the component is set after an update so we can cull updates that happened this generation.
            if (!smoothQuery.Resolve(uid, ref smooth, false)
                || smooth.Mode == IconSmoothingMode.NoSprite
                || smooth.UpdateGeneration == _generation
                || !smooth.Enabled
                || !smooth.Running)
            {
                if (smooth is { Enabled: true } &&
                    TryComp<SmoothEdgeComponent>(uid, out var edge) &&
                    xformQuery.TryGetComponent(uid, out xform))
                {
                    var directions = DirectionFlag.None;

                    if (TryComp(xform.GridUid, out MapGridComponent? grid))
                    {
                        var gridUid = xform.GridUid.Value;
                        var pos = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);

                        gridEntity = (gridUid, grid);

                        if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.North)), smoothQuery, new(0, 1))) // Frontier: added (0, 1) vector
                            directions |= DirectionFlag.North;
                        if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.South)), smoothQuery, new(0, -1))) // Frontier: added (0, -1) vector
                            directions |= DirectionFlag.South;
                        if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.East)), smoothQuery, new(1, 0))) // Frontier: added (1, 0) vector
                            directions |= DirectionFlag.East;
                        if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.West)), smoothQuery, new(-1, 0))) // Frontier: added (-1, 0) vector
                            directions |= DirectionFlag.West;
                    }

                    CalculateEdge(uid, directions, component: edge);
                }

                return;
            }

            xform = xformQuery.GetComponent(uid);
            smooth.UpdateGeneration = _generation;

            if (!spriteQuery.TryGetComponent(uid, out var sprite))
            {
                Log.Error($"Encountered a icon-smoothing entity without a sprite: {ToPrettyString(uid)}");
                RemCompDeferred(uid, smooth);
                return;
            }

            var spriteEnt = (uid, sprite);

            if (xform.Anchored)
            {
                if (TryComp(xform.GridUid, out MapGridComponent? grid))
                {
                    gridEntity = (xform.GridUid.Value, grid);
                }
                else
                {
                    Log.Error($"Failed to calculate IconSmoothComponent sprite in {uid} because grid {xform.GridUid} was missing.");
                    return;
                }
            }

            switch (smooth.Mode)
            {
                case IconSmoothingMode.Corners:
                    CalculateNewSpriteCorners(gridEntity, smooth, spriteEnt, xform, smoothQuery);
                    break;
                case IconSmoothingMode.CardinalFlags:
                    CalculateNewSpriteCardinal(gridEntity, smooth, spriteEnt, xform, smoothQuery);
                    break;
                case IconSmoothingMode.Diagonal:
                    CalculateNewSpriteDiagonal(gridEntity, smooth, spriteEnt, xform, smoothQuery);
                    break;
                case IconSmoothingMode.DiagonalNF: // Frontier
                    CalculateNewSpriteDiagonalNF(gridEntity, smooth, spriteEnt, xform, smoothQuery); // Frontier
                    break; // Frontier
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void CalculateNewSpriteDiagonal(Entity<MapGridComponent>? gridEntity, IconSmoothComponent smooth,
            Entity<SpriteComponent> sprite, TransformComponent xform, EntityQuery<IconSmoothComponent> smoothQuery)
        {
            if (gridEntity == null)
            {
                _sprite.LayerSetRsiState(sprite.AsNullable(), 0, $"{smooth.StateBase}0");
                return;
            }

            var gridUid = gridEntity.Value.Owner;
            var grid = gridEntity.Value.Comp;

            var neighbors = new Vector2[]
            {
                new(1, 0),
                new(1, -1),
                new(0, -1),
            };

            var pos = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            var rotation = xform.LocalRotation;
            var matching = true;

            for (var i = 0; i < neighbors.Length; i++)
            {
                var neighbor = (Vector2i)rotation.RotateVec(neighbors[i]);
                matching = matching && MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos + neighbor), smoothQuery, neighbor); // Frontier: add neighbor
            }

            if (matching)
            {
                _sprite.LayerSetRsiState(sprite.AsNullable(), 0, $"{smooth.StateBase}1");
            }
            else
            {
                _sprite.LayerSetRsiState(sprite.AsNullable(), 0, $"{smooth.StateBase}0");
            }
        }

        // New Frontiers - Better icon smoothing - icon smoothing that supports more diagonal window states
        //      and respects the empty sides of diagonal walls/windows
        // This code is licensed under AGPLv3. See AGPLv3.txt

        // Frontier: 5-state diagonal windows
        private void CalculateNewSpriteDiagonalNF(Entity<MapGridComponent>? gridEntity, IconSmoothComponent smooth,
            Entity<SpriteComponent> sprite, TransformComponent xform, EntityQuery<IconSmoothComponent> smoothQuery)
        {
            if (gridEntity == null)
            {
                sprite.Comp.LayerSetState(0, $"{smooth.StateBase}0");
                return;
            }

            var gridUid = gridEntity.Value.Owner;
            var grid = gridEntity.Value.Comp;

            var neighbors = new Vector2[]
            {
                new(1, 0),
                new(0, -1),
            };

            var pos = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            var rotation = xform.LocalRotation;
            int value = 0;

            for (var i = 0; i < neighbors.Length; i++)
            {
                var neighbor = (Vector2i)rotation.RotateVec(neighbors[i]);
                if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos + neighbor), smoothQuery, neighbor))
                    value |= 1 << i;
            }

            // both walls checked, check the corner
            if (value == 3)
            {
                var neighbor = (Vector2i)rotation.RotateVec(new(1, -1));
                if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos + neighbor), smoothQuery, neighbor))
                    value = 4;
            }

            sprite.Comp.LayerSetState(0, $"{smooth.StateBase}{value}");
        }

        // Check if a particular window/wall allows smoothing on a given edge
        private bool EntityIsSmoothOnEdge(EntityUid? target, IconSmoothComponent targetComp, Vector2 sourceDir)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            xformQuery.TryGetComponent(target, out var xform);
            if (xform is null)
                return false;

            if (targetComp.Mode is IconSmoothingMode.Diagonal or IconSmoothingMode.DiagonalNF)
            {
                var rot = -xform.LocalRotation; // Source direction must be transformed into our reference.
                var angle = new Angle(rot.RotateVec(-sourceDir)); // Direction is given from sourge to target, we need the direction from target to source.
                angle += Math.PI / 2; // Cardinal directions start at north(?), but positive X angle is 0.
                var dir = angle.GetCardinalDir();
                return dir is Direction.South or Direction.SouthEast or Direction.East;
            }
            return true; // All other target components use all sides
        }
        // End of modified code

        private void CalculateNewSpriteCardinal(Entity<MapGridComponent>? gridEntity, IconSmoothComponent smooth, Entity<SpriteComponent> sprite, TransformComponent xform, EntityQuery<IconSmoothComponent> smoothQuery)
        {
            var dirs = CardinalConnectDirs.None;

            if (gridEntity == null)
            {
                _sprite.LayerSetRsiState(sprite.AsNullable(), 0, $"{smooth.StateBase}{(int)dirs}");
                return;
            }

            var gridUid = gridEntity.Value.Owner;
            var grid = gridEntity.Value.Comp;

            var pos = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.North)), smoothQuery, new(0, 1))) // Frontier: add vector
                dirs |= CardinalConnectDirs.North;
            if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.South)), smoothQuery, new(0, -1))) // Frontier: add vector
                dirs |= CardinalConnectDirs.South;
            if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.East)), smoothQuery, new(1, 0))) // Frontier: add vector
                dirs |= CardinalConnectDirs.East;
            if (MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.West)), smoothQuery, new(-1, 0))) // Frontier: add vector
                dirs |= CardinalConnectDirs.West;

            _sprite.LayerSetRsiState(sprite.AsNullable(), 0, $"{smooth.StateBase}{(int)dirs}");

            var directions = DirectionFlag.None;

            if ((dirs & CardinalConnectDirs.South) != 0x0)
                directions |= DirectionFlag.South;
            if ((dirs & CardinalConnectDirs.East) != 0x0)
                directions |= DirectionFlag.East;
            if ((dirs & CardinalConnectDirs.North) != 0x0)
                directions |= DirectionFlag.North;
            if ((dirs & CardinalConnectDirs.West) != 0x0)
                directions |= DirectionFlag.West;

            CalculateEdge(sprite, directions, sprite);
        }

        private bool MatchingEntity(IconSmoothComponent smooth, AnchoredEntitiesEnumerator candidates, EntityQuery<IconSmoothComponent> smoothQuery, Vector2 offset)
        {
            while (candidates.MoveNext(out var entity))
            {
                if (smoothQuery.TryGetComponent(entity, out var other) &&
                    other.SmoothKey != null &&
                    (other.SmoothKey == smooth.SmoothKey || smooth.AdditionalKeys.Contains(other.SmoothKey)) &&
                    other.Enabled &&
                    EntityIsSmoothOnEdge(entity, other, offset)) // Frontier: added EntityIsSmoothOnEdge
                {
                    return true;
                }
            }

            return false;
        }

        private void CalculateNewSpriteCorners(Entity<MapGridComponent>? gridEntity, IconSmoothComponent smooth, Entity<SpriteComponent> spriteEnt, TransformComponent xform, EntityQuery<IconSmoothComponent> smoothQuery)
        {
            var (cornerNE, cornerNW, cornerSW, cornerSE) = gridEntity == null
                ? (CornerFill.None, CornerFill.None, CornerFill.None, CornerFill.None)
                : CalculateCornerFill(gridEntity.Value, smooth, xform, smoothQuery);

            // TODO figure out a better way to set multiple sprite layers.
            // This will currently re-calculate the sprite bounding box 4 times.
            // It will also result in 4-8 sprite update events being raised when it only needs to be 1-2.
            // At the very least each event currently only queues a sprite for updating.
            // Oh god sprite component is a mess.
            var sprite = spriteEnt.Comp;
            _sprite.LayerSetRsiState(spriteEnt.AsNullable(), CornerLayers.NE, $"{smooth.StateBase}{(int)cornerNE}");
            _sprite.LayerSetRsiState(spriteEnt.AsNullable(), CornerLayers.SE, $"{smooth.StateBase}{(int)cornerSE}");
            _sprite.LayerSetRsiState(spriteEnt.AsNullable(), CornerLayers.SW, $"{smooth.StateBase}{(int)cornerSW}");
            _sprite.LayerSetRsiState(spriteEnt.AsNullable(), CornerLayers.NW, $"{smooth.StateBase}{(int)cornerNW}");

            var directions = DirectionFlag.None;

            if ((cornerSE & cornerSW) != CornerFill.None)
                directions |= DirectionFlag.South;

            if ((cornerSE & cornerNE) != CornerFill.None)
                directions |= DirectionFlag.East;

            if ((cornerNE & cornerNW) != CornerFill.None)
                directions |= DirectionFlag.North;

            if ((cornerNW & cornerSW) != CornerFill.None)
                directions |= DirectionFlag.West;

            CalculateEdge(spriteEnt, directions, sprite);
        }

        private (CornerFill ne, CornerFill nw, CornerFill sw, CornerFill se) CalculateCornerFill(Entity<MapGridComponent> gridEntity, IconSmoothComponent smooth, TransformComponent xform, EntityQuery<IconSmoothComponent> smoothQuery)
        {
            var gridUid = gridEntity.Owner;
            var grid = gridEntity.Comp;

            var pos = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            var n = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.North)), smoothQuery, new(0, 1)); // Frontier: add vector
            var ne = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.NorthEast)), smoothQuery, new(1, 1)); // Frontier: add vector
            var e = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.East)), smoothQuery, new(1, 0)); // Frontier: add vector
            var se = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.SouthEast)), smoothQuery, new(1, -1)); // Frontier: add vector
            var s = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.South)), smoothQuery, new(0, -1)); // Frontier: add vector
            var sw = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.SouthWest)), smoothQuery, new(-1, -1)); // Frontier: add vector
            var w = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.West)), smoothQuery, new(-1, 0)); // Frontier: add vector
            var nw = MatchingEntity(smooth, _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, pos.Offset(Direction.NorthWest)), smoothQuery, new(-1, 1)); // Frontier: add vector

            // ReSharper disable InconsistentNaming
            var cornerNE = CornerFill.None;
            var cornerSE = CornerFill.None;
            var cornerSW = CornerFill.None;
            var cornerNW = CornerFill.None;
            // ReSharper restore InconsistentNaming

            if (n)
            {
                cornerNE |= CornerFill.CounterClockwise;
                cornerNW |= CornerFill.Clockwise;
            }

            if (ne)
            {
                cornerNE |= CornerFill.Diagonal;
            }

            if (e)
            {
                cornerNE |= CornerFill.Clockwise;
                cornerSE |= CornerFill.CounterClockwise;
            }

            if (se)
            {
                cornerSE |= CornerFill.Diagonal;
            }

            if (s)
            {
                cornerSE |= CornerFill.Clockwise;
                cornerSW |= CornerFill.CounterClockwise;
            }

            if (sw)
            {
                cornerSW |= CornerFill.Diagonal;
            }

            if (w)
            {
                cornerSW |= CornerFill.Clockwise;
                cornerNW |= CornerFill.CounterClockwise;
            }

            if (nw)
            {
                cornerNW |= CornerFill.Diagonal;
            }

            // Local is fine as we already know it's parented to the grid (due to the way anchoring works).
            switch (xform.LocalRotation.GetCardinalDir())
            {
                case Direction.North:
                    return (cornerSW, cornerSE, cornerNE, cornerNW);
                case Direction.West:
                    return (cornerSE, cornerNE, cornerNW, cornerSW);
                case Direction.South:
                    return (cornerNE, cornerNW, cornerSW, cornerSE);
                default:
                    return (cornerNW, cornerSW, cornerSE, cornerNE);
            }
        }

        // TODO consider changing this to use DirectionFlags?
        // would require re-labelling all the RSI states.
        [Flags]
        private enum CardinalConnectDirs : byte
        {
            None = 0,
            North = 1,
            South = 2,
            East = 4,
            West = 8
        }


        [Flags]
        private enum CornerFill : byte
        {
            // These values are pulled from Baystation12.
            // I'm too lazy to convert the state names.
            None = 0,

            // The cardinal tile counter-clockwise of this corner is filled.
            CounterClockwise = 1,

            // The diagonal tile in the direction of this corner.
            Diagonal = 2,

            // The cardinal tile clockwise of this corner is filled.
            Clockwise = 4,
        }

        private enum CornerLayers : byte
        {
            SE,
            NE,
            NW,
            SW,
        }
    }
}
